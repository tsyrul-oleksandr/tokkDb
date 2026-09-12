using System.Globalization;
using System.Text;
using TokkDb.Assistant.Storage;

namespace TokkDb.Assistant.Ingestion;

/// <summary>
/// A table as a model should see it: what the columns are, and a few rows to look at.
///
/// IN-3's first half, and the reason it is a requirement rather than an optimisation. A
/// spreadsheet of fifty thousand rows says nothing more about what its columns mean than the
/// first five rows do, and sending the rest costs the whole budget to say it again. So the
/// rendering is a <b>description</b> of the file with a sample attached, and its size is set by
/// the number of columns and the budget rather than by the number of rows.
///
/// What the description carries is chosen for the job the model has - deciding where this data
/// belongs and what the columns should be called:
///
/// <list type="bullet">
/// <item><b>The row count</b>, as a number. It is one token and it is the difference between
/// proposing a new collection and adding to one.</item>
/// <item><b>The preamble</b>, if the file had one. "Expense report - Q3" above a table is the
/// best sentence anyone will ever write about what the table is for.</item>
/// <item><b>Each column's type, how much of it is missing, and how varied it is.</b> A column
/// with two distinct values in five thousand rows is a category; one with five thousand is an
/// identifier.</item>
/// <item><b>The values that did not fit</b>, when a column had to be widened. This is the part a
/// raw sample would almost certainly miss - two bad values in a thousand rows will not be in the
/// first five - and it is exactly what the user needs to be asked about.</item>
/// </list>
/// </summary>
public static class TableRendering
{
    /// <summary>How many rows the sample aims at, budget permitting.</summary>
    public const int SampleRows = 5;

    /// <summary>How much of one cell is shown. A long note says what kind of thing it is well before its end.</summary>
    public const int MaxCellCharacters = 60;

    /// <summary>How many offending values are named per column.</summary>
    private const int MaxExceptionsShown = 3;

    public static string Render(Table table) => Render(table, RenderingBudget.Mapping);

    public static string Render(Table table, RenderingBudget budget)
    {
        ArgumentNullException.ThrowIfNull(table);
        ArgumentNullException.ThrowIfNull(budget);

        var text = new StringBuilder();

        text.Append("table: ").Append(table.Name).Append('\n');
        text.Append("rows: ").Append(table.RowCount.ToString(CultureInfo.InvariantCulture)).Append('\n');

        if (table.Reading.Preamble.Count > 0)
        {
            text.Append("said above the table: ")
                .Append(Shorten(string.Join(" / ", table.Reading.Preamble), 160))
                .Append('\n');
        }

        if (!table.Reading.HeaderWasFound)
        {
            // The names are positions rather than names, and a model that was not told would
            // propose "column 1" as a column name in perfect good faith.
            text.Append("note: the file has no header row, so the columns are unnamed\n");
        }

        text.Append("columns:\n");

        // The columns come first and are paid for first: a sample without them is rows of
        // untyped strings, and a description without a sample is still usable.
        var described = 0;

        foreach (var profile in table.Profiles)
        {
            var line = Describe(profile);

            if (described > 0 && Tokens.Estimate(text.ToString() + line) > budget.Tokens)
            {
                text.Append("  ... and ")
                    .Append((table.Profiles.Count - described).ToString(CultureInfo.InvariantCulture))
                    .Append(" more columns\n");
                break;
            }

            text.Append(line);
            described++;
        }

        AppendSample(text, table, budget, described);

        return text.ToString().TrimEnd('\n');
    }

