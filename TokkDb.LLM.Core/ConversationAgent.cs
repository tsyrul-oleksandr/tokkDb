using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using System.ComponentModel;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace TokkDb.LLM.Core;

public sealed class ConversationAgent : IConversationAgent, IDisposable
{
    /// <summary>
    /// System prompt for the main chat agent.
    ///
    /// Written as short, concrete rules rather than prose: the models this runs
    /// against are often small, and each line addresses a mistake seen in
    /// practice - guessing a collection name, describing a tool call instead of
    /// making it, or ending a turn with nothing to show.
    /// </summary>
    private const string DefaultInstructions =
        "You are the TokkDb assistant. You inspect and manage a dynamic record store on the user's behalf, " +
        "using the provided tools. Reply in the same language the user wrote in.\n" +

        "Know the schema before acting. Call GetCollections to see what exists and GetCollectionSchema to see a " +
        "collection's columns and relations. Never guess a collection, column or relation name - read it from the schema. " +
        "Names are exact, so 'Customer' and 'Customers' are different collections.\n" +

        "To read records, call QueryRecords. It is the only way to read them, and it covers every case in one call: " +
        "a condition, a sort order, a page, specific ids, or nothing at all to look at the collection. " +
        "It returns 10 records unless you ask for more.\n" +

        "To show records to the user, query them and then actually call ShowRecords with the collection name and the " +
        "record ids. Describing the intention is not enough - the list only appears if the call is made. Add a short " +
        "sentence of your own, such as how many records were found; the application renders the records themselves, " +
        "so never repeat every field value in your reply.\n" +

        "To change a schema - adding, removing or renaming a column, creating or removing a collection - call ChangeSchema once. " +
        "It validates the change, works out what it affects, and either applies it or asks the user. Read the outcome: " +
        "Applied means it is done; AwaitingConfirmation means the user is being asked, and once they answer you call " +
        "ConfirmSchemaChange with the confirmationId it gave you.\n" +

        "For display rules, read the collection with GetCollectionSchema, check a candidate with ValidateDisplayRule, " +
        "then submit it with ProposeDisplayRule. Always check the result rather than assuming it was applied.\n" +

        "When an action needs the user to decide, call RequestUserAction and wait for the answer.\n" +

        "When a tool returns errors, read them: they name the column, relation or field at fault and usually say what " +
        "is allowed. Correct the call rather than repeating it unchanged, and if it still fails, tell the user plainly " +
        "what did not work instead of retrying silently.\n" +

        "Always finish your turn with a brief reply to the user. Never end a turn silently.";

    /// <summary>
    /// Internal nudge used to recover a turn that ended without a reply. It is
    /// never shown in the chat and never becomes part of the visible transcript.
    /// </summary>
    private const string ContinuationPrompt =
        "Continue. Reply to the user now, briefly, in their language.";

    /// <summary>
    /// Serializer used for every tool's arguments.
    ///
    /// Built from the AI defaults rather than the plain framework defaults, so
    /// the camelCase names models actually send keep binding; on top of that it
    /// tolerates a value arriving as the wrong JSON type.
    /// </summary>
    private static readonly JsonSerializerOptions Options = BuildToolSerializerOptions();

    private static JsonSerializerOptions BuildToolSerializerOptions()
    {
        var options = new JsonSerializerOptions(AIJsonUtilities.DefaultOptions);
        options.Converters.Add(new ForgivingStringConverter());
        return options;
    }

    private readonly IServiceProvider _serviceProvider;
    private readonly IStorageToolGateway _storageTools;
    private readonly ISemanticTypeAgent _semanticTypeAgent;
    private readonly ILoggerFactory _loggerFactory;
    private readonly ILogger<ConversationAgent> _logger;
    private readonly SemaphoreSlim _sessionGate = new(1, 1);
    private readonly List<AgentToolExecution> _recentToolExecutions = new();
    private UserInteractionRequest? _requestedUserInteraction;

    private ChatClientAgent? _agent;
    private AgentSession? _session;
    private ConversationRequest? _activeConfiguration;

    public ConversationAgent(
        IServiceProvider serviceProvider,
        IStorageToolGateway storageTools,
        ISemanticTypeAgent semanticTypeAgent,
        ILoggerFactory loggerFactory,
        ILogger<ConversationAgent> logger)
    {
        _serviceProvider = serviceProvider;
        _storageTools = storageTools;
        _semanticTypeAgent = semanticTypeAgent;
        _loggerFactory = loggerFactory;
        _logger = logger;
    }

    public event EventHandler<AgentToolExecutionEventArgs>? ToolExecutionStatusChanged;

    public event EventHandler<AgentReasoningEventArgs>? ReasoningUpdated;

    public event EventHandler<RecordsDisplayEventArgs>? RecordsDisplayRequested;

