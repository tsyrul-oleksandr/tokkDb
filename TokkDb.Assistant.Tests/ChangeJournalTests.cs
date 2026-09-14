using TokkDb.Assistant.Trace;

namespace TokkDb.Assistant.Tests;

/// <summary>
/// TR-2b and V-18: what a change writes down, and what stops it writing down too much.
///
/// A record change writes down references - the record and two versions - and no values, because
/// the values are in the engine's version history (step 9.1 of the versioning plan). A structural
/// change is where the journal is still the only copy of what went, and there the payload is
/// <b>shaped by the operation first and bounded by size second</b>: the operation decides what is
/// recorded and the cap decides how much of it survives, with the change declared irreversible
/// rather than truncated when the cap cannot hold its inverse.
///
/// The size half is not a preference: it is the difference between a journal of an import and a
/// second copy of the file.
/// </summary>
public sealed class ChangeJournalTests
{
    private static readonly Ulid Request = Ulid.NewUlid();

    /// <summary>An expense, as the assistant would store one.</summary>
    private static Dictionary<string, object?> Expense() => new(StringComparer.Ordinal)
    {
        ["description"] = "EuroPython tickets",
        ["cost"] = 840.50m,
        ["nights"] = 4L,
        ["paidOn"] = new DateOnly(2026, 7, 20),
        ["reimbursed"] = false
    };

    /// <summary>A row from the kind of export that is sixty columns wide and two hundred characters a cell.</summary>
    private static Dictionary<string, object?> WideRow(int columns = 60, int width = 200) =>
        Enumerable.Range(0, columns).ToDictionary(
            column => $"column{column:00}",
            column => (object?)new string((char)('a' + column % 26), width),
            StringComparer.Ordinal);

    private static int Characters(IReadOnlyDictionary<string, object?> row) =>
        row.Sum(entry => entry.Key.Length + JournalValue.TextOf(entry.Value).Length);

    // ---- Record changes: references, not values (V-18, AJ-1) --------------------------------------

    /// <summary>
    /// A record change records the record and two versions and <b>not the values</b>. The values
    /// are in the engine's version history, where an undo restores them and the before-and-after
    /// table reads them; what the journal keeps answering is which record, which version it
    /// replaced, and which it produced - the last of which says exactly whether anybody has
    /// touched the record since (AG-11a).
    /// </summary>
    [Fact]
    public void A_record_change_is_a_fixed_size_reference_and_carries_no_values()
    {
        var record = Ulid.NewUlid();
        var before = Ulid.NewUlid();
        var after = Ulid.NewUlid();

        var inserted = DataChanges.Insert(Request, "expenses", record, after, Reversibility.Reversible);
        var updated = DataChanges.Update(Request, "expenses", record, before, after, Reversibility.ReversibleWithConditions);
        var deleted = DataChanges.Delete(Request, "expenses", record, before, after, Reversibility.ReversibleWithConditions);

        foreach (var change in new[] { inserted, updated, deleted })
        {
            Assert.True(change.IsRecordChange);
            Assert.Equal(record, change.RecordId);
            Assert.Equal(after, change.VersionId);
            Assert.Empty(change.Fields);
            Assert.Null(change.ContentHash);
            Assert.Equal(0, change.OmittedFields);
            Assert.Equal(0, change.Weight);
        }

        Assert.Equal(ChangeKind.Insert, inserted.Kind);
        Assert.Null(inserted.PreviousVersionId);
        Assert.Equal(ChangeKind.Update, updated.Kind);
        Assert.Equal(before, updated.PreviousVersionId);
        Assert.Equal(ChangeKind.Delete, deleted.Kind);
        Assert.Equal(before, deleted.PreviousVersionId);
    }

    /// <summary>
    /// Step 3.2's acceptance condition, kept: <b>importing ten thousand records produces change
    /// records whose size does not depend on the width of the rows</b>. The narrow row is five
    /// fields; the wide one is sixty of two hundred characters. The journal costs exactly the
    /// same for both, because a record change carries no payload at all.
    /// </summary>
    [Fact]
    public void The_journal_of_an_import_does_not_grow_with_the_width_of_the_rows()
    {
        const int records = 10_000;

        var forNarrow = Journal(records, "expenses", Expense());
        var forWide = Journal(records, "exports", WideRow());

        Assert.Equal(forNarrow, forWide);
        Assert.Equal(0, forWide);
        Assert.True(Characters(WideRow()) * records > 0);
    }

