using System.Globalization;
using TokkDb.Assistant.Agents.Models;
using TokkDb.Assistant.Agents.Operations;
using Catalogue = TokkDb.Assistant.Agents.Operations.Operations;
using TokkDb.Assistant.Storage;

namespace TokkDb.Assistant.Agents.Orchestration;

/// <summary>
/// Finding what is stored (S-3, QR-1, QR-2, QR-3a, D-6): the model writes a query, C# validates it
/// against the shape and runs it through the storage, the records are rendered by the application,
/// and the reply carries a handle - the query, the metadata and the identities of the page shown -
/// that survives a restart with the conversation.
/// </summary>
internal static class RetrievalFlow
{
    public static async Task<TurnOutcome> HandleAsync(TurnContext ctx, string question)
    {
        var definitions = ctx.Storage.GetCollectionDefinitions();
        if (definitions.Count == 0)
        {
            return ctx.Complete("Nothing is stored yet, so there is nothing to look through.");
        }

        var content = $"today is {ctx.Options.Clock():yyyy-MM-dd}\nquestion: {question}";
        var result = await ctx.RunAsync(Catalogue.Query, content, EgressClass.SchemaDigest, Answers.Query(definitions), summary: question).ConfigureAwait(false);

        var query = Build(ctx, result.Value);
        return Run(ctx, query, result.Value.Total, null);
    }

    /// <summary>Runs a query, shows a page, and hands back what a follow-up needs (QR-3a).</summary>
    public static TurnOutcome Run(TurnContext ctx, StorageQuery query, string? totalField, string? preface)
    {
        var definition = ctx.Storage.GetCollectionDefinition(query.CollectionName) ?? throw new UnknownCollectionException(query.CollectionName);
        var looking = ctx.Tracer.Step("looking", input: QueryJson.Write(query));

        StorageQueryResult result;
        try
        {
            result = ctx.Storage.ExecuteQuery(query);
        }
        catch (StorageValidationException refused)
        {
            looking.Failed(refused.Message);
            return ctx.Fail(refused.Message);
        }

        looking.Done($"{result.Execution}; {result.Records.Count} of {Count(result)} shown");

        var page = Page(ctx, definition, query, result, totalField);
        var reply = (preface is null ? "" : preface + " ") + Replies.Found(page, totalField, totalField is null ? null : result.Aggregates.GetValueOrDefault($"sum({totalField})"));

        return ctx.Complete(reply, results: page, payload: page.Handle.Write());
    }

    public static StorageQuery Build(TurnContext ctx, QueryAnswer answer)
    {
        var definition = ctx.Storage.GetCollectionDefinition(answer.Thing) ?? throw new UnknownCollectionException(answer.Thing);
        var where = new List<QueryCondition>();

        foreach (var (field, word, texts) in answer.Where)
        {
            var column = definition.Column(field)!;
            var values = texts.Select(text => Values.TryRead(column.Type, text, out var value) ? value : text).ToList();

            var @operator = word switch
            {
                "is" => QueryOperator.Equals,
                "is not" => QueryOperator.NotEquals,
                "before" => QueryOperator.LessThan,
                "after" => QueryOperator.GreaterThan,
                "at least" => QueryOperator.GreaterOrEqual,
                "at most" => QueryOperator.LessOrEqual,
                "contains" => column.Type is ColumnType.Text ? QueryOperator.Contains : QueryOperator.Equals,
                "one of" => QueryOperator.In,
                "has nothing" => QueryOperator.IsNothing,
                "has something" => QueryOperator.IsSomething,
                _ => QueryOperator.Equals
            };

            where.Add(@operator is QueryOperator.IsNothing or QueryOperator.IsSomething
                ? new QueryCondition(column.Name, @operator)
                : @operator is QueryOperator.In
                    ? new QueryCondition(column.Name, @operator, values)
                    : new QueryCondition(column.Name, @operator, values[0]));
        }

        var orderBy = answer.OrderBy is { } sort ? new[] { new QuerySort(sort, answer.LargestFirst) } : [];
        var take = answer.Limit > 0 ? Math.Min(answer.Limit, ctx.Options.PageSize) : ctx.Options.PageSize;

        var query = new StorageQuery(definition.Name, where, orderBy, take: take)
            .Computing(new QueryAggregate(AggregateFunction.Count));

        if (answer.Total is { } total)
        {
            query = query.Computing(new QueryAggregate(AggregateFunction.Sum, total));
        }

        // Ranges for the handle (QR-3a), over everything matched: a few numeric and date fields.
        foreach (var column in definition.Columns.Where(static column => column.Type is ColumnType.Integer or ColumnType.Decimal or ColumnType.Date or ColumnType.Timestamp).Take(4))
        {
            query = query.Computing(new QueryAggregate(AggregateFunction.Minimum, column.Name), new QueryAggregate(AggregateFunction.Maximum, column.Name));
        }

        return query;
    }

    public static int Count(StorageQueryResult result) =>
        result.Aggregates.TryGetValue("count", out var count) && count is not null ? Convert.ToInt32(count, CultureInfo.InvariantCulture) : result.Records.Count;

    private static ResultPage Page(TurnContext ctx, CollectionDefinition definition, StorageQuery query, StorageQueryResult result, string? totalField)
    {
        var titles = result.Records.Select(record => DisplayValue.For(definition, record)).ToList();
        var shown = result.Records.Select((record, index) => new ShownRecord(record.Id, ctx.Storage.HeadVersion(definition.Name, record.Id) ?? Ulid.Empty, titles[index])).ToList();

        var ranges = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var column in definition.Columns)
        {
            if (result.Aggregates.TryGetValue($"minimum({column.Name})", out var min) && result.Aggregates.TryGetValue($"maximum({column.Name})", out var max) && min is not null && max is not null)
            {
                ranges[column.Name] = $"{Values.Show(min)} to {Values.Show(max)}";
            }
        }

        var total = totalField is null ? null : result.Aggregates.GetValueOrDefault($"sum({totalField})");

        var handle = new QueryResultHandle(
            definition.Name,
            QueryJson.Write(query),
            Count(result),
            [.. definition.Columns.Select(static column => column.Name)],
            ranges,
            shown,
            totalField,
            total is null ? null : Values.Show(total),
            ctx.Options.Clock());

        return new ResultPage(definition.Name, result.Records, titles, Count(result), result.Aggregates, result.Execution, handle);
    }
}