    public async Task<ConversationResponse> SendAsync(
        ConversationRequest request,
        CancellationToken cancellationToken = default)
    {
        ValidateRequest(request);
        await _sessionGate.WaitAsync(cancellationToken);

        try
        {
            if (_agent is null || _session is null || HasConfigurationChanged(request, _activeConfiguration))
            {
                // Agent selection: a new agent is built whenever the provider,
                // endpoint or model changes.
                _logger.LogInformation(
                    "Agent selected. AgentName: {AgentName}, Provider: {Provider}, Model: {Model}, ContextSize: {ContextSize}",
                    "TokkDbAgent",
                    request.Provider,
                    request.Model,
                    request.ContextSize);

                _agent = BuildAgent(request);
                _session = await _agent.CreateSessionAsync(cancellationToken);
                _activeConfiguration = request;

                _logger.LogDebug(
                    "Agent session created. AgentName: {AgentName}, Provider: {Provider}, Model: {Model}",
                    "TokkDbAgent",
                    request.Provider,
                    request.Model);
            }

            _recentToolExecutions.Clear();
            _requestedUserInteraction = null;

            // Streamed so that reasoning can be surfaced while it is produced,
            // and so that the interleaving of reasoning and answer text is
            // observed in the order the model emits it.
            var collector = new ReasoningStreamAssembler(RaiseReasoningUpdate);
            var answerText = new StringBuilder();
            var stopwatch = System.Diagnostics.Stopwatch.StartNew();
            var updateCount = 0;
            // Tool calls make several round trips; each reports its own usage.
            var usage = AgentTokenUsage.None;

            // The prompt itself is logged only at Trace: it can be large and may
            // contain document or user content.
            _logger.LogInformation(
                "LLM request started. Provider: {Provider}, Model: {Model}, AgentName: {AgentName}",
                request.Provider,
                request.Model,
                "TokkDbAgent");
            _logger.LogTrace(
                "LLM prompt. Provider: {Provider}, Model: {Model}, PromptLength: {PromptLength}",
                request.Provider,
                request.Model,
                request.Message.Length);
            _logger.LogDebug("LLM streaming started. Provider: {Provider}, Model: {Model}", request.Provider, request.Model);

            var runChatOptions = new ChatOptions
            {
                ModelId = request.Model,
                Instructions = string.IsNullOrWhiteSpace(request.SystemPrompt)
                    ? DefaultInstructions
                    : request.SystemPrompt
            };
            LlmProviderFactory.ApplyProviderOptions(runChatOptions, request.Provider, request.ContextSize);
            var runOptions = new ChatClientAgentRunOptions(runChatOptions);

            async Task StreamTurnAsync(string message)
            {
                await foreach (var update in _agent.RunStreamingAsync(
                                   message,
                                   _session,
                                   runOptions,
                                   cancellationToken).ConfigureAwait(false))
                {
                    updateCount++;
                    foreach (var content in update.Contents)
                    {
                        switch (content)
                        {
                            // Provider-independent reasoning: the OpenAI and Ollama
                            // clients both normalise their own reasoning fields into
                            // this type, so no provider branching is needed here.
                            case TextReasoningContent reasoning:
                                collector.AppendReasoning(reasoning.Text);
                                break;

                            case TextContent text:
                                collector.NoteAnswerText();
                                answerText.Append(text.Text);
                                break;

                            case UsageContent reported:
                                usage = usage.Add(
                                    reported.Details.InputTokenCount ?? 0,
                                    reported.Details.OutputTokenCount ?? 0,
                                    reported.Details.TotalTokenCount ?? 0);
                                break;
                        }
                    }
                }
            }

            try
            {
                await StreamTurnAsync(request.Message).ConfigureAwait(false);

                // Small reasoning models intermittently end a turn having emitted
                // only their thinking block and no visible content. It is not a
                // truncation - the turn stops normally - and it clears on a second
                // attempt, so the turn is continued once on the same session.
                // Skipped when the agent is waiting on a user decision, so
                // human-in-the-loop is never disturbed.
                if (answerText.Length == 0 &&
                    _requestedUserInteraction is null &&
                    !cancellationToken.IsCancellationRequested)
                {
                    _logger.LogWarning(
                        "Turn produced no visible reply; continuing once. Provider: {Provider}, Model: {Model}, Updates: {UpdateCount}",
                        request.Provider,
                        request.Model,
                        updateCount);

                    await StreamTurnAsync(ContinuationPrompt).ConfigureAwait(false);

                    _logger.LogInformation(
                        "Continuation finished. Provider: {Provider}, Model: {Model}, Recovered: {Recovered}, Updates: {UpdateCount}",
                        request.Provider,
                        request.Model,
                        answerText.Length > 0,
                        updateCount);
                }

                collector.Complete();

                _logger.LogDebug(
                    "LLM streaming completed. Provider: {Provider}, Model: {Model}, Updates: {UpdateCount}, UsageReported: {UsageReported}, Duration: {Duration}",
                    request.Provider,
                    request.Model,
                    updateCount,
                    usage.HasValue,
                    stopwatch.Elapsed);

                var response = new ConversationResponse(
                    answerText.ToString(),
                    _recentToolExecutions.ToArray(),
                    _requestedUserInteraction)
                {
                    Reasoning = collector.Segments.ToArray(),
                    Usage = usage.HasValue ? usage : null
                };

                if (response.Reasoning.Count > 0)
                {
                    _logger.LogInformation(
                        "LLM reasoning received. Provider: {Provider}, Model: {Model}, Segments: {ReasoningSegments}",
                        request.Provider,
                        request.Model,
                        response.Reasoning.Count);
                }

                _logger.LogInformation(
                    "LLM response processing completed. Provider: {Provider}, Model: {Model}, ResponseLength: {ResponseLength}, ToolCalls: {ToolCallCount}, RequiresUserDecision: {RequiresUserDecision}, InputTokens: {InputTokens}, OutputTokens: {OutputTokens}, Duration: {Duration}",
                    request.Provider,
                    request.Model,
                    response.Text.Length,
                    _recentToolExecutions.Count,
                    response.UserInteractionRequest is not null,
                    usage.InputTokens,
                    usage.OutputTokens,
                    stopwatch.Elapsed);

                return response;
            }
            catch (OperationCanceledException ex)
            {
                _logger.LogWarning(
                    ex,
                    "LLM request cancelled. Provider: {Provider}, Model: {Model}, Duration: {Duration}",
                    request.Provider,
                    request.Model,
                    stopwatch.Elapsed);
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogError(
                    ex,
                    "LLM request failed. Provider: {Provider}, Model: {Model}, Duration: {Duration}",
                    request.Provider,
                    request.Model,
                    stopwatch.Elapsed);
                throw;
            }
        }
        finally
        {
            _sessionGate.Release();
        }
    }