    private static string Describe(ColumnProfile profile)
    {
        var line = new StringBuilder();

        line.Append("  ").Append(profile.Name).Append(": ").Append(Name(profile.Inferred));

        if (profile.BlankCount > 0)
        {
            line.Append(", ").Append(profile.BlankCount.ToString(CultureInfo.InvariantCulture)).Append(" empty");
        }

        if (profile.ValueCount > 0)
        {
            line.Append(", ").Append(profile.DistinctCount.ToString(CultureInfo.InvariantCulture)).Append(" distinct");
        }

        if (profile.Minimum is not null && profile.Inferred is not ColumnType.Text)
        {
            line.Append(", ").Append(profile.Minimum).Append(" to ").Append(profile.Maximum);
        }

        if (profile.Examples.Count > 0)
        {
            line.Append(", e.g. ")
                .Append(string.Join(" | ", profile.Examples.Select(static example => Shorten(example, 24))));
        }

        // The part of the description that a sample could not have given.
        if (profile.WasWidened)
        {
            line.Append("\n    mostly ").Append(Name(profile.Majority)).Append(" (")
                .Append(Share(profile.MajorityShare))
                .Append("), but ")
                .Append(profile.ExceptionCount.ToString(CultureInfo.InvariantCulture))
                .Append(profile.ExceptionCount == 1 ? " value is not: " : " values are not: ")
                .Append(string.Join(", ", profile.Exceptions
                    .Take(MaxExceptionsShown)
                    .Select(static exception => $"row {exception.LineNumber} \"{Shorten(exception.Value, 24)}\"")));
        }

        if (profile.HadAmbiguousValues)
        {
            line.Append("\n    ")
                .Append(profile.AmbiguousCount.ToString(CultureInfo.InvariantCulture))
                .Append(profile.AmbiguousCount == 1 ? " value could" : " values could")
                .Append(" have been read two ways and was read as the rest of the column reads");
        }

        return line.Append('\n').ToString();
    }

    /// <summary>
    /// A few rows, and only as many as the budget has room for after the columns are described.
    /// Zero is an acceptable answer: a file with two hundred columns has spent its budget saying
    /// what they are, which is the more useful half.
    /// </summary>
    private static void AppendSample(StringBuilder text, Table table, RenderingBudget budget, int columns)
    {
        if (table.RowCount == 0 || columns == 0) return;

        var header = "sample of the rows:\n";
        var names = "  " + string.Join(" | ", table.Columns.Take(columns)) + "\n";

        if (Tokens.Estimate(text.ToString() + header + names) > budget.Tokens) return;

        text.Append(header).Append(names);

        var shown = 0;

        foreach (var row in table.Rows.Take(SampleRows))
        {
            var line = "  " + string.Join(" | ", Enumerable.Range(0, columns)
                .Select(column => Shorten(row[column] ?? "", MaxCellCharacters))) + "\n";

            if (Tokens.Estimate(text.ToString() + line) > budget.Tokens) break;

            text.Append(line);
            shown++;
        }

        if (shown == 0)
        {
            // The header was affordable and no row was. Take it back out rather than leaving a
            // heading over nothing.
            text.Length -= header.Length + names.Length;
        }
    }

    /// <summary>The type in the words the assistant uses, rather than the contract's spelling of it.</summary>
    private static string Name(ColumnType type) => type switch
    {
        ColumnType.Text => "text",
        ColumnType.Integer => "whole number",
        ColumnType.Decimal => "number",
        ColumnType.Boolean => "true or false",
        ColumnType.Date => "date",
        ColumnType.Timestamp => "date and time",
        _ => type.ToString().ToLowerInvariant()
    };

    /// <summary>
    /// The share as a percentage, never rounded up to all of it. A column that is 49 999 whole
    /// numbers out of 50 000 is 99.998 per cent, and printing "100%" beside "but 1 value is not"
    /// tells the model two things that cannot both be true - which is worse than a vaguer number,
    /// because the model has to pick one.
    /// </summary>
    private static string Share(double share)
    {
        var percent = share * 100;

        if (percent >= 99.5 && percent < 100) return "over 99%";

        return percent.ToString("0", CultureInfo.InvariantCulture) + "%";
    }

    private static string Shorten(string value, int limit)
    {
        var flattened = value.ReplaceLineEndings(" ").Trim();

        return flattened.Length <= limit ? flattened : flattened[..(limit - 1)] + "…";
    }
}
