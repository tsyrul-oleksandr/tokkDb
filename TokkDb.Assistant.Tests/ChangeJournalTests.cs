using TokkDb.Assistant.Trace;

namespace TokkDb.Assistant.Tests;

/// <summary>
/// TR-2b: what a change writes down, and what stops it writing down too much.
///
/// <b>Shaped by the operation first and bounded by size second</b>, and the order is what these
/// tests are mostly about. A purely size-based rule would look reasonable and would truncate the
/// one payload that cannot be truncated - a delete, after which the journal is the only copy of
/// the record - while keeping a full copy of an insert, whose data is still in storage and which
/// is therefore the payload that can afford to be almost nothing.
///
/// The size half is not a preference either: it is the difference between a journal of an import
/// and a second copy of the file.
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

    // ---- Shaped by the operation ----------------------------------------------------------------

    /// <summary>
    /// An insert records the identity and a content hash and <b>not the row</b>.
    ///
    /// The data is in storage, and copying it here would make the journal a second copy of it.
    /// What the journal has to keep answering is narrower: which record this request created, so
    /// an undo knows what to remove, and whether anybody has touched it since, which is what the
    /// hash is for (AG-11a).
    /// </summary>
    [Fact]
    public void An_insert_records_the_identity_and_a_hash_and_not_the_row()
    {
        var record = Ulid.NewUlid();
        var change = DataChanges.Insert(Request, "expenses", record, Expense());

        Assert.Equal(ChangeKind.Insert, change.Kind);
        Assert.Equal(record, change.RecordId);
        Assert.Empty(change.Fields);
        Assert.Equal(Hashes.Length, change.ContentHash!.Length);

        // Undoing an insert is a delete by identity, and the identity is here whatever the row
        // was, so nothing about the row can make this one conditional.
        Assert.Equal(Reversibility.Reversible, change.Reversibility);
    }

    /// <summary>
    /// Step 3.2's acceptance condition, and the reason the insert payload is shaped the way it is:
    /// <b>importing ten thousand records produces a change record that does not grow with the
    /// width of the rows.</b>
    ///
    /// The narrow row is five fields; the wide one is sixty of two hundred characters. The journal
    /// costs exactly the same for both, because the hash is fixed width and there is nothing else
    /// in it.
    /// </summary>
    [Fact]
    public void The_journal_of_an_import_does_not_grow_with_the_width_of_the_rows()
    {
        const int records = 10_000;

        var narrow = Expense();
        var wide = WideRow();

        var forNarrow = Journal(records, "expenses", narrow);
        var forWide = Journal(records, "exports", wide);

        Assert.Equal(forNarrow, forWide);
        Assert.Equal(records * Hashes.Length, forWide);

        // And it is nowhere near the size of the data it describes: a hundredth of it, for a row
        // of this width, and a smaller fraction the wider the rows get.
        Assert.True(
            forWide < Characters(wide) * records / 100,
            $"the journal of {records} wide rows is {forWide} characters against {Characters(wide) * records} of data");
    }

    private static int Journal(int records, string collectionName, IReadOnlyDictionary<string, object?> row) =>
        Enumerable.Range(0, records)
            .Sum(_ => DataChanges.Insert(Request, collectionName, Ulid.NewUlid(), row).Weight);

    /// <summary>
    /// An update records the fields that changed, old beside new, and not the ones that did not
    /// (TR-2a).
    /// </summary>
    [Fact]
    public void An_update_records_the_fields_that_changed_old_beside_new()
    {
        var before = Expense();
        var after = new Dictionary<string, object?>(before, StringComparer.Ordinal)
        {
            ["cost"] = 910.00m,
            ["reimbursed"] = true
        };

        var change = DataChanges.Update(Request, "expenses", Ulid.NewUlid(), before, after);

        Assert.Equal(["cost", "reimbursed"], change.Fields.Select(static field => field.Name));

        var cost = change.Fields[0];
        Assert.Equal(840.50m, cost.Before.Value);
        Assert.Equal(910.00m, cost.After.Value);

        // Unconditionally reversible: putting a value back that was there cannot break a rule
        // the record already satisfied.
        Assert.Equal(Reversibility.Reversible, change.Reversibility);
    }

    /// <summary>
    /// A delete records the whole record, because the journal is about to be the only copy of it
    /// (TR-2b, D-17) - and the values come back as the values they were rather than as text,
    /// because storage refuses to parse on the way in (SC-3) and a row of strings could not be
    /// put back.
    /// </summary>
    [Fact]
    public void A_delete_records_enough_to_put_the_record_back()
    {
        var removed = Expense();
        var change = DataChanges.Delete(Request, "expenses", Ulid.NewUlid(), removed);

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
    /// And a delete is <b>conditionally</b> reversible even when the record is all there, which is
    /// draft 7's correction to AG-11c. A uniqueness rule or a relation created since the deletion
    /// can refuse the record on its way back in, and that is found out when the undo is attempted
    /// rather than when it is offered.
    /// </summary>
    [Fact]
    public void A_delete_is_reversible_only_with_conditions()
    {
        var change = DataChanges.Delete(Request, "expenses", Ulid.NewUlid(), Expense());

        Assert.Equal(Reversibility.ReversibleWithConditions, change.Reversibility);
    }

    // ---- Bounded by size ------------------------------------------------------------------------

    /// <summary>
    /// TR-2b's named case: a single two-megabyte value is kept as a size, a hash and a bounded
    /// preview, and the trace does not approach the size of the data.
    ///
    /// The delete is then <see cref="Reversibility.NotReversible"/>, which is the honest answer
    /// and the one the confirmation card shows <b>before</b> the delete runs: two hundred
    /// characters of a two-megabyte note is not an inverse, and an undo that restored it would
    /// have destroyed the rest of the note itself.
    /// </summary>
    [Fact]
    public void A_value_too_large_to_keep_is_kept_as_its_size_a_hash_and_a_preview()
    {
        var minutes = new string('x', 2_000_000);

        var change = DataChanges.Delete(Request, "notes", Ulid.NewUlid(), new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["title"] = "Minutes of the meeting",
            ["body"] = minutes
        });

        var body = change.Fields.Single(static field => field.Name == "body").Before;

        Assert.False(body.IsWhole);
        Assert.Null(body.Value);
        Assert.Equal(minutes.Length, body.Length);
        Assert.Equal(PayloadLimits.Default.Preview, body.Preview!.Length);
        Assert.Equal(Hashes.Length, body.Hash!.Length);

        Assert.Equal(Reversibility.NotReversible, change.Reversibility);
        Assert.True(change.Weight < 1_000, $"the change weighs {change.Weight} characters");

        // The rest of the record is untouched by one field's size, and is still there to be read.
        var title = change.Fields.Single(static field => field.Name == "title").Before;
        Assert.True(title.IsWhole);
        Assert.Equal("Minutes of the meeting", title.Value);
    }

    /// <summary>
    /// The other half of the cap, which a per-value bound cannot do: a record of five thousand
    /// short fields passes every per-value bound there is.
    ///
    /// Past the cap <b>the inverse is not kept at all</b>, rather than half of it being kept. The
    /// fields are still named and still hashed, so the audit says what changed; there is nothing
    /// to restore from, and the change says so.
    /// </summary>
    [Fact]
    public void A_record_of_too_many_fields_gives_up_its_inverse_rather_than_keeping_half_of_one()
    {
        var record = Enumerable.Range(0, 5_000).ToDictionary(
            column => $"field{column:0000}",
            column => (object?)$"value {column}",
            StringComparer.Ordinal);

        var change = DataChanges.Delete(Request, "wide", Ulid.NewUlid(), record);

        Assert.Equal(Reversibility.NotReversible, change.Reversibility);
        Assert.True(change.Weight <= PayloadLimits.Default.LongestPayload, $"the change weighs {change.Weight}");
        Assert.True(change.OmittedFields > 0);
        Assert.Equal(record.Count, change.Fields.Count + change.OmittedFields);
        Assert.Empty(DataChanges.Restore(change));
    }

    /// <summary>
    /// A new value too large to keep costs the audit a preview and costs the undo nothing, because
    /// the inverse of an update is what the fields <b>were</b>.
    /// </summary>
    [Fact]
    public void An_update_to_something_enormous_is_still_reversible()
    {
        var before = new Dictionary<string, object?>(StringComparer.Ordinal) { ["body"] = "Two lines." };
        var after = new Dictionary<string, object?>(StringComparer.Ordinal) { ["body"] = new string('y', 2_000_000) };

        var change = DataChanges.Update(Request, "notes", Ulid.NewUlid(), before, after);
        var body = change.Fields.Single();

        Assert.True(body.Before.IsWhole);
        Assert.False(body.After.IsWhole);
        Assert.Equal(Reversibility.Reversible, change.Reversibility);
        Assert.Equal("Two lines.", DataChanges.Restore(change)["body"]);
    }

    // ---- The two hashes -------------------------------------------------------------------------

    /// <summary>
    /// The content hash is of the record rather than of the dictionary that happened to carry it:
    /// fixed width, ordered by field name, and rendered invariantly. TR-2b's bound rests on the
    /// first of those and AG-11a's question rests on the other two.
    /// </summary>
    [Fact]
    public void The_content_hash_says_what_the_record_was_and_not_how_it_arrived()
    {
        var written = Expense();

        var reordered = new Dictionary<string, object?>(StringComparer.Ordinal);
        foreach (var (name, value) in written.Reverse()) reordered[name] = value;

        var first = DataChanges.Insert(Request, "expenses", Ulid.NewUlid(), written);
        var second = DataChanges.Insert(Request, "expenses", Ulid.NewUlid(), reordered);

        Assert.Equal(first.ContentHash, second.ContentHash);

        var edited = new Dictionary<string, object?>(written, StringComparer.Ordinal) { ["cost"] = 840.51m };

        Assert.NotEqual(
            first.ContentHash,
            DataChanges.Insert(Request, "expenses", Ulid.NewUlid(), edited).ContentHash);
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