    private void RaiseReasoningUpdate(AgentReasoningUpdate update)
    {
        ReasoningUpdated?.Invoke(this, new AgentReasoningEventArgs(update));
    }


    public void ResetConversation()
    {
        _activeConfiguration = null;
        _agent = null;
        _session = null;
        _recentToolExecutions.Clear();
    }

    public void Dispose()
    {
        _sessionGate.Dispose();
    }

    private ChatClientAgent BuildAgent(ConversationRequest request)
    {
        var chatClient = LlmProviderFactory.CreateChatClient(request.Provider, request.Url, request.Model,
            request.AuthenticationToken);

        var options = new ChatClientAgentOptions
        {
            Name = "TokkDbAgent",
            Description =
                "Assistant for the TokkDb record store. Inspects collections and their schemas, searches and " +
                "displays records, and proposes schema and display-rule changes through controlled workflows.",
            ChatOptions = new ChatOptions
            {
                ModelId = request.Model,
                Instructions = string.IsNullOrWhiteSpace(request.SystemPrompt)
                    ? DefaultInstructions
                    : request.SystemPrompt,
                ToolMode = ChatToolMode.Auto,
                Tools = BuildTools()
            }
        };

        LlmProviderFactory.ApplyProviderOptions(options.ChatOptions, request.Provider, request.ContextSize);

        return new ChatClientAgent(chatClient, options, _loggerFactory, _serviceProvider);
    }

