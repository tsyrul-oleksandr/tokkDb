using TokkDb.Assistant.Ingestion;

namespace TokkDb.Assistant.Tests;

/// <summary>
/// One piece of text read as one kind of value, by the readers a column is profiled with: what
/// the extraction step hands back reads the way a spreadsheet cell reads, and a month written as
/// a word - which is what a person types and what a model writes - is a date and not text.
/// </summary>
public sealed class ValueParsingTests
{
    [Theory]
    [InlineData("14 March 2025", 2025, 3, 14)]
    [InlineData("14 Mar 2025", 2025, 3, 14)]
    [InlineData("March 14, 2025", 2025, 3, 14)]
    [InlineData("Mar 14 2025", 2025, 3, 14)]
    [InlineData("20 July 2025", 2025, 7, 20)]
    [InlineData("14th March 2025", 2025, 3, 14)]
    [InlineData("2025-05-20", 2025, 5, 20)]
    [InlineData("20/05/2025", 2025, 5, 20)]
    [InlineData("1 December 2024", 2024, 12, 1)]
    public void A_date_written_as_a_person_writes_it_reads_as_a_day(string text, int year, int month, int day)
    {
        Assert.True(ValueParsing.TryRead(ValueKind.Date, text, out var value), text);
        Assert.Equal(new DateOnly(year, month, day), value);
        Assert.Equal(ValueKind.Date, ValueParsing.KindOf(text));
    }

    [Theory]
    [InlineData("12000", 12000L)]
    [InlineData("12 000", 12000L)]
    [InlineData("8,500", 8500L)]
    public void A_whole_number_with_grouping_reads_as_a_whole_number(string text, long expected)
    {
        Assert.True(ValueParsing.TryRead(ValueKind.Integer, text, out var value), text);
        Assert.Equal(expected, value);
    }

    [Fact]
    public void What_is_not_of_the_kind_is_refused_rather_than_guessed()
    {
        Assert.False(ValueParsing.TryRead(ValueKind.Date, "sometime in March", out _));
        Assert.False(ValueParsing.TryRead(ValueKind.Decimal, "about 600", out _));
        Assert.False(ValueParsing.TryRead(ValueKind.Boolean, "maybe", out _));
        Assert.True(ValueParsing.TryRead(ValueKind.Text, "anything at all", out var text));
        Assert.Equal("anything at all", text);
        Assert.True(ValueParsing.TryRead(ValueKind.Decimal, "  ", out var blank));
        Assert.Null(blank);
        Assert.Equal(ValueKind.Text, ValueParsing.KindOf("sometime in March"));
        Assert.Equal(ValueKind.Decimal, ValueParsing.KindOf("840.50"));
        Assert.Equal(ValueKind.Boolean, ValueParsing.KindOf("yes"));
    }
}
