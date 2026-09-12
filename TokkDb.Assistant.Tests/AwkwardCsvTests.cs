using System.Text;
using TokkDb.Assistant.Ingestion;
using TokkDb.Assistant.Storage;

namespace TokkDb.Assistant.Tests;

/// <summary>
/// IN-1's acceptance condition, and the neighbouring awkwardness that comes with it.
///
/// The file in <see cref="The_awkward_file"/> is the one the requirement describes: two rows of
/// preamble before the header, European and invariant decimal separators mixed in one column,
/// and a column that is almost all whole numbers with a few pieces of text in it. Everything
/// else here is a piece of that file's difficulty on its own, so that a failure says which piece.
/// </summary>
public sealed class AwkwardCsvTests
{
    /// <summary>
    /// Exported by a person, from a system that writes numbers one way, edited by somebody whose
    /// machine writes them the other way. Eight rows, and almost nothing about it is regular.
    /// </summary>
    private const string AwkwardFile =
        """
        Expense report - Q3
        Generated 2026-10-01 by the finance system

        Date,Description,Amount,Nights,Receipt
        2026-07-20,EuroPython tickets,840.50,4,R-0001
        21/07/2026,"Hotel, four nights","1.234,56",4,R-0002
        2026-07-22,Taxi to the venue,44,1,R-0003
        2026-08-03,Brake pads,312.00,0,R-0004
        2026-08-15,Conference dinner,"1 240,75",2,R-0005
        2026-09-01,Train to Brno,89,5,n/a
        2026-09-14,"Books, three of them",57.25,pending,R-0006
        2026-09-30,Annual membership,1500,1,R-0007
        """;

    private static Table Read(string text, string name = "expenses") =>
        Assert.Single(TabularFile.ReadSeparatedValues(Encoding.UTF8.GetBytes(text), name));

    /// <summary>
    /// The requirement's own case, end to end. Everything asserted here is something the parse
    /// had to work out rather than be told.
    /// </summary>
    [Fact]
    public void The_awkward_file()
    {
        var table = Read(AwkwardFile);

        // The header was found under two rows of preamble and a blank line, and the preamble was
        // kept rather than thrown away - it says what the file is, which the assistant will want.
        Assert.True(table.Reading.HeaderWasFound);
        Assert.Equal(4, table.Reading.HeaderLineNumber);
        Assert.Equal(
            ["Expense report - Q3", "Generated 2026-10-01 by the finance system"],
            table.Reading.Preamble);

        Assert.Equal(["Date", "Description", "Amount", "Nights", "Receipt"], table.Columns);
        Assert.Equal(8, table.RowCount);
        Assert.Equal(',', table.Reading.Delimiter);

        var byName = table.Profiles.ToDictionary(static profile => profile.Name, StringComparer.Ordinal);

        // The dates: one written the other way round, and no twentieth month, so the column
        // settles the order for itself.
        Assert.Equal(ColumnType.Date, byName["Date"].Inferred);
        Assert.Equal("2026-07-20", byName["Date"].Minimum);
        Assert.Equal("2026-09-30", byName["Date"].Maximum);

        // The amounts: 840.50 invariant, 1.234,56 European, 1 240,75 European with a space, 44
        // and 312.00 and 57.25 and 1.500. All of them numbers, and the column is decimal because
        // some of them have a fractional part.
        Assert.Equal(ColumnType.Decimal, byName["Amount"].Inferred);
        Assert.Equal(0, byName["Amount"].BlankCount);
        Assert.Equal(8, byName["Amount"].ValueCount);

        // IN-1's named case: seven whole numbers and one "pending".
        var nights = byName["Nights"];
        Assert.Equal(ColumnType.Text, nights.Inferred);
        Assert.Equal(ColumnType.Integer, nights.Majority);
        Assert.True(nights.WasWidened);
        Assert.Equal(7.0 / 8.0, nights.MajorityShare, 3);

        var exception = Assert.Single(nights.Exceptions);
        Assert.Equal("pending", exception.Value);
        Assert.Equal(11, exception.LineNumber);
        Assert.Equal(1, nights.ExceptionCount);

        // And the receipt column, which is text throughout and says so without exceptions.
        Assert.Equal(ColumnType.Text, byName["Receipt"].Inferred);
        Assert.Empty(byName["Receipt"].Exceptions);
    }

    /// <summary>
    /// The amounts, one at a time, because "the column is decimal" would pass even if every
    /// value in it were wrong. These are the numbers a person reading the file would write down.
    /// </summary>
    [Fact]
    public void Every_amount_is_read_as_the_number_a_person_would_read_it_as()
    {
        var table = Read(AwkwardFile);
        var amounts = table.Rows.Select(static row => row[2]).ToArray();

        Assert.Equal(
            ["840.50", "1.234,56", "44", "312.00", "1 240,75", "89", "57.25", "1500"],
            amounts);

        var amountColumn = table.Profiles[2];

        Assert.Equal(
            [840.50m, 1234.56m, 44m, 312.00m, 1240.75m, 89m, 57.25m, 1500m],
            amounts.Select(text => Assert.IsType<decimal>(amountColumn.Read(text))));
    }