    private List<AITool> BuildTools()
    {
        return
        [
            AIFunctionFactory.Create(
                (Func<string, List<string>?, RecordFilter?, List<RecordQuerySort>?, int?, int?, List<string>?, StorageToolResult<RecordQueryResult>>)QueryRecords,
                new AIFunctionFactoryOptions
                {
                    SerializerOptions = Options,
                    Name = "QueryRecords",
                    Description =
                        "Reads records from a collection. This is the only tool that reads records, and it covers every case: " +
                        "filtering, sorting, paging, fetching particular records by id, and reading a collection with no condition at all. " +
                        "The query is validated against the schema and run immediately. " +
                        "It returns at most 10 records unless take says otherwise, so ask for more only when they are needed. " +
                        "where is a filter tree: a field predicate {field, operator, value} with operator eq/neq/gt/gte/lt/lte/startsWith/endsWith/contains/in/between/isNull/isNotNull; " +
                        "a group {logic: and|or|not, filters: [...]}; or a relation step {relation, quantifier: any|none|all, where: {...}} that follows a declared relation by name. " +
                        "Text operators only work on string columns and comparison operators only on numeric or date columns. " +
                        "Also accepts recordIds, orderBy [{column, direction}], skip, take and select. " +
                        "Example - the first records of a collection: {collectionName:'Customer'}. " +
                        "Example - one record by id: {collectionName:'Customer', recordIds:['...']}. " +
                        "Example - one column equalling a value: {collectionName:'Customer', where:{field:'FullName', operator:'eq', value:'Olena'}}. " +
                        "Example - customers with a Ukrainian phone: {collectionName:'Customer', where:{field:'Phone', operator:'startsWith', value:'+380'}}. " +
                        "Example - customers who bought a product costing 40 or more: {collectionName:'Customer', where:{relation:'CustomerOrders', quantifier:'any', where:{relation:'OrderProduct', quantifier:'any', where:{field:'Price', operator:'gte', value:'40'}}}}. " +
                        "Call GetCollectionSchema first when unsure of column or relation names."
                }),
            AIFunctionFactory.Create(
                (Func<string, List<string>?, List<string>?, StorageToolResult<RecordsDisplayMessage>>)ShowRecords,
                new AIFunctionFactoryOptions
                {
                    SerializerOptions = Options,
                    Name = "ShowRecords",
                    Description =
                        "Displays records in the chat as an interactive list. Use this whenever the user asks to see, list or display records: " +
                        "first query the records, then pass their ids here. Do not repeat the record values in your reply - the application renders them."
                }),
            AIFunctionFactory.Create(
                (Func<string, StorageToolResult<DisplayRuleToolResult>>)GetDisplayRule,
                new AIFunctionFactoryOptions
                {
                    SerializerOptions = Options,
                    Name = "GetDisplayRule",
                    Description = "Returns a collection\u0027s display rule template and whether it still matches the current schema."
                }),
            AIFunctionFactory.Create(
                (Func<string, string, StorageToolResult<DisplayRuleToolResult>>)ValidateDisplayRule,
                new AIFunctionFactoryOptions
                {
                    SerializerOptions = Options,
                    Name = "ValidateDisplayRule",
                    Description = "Validates a display rule template against a collection schema without applying it. Template syntax uses column references in braces, for example '{FullName} - {Email}'. Arguments: collectionName, template."
                }),
            AIFunctionFactory.Create(
                (Func<string, string, string?, StorageToolResult<DisplayRuleProposalResult>>)ProposeDisplayRule,
                new AIFunctionFactoryOptions
                {
                    SerializerOptions = Options,
                    Name = "ProposeDisplayRule",
                    Description = "Proposes a display rule for a collection. The rule is validated deterministically and either applied or returned for user approval; never assume it was applied without checking the result. Arguments: request."
                }),
            AIFunctionFactory.Create(
                (Func<string, string?, List<string>, string?, Task<StorageToolResult<SemanticTypeResolutionToolResult>>>)ResolveSemanticType,
                new AIFunctionFactoryOptions
                {
                    SerializerOptions = Options,
                    Name = "ResolveSemanticType",
                    Description = "Suggests which registered semantic type best fits a column, given its name, description, expected base type and a few example values. " +
                                  "Use it to fill in a column\u0027s semanticTypeName before a schema change. Returns a suggestion with a confidence score; " +
                                  "it registers nothing, and new semantic types are registered during document import rather than from chat."
                }),
            AIFunctionFactory.Create(
                (Func<string, List<UserAction>?, StorageToolResult<UserInteractionRequest>>)RequestUserAction,
                new AIFunctionFactoryOptions
                {
                    SerializerOptions = Options,
                    Name = "RequestUserAction",
                    Description = "Pauses and asks the user to decide. Give a message explaining the choice and the actions they can pick from, such as approve and reject. The turn waits for their answer, so call this only when the decision genuinely cannot be made from the data."
                }),
            AIFunctionFactory.Create(
                (Func<StorageToolResult<IReadOnlyCollection<string>>>)GetCollections,
                new AIFunctionFactoryOptions
                {
                    SerializerOptions = Options,
                    Name = "GetCollections",
                    Description = "Lists the names of every collection in the store. Start here when you do not yet know what data exists."
                }),
            AIFunctionFactory.Create(
                (Func<string, StorageToolResult<CollectionSchemaResult>>)GetCollectionSchema,
                new AIFunctionFactoryOptions
                {
                    SerializerOptions = Options,
                    Name = "GetCollectionSchema",
                    Description = "Returns one collection\u0027s columns, with their types and descriptions, and the relations it takes part in. Read this before filtering, inserting or changing anything, so names and types are exact."
                }),
            AIFunctionFactory.Create(
                AnalyzeRecords,
                new AIFunctionFactoryOptions
                {
                    SerializerOptions = Options,
                    Name = "AnalyzeRecords",
                    Description =
                        "Answers a question about a collection as a whole, in one call. Use it when the answer is a count or a " +
                        "comparison across records rather than a list of them - do not try to work these out by reading records yourself. " +
                        "MostFrequent - which value is commonest, what are the top few: counts records per distinct value of groupByColumns " +
                        "and returns the commonest first, with a Count column. " +
                        "FindDuplicates - is anything repeated, are there duplicates: returns the values of groupByColumns that more than one record shares. " +
                        "FindUnreferenced - which ones were never used, ordered, assigned or referenced: returns records of collectionName whose " +
                        "collectionKeyColumn value appears in no record of relatedCollectionName under relatedKeyColumn. " +
                        "Example - products that were never ordered: " +
                        "{queryType:'FindUnreferenced', collectionName:'Product', collectionKeyColumn:'Sku', relatedCollectionName:'Order', relatedKeyColumn:'Sku'}. " +
                        "relatedCollectionName is a collection, never a relation name. " +
                        "For anything a list of records can answer - a condition, an order, a limit - use QueryRecords instead."
                }),
            AIFunctionFactory.Create(
                CreateSchemaChangeProposal,
                new AIFunctionFactoryOptions
                {
                    SerializerOptions = Options,
                    Name = "ChangeSchema",
                    Description =
                        "Changes a collection's schema: adds, removes or renames a column, creates or removes a collection. " +
                        "One call validates the change, analyses what it would affect, and then either applies it or asks the user to approve it. " +
                        "Read the result: outcome is Applied when the change is done, or AwaitingConfirmation when the user must decide, " +
                        "in which case a confirmationId is returned and the user is asked automatically. " +
                        "Set targetColumn for every column operation, including the name of a column being added."
                }),
            AIFunctionFactory.Create(
                (Func<string, bool, string?, StorageToolResult<SchemaChangeOperationResult>>)ConfirmSchemaChange,
                new AIFunctionFactoryOptions
                {
                    SerializerOptions = Options,
                    Name = "ConfirmSchemaChange",
                    Description =
                        "Applies or discards a schema change that ChangeSchema held for approval. " +
                        "Call this once the user has answered, passing the confirmationId from that result and whether they approved."
                }),
            AIFunctionFactory.Create(
                InsertRecord,
                new AIFunctionFactoryOptions
                {
                    SerializerOptions = Options,
                    Name = "InsertRecord",
                    Description = "Inserts one record. Pass fields as column name to value, using the exact column names from GetCollectionSchema; values are given as text and converted to the column\u0027s type. Omitted columns take their default."
                }),
            AIFunctionFactory.Create(
                UpdateRecord,
                new AIFunctionFactoryOptions
                {
                    SerializerOptions = Options,
                    Name = "UpdateRecord",
                    Description = "Replaces the fields of an existing record, identified by its id. Pass every column the record should end up with, since this overwrites rather than merges."
                }),
            AIFunctionFactory.Create(
                (Func<string, string, StorageToolResult<DeleteRecordResult>>)DeleteRecord,
                new AIFunctionFactoryOptions
                {
                    SerializerOptions = Options,
                    Name = "DeleteRecord",
                    Description = "Permanently deletes one record by its id. This cannot be undone, so confirm with the user first unless they clearly asked for it."
                })
        ];
    }

