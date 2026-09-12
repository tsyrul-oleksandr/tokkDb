using System.Text;
using TokkDb.Assistant.Ingestion;
using TokkDb.Assistant.Storage;

namespace TokkDb.Assistant.Tests;

/// <summary>
/// The three things about a file that have to be worked out before a single value can be read:
/// what the bytes mean, what separates the fields, and where the table starts. Each of them is a
/// guess, and each guess is reported so that the one person who can tell is told.
/// </summary>
public sealed class ReadingFilesTests
{
    private static Table Read(byte[] bytes) =>
        Assert.Single(TabularFile.ReadSeparatedValues(bytes, "file"));

    private static Table Read(string text) => Read(Encoding.UTF8.GetBytes(text));

    // ---- What the bytes mean ---------------------------------------------------------------

    [Fact]
    public void A_byte_order_mark_settles_the_encoding_and_does_not_become_part_of_the_first_name()
    {
        var bytes = Encoding.UTF8.GetPreamble().Concat(Encoding.UTF8.GetBytes("Event,City\nEuroPython,Prague")).ToArray();
        var table = Read(bytes);

        Assert.Equal("utf-8", table.Reading.EncodingName);
        Assert.True(table.Reading.EncodingWasDeclared);
        Assert.Equal(["Event", "City"], table.Columns);
    }

    [Fact]
    public void Utf16_with_a_mark_is_read_as_utf16()
    {
        var bytes = Encoding.Unicode.GetPreamble()
            .Concat(Encoding.Unicode.GetBytes("Event,City\nEuroPython,Zürich"))
            .ToArray();

        var table = Read(bytes);

        Assert.Equal("utf-16", table.Reading.EncodingName);
        Assert.Equal("Zürich", table.Rows[0][1]);
    }

    [Fact]
    public void Utf8_without_a_mark_is_recognised_by_being_valid_utf8()
    {
        var table = Read("Event,City\nEuroPython,Zürich");

        Assert.Equal("utf-8", table.Reading.EncodingName);
        Assert.False(table.Reading.EncodingWasDeclared);
        Assert.Equal("Zürich", table.Rows[0][1]);
    }

    /// <summary>
    /// A file that is not UTF-8 and says nothing about itself. There is no right answer available
    /// - only a better guess than latin-1 - so the candidates are scored and the one that
    /// produces writing rather than symbols wins, and the answer is marked as undeclared.
    /// </summary>
    [Fact]
    public void A_single_byte_file_is_guessed_at_and_the_guess_is_declared_as_one()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

        var cyrillic = Encoding.GetEncoding(1251).GetBytes("Подія,Місто\nЄвроПайтон,Київ");
        var table = Read(cyrillic);

