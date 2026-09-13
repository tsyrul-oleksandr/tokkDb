using TokkDb.Assistant.Storage;
using Xunit;

namespace TokkDb.Assistant.Tests;

/// <summary>
/// Storing many rows at once, and knowing which of them are already there (IN-4 to IN-9a).
///
/// The two questions IN-5 keeps apart are visible in what is tested and what is not: nothing here
/// decides what kind of thing a row is, which is the mapping step's job. This is record identity,
/// answered deterministically in C#, and the same answers have to come from both implementations.
/// </summary>
public abstract partial class StorageContractTests
{
    private const string Papers = "papers";

    private IStorage GivenPapers(bool withDoi = true)
    {
        var storage = Storage;

        storage.CreateCollection(new CollectionDefinition(Papers, "papers I have read", columns:
        [
            new ColumnDefinition("title", ColumnType.Text, "what it is called", required: true),
            new ColumnDefinition("doi", ColumnType.Text, "the paper's own identifier"),
            new ColumnDefinition("year", ColumnType.Integer, "when it came out")
        ]));

        if (!withDoi) return storage;

        return storage;
    }

    /// <summary>Twenty-two rows, the shape IN-5's acceptance condition is written about.</summary>
    private static List<ImportRow> TwentyTwoPapers(bool withDoi = true) =>
    [
        .. Enumerable.Range(0, 22).Select(i => new ImportRow(
            new Dictionary<string, object?>
            {
                ["title"] = $"paper {i:00}",
                ["doi"] = withDoi ? $"10.1000/{i:00}" : null,
                ["year"] = 2020L + i % 5
            },
            LineNumber: i + 2))
    ];

    // ---- Have I seen this row before (IN-5, IN-6, IN-7) ------------------------------------------

    /// <summary>
    /// IN-5's acceptance condition, worded the way it is for a reason: an earlier version of it
    /// was satisfied by storing 44 records from importing 22 twice. Both halves are tested,
    /// because the second - no natural key - is the one that used to be allowed to fail.
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Importing_the_same_file_twice_stores_twenty_two_records_and_not_forty_four(bool withDoi)
    {
        var storage = GivenPapers();
        var importer = new RecordImporter(storage);
        var key = withDoi ? "doi" : null;

        var first = importer.Import(new ImportRequest(Papers, TwentyTwoPapers(withDoi), KeyColumn: key));

        Assert.Equal(22, first.Inserted);
        Assert.Equal(0, first.Skipped);

        var second = importer.Import(new ImportRequest(Papers, TwentyTwoPapers(withDoi), KeyColumn: key));

        Assert.Equal(0, second.Inserted);
        Assert.Equal(22, second.Skipped);

        Assert.Equal(22, storage.GetAll(Papers).Count);
    }

    [Fact]
    public void A_file_that_lists_the_same_row_twice_stores_it_once()
    {
        var storage = GivenPapers();

        var rows = TwentyTwoPapers();
        rows.Add(rows[3]);

        var report = new RecordImporter(storage).Import(new ImportRequest(Papers, rows, KeyColumn: "doi"));

        Assert.Equal(22, report.Inserted);
        Assert.Equal(1, report.Skipped);

        var repeated = Assert.Single(report.Rows, static row => row.Outcome is RowOutcome.Skipped);
        var reason = Assert.IsType<MatchedEarlierRow>(repeated.Reason);
        Assert.Equal("doi", reason.By);
    }

