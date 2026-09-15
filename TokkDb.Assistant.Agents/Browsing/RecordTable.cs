using System.Globalization;
using System.Text;
using TokkDb.Assistant.Agents.Orchestration;
using TokkDb.Assistant.Storage;

namespace TokkDb.Assistant.Agents.Browsing;

/// <summary>One row of the table: the display value leads, then the fields in a stable order (BR-2).</summary>
public sealed record TableRow(StorageRecord Record, string Title, IReadOnlyList<string> Cells)
{
    public Ulid Id => Record.Id;
}

/// <summary>
/// The table, a page at a time (BR-2, BR-3, BR-3a, BR-3b, BR-4, BR-10): loaded through the
/// storage's ordered walk by cursor, never by offset and never by materialising the thing; a
/// cursor valid only for the sort and filter it was issued for; and an honest indicator when the
/// thing has changed underneath an open sequence, driven by the last-changed time the overview
/// already maintains, so it costs no extra read.
/// </summary>
public sealed class RecordTable
{
    private readonly IStorage _storage;
    private readonly List<TableRow> _rows = [];
    private QueryCursor? _next;
    private DateTimeOffset? _openedAt;
    private QueryExecutionInfo? _lastExecution;

    public RecordTable(IStorage storage, string thing, int pageSize = 50)
    {
        _storage = storage ?? throw new ArgumentNullException(nameof(storage));
        Definition = storage.GetCollectionDefinition(thing) ?? throw new UnknownCollectionException(thing);
        PageSize = pageSize;

        // The display value leads; the rest follow in the order the thing declares them, which
        // is stable, with the storage's own bookkeeping left out.
        var leading = DisplayValue.Column(Definition);
        Columns =
        [
            .. Definition.Columns
                .Where(column => !Names.Same(column.Name, Fingerprints.ColumnName))
                .OrderBy(column => leading is not null && Names.Same(column.Name, leading.Name) ? 0 : 1)
        ];
    }

    public CollectionDefinition Definition { get; }

    public int PageSize { get; }

    /// <summary>The fields shown, display value first.</summary>
    public IReadOnlyList<ColumnDefinition> Columns { get; }

    /// <summary>The sort the sequence runs in; identity when none was chosen.</summary>
    public QuerySort? Sort { get; private set; }

    public IReadOnlyList<TableFilter> Filters { get; private set; } = [];

    /// <summary>The rows loaded so far, in order.</summary>
    public IReadOnlyList<TableRow> Rows => _rows;

    /// <summary>How many records match the sort and filter, in all: shown beside the table (BR-2).</summary>
    public int Total { get; private set; }

    public bool HasMore => _next is not null;

    /// <summary>How the last page was served, for the diagnostics area: the planner's own words.</summary>
    public QueryExecutionInfo? LastExecution => _lastExecution;

    /// <summary>
    /// Whether the thing has changed since this sequence began (BR-3b): a record edited so that it
    /// moves in the order may then be seen twice or missed, so the table says so and offers to
    /// start again rather than showing a silently wrong list.
    /// </summary>
    public bool HasChangedUnderneath =>
        _openedAt is { } opened && _storage.Describe(Definition.Name)?.LastChanged is { } changed && changed > opened;

    /// <summary>Starts a sequence from the beginning, for this sort and these filters (BR-3a).</summary>
    public string? Open(QuerySort? sort = null, IReadOnlyList<TableFilter>? filters = null)
    {
        // The sequence that was showing stays showing if the new one is refused: a sort by a
        // field that is not there, or a filter that does not read, is a line on screen and not an
        // empty table.
        var was = (Sort, Filters, Rows: _rows.ToList(), Next: _next, OpenedAt: _openedAt, Total, Last: _lastExecution);

        Sort = sort;
        Filters = filters ?? [];
        _rows.Clear();
        _next = null;
        _openedAt = _storage.Describe(Definition.Name)?.LastChanged ?? DateTimeOffset.UtcNow;

        var problem = LoadPage(null);
        if (problem is null) return null;

        (Sort, Filters, _next, _openedAt, Total, _lastExecution) = (was.Sort, was.Filters, was.Next, was.OpenedAt, was.Total, was.Last);
        _rows.Clear();
        _rows.AddRange(was.Rows);
        return problem;
    }

    /// <summary>Starts again from the top, after the thing changed underneath (BR-3b).</summary>
    public string? Restart() => Open(Sort, Filters);

    /// <summary>The next page of the same sequence, or nothing when there is none.</summary>
    public string? More() => _next is null ? null : LoadPage(_next);

    /// <summary>The query one page of this sequence is: what the assistant would run for the same ask (SC-7).</summary>
    public StorageQuery Query(QueryCursor? after, out string? problem)
    {
        problem = null;
        var conditions = new List<QueryCondition>();

        foreach (var filter in Filters)
        {
            var condition = filter.ToCondition(Definition, out var failure);
            if (condition is null)
            {
                problem = failure;
                continue;
            }

            conditions.Add(condition);
        }

        var order = Sort ?? new QuerySort(Columns.Count > 0 ? Columns[0].Name : Definition.Columns[0].Name);

        // The count comes with the first page; a later page is the same sequence, so asking again
        // would only cost a pass over it.
        var page = new StorageQuery(Definition.Name, conditions, [order], take: PageSize).Continuing(after);
        return after is null ? page.Computing(new QueryAggregate(AggregateFunction.Count)) : page;
    }

    private string? LoadPage(QueryCursor? after)
    {
        var query = Query(after, out var problem);
        if (problem is not null) return problem;

        StorageQueryResult result;
        try
        {
            result = _storage.ExecuteQuery(query);
        }
        catch (StorageException failure)
        {
            return failure.Message;
        }

        _lastExecution = result.Execution;
        _next = result.NextCursor;

        if (result.Aggregates.TryGetValue("count", out var count) && count is not null)
        {
            Total = Convert.ToInt32(count, CultureInfo.InvariantCulture);
        }

        foreach (var record in result.Records)
        {
            _rows.Add(new TableRow(record, DisplayValue.For(Definition, record), [.. Columns.Select(column => Values.Show(record[column.Name]))]));
        }

        return null;
    }

    /// <summary>
    /// What is on screen, with its sort and filter applied, as CSV (BR-10): every page of the
    /// sequence, not only the ones loaded, walked through the same cursor.
    /// </summary>
    public string ToCsv()
    {
        var text = new StringBuilder();
        text.AppendLine(string.Join(",", Columns.Select(column => Quote(column.Name))));

        QueryCursor? after = null;
        do
        {
            var result = _storage.ExecuteQuery(Query(after, out _));
            foreach (var record in result.Records)
            {
                text.AppendLine(string.Join(",", Columns.Select(column => Quote(Values.Show(record[column.Name])))));
            }

            after = result.NextCursor;
        } while (after is not null);

        return text.ToString();
    }

    private static string Quote(string cell) =>
        cell.Contains(',') || cell.Contains('"') || cell.Contains('\n') ? "\"" + cell.Replace("\"", "\"\"") + "\"" : cell;
}
