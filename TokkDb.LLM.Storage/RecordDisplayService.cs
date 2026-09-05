using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using TokkDb.LLM.Core;

namespace TokkDb.LLM.Storage;

public interface IRecordDisplayService
{
    /// <summary>
    /// Human-readable value for a record. Deterministic, and always returns
    /// something renderable: a fallback is used when the collection has no rule
    /// or the rule cannot be applied.
    /// </summary>
    string GetDisplayValue(CollectionDefinition collection, IReadOnlyDictionary<string, object?> record);

    string GetDisplayValue(CollectionDefinition collection, StorageRecord record);

    /// <summary>
    /// Resolves a <see cref="ShowRecordsRequest"/> into a structured message the
    /// chat can render.
    ///
    /// Fully deterministic: it loads the named records, evaluates the
    /// collection's DisplayRule and formats the requested additional fields.
    /// Nothing supplied by the model is trusted - an unknown collection, an id
    /// that does not belong to the collection and an unknown field are each
    /// rejected in a controlled way, and valid records still render.
    /// </summary>
    RecordsDisplayMessage BuildRecordsDisplay(IStorage storage, ShowRecordsRequest request);
}

/// <inheritdoc />
public sealed class RecordDisplayService : IRecordDisplayService
{
    private readonly IDisplayRuleEvaluator _evaluator;
    private readonly IDisplayRuleValidator _validator;
    private readonly ILogger<RecordDisplayService> _logger;

    /// <summary>
    /// Remembers which (collection, template) pairs already validated, so the
    /// schema check does not run for every record in a list.
    /// </summary>
    private readonly Dictionary<string, bool> _validationCache = new(StringComparer.Ordinal);
    private readonly object _sync = new();

    public RecordDisplayService(
        IDisplayRuleEvaluator evaluator,
        IDisplayRuleValidator validator,
        ILogger<RecordDisplayService>? logger = null)
    {
        ArgumentNullException.ThrowIfNull(evaluator);
        ArgumentNullException.ThrowIfNull(validator);
        _evaluator = evaluator;
        _validator = validator;
        _logger = logger ?? NullLogger<RecordDisplayService>.Instance;
    }