    private static int Journal(int records, string collectionName, IReadOnlyDictionary<string, object?> row)
    {
        Assert.NotEmpty(row);
        return Enumerable.Range(0, records)
            .Sum(_ => DataChanges.Insert(Request, collectionName, Ulid.NewUlid(), Ulid.NewUlid(), Reversibility.Reversible).Weight);
    }

    /// <summary>
    /// AJ-5 and AG-11c: a record change's reversibility is decided by what its collection
    /// declares, before it runs, and by nothing about the record - not its width, not a cap.
    /// </summary>
    [Theory]
    [InlineData(false, false, Reversibility.Reversible)]
    [InlineData(true, false, Reversibility.ReversibleWithConditions)]
    [InlineData(false, true, Reversibility.ReversibleWithConditions)]
    [InlineData(true, true, Reversibility.ReversibleWithConditions)]
    public void A_record_changes_reversibility_comes_from_its_collection(bool unique, bool related, Reversibility expected)
    {
        Assert.Equal(expected, Reversibilities.OfRecordChange(unique, related));
    }

    /// <summary>A record change takes no payload: the shaped and bounded rules are for structural changes only.</summary>
    [Fact]
    public void A_record_change_kind_cannot_be_given_a_payload()
    {
        Assert.Throws<ArgumentException>(() =>
            DataChanges.Structural(Request, ChangeKind.Delete, "expenses", [], Reversibility.ReversibleWithConditions));
    }

    // ---- Structural changes: shaped by the operation, bounded by size (TR-2b) ---------------------

    /// <summary>
    /// A structural change keeps its inverse in the journal - the values a removed field held,
    /// old beside nothing - and inside the cap it stays as reversible as the caller said.
    /// </summary>
    [Fact]
    public void A_structural_change_within_the_cap_keeps_its_inverse()
    {
        var removed = Expense();
        var fields = removed.Keys.OrderBy(static name => name, StringComparer.Ordinal)
            .Select(name => FieldChange.Removed(name, JournalValue.Of(removed[name], PayloadLimits.Default)))
            .ToList();

        var change = DataChanges.Structural(Request, ChangeKind.FieldRemoved, "expenses", fields, Reversibility.ReversibleWithConditions);

        Assert.Equal(Reversibility.ReversibleWithConditions, change.Reversibility);
        Assert.Equal(removed.Count, change.Fields.Count);
        Assert.All(change.Fields, static field => Assert.True(field.After.IsEmpty));

        var restored = DataChanges.Restore(change);

        Assert.Equal(removed.Count, restored.Count);

        foreach (var (name, value) in removed)
        {
            Assert.Equal(value, restored[name]);
            Assert.Equal(value!.GetType(), restored[name]!.GetType());
        }
    }

    /// <summary>
    /// TR-2b's named case: a single two-megabyte value is kept as a size, a hash and a bounded
    /// preview, and the trace does not approach the size of the data.
    ///
    /// The change is then <see cref="Reversibility.NotReversible"/>, which is the honest answer
    /// and the one the confirmation card shows <b>before</b> the change runs: two hundred
    /// characters of a two-megabyte note is not an inverse, and an undo that restored it would
    /// have destroyed the rest of the note itself.
    /// </summary>
    [Fact]
    public void A_value_too_large_to_keep_is_kept_as_its_size_a_hash_and_a_preview()
    {
        var minutes = new string('x', 2_000_000);

        var change = DataChanges.Structural(Request, ChangeKind.FieldRemoved, "notes",
        [
            FieldChange.Removed("title", JournalValue.Of("Minutes of the meeting", PayloadLimits.Default)),
            FieldChange.Removed("body", JournalValue.Of(minutes, PayloadLimits.Default))
        ], Reversibility.ReversibleWithConditions);

        var body = change.Fields.Single(static field => field.Name == "body").Before;

        Assert.False(body.IsWhole);
        Assert.Null(body.Value);
        Assert.Equal(minutes.Length, body.Length);
        Assert.Equal(PayloadLimits.Default.Preview, body.Preview!.Length);
        Assert.Equal(Hashes.Length, body.Hash!.Length);

        Assert.Equal(Reversibility.NotReversible, change.Reversibility);
        Assert.True(change.Weight < 1_000, $"the change weighs {change.Weight} characters");

        // The rest of the change is untouched by one field's size, and is still there to be read.
        var title = change.Fields.Single(static field => field.Name == "title").Before;
        Assert.True(title.IsWhole);
        Assert.Equal("Minutes of the meeting", title.Value);
    }

