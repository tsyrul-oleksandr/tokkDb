using TokkDb.Assistant.Storage;
using TokkDb.Assistant.Storage.Engine;
using TokkDb;
using TokkDb.Pages;
using TokkDb.Values;

namespace TokkDb.Assistant.Tests;

/// <summary>
/// What only the engine-backed implementation can be asked.
///
/// Three kinds of question live here rather than in the shared suite. Whether a claim about an
/// access path is true, which needs a planner and an index to be true about (SC-7a). Whether a
/// bound on page reads holds, which needs pages (SC-9). And whether a definition written before a
/// field existed still loads, which needs a stored format to have written it (EX-1).
/// </summary>
public sealed class TokkDbStorageEngineTests : IDisposable
{
    private readonly TemporaryDatabase _database = new("engine");

    public void Dispose() => _database.Dispose();

    private TokkDbStorage Open() => new(_database.FilePath);

    // ---- One ordered pass, not one lookup per value (SC-9) ---------------------------------------

    /// <summary>
    /// SC-9, and what it costs here rather than what it was expected to cost.
    ///
    /// The requirement's shape holds: a set of values goes in, the implementation decides how to
    /// ask, and what it did comes back in the execution info. What does not hold is the bound
    /// SC-9 predicted, and the reason is worth having in a test rather than in a comment. Through
    /// the engine's public surface a range walk reads <b>every record in the range</b>, not the
    /// index entries over it - measured here at about one page read per record - so the ordered
    /// pass only wins once the values are a large fraction of the collection. An index-only walk
    /// would need an accessor the connection does not expose, which is a second engine change
    /// this plan does not allow.
    ///
    /// So what is asserted is what is true: each path is taken when it is the cheaper one, and
    /// the expensive question never costs more than reading the collection once.
    ///
    /// Twenty thousand records rather than a hundred thousand: the crossing point is a ratio, the
    /// ratio is what is being shown, and a hundred thousand records would add minutes to every
    /// run of the suite without moving it.
    /// </summary>
    [Fact]
    public void Checking_many_values_at_once_takes_the_cheaper_path_and_says_which()
    {
        const int records = 20_000;

        using var storage = Open();

        storage.CreateCollection(new CollectionDefinition("rows", columns:
        [
            new ColumnDefinition("fingerprint", ColumnType.Text, required: true),
            new ColumnDefinition("value", ColumnType.Integer)
        ]));

        storage.InUnitOfWork(() =>
        {
            foreach (var i in Enumerable.Range(0, records))
            {
                storage.Create("rows", new Dictionary<string, object?>
                {
                    ["fingerprint"] = $"1:{i:00000}", ["value"] = (long)i
                });
            }
        });

        var everything = Enumerable.Range(0, 8_000).Select(i => (object?)$"1:{i * 2:00000}").ToList();

        // Asked once and thrown away, so that the index the first large question builds is
        // charged to that question rather than to the ones being measured.
        storage.MatchValues("rows", "fingerprint", everything);

        var few = storage.MatchValues("rows", "fingerprint",
            [.. Enumerable.Range(0, 10).Select(i => (object?)$"1:{i:00000}")]);

        var many = storage.MatchValues("rows", "fingerprint", everything);

        Assert.Equal(10, few.Found.Count);
        Assert.Equal(8_000, many.Found.Count);

        // A handful of values is a seek each (SC-9a's small-set branch); eight thousand of twenty
        // thousand is one walk.
        Assert.Equal(QueryAccess.IndexSeek, few.Execution.Access);
        Assert.Equal(QueryAccess.OrderedPass, many.Execution.Access);

        var perSeek = (double)few.Execution.PagesRead / 10;

        // The walk is chosen because it is cheaper: eight thousand seeks would cost more than it
        // does, and it costs no more than one pass over the collection.
        Assert.True(
            many.Execution.PagesRead < perSeek * 8_000,
            $"the walk read {many.Execution.PagesRead} pages against {perSeek * 8_000:F0} for seeks");

        Assert.True(
            many.Execution.PagesRead < records * 1.5,
            $"the walk read {many.Execution.PagesRead} pages for {records} records");
    }

