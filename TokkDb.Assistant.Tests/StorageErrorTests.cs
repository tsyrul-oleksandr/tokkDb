using TokkDb.Assistant.Storage;

namespace TokkDb.Assistant.Tests;

/// <summary>
/// The typed errors carry what SC-3 and SC-4 require them to carry, and they say it in a
/// sentence. The sentence is not the product's wording - the conversation phrases its own - but
/// a log entry and a failing test both read it, so it has to be a sentence and not a code.
/// </summary>
public sealed class StorageErrorTests
{
    [Fact]
    public void A_type_mismatch_names_the_column_the_type_it_wanted_and_what_it_got()
    {
        var error = new ColumnTypeMismatch("expenses", "paid_on", ColumnType.Date, "the 20th");

        Assert.Equal("expenses", error.CollectionName);
        Assert.Equal("paid_on", error.ColumnName);
        Assert.Equal(ColumnType.Date, error.Expected);
        Assert.Equal("text", error.ActualDescription);
        Assert.Equal("Column 'paid_on' of 'expenses' keeps Date, and \"the 20th\" is text.", error.ToString());
    }

    /// <summary>SC-4: naming the column is not enough. The record that holds the value is what the user wants to see.</summary>
    [Fact]
    public void A_duplicate_names_the_column_and_the_record_that_already_holds_the_value()
    {
        var holder = Ulid.NewUlid();
        var error = new DuplicateValue("reading_list", "title", "Designing Data-Intensive Applications", holder);

        Assert.Equal("title", error.ColumnName);
        Assert.Equal(holder, error.HeldBy);
        Assert.Contains(holder.ToString(), error.Describe(), StringComparison.Ordinal);
    }

    [Fact]
    public void An_error_prints_its_sentence_rather_than_its_fields()
    {
        StorageError error = new RequiredValueMissing("expenses", "amount_eur");
        Assert.Equal("Column 'amount_eur' of 'expenses' has to have a value.", error.ToString());
    }

    [Fact]
    public void A_refusal_reports_every_reason_not_only_the_first()
    {
        var thrown = new StorageValidationException(
        [
            new RequiredValueMissing("expenses", "amount_eur"),
            new UnknownColumn("expenses", "venue")
        ]);

        Assert.Equal(2, thrown.Errors.Count);
        Assert.Contains("2 reasons", thrown.Message, StringComparison.Ordinal);
        Assert.Contains("amount_eur", thrown.Message, StringComparison.Ordinal);
        Assert.Contains("venue", thrown.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// One type to catch means "the request did not fit". A caller catching it is not also
    /// catching its own bugs, which is why none of these derive from ArgumentException.
    /// </summary>
    [Fact]
    public void Everything_the_contract_throws_on_purpose_is_a_StorageException()
    {
        Assert.IsAssignableFrom<StorageException>(new InvalidDefinitionException("column name", "no"));
        Assert.IsAssignableFrom<StorageException>(new UnknownCollectionException("expenses"));
        Assert.IsAssignableFrom<StorageException>(new CollectionAlreadyExistsException("expenses"));
        Assert.IsAssignableFrom<StorageException>(new StorageValidationException([]));

        Assert.IsNotAssignableFrom<ArgumentException>(new InvalidDefinitionException("column name", "no"));
    }

    [Fact]
    public void An_error_message_does_not_move_with_the_machines_culture()
    {
        var error = new ColumnTypeMismatch("expenses", "amount_eur", ColumnType.Text, 840.5m);
        Assert.Contains("840.5", error.Describe(), StringComparison.Ordinal);
    }
}