    /// <summary>
    /// The other half of the cap, which a per-value bound cannot do: five thousand short fields
    /// pass every per-value bound there is.
    ///
    /// Past the cap <b>the inverse is not kept at all</b>, rather than half of it being kept. The
    /// fields are still named and still hashed, so the audit says what changed; there is nothing
    /// to restore from, and the change says so.
    /// </summary>
    [Fact]
    public void A_change_of_too_many_fields_gives_up_its_inverse_rather_than_keeping_half_of_one()
    {
        var fields = Enumerable.Range(0, 5_000)
            .Select(column => FieldChange.Removed($"field{column:0000}", JournalValue.Of($"value {column}", PayloadLimits.Default)))
            .ToList();

        var change = DataChanges.Structural(Request, ChangeKind.FieldRemoved, "wide", fields, Reversibility.ReversibleWithConditions);

        Assert.Equal(Reversibility.NotReversible, change.Reversibility);
        Assert.True(change.Weight <= PayloadLimits.Default.LongestPayload, $"the change weighs {change.Weight}");
        Assert.True(change.OmittedFields > 0);
        Assert.Equal(fields.Count, change.Fields.Count + change.OmittedFields);
        Assert.Empty(DataChanges.Restore(change));
    }

    // ---- The two hashes -------------------------------------------------------------------------

    /// <summary>
    /// The hash of a set of fields is of the record rather than of the dictionary that happened
    /// to carry it: fixed width, ordered by field name, and rendered invariantly. It no longer
    /// rides on a record change (V-18), but it is still what an old change carries and what a
    /// structural change may hash a large value with.
    /// </summary>
    [Fact]
    public void The_hash_of_fields_says_what_the_record_was_and_not_how_it_arrived()
    {
        var written = Expense();

        var reordered = new Dictionary<string, object?>(StringComparer.Ordinal);
        foreach (var (name, value) in written.Reverse()) reordered[name] = value;

        Assert.Equal(Hashes.OfFields(written), Hashes.OfFields(reordered));
        Assert.Equal(Hashes.Length, Hashes.OfFields(written).Length);

        var edited = new Dictionary<string, object?>(written, StringComparer.Ordinal) { ["cost"] = 840.51m };

        Assert.NotEqual(Hashes.OfFields(written), Hashes.OfFields(edited));
    }

    /// <summary>
    /// TR-3 and D-8: a model call records a hash of the prompt and never the prompt. The factory
    /// exists so that the rule is something the code does rather than something callers remember.
    /// </summary>
    [Fact]
    public void A_model_call_keeps_a_hash_of_the_prompt_and_nothing_of_the_prompt()
    {
        const string prompt = "Which collection do these columns belong in? amount, paid on, supplier";

        var call = ModelCalls.For("qwen3.5:4b", prompt, promptTokens: 412, completionTokens: 38, TimeSpan.FromSeconds(2.4));

        Assert.Equal(Hashes.Of(prompt), call.PromptHash);
        Assert.Equal(Hashes.Length, call.PromptHash.Length);
        Assert.Equal(450, call.TotalTokens);

        // Nothing the call carries contains anything the person wrote.
        var written = string.Join(
            " ",
            call.Model, call.PromptHash, call.PromptTokens, call.CompletionTokens, call.Duration);

        foreach (var word in prompt.Split(' '))
        {
            Assert.DoesNotContain(word, written, StringComparison.OrdinalIgnoreCase);
        }
    }
}