    private StorageToolResult<IReadOnlyCollection<string>> GetCollections()
    {
        return ExecuteTool("GetCollections", _storageTools.GetCollections);
    }

    private StorageToolResult<UserInteractionRequest> RequestUserAction(
        [Description("The question to put to the user, in their language, stating plainly what is being decided.")]
        string message,
        [Description("The choices to offer. Omit for a plain confirmation.")]
        List<UserAction>? actions)
    {
        return ExecuteTool(
            "RequestUserAction",
            () =>
            {
                if (string.IsNullOrWhiteSpace(message))
                {
                    return StorageToolResult<UserInteractionRequest>.Fail(
                        new StorageToolError("InvalidUserInteractionRequest", "message", "User interaction message is required."));
                }

                var normalizedActions = (actions ?? [])
                    .Where(static action => action is not null)
                    .Select(action =>
                    {
                        var title = string.IsNullOrWhiteSpace(action.Title) ? action.Id : action.Title;
                        var id = string.IsNullOrWhiteSpace(action.Id)
                            ? Regex.Replace(title ?? string.Empty, "[^A-Za-z0-9_]+", "_").Trim('_')
                            : action.Id.Trim();
                        return new UserAction(
                            string.IsNullOrWhiteSpace(id) ? Guid.NewGuid().ToString("N") : id,
                            string.IsNullOrWhiteSpace(title) ? id : title.Trim(),
                            string.IsNullOrWhiteSpace(action.Description) ? null : action.Description.Trim());
                    })
                    .ToArray();

                if (normalizedActions.Length == 0)
                {
                    normalizedActions =
                    [
                        new UserAction("approve", "Approve", "Continue with the proposed operation."),
                        new UserAction("reject", "Reject", "Stop and cancel the current operation."),
                        new UserAction("provide_instructions", "Provide Instructions", "Continue with additional user instructions.")
                    ];
                }

                if (normalizedActions.GroupBy(action => action.Id, StringComparer.OrdinalIgnoreCase).Any(group => group.Count() > 1))
                {
                    return StorageToolResult<UserInteractionRequest>.Fail(
                        new StorageToolError("DuplicateUserAction", "actions", "User action IDs must be unique."));
                }

                var request = new UserInteractionRequest(
                    Guid.NewGuid().ToString("N"),
                    message.Trim(),
                    normalizedActions,
                    DateTimeOffset.UtcNow);

                _requestedUserInteraction = request;
                return StorageToolResult<UserInteractionRequest>.Ok(request);
            },
            new { message, actions });
    }

    private Task<StorageToolResult<SemanticTypeResolutionToolResult>> ResolveSemanticType(
        [Description("Name of the column to classify.")]
        string columnName,
        [Description("What the column holds, if it is known.")]
        string? columnDescription,
        [Description("A few representative values from the column; they carry most of the signal.")]
        List<string>? exampleValues,
        [Description("Expected underlying type: String, Boolean, Int32, Int64, Decimal, DateTime or Guid.")]
        string? expectedBaseType)
    {
        return ExecuteToolAsync(
            "ResolveSemanticType",
            async () =>
            {
                if (_activeConfiguration is null)
                {
                    return StorageToolResult<SemanticTypeResolutionToolResult>.Fail(
                        new StorageToolError("ProviderConfigurationMissing", null, "Active provider configuration is not available."));
                }

                if (string.IsNullOrWhiteSpace(columnName))
                {
                    return StorageToolResult<SemanticTypeResolutionToolResult>.Fail(
                        new StorageToolError("InvalidColumnName", "columnName", "Column name is required."));
                }

                var semanticTypes = _storageTools.GetSemanticTypes();
                if (!semanticTypes.Success || semanticTypes.Data is null)
                {
                    return StorageToolResult<SemanticTypeResolutionToolResult>.Fail(semanticTypes.Errors);
                }

                var resolution = await _semanticTypeAgent.ResolveAsync(
                    _activeConfiguration,
                    new SemanticTypeResolutionInput(
                        columnName,
                        columnDescription,
                        (exampleValues ?? []).Where(static value => !string.IsNullOrWhiteSpace(value)).ToArray(),
                        semanticTypes.Data,
                        expectedBaseType));

                return StorageToolResult<SemanticTypeResolutionToolResult>.Ok(
                    new SemanticTypeResolutionToolResult(
                        resolution.SuggestedSemanticTypeName,
                        resolution.Confidence,
                        resolution.Reason,
                        resolution.ProposedSemanticType));
            },
            new { columnName, columnDescription, exampleValues, expectedBaseType });
    }