    /// <summary>
    /// IN-7's acceptance condition: what happened is reported per row and reads as a sentence,
    /// rather than as a count alone.
    /// </summary>
    [Fact]
    public void A_second_import_says_what_it_kept_and_what_it_skipped_and_on_what()
    {
        var storage = GivenPapers();
        var importer = new RecordImporter(storage);

        importer.Import(new ImportRequest(Papers, TwentyTwoPapers().Take(1).ToList(), KeyColumn: "doi"));

        var report = importer.Import(new ImportRequest(Papers, TwentyTwoPapers(), KeyColumn: "doi"));

        Assert.Equal("kept 21, skipped 1 that matched on doi", report.Describe());
        Assert.Equal("doi", report.IdentifiedBy);

        var skipped = Assert.Single(report.Rows, static row => row.Outcome is RowOutcome.Skipped);
        var matched = Assert.IsType<MatchedStoredRecord>(skipped.Reason);

        Assert.Equal("doi", matched.By);
        Assert.Equal(skipped.RecordId, matched.ExistingRecord);
    }

    [Fact]
    public void Adding_everything_is_an_explicit_choice_and_stores_the_row_twice()
    {
        var storage = GivenPapers();
        var importer = new RecordImporter(storage);

        importer.Import(new ImportRequest(Papers, TwentyTwoPapers(), KeyColumn: "doi"));

        var again = importer.Import(new ImportRequest(
            Papers, TwentyTwoPapers(), MergePolicy.AddAll, KeyColumn: "doi"));

        Assert.Equal(22, again.Inserted);
        Assert.Equal(44, storage.GetAll(Papers).Count);
    }

    [Fact]
    public void Bringing_records_up_to_date_changes_the_ones_that_matched()
    {
        var storage = GivenPapers();
        var importer = new RecordImporter(storage);

        importer.Import(new ImportRequest(Papers, TwentyTwoPapers(), KeyColumn: "doi"));

        var corrected = TwentyTwoPapers();
        corrected[4] = corrected[4] with
        {
            Fields = new Dictionary<string, object?>(corrected[4].Fields) { ["year"] = 1999L }
        };

        var report = importer.Import(new ImportRequest(
            Papers, corrected, MergePolicy.UpdateExisting, KeyColumn: "doi"));

        Assert.Equal(22, report.Updated);
        Assert.Equal(0, report.Inserted);
        Assert.Equal(22, storage.GetAll(Papers).Count);

        var record = Assert.Single(storage.ExecuteQuery(new StorageQuery(
            Papers, [new QueryCondition("doi", QueryOperator.Equals, "10.1000/04")])).Records);

        Assert.Equal(1999L, record["year"]);
    }

    /// <summary>
    /// IN-7: update existing requires a natural key, and says so, rather than silently behaving
    /// as add all. A fingerprint covers every value, so a row that changed has a different one
    /// and can never be matched to the record it changed.
    /// </summary>
    [Fact]
    public void Bringing_records_up_to_date_without_a_key_is_refused_with_that_reason()
    {
        var storage = GivenPapers();

        var thrown = Assert.Throws<InvalidDefinitionException>(() => new RecordImporter(storage).Import(
            new ImportRequest(Papers, TwentyTwoPapers(), MergePolicy.UpdateExisting)));

        Assert.Equal("merge policy", thrown.Member);
        Assert.Empty(storage.GetAll(Papers));
    }

    [Fact]
    public void Asking_leaves_the_rows_that_matched_for_a_person_to_decide_about()
    {
        var storage = GivenPapers();
        var importer = new RecordImporter(storage);

        importer.Import(new ImportRequest(Papers, TwentyTwoPapers().Take(2).ToList(), KeyColumn: "doi"));

        var report = importer.Import(new ImportRequest(
            Papers, TwentyTwoPapers(), MergePolicy.Ask, KeyColumn: "doi"));

        Assert.Equal(20, report.Inserted);
        Assert.Equal(2, report.Skipped);
        Assert.All(
            report.Rows.Where(static row => row.Outcome is RowOutcome.Skipped),
            row => Assert.IsType<AwaitingDecision>(row.Reason));
    }

    // ---- The fingerprint (IN-6a) -------------------------------------------------------------------