    /// <summary>
    /// The engine assumption SC-9 says to verify rather than inherit, recorded where the code can
    /// contradict it: <b>the B+Tree walks its leaves without descending again</b>. Its leaves are
    /// chained and <c>Range</c> follows the chain, which is why the bound above is the pages of
    /// the range rather than one page per value.
    ///
    /// Asserted rather than written in a comment, because a change to the tree that broke the
    /// chain would otherwise turn every ordered pass into a descent per key and nothing would
    /// say so.
    /// </summary>
    [Fact]
    public void The_index_leaves_are_a_chain_and_a_range_walks_it()
    {
        using var connection = new TokkDbConnection(_database.FilePath);
        connection.Load();

        connection.CreateCollection("rows",
            [new ColumnDescriptor("value", ValueTypeEnum.Long, "a number", unique: true)]);

        var entities = connection.Entities<Row>("rows");

        connection.InTransaction(() =>
        {
            foreach (var i in Enumerable.Range(0, 2_000)) entities.Insert(new Row { Value = i });
        });

        var tree = connection.PrimaryIndex("rows");
        var leaves = tree.Leaves().ToList();

        Assert.True(leaves.Count > 1, "a tree of 2,000 entries has more than one leaf");
        Assert.Equal(tree.Scan().Count(), leaves.Sum(static leaf => leaf.Entries.Count));

        // Every leaf but the last points at the next one, which is what makes the walk a walk.
        Assert.All(leaves.SkipLast(1), leaf => Assert.NotEqual(default, leaf.NextPageIndex));
        Assert.Equal(default, leaves[^1].NextPageIndex);
    }

    private sealed class Row
    {
        public long Value { get; set; }
    }

    // ---- What a capability added later does to an older database (EX-1) ---------------------------

    /// <summary>
    /// EX-1's acceptance condition, made real rather than promised.
    ///
    /// A definition is stored as a descriptor plus a settings document, and everything the
    /// descriptor has no room for is a key in that document. So a database written before a
    /// property existed simply has no key of that name - and reads it as its default. This writes
    /// such a database by taking the keys back out, closes it, and opens it again.
    /// </summary>
    [Fact]
    public void A_definition_written_before_a_property_existed_still_loads()
    {
        using (var storage = Open())
        {
            storage.CreateCollection(new CollectionDefinition("expenses", "money I spent", columns:
            [
                new ColumnDefinition("event", ColumnType.Text, "what it was for", required: true),
                new ColumnDefinition("paid_on", ColumnType.Date, "the day it was paid"),
                new ColumnDefinition("amount_eur", ColumnType.Decimal, "what it came to")
            ]));

            storage.Create("expenses", new Dictionary<string, object?>
            {
                ["event"] = "EuroPython",
                ["paid_on"] = new DateOnly(2026, 7, 14),
                ["amount_eur"] = 840.50m
            });
        }

        // The database as it would have been written by a version that had neither of the two
        // things the settings document carries: no required columns, no dates.
        using (var connection = new TokkDbConnection(_database.FilePath))
        {
            connection.Load();
            connection.SetMetadata("expenses", new Dictionary<string, string>());
        }

        using (var storage = Open())
        {
            var definition = storage.GetCollectionDefinition("expenses")!;

            // Read as the defaults: nothing has to have a value, and a moment is a moment.
            Assert.All(definition.Columns, column => Assert.False(column.Required));
            Assert.Equal(ColumnType.Timestamp, definition.Column("paid_on")!.Type);

            // And the collection still opens, still holds its record, and still reads it.
            var record = Assert.Single(storage.GetAll("expenses"));
            Assert.Equal("EuroPython", record["event"]);
            Assert.Equal(840.50m, record["amount_eur"]);
        }
    }

    // ---- Relations and conversations across a reopen ------------------------------------------------