    private StorageToolResult<RecordQueryResult> QueryRecords(
        [Description("Name of the collection to read.")]
        string collectionName,
        [Description(
            "Optional ids of specific records to fetch, taken from a previous result. " +
            "Omit to search the whole collection.")]
        List<string>? recordIds = null,
        [Description("Optional condition. Omit to match every record.")]
        RecordFilter? where = null,
        [Description("Optional sort order, applied in sequence.")]
        List<RecordQuerySort>? orderBy = null,
        [Description("Number of records to skip. Defaults to 0.")]
        int? skip = null,
        [Description("Maximum number of records to return. Defaults to 10; ask for more only when they are needed.")]
        int? take = null,
        [Description("Columns to return. Omit to return every column.")]
        List<string>? select = null)
    {
        var query = new RecordQuery
        {
            CollectionName = collectionName ?? string.Empty,
            RecordIds = recordIds,
            Where = where,
            OrderBy = orderBy,
            Skip = skip,
            Take = take,
            Select = select
        };

        return ExecuteTool(
            "QueryRecords",
            () => _storageTools.QueryRecords(query),
            new { collectionName, skip, take });
    }

    private StorageToolResult<RecordsDisplayMessage> ShowRecords(
        [Description("Collection the records belong to.")]
        string collectionName,
        [Description(
            "Ids of the records to display, in the order they should appear. Take these from a previous query result; " +
            "an empty list renders an empty state.")]
        List<string>? recordIds,
        [Description(
            "Optional column names to show beside each record's display value, such as a price or a status. " +
            "Only columns of this collection are accepted.")]
        List<string>? additionalFields)
    {
        var request = new ShowRecordsRequest(
            collectionName ?? string.Empty,
            recordIds ?? [],
            additionalFields);

        var result = ExecuteTool(
            "ShowRecords",
            () => _storageTools.ShowRecords(request),
            new { collectionName, RecordIdCount = recordIds?.Count ?? 0, additionalFields });

        // The records reach the chat as a dedicated structured message rather
        // than as assistant text.
        if (result is { Success: true, Data: not null })
        {
            RecordsDisplayRequested?.Invoke(this, new RecordsDisplayEventArgs(result.Data));
        }

        return result;
    }

    private StorageToolResult<DisplayRuleToolResult> GetDisplayRule(
        [Description("Collection whose display rule to read.")]
        string collectionName)
    {
        return ExecuteTool("GetDisplayRule", () => _storageTools.GetDisplayRule(collectionName), new { collectionName });
    }

    private StorageToolResult<DisplayRuleToolResult> ValidateDisplayRule(
        [Description("Collection the template belongs to.")]
        string collectionName,
        [Description(
            "Template to check. Column names go in braces and everything else is literal text, " +
            "for example '{FullName} - {Email}'. Only columns of this collection may be referenced.")]
        string template)
    {
        return ExecuteTool(
            "ValidateDisplayRule",
            () => _storageTools.ValidateDisplayRule(collectionName, template),
            new { collectionName, template });
    }

    private StorageToolResult<DisplayRuleProposalResult> ProposeDisplayRule(
        [Description("Collection the display rule belongs to.")]
        string collectionName,
        [Description(
            "Template for a record's display value. Column names go in braces and everything else is literal text, " +
            "for example '{FullName} - {Email}'. Only columns of this collection may be referenced.")]
        string template,
        [Description("Why this template was chosen, in one short sentence.")]
        string? reason)
    {
        var request = new DisplayRuleProposalRequest(collectionName ?? string.Empty, template ?? string.Empty, reason);
        return ExecuteTool(
            "ProposeDisplayRule",
            () => _storageTools.ProposeDisplayRule(request),
            new { collectionName, template, reason });
    }

    private StorageToolResult<CollectionSchemaResult> GetCollectionSchema(
        [Description("Collection whose columns and relations to read.")]
        string collectionName)
    {
        return ExecuteTool("GetCollectionSchema", () => _storageTools.GetCollectionSchema(collectionName), new { collectionName });
    }