    [Fact]
    public void A_keyless_import_gives_every_record_a_fingerprint_carrying_its_version()
    {
        var storage = GivenPapers();

        new RecordImporter(storage).Import(new ImportRequest(Papers, TwentyTwoPapers(withDoi: false)));

        var definition = storage.GetCollectionDefinition(Papers)!;

        Assert.NotNull(definition.Column(Fingerprints.ColumnName));
        Assert.Equal(
            Fingerprints.Version.ToString(),
            definition.Metadata[Fingerprints.VersionMetadataKey]);

        var fingerprints = storage.GetAll(Papers)
            .Select(record => (string?)record[Fingerprints.ColumnName])
            .ToList();

        Assert.Equal(22, fingerprints.Distinct().Count());
        Assert.All(fingerprints, fingerprint => Assert.Equal(Fingerprints.Version, Fingerprints.VersionOf(fingerprint)));
    }

    /// <summary>
    /// The backfill that is easy to leave out and expensive to leave out: records stored before
    /// any import have no fingerprint, so without it the first keyless import would duplicate
    /// every one of them.
    /// </summary>
    [Fact]
    public void Records_stored_before_the_first_import_are_given_fingerprints_too()
    {
        var storage = GivenPapers();

        var written = storage.Create(Papers, new Dictionary<string, object?>
        {
            ["title"] = "paper 00", ["doi"] = "10.1000/00", ["year"] = 2020L
        });

        var report = new RecordImporter(storage).Import(new ImportRequest(Papers, TwentyTwoPapers()));

        Assert.Equal(21, report.Inserted);
        Assert.Equal(1, report.Skipped);
        Assert.Equal(22, storage.GetAll(Papers).Count);
        Assert.NotNull(storage.GetById(Papers, written.Id)![Fingerprints.ColumnName]);
    }

    // ---- Row failure against operation failure (IN-4, IN-9) ------------------------------------------

    /// <summary>
    /// IN-4's acceptance condition. Three rows of a hundred cannot be read; ninety-seven commit,
    /// and the three are reported with their line numbers and their reasons.
    /// </summary>
    [Fact]
    public void A_hundred_rows_of_which_three_are_unreadable_store_ninety_seven_and_report_three()
    {
        var storage = GivenPapers();

        var rows = new List<ImportRow>();
        foreach (var i in Enumerable.Range(0, 100))
        {
            rows.Add(i is 7 or 31 or 64
                ? new ImportRow(new Dictionary<string, object?>(), LineNumber: i + 2,
                    Unreadable: $"line {i + 2} has three fields and the header has four")
                : new ImportRow(
                    new Dictionary<string, object?>
                    {
                        ["title"] = $"paper {i:000}", ["doi"] = $"10.1000/{i:000}", ["year"] = 2020L
                    },
                    LineNumber: i + 2));
        }

        var report = new RecordImporter(storage).Import(new ImportRequest(Papers, rows, KeyColumn: "doi"));

        Assert.Equal(97, report.Inserted);
        Assert.Equal(3, report.Rejected);
        Assert.Equal(97, storage.GetAll(Papers).Count);

        Assert.Equal([9, 33, 66], report.Problems.Select(static row => row.LineNumber!.Value));
        Assert.All(report.Problems, row => Assert.IsType<RowUnreadable>(row.Reason));
    }

    [Fact]
    public void A_row_whose_values_do_not_fit_is_rejected_and_the_rest_are_stored()
    {
        var storage = GivenPapers();

        var rows = TwentyTwoPapers();
        rows[5] = rows[5] with
        {
            Fields = new Dictionary<string, object?>(rows[5].Fields) { ["year"] = "nineteen ninety" }
        };
        rows[9] = rows[9] with
        {
            Fields = new Dictionary<string, object?>(rows[9].Fields) { ["title"] = null }
        };

        var report = new RecordImporter(storage).Import(new ImportRequest(Papers, rows, KeyColumn: "doi"));

        Assert.Equal(20, report.Inserted);
        Assert.Equal(2, report.Rejected);

        var wrongType = Assert.IsType<RowDoesNotFit>(report.Problems[0].Reason);
        Assert.Single(wrongType.Errors.OfType<ColumnTypeMismatch>());

        var missing = Assert.IsType<RowDoesNotFit>(report.Problems[1].Reason);
        Assert.Single(missing.Errors.OfType<RequiredValueMissing>());
    }