        Assert.Equal("windows-1251", table.Reading.EncodingName);
        Assert.False(table.Reading.EncodingWasDeclared);
        Assert.Equal(["Подія", "Місто"], table.Columns);
        Assert.Equal("Київ", table.Rows[0][1]);
    }

    // ---- What separates the fields ---------------------------------------------------------

    [Theory]
    [InlineData(',')]
    [InlineData(';')]
    [InlineData('\t')]
    [InlineData('|')]
    public void The_delimiter_is_worked_out_from_the_file(char delimiter)
    {
        var table = Read(
            $"Event{delimiter}City{delimiter}Amount\n" +
            $"EuroPython{delimiter}Prague{delimiter}840.50\n" +
            $"DevDays{delimiter}Vilnius{delimiter}600");

        Assert.Equal(delimiter, table.Reading.Delimiter);
        Assert.Equal(["Event", "City", "Amount"], table.Columns);
        Assert.Equal(ColumnType.Decimal, table.Profiles[2].Inferred);
    }

    /// <summary>
    /// A file of one column holding comma fractions splits into two columns as consistently as a
    /// real comma-separated file does. What tells them apart is that only one of them has a
    /// header once it is split.
    /// </summary>
    [Fact]
    public void A_file_of_one_column_is_not_split_by_the_commas_inside_its_values()
    {
        var table = Read("Rate\n0,75\n1,5\n2,25");

        Assert.Null(table.Reading.Delimiter);
        Assert.Equal(["Rate"], table.Columns);
        Assert.Equal(3, table.RowCount);
        Assert.Equal(ColumnType.Decimal, Assert.Single(table.Profiles).Inferred);
    }

    /// <summary>
    /// A semicolon-separated file is what a European spreadsheet writes precisely so that its
    /// comma fractions do not need quoting. Both have to work, and the amounts have to come out
    /// as numbers either way.
    /// </summary>
    [Fact]
    public void A_european_file_uses_semicolons_so_that_its_fractions_need_no_quoting()
    {
        var table = Read(
            """
            Event;Stadt;Betrag
            EuroPython;Prag;1.234,56
            DevDays;Vilnius;600,00
            """);

        Assert.Equal(';', table.Reading.Delimiter);

        var amount = table.Profiles[2];
        Assert.Equal(ColumnType.Decimal, amount.Inferred);
        Assert.Equal("600.00", amount.Minimum);
        Assert.Equal("1234.56", amount.Maximum);
    }

    [Fact]
    public void A_quoted_field_may_hold_the_delimiter_a_newline_and_a_quote()
    {
        var table = Read("Event,Note\n\"Hotel, four nights\",\"a note\nover two lines with a \"\"quote\"\" in it\"");

        var row = Assert.Single(table.Rows);
        Assert.Equal("Hotel, four nights", row[0]);
        Assert.Equal("a note\nover two lines with a \"quote\" in it", row[1]);
    }

    // ---- Where the table starts -------------------------------------------------------------

    [Fact]
    public void A_file_whose_first_row_is_data_gets_columns_named_after_their_positions()
    {
        var table = Read("2026-07-20,840.50\n2026-08-03,312.00");

        Assert.False(table.Reading.HeaderWasFound);
        Assert.Equal(["column 1", "column 2"], table.Columns);

        // And every row is kept, rather than the first one being eaten as names.
        Assert.Equal(2, table.RowCount);
        Assert.Equal(ColumnType.Date, table.Profiles[0].Inferred);
        Assert.Equal(ColumnType.Decimal, table.Profiles[1].Inferred);
    }

    [Fact]
    public void Two_columns_with_the_same_name_are_given_names_of_their_own()
    {
        var table = Read("Amount,Amount,Note\n1,2,three");

        Assert.Equal(["Amount", "Amount 2", "Note"], table.Columns);
    }

    /// <summary>
    /// A spreadsheet with an unlabelled column is ordinary, so the blank gets a name of its own
    /// rather than the whole header being rejected - which would have cost the other two names
    /// as well and made the first row of data into the headings.
    /// </summary>
    [Fact]
    public void A_column_with_no_name_is_given_one()
    {
        var table = Read("Event,,Amount\nEuroPython,something,840.50\nDevDays,else,600");

        Assert.True(table.Reading.HeaderWasFound);
        Assert.Equal(["Event", "column 2", "Amount"], table.Columns);
        Assert.Equal(2, table.RowCount);
    }

    [Fact]
    public void A_header_with_nothing_under_it_is_not_a_header()
    {
        var table = Read("Event,City,Amount");

        Assert.False(table.Reading.HeaderWasFound);
        Assert.Single(table.Rows);
    }

    // ---- The profile ------------------------------------------------------------------------

    [Fact]
    public void A_profile_says_what_the_column_holds_and_how_much_of_it_is_missing()
    {
        var table = Read(
            """
            Event,Nights
            EuroPython,4
            DevDays,
            PyCon,2
            Brno,4
            """);

        var nights = table.Profiles[1];

        Assert.Equal(ColumnType.Integer, nights.Inferred);
        Assert.Equal(3, nights.ValueCount);
        Assert.Equal(1, nights.BlankCount);
        Assert.Equal(2, nights.DistinctCount);
        Assert.Equal("2", nights.Minimum);
        Assert.Equal("4", nights.Maximum);
        Assert.Equal(["4", "2"], nights.Examples);
    }

    [Fact]
    public void A_column_of_nothing_at_all_is_text_because_text_holds_whatever_arrives_later()
    {
        var table = Read("Event,Note\nEuroPython,\nDevDays,");

        var note = table.Profiles[1];
        Assert.Equal(ColumnType.Text, note.Inferred);
        Assert.Equal(0, note.ValueCount);
        Assert.Equal(2, note.BlankCount);
        Assert.Null(note.Minimum);
    }

    [Fact]
    public void Only_a_handful_of_offending_values_are_kept_however_many_there_are()
    {
        var rows = Enumerable.Range(0, 40)
            .Select(static i => i % 4 == 3 ? "n/a" : $"{i}")
            .Select(static value => $"row,{value}");

        var table = Read("Event,Nights\n" + string.Join('\n', rows));
        var nights = table.Profiles[1];

        Assert.Equal(ColumnType.Text, nights.Inferred);
        Assert.Equal(ColumnType.Integer, nights.Majority);
        Assert.Equal(10, nights.ExceptionCount);
        Assert.Equal(ColumnInference.MaxExceptionsKept, nights.Exceptions.Count);
    }

    [Theory]
    [InlineData("yes", true)]
    [InlineData("No", false)]
    [InlineData("TRUE", true)]
    public void True_and_false_are_read_as_people_write_them(string written, bool expected)
    {
        var table = Read($"Event,Reimbursed\nEuroPython,{written}\nDevDays,no");

        Assert.Equal(ColumnType.Boolean, table.Profiles[1].Inferred);
        Assert.Equal(expected, table.Profiles[1].Read(written));
    }

    /// <summary>
    /// A column of ones and zeroes is a column of numbers, not of answers. Reading it as true and
    /// false would turn a count of nights into a flag with nothing to say it had happened.
    /// </summary>
    [Fact]
    public void Ones_and_zeroes_are_numbers_and_not_answers()
    {
        var table = Read("Event,Nights\nEuroPython,1\nDevDays,0\nPyCon,1");

        Assert.Equal(ColumnType.Integer, table.Profiles[1].Inferred);
    }

    [Fact]
    public void A_column_of_dates_and_moments_is_a_column_of_moments()
    {
        var table = Read(
            """
            Event,Seen
            EuroPython,2026-07-20
            DevDays,2026-07-21 09:30:00
            """);

        Assert.Equal(ColumnType.Timestamp, table.Profiles[1].Inferred);
    }

    [Fact]
    public void The_day_and_the_month_are_told_apart_by_a_value_that_settles_it()
    {
        // 20 cannot be a month, so the column is day first, and 03/04 is the third of April.
        var table = Read(
            """
            Event,Paid
            EuroPython,20/07/2026
            DevDays,03/04/2026
            """);

        var paid = table.Profiles[1];
        Assert.Equal(ColumnType.Date, paid.Inferred);
        Assert.Equal(new DateOnly(2026, 4, 3), paid.Read("03/04/2026"));
        Assert.Equal("2026-04-03", paid.Minimum);
        Assert.Equal("2026-07-20", paid.Maximum);
    }

    [Fact]
    public void A_column_of_dates_none_of_which_settles_the_order_is_read_day_first_and_says_so()
    {
        var table = Read(
            """
            Event,Paid
            EuroPython,03/04/2026
            DevDays,05/06/2026
            """);

        var paid = table.Profiles[1];
        Assert.Equal(ColumnType.Date, paid.Inferred);
        Assert.True(paid.HadAmbiguousValues);
        Assert.Equal(2, paid.AmbiguousCount);
        Assert.Equal(new DateOnly(2026, 4, 3), paid.Read("03/04/2026"));
    }

    [Fact]
    public void A_month_first_column_is_read_month_first()
    {
        // 20 cannot be a month, and it is in second place, so the column is month first.
        var table = Read(
            """
            Event,Paid
            EuroPython,07/20/2026
            DevDays,04/03/2026
            """);

        Assert.Equal(new DateOnly(2026, 4, 3), table.Profiles[1].Read("04/03/2026"));
    }
}