    private StorageToolResult<DataQueryExecutionResult> AnalyzeRecords(
        [Description(
            "Kind of analysis: MostFrequent, FindDuplicates or FindUnreferenced.")]
        DataQueryType queryType,
        [Description("Collection to analyse.")]
        string collectionName,
        [Description("Columns whose combined value forms the group, for MostFrequent and FindDuplicates.")]
        List<string>? groupByColumns = null,
        [Description("Columns to return, for FindUnreferenced. Omit for all columns.")]
        List<string>? selectColumns = null,
        [Description("Maximum number of rows, between 1 and 500. Defaults to 50.")]
        int? limit = null,
        [Description("Other collection to compare against, for FindUnreferenced.")]
        string? relatedCollectionName = null,
        [Description("Key column in this collection used for the comparison, for FindUnreferenced.")]
        string? collectionKeyColumn = null,
        [Description("Matching key column in the related collection, for FindUnreferenced.")]
        string? relatedKeyColumn = null)
    {
        var definition = new DataQueryDefinition(
            queryType,
            collectionName ?? string.Empty,
            selectColumns,
            groupByColumns,
            limit is null or < 1 ? 50 : limit.Value,
            relatedCollectionName,
            collectionKeyColumn,
            relatedKeyColumn);

        return ExecuteTool(
            "AnalyzeRecords",
            () => _storageTools.AnalyzeRecords(definition),
            new { queryType, collectionName, limit });
    }

    private StorageToolResult<SchemaChangeOperationResult> CreateSchemaChangeProposal(
        [Description(
            "What the change does: CreateCollection, RemoveCollection, RenameCollection, AddColumn, RemoveColumn, " +
            "RenameColumn, ChangeColumnType, ChangeColumnDescription, ChangeSemanticType, AddRelation or RemoveRelation.")]
        SchemaChangeOperationType operationType,
        [Description("Name of the collection the change applies to. Must already exist unless creating one.")]
        string targetCollection,
        [Description(
            "Name of the column the change applies to. Required for every column operation, including AddColumn, " +
            "where it is the name of the column being added. Leave empty for collection and relation operations.")]
        string? targetColumn,
        [Description("Optional human-readable summary of the current state, as plain text. Not a structured value.")]
        string? currentDefinition,
        [Description(
            "Optional human-readable summary of the intended state, as plain text. Not a structured value: " +
            "put the actual change in 'definition'.")]
        string? proposedDefinition,
        [Description(
            "The change itself. Use 'column' for a single-column operation, 'columns' when creating a collection, " +
            "and 'newName' when renaming.")]
        SchemaChangeDefinition? definition,
        [Description("Why the change is being proposed, in one short sentence.")]
        string reason,
        [Description("Optional note about a decision the user must make before this is applied.")]
        string? requiredUserAction)
    {
        var request = new SchemaChangeProposalRequest(
            operationType,
            targetCollection ?? string.Empty,
            targetColumn,
            currentDefinition,
            proposedDefinition,
            definition,
            reason ?? string.Empty,
            "AI",
            requiredUserAction);

        var result = ExecuteTool(
            "ChangeSchema",
            () => _storageTools.ChangeSchema(request),
            new { operationType, targetCollection, targetColumn, reason });

        RaiseConfirmationIfNeeded(result);
        return result;
    }

    private StorageToolResult<SchemaChangeOperationResult> ConfirmSchemaChange(
        [Description("The confirmationId returned by ChangeSchema when it asked for approval.")]
        string confirmationId,
        [Description("True to apply the held change, false to discard it.")]
        bool approved,
        [Description("Any extra instruction the user gave alongside their answer.")]
        string? note)
    {
        return ExecuteTool(
            "ConfirmSchemaChange",
            () => _storageTools.ConfirmSchemaChange(confirmationId ?? string.Empty, approved, note),
            new { confirmationId, approved });
    }

    /// <summary>
    /// A schema change that needs approval is raised through the ordinary
    /// user-interaction channel, which is what suspends the turn at the
    /// orchestrator. The tool itself cannot pause; it has already returned.
    /// </summary>
    private void RaiseConfirmationIfNeeded(StorageToolResult<SchemaChangeOperationResult> result)
    {
        if (result is { Success: true, Data.Confirmation: not null })
        {
            _requestedUserInteraction = result.Data.Confirmation;
        }
    }

    private StorageToolResult<RecordResult> InsertRecord(
        [Description("Collection to insert into.")]
        string collectionName,
        [Description(
            "The new record, as column name to value. Use the exact column names from GetCollectionSchema; " +
            "values are given as text and converted to the column's type. Omitted columns take their default.")]
        Dictionary<string, string?> fields)
    {
        return ExecuteTool("InsertRecord", () => _storageTools.InsertRecord(collectionName, fields), new { collectionName, fields });
    }

    private StorageToolResult<RecordResult> UpdateRecord(
        [Description("Collection the record belongs to.")]
        string collectionName,
        [Description("Id of the record to replace, from a previous query result.")]
        string recordId,
        [Description(
            "Every column the record should end up with, as column name to value. This overwrites rather than merges, " +
            "so a column left out is cleared, not kept.")]
        Dictionary<string, string?> fields)
    {
        return ExecuteTool("UpdateRecord", () => _storageTools.UpdateRecord(collectionName, recordId, fields), new { collectionName, recordId, fields });
    }

    private StorageToolResult<DeleteRecordResult> DeleteRecord(
        [Description("Collection the record belongs to.")]
        string collectionName,
        [Description("Id of the record to delete, from a previous query result.")]
        string recordId)
    {
        return ExecuteTool("DeleteRecord", () => _storageTools.DeleteRecord(collectionName, recordId), new { collectionName, recordId });
    }