    public string GetDisplayValue(CollectionDefinition collection, StorageRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);
        return GetDisplayValue(collection, record.Fields);
    }

    public string GetDisplayValue(CollectionDefinition collection, IReadOnlyDictionary<string, object?> record)
    {
        ArgumentNullException.ThrowIfNull(collection);
        ArgumentNullException.ThrowIfNull(record);

        var rule = collection.DisplayRule;
        if (rule is null)
        {
            return Fallback(collection, record, "no display rule configured");
        }

        if (!IsUsable(collection, rule))
        {
            return Fallback(collection, record, "display rule is invalid for the current schema");
        }

        var value = _evaluator.Evaluate(rule, record, collection.Name);
        return string.IsNullOrWhiteSpace(value)
            ? Fallback(collection, record, "display rule produced an empty value")
            : value;
    }

    public RecordsDisplayMessage BuildRecordsDisplay(IStorage storage, ShowRecordsRequest request)
    {
        ArgumentNullException.ThrowIfNull(storage);
        ArgumentNullException.ThrowIfNull(request);

        var collectionName = request.CollectionName?.Trim() ?? string.Empty;
        var requestedIds = request.RecordIds ?? Array.Empty<string>();

        _logger.LogInformation(
            "ShowRecords invoked. Collection: {CollectionName}, RequestedIds: {RequestedIdCount}, RequestedFields: {RequestedFieldCount}",
            collectionName,
            requestedIds.Count,
            request.AdditionalFields?.Count ?? 0);

        var collection = string.IsNullOrWhiteSpace(collectionName)
            ? null
            : storage.GetCollectionDefinition(collectionName);

        if (collection is null)
        {
            _logger.LogWarning(
                "ShowRecords referenced an unknown collection. Collection: {CollectionName}",
                collectionName);
            return RecordsDisplayMessage.Empty(collectionName);
        }

        var (fields, invalidFields) = ResolveAdditionalFields(collection, request.AdditionalFields);

        // Duplicates are removed keeping the first occurrence, and the caller's
        // order is preserved: the query decided the order, not the UI.
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var items = new List<RecordDisplayItem>();
        var unresolved = new List<string>();

        foreach (var rawId in requestedIds)
        {
            var id = rawId?.Trim() ?? string.Empty;
            if (id.Length == 0 || !seen.Add(id))
            {
                continue;
            }

            var record = TryLoadRecord(storage, collection.Name, id);
            if (record is null)
            {
                unresolved.Add(id);
                continue;
            }

            items.Add(new RecordDisplayItem(
                record.Id.ToString(),
                collection.Name,
                GetDisplayValue(collection, record),
                BuildAdditionalFields(collection, record, fields)));
        }

        if (unresolved.Count > 0)
        {
            _logger.LogWarning(
                "ShowRecords could not resolve some record ids. Collection: {CollectionName}, UnresolvedIds: {UnresolvedIds}, UnresolvedCount: {UnresolvedCount}",
                collection.Name,
                string.Join(",", unresolved),
                unresolved.Count);
        }

        _logger.LogInformation(
            "ShowRecords resolved. Collection: {CollectionName}, Requested: {RequestedIdCount}, Resolved: {ResolvedCount}, InvalidFields: {InvalidFieldCount}",
            collection.Name,
            requestedIds.Count,
            items.Count,
            invalidFields.Count);

        return new RecordsDisplayMessage(
            collection.Name,
            items,
            fields.Select(field => field.Name).ToArray(),
            requestedIds.Count,
            unresolved,
            invalidFields);
    }

    /// <summary>
    /// Keeps only fields that exist as columns; unknown names are reported and
    /// dropped rather than failing the whole request.
    /// </summary>
    private (IReadOnlyList<ColumnDefinition> Fields, IReadOnlyList<string> Invalid) ResolveAdditionalFields(
        CollectionDefinition collection,
        IReadOnlyList<string>? requested)
    {
        if (requested is null || requested.Count == 0)
        {
            return (Array.Empty<ColumnDefinition>(), Array.Empty<string>());
        }

        var resolved = new List<ColumnDefinition>();
        var invalid = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var name in requested)
        {
            var trimmed = name?.Trim();
            if (string.IsNullOrEmpty(trimmed) || !seen.Add(trimmed))
            {
                continue;
            }

            var column = collection.Columns.FirstOrDefault(candidate =>
                string.Equals(candidate.Name, trimmed, StringComparison.OrdinalIgnoreCase));

            if (column is null)
            {
                invalid.Add(trimmed);
                continue;
            }

            resolved.Add(column);
        }

        if (invalid.Count > 0)
        {
            _logger.LogWarning(
                "ShowRecords requested unknown additional fields. Collection: {CollectionName}, InvalidFields: {InvalidFields}",
                collection.Name,
                string.Join(",", invalid));
        }

        return (resolved, invalid);
    }

    /// <summary>
    /// Formats the requested columns through the shared value formatter. A field
    /// that is missing or empty for a record is omitted rather than rendered as
    /// "null".
    /// </summary>
    private static IReadOnlyList<RecordDisplayField> BuildAdditionalFields(
        CollectionDefinition collection,
        StorageRecord record,
        IReadOnlyList<ColumnDefinition> fields)
    {
        if (fields.Count == 0)
        {
            return Array.Empty<RecordDisplayField>();
        }

        var result = new List<RecordDisplayField>(fields.Count);
        foreach (var column in fields)
        {
            if (!record.Fields.TryGetValue(column.Name, out var value))
            {
                continue;
            }

            var text = RecordValueFormatter.Format(value);
            if (text.Length == 0)
            {
                continue;
            }

            result.Add(new RecordDisplayField(column.Name, text));
        }

        return result;
    }

    /// <summary>
    /// Loads a record by its textual id, tolerating anything the model may send.
    /// </summary>
    private StorageRecord? TryLoadRecord(IStorage storage, string collectionName, string recordId)
    {
        if (!Ulid.TryParse(recordId, out var id))
        {
            _logger.LogWarning(
                "ShowRecords received a record id that is not a valid identifier. Collection: {CollectionName}, RecordId: {RecordId}",
                collectionName,
                recordId);
            return null;
        }

        try
        {
            // GetById is scoped to the collection, so a record from another
            // collection cannot be displayed under this one.
            return storage.GetById(collectionName, id);
        }
        catch (Exception ex)
        {
            _logger.LogError(
                ex,
                "ShowRecords failed to load a record. Collection: {CollectionName}, RecordId: {RecordId}",
                collectionName,
                recordId);
            return null;
        }
    }

    private bool IsUsable(CollectionDefinition collection, DisplayRule rule)
    {
        // Schema version is part of the key so the cached verdict is discarded
        // whenever the collection's schema changes.
        var key = string.Concat(
            collection.Name,
            "",
            collection.Metadata.TryGetValue(MemoryStorage.SchemaVersionMetadataKey, out var version)
                ? version
                : "1",
            "",
            rule.Template);

        lock (_sync)
        {
            if (_validationCache.TryGetValue(key, out var cached))
            {
                return cached;
            }

            var result = _validator.Validate(rule, collection);
            _validationCache[key] = result.IsValid;
            return result.IsValid;
        }
    }

    /// <summary>
    /// Deterministic fallback: the first non-empty field value, otherwise the
    /// collection name. Field values are not logged.
    /// </summary>
    private string Fallback(
        CollectionDefinition collection,
        IReadOnlyDictionary<string, object?> record,
        string reason)
    {
        _logger.LogWarning(
            "Fallback display value used. Collection: {CollectionName}, Reason: {Reason}",
            collection.Name,
            reason);

        foreach (var column in collection.Columns)
        {
            if (!record.TryGetValue(column.Name, out var value) || value is null)
            {
                continue;
            }

            var text = value.ToString()?.Trim();
            if (!string.IsNullOrEmpty(text))
            {
                return text;
            }
        }

        return collection.Name;
    }
}