    [Theory]
    // Settled by the value itself: both separators present, so the last one is the fraction.
    [InlineData("1.234,56", "1234.56")]
    [InlineData("1,234.56", "1234.56")]
    [InlineData("1 234,56", "1234.56")]
    // Settled by the value itself: a lone separator with something other than three digits after it.
    [InlineData("1234.56", "1234.56")]
    [InlineData("0,5", "0.5")]
    [InlineData("1,25", "1.25")]
    [InlineData("-840.50", "-840.50")]
    // Settled by the value itself: a repeated separator groups.
    [InlineData("1.234.567", "1234567")]
    [InlineData("1,234,567", "1234567")]
    // The one ambiguous shape, with nothing else to go on: three digits reads as a group.
    [InlineData("1,234", "1234")]
    [InlineData("1.500", "1500")]
    public void A_number_is_read_the_way_it_was_written(string written, string expected)
    {
        var reading = Numbers.SettleUnaided(written, Numbers.Read(written));

        Assert.True(reading.IsNumber, $"'{written}' was not read as a number at all");
        Assert.Equal(decimal.Parse(expected, System.Globalization.CultureInfo.InvariantCulture), reading.Value);
    }

    [Theory]
    [InlineData("1.23.4")]
    [InlineData("12 34")]
    [InlineData("n/a")]
    [InlineData("pending")]
    [InlineData("1-2")]
    [InlineData("")]
    [InlineData("-")]
    public void Something_that_is_not_a_number_is_not_read_as_one(string written)
    {
        Assert.False(Numbers.Read(written).IsNumber);
    }

    /// <summary>
    /// The ambiguous shape, settled by the column rather than by itself. This is what a
    /// one-value-at-a-time parser cannot do, and the reason the inference makes two passes.
    /// </summary>
    [Fact]
    public void A_number_that_cannot_settle_itself_is_read_the_way_its_column_reads()
    {
        // A column whose other values put the fraction after a comma: 1.234 groups, so 1234.
        var european = Read(
            """
            Amount
            1.234
            0,5
            """);

        Assert.Equal(ColumnType.Decimal, Assert.Single(european.Profiles).Inferred);
        Assert.Equal("0.5", Assert.Single(european.Profiles).Minimum);
        Assert.Equal("1234", Assert.Single(european.Profiles).Maximum);

        // A column whose other values put the fraction after a dot: 1,234 groups, so 1234.
        var invariant = Read(
            """
            Amount
            1,234
            0.5
            """);

        Assert.Equal("1234", Assert.Single(invariant.Profiles).Maximum);
    }

    /// <summary>
    /// The corner the rules cannot get right, written down rather than guessed at harder.
    ///
    /// In a column whose other values put the fraction after a dot, <c>1.500</c> is one and a
    /// half. That is the consistent reading and it is very probably not what the person meant,
    /// because a membership does not cost one and a half euros. Nothing in the column says so -
    /// only the world does - so the column reads it consistently and says that it had to choose.
    /// The assistant has something true to ask about, which is the most a parser can do here.
    /// </summary>
    [Fact]
    public void A_value_that_could_be_read_two_ways_is_read_consistently_and_reported_as_such()
    {
        var table = Read(
            """
            Amount
            840.50
            312.00
            1.500
            """);

        var profile = Assert.Single(table.Profiles);

        Assert.Equal(ColumnType.Decimal, profile.Inferred);
        Assert.True(profile.HadAmbiguousValues);
        Assert.Equal(1, profile.AmbiguousCount);

        // Read the way the rest of the column reads, which here is as a fraction.
        Assert.Equal(1.5m, profile.Read("1.500"));

        // With nothing else to go on it would have been a thousands group instead - and a column
        // of one whole number is a column of whole numbers, so it comes back as one.
        var alone = Assert.Single(Read(
            """
            Amount
            1.500
            """).Profiles);

        Assert.Equal(ColumnType.Integer, alone.Inferred);
        Assert.Equal(1500L, alone.Read("1.500"));
        Assert.True(alone.HadAmbiguousValues);
    }

    [Fact]
    public void A_column_of_fractions_after_a_comma_reads_a_lone_group_as_a_fraction()
    {
        // Nothing here groups thousands, and every other value has a comma fraction, so 1,250
        // is one and a quarter rather than one thousand two hundred and fifty.
        var table = Read(
            """
            Rate
            0,75
            1,5
            2,25
            """);

        var profile = Assert.Single(table.Profiles);
        Assert.Equal(ColumnType.Decimal, profile.Inferred);
        Assert.Equal("0.75", profile.Minimum);
        Assert.Equal("2.25", profile.Maximum);
    }
}