    [Fact]
    public void A_relation_and_its_integrity_action_survive_a_reopen()
    {
        using (var storage = Open())
        {
            storage.CreateCollection(new CollectionDefinition("conferences", columns:
                [new ColumnDefinition("name", ColumnType.Text, required: true, unique: true)]));
            storage.CreateCollection(new CollectionDefinition("expenses", columns:
            [
                new ColumnDefinition("what", ColumnType.Text, required: true),
                new ColumnDefinition("conference", ColumnType.Text)
            ]));

            storage.AddRelation(new RelationDefinition(
                "expense_conference", "expenses", "conference", "conferences", "name",
                RelationIntegrity.Cascade, "the conference this expense was for"));

            storage.Create("conferences", new Dictionary<string, object?> { ["name"] = "EuroPython" });
            storage.Create("expenses", new Dictionary<string, object?>
            {
                ["what"] = "a train", ["conference"] = "EuroPython"
            });
        }

        using (var storage = Open())
        {
            var relation = Assert.Single(storage.GetRelations());

            Assert.Equal(RelationIntegrity.Cascade, relation.Integrity);
            Assert.Equal("the conference this expense was for", relation.Purpose);

            var conference = Assert.Single(storage.GetAll("conferences"));
            var result = storage.Delete("conferences", conference.Id);

            Assert.Single(result.AlsoRemoved);
            Assert.Empty(storage.GetAll("expenses"));
        }
    }

    /// <summary>
    /// SC-10 and S-7: the application is closed mid-conversation and reopened, and everything said
    /// is still there.
    /// </summary>
    [Fact]
    public void A_conversation_and_its_turns_survive_a_close_and_reopen()
    {
        Ulid conversation;
        var request = RecordIdentity.Next();

        using (var storage = Open())
        {
            conversation = storage.Conversations.Start("Conference expenses").Id;

            storage.Conversations.Append(
                conversation, TurnSpeaker.Person, "I want to save this", ["/tmp/expenses.csv"], request);
            storage.Conversations.Append(conversation, TurnSpeaker.Assistant, "Kept 22 of them.");
        }

        using (var storage = Open())
        {
            var found = storage.Conversations.Get(conversation);

            Assert.NotNull(found);
            Assert.Equal("Conference expenses", found.Title);
            Assert.Equal(2, found.TurnCount);

            var turns = storage.Conversations.Turns(conversation);

            Assert.Equal("I want to save this", turns[0].Text);
            Assert.Equal(["/tmp/expenses.csv"], turns[0].Attachments);
            Assert.Equal(request, turns[0].RequestId);
            Assert.Equal(TurnSpeaker.Assistant, turns[1].Speaker);

            Assert.Single(storage.Conversations.All());
        }
    }

    /// <summary>
    /// SC-6b across a reopen, which is the case the in-memory storage cannot have: the value that
    /// would not convert is on disk, in the type it was written as, and comes back as itself with
    /// its column still marked.
    /// </summary>
    [Fact]
    public void A_value_that_would_not_convert_is_still_there_after_a_reopen()
    {
        Ulid kept;

        using (var storage = Open())
        {
            storage.CreateCollection(new CollectionDefinition("readings", columns:
                [new ColumnDefinition("value", ColumnType.Text)]));

            storage.Create("readings", new Dictionary<string, object?> { ["value"] = "840" });
            kept = storage.Create("readings", new Dictionary<string, object?> { ["value"] = "n/a" }).Id;

            storage.RetypeColumn("readings", "value", ColumnType.Integer);
        }

        using (var storage = Open())
        {
            var record = storage.GetById("readings", kept)!;

            Assert.Equal("n/a", record["value"]);
            Assert.Contains("value", record.NeedsAttention);
            Assert.Equal(2, storage.CountNeedingAttention("readings", "value"));

            var report = storage.Converge("readings");

            Assert.Equal("n/a", Assert.Single(report.CouldNotConvert).Value);
            Assert.Equal(1, storage.CountNeedingAttention("readings", "value"));

            // And the one that could convert has, permanently.
            Assert.Contains(840L, storage.GetAll("readings").Select(static record => record["value"]));
        }
    }
}
