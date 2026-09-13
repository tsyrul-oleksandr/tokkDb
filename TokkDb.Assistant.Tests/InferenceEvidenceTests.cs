using System.Text;
using TokkDb.Assistant.Ingestion;

namespace TokkDb.Assistant.Tests;

/// <summary>
/// Inference that admits what it does not know (IN-1a, IN-1b).
///
/// The type on its own is a verdict nobody can argue with. What makes it answerable is the rest:
/// how much of the column the type describes, how many values read as each candidate with a few
/// of them quoted, and the questions the column had to guess at.
/// </summary>
public sealed class InferenceEvidenceTests
{
    private static ColumnProfile Profile(string column, params string[] values)
    {
        var text = new StringBuilder(column).Append('\n');
        foreach (var value in values) text.Append(value).Append('\n');

        var table = Assert.Single(TabularFile.ReadSeparatedValues(Encoding.UTF8.GetBytes(text.ToString()), "file"));

        return Assert.Single(table.Profiles);
    }

    // ---- The wider type wins (IN-1b) -------------------------------------------------------------

    /// <summary>
    /// IN-1b's own example. <c>007</c> is a room, a route or an account, and the zeros are the
    /// part that makes it recognisable - so it is text, and the leading zeros are the evidence.
    ///
    /// The asymmetry is a rule rather than a preference: text can be narrowed later by a retype,
    /// which the engine performs lazily, and seven cannot be widened back into 007.
    /// </summary>
    [Fact]
    public void A_column_of_007_and_013_and_142_is_text_and_the_leading_zeros_are_the_reason()
    {
        var profile = Profile("route", "007", "013", "142");

        Assert.Equal(ValueKind.Text, profile.Inferred);
        Assert.Equal(3, profile.ValueCount);

        // Two of the three are text because of their zeros; the third is a whole number and is
        // what the column would have been if they had not been there.
        var text = Assert.Single(profile.Evidence, entry => entry.Kind == ValueKind.Text);
        Assert.Equal(2, text.Count);
        Assert.Equal(["007", "013"], text.Examples);

        var numbers = Assert.Single(profile.Evidence, entry => entry.Kind == ValueKind.Integer);
        Assert.Equal(1, numbers.Count);
    }

    [Theory]
    [InlineData("0")]
    [InlineData("0.5")]
    [InlineData("0,5")]
    [InlineData("-0.5")]
    public void A_zero_that_is_the_value_rather_than_padding_is_still_a_number(string value)
    {
        Assert.NotEqual(ValueKind.Text, Profile("value", value, "1", "2").Inferred);
    }

    /// <summary>
    /// The narrowing direction is never taken automatically, pinned. Whatever the majority is,
    /// the type chosen holds every value in the column - so a column that is nearly all numbers
    /// with one piece of text in it is text, and the one value is named rather than dropped.
    /// </summary>
    [Fact]
    public void A_type_is_never_narrowed_past_what_the_column_holds()
    {
        var profile = Profile("nights", "1", "2", "3", "4", "pending");

        Assert.Equal(ValueKind.Text, profile.Inferred);
        Assert.Equal(ValueKind.Integer, profile.Majority);
        Assert.True(profile.WasWidened);

        Assert.Equal(0.8, profile.MajorityShare, 3);
        Assert.Equal(0.2, profile.Confidence, 3);

        var exception = Assert.Single(profile.Exceptions);
        Assert.Equal("pending", exception.Value);
    }

    [Fact]
    public void A_column_of_whole_numbers_and_fractions_is_read_as_numbers_rather_than_as_text()
    {
        var profile = Profile("amount", "1", "2.5", "3");

        Assert.Equal(ValueKind.Decimal, profile.Inferred);
        Assert.Equal(1, profile.Confidence, 3);
    }

    // ---- Confidence (IN-1a) ------------------------------------------------------------------------