    /// <summary>
    /// IN-8: a row skipped as a duplicate and a row rejected for a value nobody can read are
    /// distinguishable in the report, by outcome and by reason.
    /// </summary>
    [Fact]
    public void A_skipped_row_and_a_rejected_row_are_told_apart()
    {
        var storage = GivenPapers();
        var importer = new RecordImporter(storage);

        importer.Import(new ImportRequest(Papers, TwentyTwoPapers().Take(1).ToList(), KeyColumn: "doi"));

        var rows = TwentyTwoPapers();
        rows[3] = rows[3] with
        {
            Fields = new Dictionary<string, object?>(rows[3].Fields) { ["year"] = "not a year" }
        };

        var report = importer.Import(new ImportRequest(Papers, rows, KeyColumn: "doi"));

        Assert.Equal(1, report.Skipped);
        Assert.Equal(1, report.Rejected);
        Assert.Equal(20, report.Inserted);

        Assert.NotEqual(
            report.Rows.First(static row => row.Outcome is RowOutcome.Skipped).Reason!.GetType(),
            report.Rows.First(static row => row.Outcome is RowOutcome.Rejected).Reason!.GetType());
    }

    /// <summary>
    /// IN-9's third class: a fault rolls the whole import back, where a bad row does not. The
    /// import is inside the caller's own unit of work here, which is what an orchestrator does
    /// when a request is cancelled after the write.
    /// </summary>
    [Fact]
    public void A_cancellation_leaves_nothing()
    {
        var storage = GivenPapers();

        Assert.Throws<OperationCanceledException>(() => storage.InUnitOfWork(() =>
        {
            new RecordImporter(storage).Import(new ImportRequest(Papers, TwentyTwoPapers(), KeyColumn: "doi"));
            throw new OperationCanceledException();
        }));

        Assert.Empty(storage.GetAll(Papers));
    }

    [Fact]
    public void A_row_holding_a_value_a_unique_column_already_has_is_rejected_on_its_own()
    {
        var storage = Storage;
        storage.CreateCollection(new CollectionDefinition(Papers, columns:
        [
            new ColumnDefinition("title", ColumnType.Text, required: true),
            new ColumnDefinition("doi", ColumnType.Text, unique: true)
        ]));

        storage.Create(Papers, new Dictionary<string, object?>
        {
            ["title"] = "already here", ["doi"] = "10.1000/05"
        });

        var rows = Enumerable.Range(0, 10).Select(i => new ImportRow(
            new Dictionary<string, object?> { ["title"] = $"paper {i}", ["doi"] = $"10.1000/{i:00}" },
            LineNumber: i + 2)).ToList();

        var report = new RecordImporter(storage).Import(new ImportRequest(Papers, rows, MergePolicy.AddAll));

        Assert.Equal(9, report.Inserted);
        Assert.Equal(1, report.Rejected);

        var taken = Assert.IsType<UniqueValueTaken>(Assert.Single(report.Problems).Reason);
        Assert.Equal("doi", taken.ColumnName);
        Assert.Equal("10.1000/05", taken.Value);
    }

    // ---- Confirming a key (IN-6, IN-6b) ---------------------------------------------------------------

    [Fact]
    public void A_column_that_is_unique_and_present_in_every_row_can_be_the_key()
    {
        var storage = GivenPapers();

        var verdict = new RecordImporter(storage).ConfirmKey(Papers, "doi", TwentyTwoPapers());

        Assert.True(verdict.IsUsable);
        Assert.True(verdict.IsCertain);
        Assert.Empty(verdict.RepeatedInFile);
        Assert.Empty(verdict.AlreadyStored);
    }