    private T ExecuteTool<T>(string name, Func<T> function, object? arguments = null)
    {
        var call = BeginToolCall(name, arguments);
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            var result = function();
            CompleteToolCall(call, result, stopwatch.Elapsed);
            return result;
        }
        catch (Exception ex)
        {
            _logger.LogError(
                ex,
                "Tool execution failed. ToolName: {ToolName}, CallId: {CallId}, Duration: {Duration}",
                name,
                call.CallId,
                stopwatch.Elapsed);
            NotifyToolExecution(call with
            {
                Status = AgentToolExecutionStatus.Failed,
                Details = ex.Message,
                Error = ToolPayloadFormatter.FormatError(ex),
                TimestampUtc = DateTimeOffset.UtcNow
            });
            throw;
        }
    }

    private async Task<T> ExecuteToolAsync<T>(string name, Func<Task<T>> function, object? arguments = null)
    {
        var call = BeginToolCall(name, arguments);
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            var result = await function();
            CompleteToolCall(call, result, stopwatch.Elapsed);
            return result;
        }
        catch (Exception ex)
        {
            _logger.LogError(
                ex,
                "Tool execution failed. ToolName: {ToolName}, CallId: {CallId}, Duration: {Duration}",
                name,
                call.CallId,
                stopwatch.Elapsed);
            NotifyToolExecution(call with
            {
                Status = AgentToolExecutionStatus.Failed,
                Details = ex.Message,
                Error = ToolPayloadFormatter.FormatError(ex),
                TimestampUtc = DateTimeOffset.UtcNow
            });
            throw;
        }
    }

    /// <summary>
    /// Publishes the Started transition and returns the record that later
    /// transitions of the same call are derived from, so they share a CallId.
    /// </summary>
    private AgentToolExecution BeginToolCall(string name, object? arguments)
    {
        var started = new AgentToolExecution(name, AgentToolExecutionStatus.Started, null, DateTimeOffset.UtcNow)
        {
            CallId = Guid.NewGuid().ToString("N"),
            Arguments = ToolPayloadFormatter.Format(arguments, _logger, name)
        };

        _logger.LogDebug(
            "Tool call requested. ToolName: {ToolName}, CallId: {CallId}, ArgumentType: {ArgumentType}",
            name,
            started.CallId,
            arguments?.GetType().Name ?? "none");
        _logger.LogInformation(
            "Tool execution started. ToolName: {ToolName}, CallId: {CallId}",
            name,
            started.CallId);

        NotifyToolExecution(started);
        return started;
    }

    private void CompleteToolCall<T>(AgentToolExecution call, T result, TimeSpan duration)
    {
        // A storage tool reports domain failures in its result rather than by
        // throwing, so those surface as the Error state too.
        if (result is IStorageToolResult { Success: false } failure)
        {
            var details = failure.Errors.Count == 0
                ? "failed"
                : string.Join(" | ", failure.Errors.Select(error => $"{error.Code}:{error.Message}"));

            NotifyToolExecution(call with
            {
                Status = AgentToolExecutionStatus.Failed,
                Details = details,
                Error = ToolPayloadFormatter.Format(result, _logger, call.Name) ?? details,
                TimestampUtc = DateTimeOffset.UtcNow
            });

            _logger.LogWarning(
                "Tool execution completed with errors. ToolName: {ToolName}, CallId: {CallId}, Duration: {Duration}, Errors: {ToolErrors}",
                call.Name,
                call.CallId,
                duration,
                details);
            return;
        }

        NotifyToolExecution(call with
        {
            Status = AgentToolExecutionStatus.Succeeded,
            Details = result is null ? "null result" : "completed",
            Response = ToolPayloadFormatter.Format(result, _logger, call.Name),
            TimestampUtc = DateTimeOffset.UtcNow
        });

        _logger.LogInformation(
            "Tool execution completed. ToolName: {ToolName}, CallId: {CallId}, Duration: {Duration}, ResultType: {ResultType}",
            call.Name,
            call.CallId,
            duration,
            typeof(T).Name);
    }

    private void NotifyToolExecution(AgentToolExecution execution)
    {
        _recentToolExecutions.Add(execution);
        ToolExecutionStatusChanged?.Invoke(this, new AgentToolExecutionEventArgs(execution));
    }

    private static bool HasConfigurationChanged(ConversationRequest current, ConversationRequest? previous)
    {
        if (previous is null)
        {
            return true;
        }

        return current.Provider != previous.Provider
               || !string.Equals(current.Url, previous.Url, StringComparison.Ordinal)
               || !string.Equals(current.Model, previous.Model, StringComparison.Ordinal)
               || !string.Equals(current.AuthenticationToken, previous.AuthenticationToken, StringComparison.Ordinal)
               || !string.Equals(current.SystemPrompt, previous.SystemPrompt, StringComparison.Ordinal)
               // A different context window needs a fresh agent, otherwise the
               // change only takes effect after an unrelated setting is touched.
               || current.ContextSize != previous.ContextSize;
    }

    private static void ValidateRequest(ConversationRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.Url);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.Model);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.Message);
    }

    private sealed record ResumeSchemaWorkflowRequest(
        string OperationId,
        string ActionId,
        string? AdditionalInstructions);
}
