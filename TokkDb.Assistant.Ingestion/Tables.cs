using TokkDb.Assistant.Storage;

namespace TokkDb.Assistant.Ingestion;

/// <summary>
/// One row of a table, as it was read: the cells as text, and where it came from.
///
/// The line number is kept because IN-4 needs it. A row that cannot be stored is reported with
/// the line it was on, and "row 47" is only useful if it is the line the person would find if
/// they opened the file - so it counts lines of the file, preamble and header included, not rows
/// of the table.
/// </summary>
public sealed record TableRow(int LineNumber, IReadOnlyList<string?> Cells)
{
    /// <summary>The cell under a column, or null if the row is short.</summary>
    public string? this[int column] => column >= 0 && column < Cells.Count ? Cells[column] : null;

    public bool IsBlank => Cells.All(string.IsNullOrWhiteSpace);
}

/// <summary>
/// What one sheet, or one CSV file, turned out to contain.
/// </summary>
public sealed record Table(
    string Name,
    IReadOnlyList<string> Columns,
    IReadOnlyList<TableRow> Rows,
    IReadOnlyList<ColumnProfile> Profiles,
    TableReading Reading)
{
    public int RowCount => Rows.Count;
}

/// <summary>
/// What had to be worked out to read the file at all, kept so it can be said out loud.
///
/// None of this is certain. The encoding of a file with no byte order mark is a guess, the
/// delimiter is a guess, and where the header is is a guess. They are usually right, and when
/// they are wrong the person is the only one who can say so - which they can only do if they are
/// told what was assumed. D-13: the user may see everything.
/// </summary>
public sealed record TableReading(
    string EncodingName,
    bool EncodingWasDeclared,
    char? Delimiter,
    int HeaderLineNumber,
    IReadOnlyList<string> Preamble,
    bool HeaderWasFound);

/// <summary>
/// What one column turned out to hold. IN-1's per-column profile, and the evidence for the type
/// that was inferred.
/// </summary>
public sealed record ColumnProfile(
    string Name,
    int Position,
    ColumnType Inferred,
    ColumnType Majority,
    double MajorityShare,
    int ValueCount,
    int BlankCount,
    int DistinctCount,
    string? Minimum,
    string? Maximum,
    IReadOnlyList<string> Examples,
    int AmbiguousCount,
    int ExceptionCount,
    IReadOnlyList<ColumnException> Exceptions)
{
    /// <summary>
    /// What the column decided its values mean - which character its numbers put the fraction
    /// after, which way round its dates are - so that reading a cell later gives the same answer
    /// inference gave. Internal: it is the reasoning, and <see cref="Read"/> is the conclusion.
    /// </summary>
    internal ColumnConventions Conventions { get; init; } = default;

    /// <summary>
    /// One cell of this column, as the value <see cref="Inferred"/> says it is, or null when the
    /// cell is blank or holds something the column cannot read.
    ///
    /// This is how a row becomes a record without inference being run again, and it is why the
    /// conventions are kept: a cell reading <c>1,234</c> has to become the same number here that
    /// it was when the column was profiled, and the cell alone cannot say which number that is.
    /// </summary>
    public object? Read(string? text) => ColumnInference.Read(this, text);

    /// <summary>
    /// Whether the column had to be widened past what most of it is. A column that is almost all
    /// whole numbers and is nonetheless text is the case IN-1 names, and the two or three values
    /// that forced it are in <see cref="Exceptions"/> - which is what lets the assistant say
    /// "three rows have something in that column that is not a number, and here they are"
    /// instead of silently storing the lot as text.
    /// </summary>
    public bool WasWidened => Inferred != Majority;

    /// <summary>
    /// Whether any value in the column could have been read two ways and was read the way its
    /// neighbours were. <c>1.500</c> in a column of dot-fractions is one and a half; in a column
    /// of comma-fractions it is one thousand five hundred; and the value itself cannot say. The
    /// count is here so that the assistant can mention it rather than quietly pick one - which
    /// is the difference between a decision the user can correct and one they never hear about.
    /// </summary>
    public bool HadAmbiguousValues => AmbiguousCount > 0;
}

/// <summary>
/// A value that did not fit what most of its column is, and where it was.
///
/// <see cref="ColumnProfile.Exceptions"/> holds at most a handful of these, because a column
/// that is half one thing and half another would otherwise carry a copy of itself around.
/// <see cref="ColumnProfile.ExceptionCount"/> is how many there really were.
/// </summary>
public sealed record ColumnException(int LineNumber, string Value);