    [Fact]
    public void A_column_that_repeats_in_the_file_is_rejected_without_the_user_being_asked()
    {
        var storage = GivenPapers();

        var rows = TwentyTwoPapers();
        rows[7] = rows[7] with
        {
            Fields = new Dictionary<string, object?>(rows[7].Fields) { ["doi"] = "10.1000/02" }
        };

        var verdict = new RecordImporter(storage).ConfirmKey(Papers, "doi", rows);

        Assert.False(verdict.IsUsable);
        var (row, first, value) = Assert.Single(verdict.RepeatedInFile);
        Assert.Equal(7, row);
        Assert.Equal(2, first);
        Assert.Equal("10.1000/02", value);
        Assert.Contains("repeats", verdict.Describe(), StringComparison.Ordinal);
    }

    [Fact]
    public void A_column_with_no_value_in_some_rows_cannot_be_the_key()
    {
        var storage = GivenPapers();

        var verdict = new RecordImporter(storage).ConfirmKey(Papers, "doi", TwentyTwoPapers(withDoi: false));

        Assert.False(verdict.IsUsable);
        Assert.Equal(22, verdict.MissingInFile.Count);
    }

    /// <summary>
    /// IN-6's addition, and the half that was missing: a key unique in one spreadsheet says
    /// nothing about the twelve hundred records already stored.
    /// </summary>
    [Fact]
    public void A_column_that_collides_with_records_already_stored_cannot_be_the_key_either()
    {
        var storage = GivenPapers();

        storage.Create(Papers, new Dictionary<string, object?>
        {
            ["title"] = "already here", ["doi"] = "10.1000/03", ["year"] = 2019L
        });

        var verdict = new RecordImporter(storage).ConfirmKey(Papers, "doi", TwentyTwoPapers());

        Assert.False(verdict.IsUsable);
        Assert.Equal("10.1000/03", Assert.Single(verdict.AlreadyStored).Value);
    }

    /// <summary>
    /// IN-6b. Accepting a key creates the uniqueness rule that makes it one, and a collection
    /// that already holds a collision reports those records rather than failing at the first
    /// write of an import that is already half done.
    /// </summary>
    [Fact]
    public void Making_a_column_a_key_reports_the_records_that_stand_in_the_way()
    {
        var storage = GivenPapers();

        foreach (var title in new[] { "one", "another" })
        {
            storage.Create(Papers, new Dictionary<string, object?>
            {
                ["title"] = title, ["doi"] = "10.1000/same", ["year"] = 2020L
            });
        }

        var effect = storage.InspectUnique(Papers, "doi");

        Assert.False(effect.IsPossible);
        Assert.Equal(2, effect.CollidingRecords);

        Assert.Throws<StorageValidationException>(() => storage.SetUnique(Papers, "doi", true));
        Assert.False(storage.GetCollectionDefinition(Papers)!.Column("doi")!.Unique);
    }

    [Fact]
    public void Making_a_column_a_key_refuses_the_next_record_that_would_repeat_it()
    {
        var storage = GivenPapers();

        storage.Create(Papers, new Dictionary<string, object?>
        {
            ["title"] = "one", ["doi"] = "10.1000/01", ["year"] = 2020L
        });

        Assert.True(storage.InspectUnique(Papers, "doi").IsPossible);

        storage.SetUnique(Papers, "doi", true);

        Assert.True(storage.GetCollectionDefinition(Papers)!.Column("doi")!.Unique);

        var thrown = Assert.Throws<StorageValidationException>(() => storage.Create(Papers,
            new Dictionary<string, object?> { ["title"] = "another", ["doi"] = "10.1000/01" }));

        Assert.Single(thrown.Errors.OfType<DuplicateValue>());
    }
}