    /// <summary>
    /// What the number means, said with the two cases side by side: both columns are text and
    /// only one of them is <i>described</i> by it.
    /// </summary>
    [Fact]
    public void Confidence_tells_a_column_of_words_from_a_column_of_numbers_with_a_word_in_it()
    {
        var words = Profile("city", "Prague", "Lviv", "Brno");
        var numbers = Profile("nights", "1", "2", "3", "4", "5", "6", "7", "8", "9", "ten");

        Assert.Equal(ValueKind.Text, words.Inferred);
        Assert.Equal(ValueKind.Text, numbers.Inferred);

        Assert.Equal(1, words.Confidence, 3);
        Assert.Equal(0.1, numbers.Confidence, 3);
    }

    [Fact]
    public void Evidence_counts_every_candidate_and_quotes_a_few_of_each()
    {
        var profile = Profile("mixed", "1", "2", "2026-07-20", "true", "a word");

        Assert.Equal(
            [ValueKind.Integer, ValueKind.Text, ValueKind.Boolean, ValueKind.Date],
            profile.Evidence.Select(static entry => entry.Kind));

        Assert.Equal(2, Assert.Single(profile.Evidence, entry => entry.Kind == ValueKind.Integer).Count);
        Assert.Equal(["2026-07-20"], Assert.Single(profile.Evidence, entry => entry.Kind == ValueKind.Date).Examples);
        Assert.Equal(profile.ValueCount, profile.Evidence.Sum(static entry => entry.Count));
    }

    // ---- Ambiguity (IN-1a) ---------------------------------------------------------------------------

    /// <summary>
    /// IN-1a's other example. Every day in this column is twelve or under, so nothing in it says
    /// which number is the day - and the column says so rather than choosing quietly.
    /// </summary>
    [Fact]
    public void A_date_column_where_every_day_could_be_a_month_reports_the_ambiguity()
    {
        var profile = Profile("date", "03/04/2026", "05/06/2026", "11/12/2026");

        Assert.Equal(ValueKind.Date, profile.Inferred);
        Assert.True(profile.HadAmbiguousValues);

        var ambiguity = profile.Ambiguity;
        Assert.NotNull(ambiguity);
        Assert.Equal(AmbiguityKind.DateOrder, ambiguity.Kind);
        Assert.Equal(3, ambiguity.Count);
        Assert.Equal("the day before the month", ambiguity.ReadAs);
        Assert.Equal("the month before the day", ambiguity.OrElse);
        Assert.Contains("03/04/2026", ambiguity.Examples);
        Assert.Contains("could be read two ways", ambiguity.Describe(), StringComparison.Ordinal);
    }

    /// <summary>
    /// A column where one value settles it is not ambiguous: the thirteenth cannot be a month, so
    /// every other value in the column is read the same way and there is nothing to report.
    /// </summary>
    [Fact]
    public void A_date_column_with_one_value_that_settles_it_reports_nothing()
    {
        var profile = Profile("date", "13/04/2026", "05/06/2026");

        Assert.Equal(ValueKind.Date, profile.Inferred);
        Assert.Null(profile.Ambiguity);
    }

    [Fact]
    public void A_number_that_could_be_thousands_or_a_fraction_reports_which_way_it_was_read()
    {
        var profile = Profile("amount", "1,234", "2,500", "3,000");

        Assert.Equal(ValueKind.Integer, profile.Inferred);

        var ambiguity = profile.Ambiguity;
        Assert.NotNull(ambiguity);
        Assert.Equal(AmbiguityKind.DecimalSeparator, ambiguity.Kind);
        Assert.Equal(3, ambiguity.Count);
        Assert.Contains("thousands", ambiguity.ReadAs, StringComparison.Ordinal);
        Assert.Equal("fractions", ambiguity.OrElse);
    }

    /// <summary>
    /// The same question in a column that turned out to be text is not worth reporting: the
    /// values are stored as they were written whichever way it would have gone.
    /// </summary>
    [Fact]
    public void An_ambiguity_that_does_not_bear_on_the_type_is_not_reported()
    {
        var profile = Profile("mixed", "1,234", "a word");

        Assert.Equal(ValueKind.Text, profile.Inferred);
        Assert.Null(profile.Ambiguity);
    }
}
