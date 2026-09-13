namespace TokkDb.Assistant.Storage;

/// <summary>
/// Storing many rows at once, knowing which of them are already there.
///
/// IN-5 keeps two questions apart, and this is the second one. <i>Is this the same kind of
/// thing</i> is schema matching and belongs to the mapping step, which decides where rows go.
/// <i>Have I seen this row before</i> is record identity, and it is answered here, in C#, with no
/// model involved: a natural key where one exists (IN-6), a fingerprint where none does (IN-6a).
///
/// <b>Everything is classified before the transaction opens.</b> IN-9a says so without a "where
/// practical", and three existing requirements are what make the strict version affordable: IN-1
/// parses and profiles the whole file first, so nothing is gained by deferring validation; AG-10
/// serialises writes, so the one conflict that normally cannot be predicted - someone else
/// creating a duplicate mid-import - cannot arise; and SC-9 makes "is this row already stored"
/// one ordered pass over an index rather than ten thousand random reads. The transaction then
/// contains only rows already known to be valid, so the only thing that can fail inside it is a
/// genuine fault, and SC-5's all-or-nothing holds with no exception clause.
///
/// <b>Row failure and operation failure are different things</b> (IN-9). A malformed row is
/// reported with its line number and does not stop the other ninety-seven committing. A fault or
/// a cancellation rolls the whole thing back. The report tells them apart, rather than both
/// reading as "some rows did not make it".
///
/// It is not part of <see cref="IStorage"/>. Everything here is composed from the contract's own
/// operations, so both implementations get it and neither can implement it differently.
/// </summary>
public sealed class RecordImporter
{
    private readonly IStorage _storage;

    public RecordImporter(IStorage storage)
    {
        _storage = storage ?? throw new ArgumentNullException(nameof(storage));
    }

    /// <summary>
    /// Checks whether a column can be the natural key of a collection: unique and non-null
    /// <b>across the incoming rows and against the records already stored</b> (IN-6).
    ///
    /// Both halves matter, and the second one was a gap. A key that is unique in one spreadsheet
    /// says nothing about the twelve hundred records already in the collection, and an import
    /// that discovered the collision at the first write would have to be undone rather than
    /// refused. Neither half asks the user anything: a candidate that collides is rejected
    /// deterministically and the model is asked for another.
    /// </summary>
    public KeyVerdict ConfirmKey(string collectionName, string columnName, IReadOnlyList<ImportRow> rows)
    {
        ArgumentNullException.ThrowIfNull(rows);

        var definition = Require(collectionName);
        var column = definition.Column(columnName)
            ?? throw new UnknownColumnException(definition.Name, columnName);

        var seen = new Dictionary<object, int>();
        var repeated = new List<(int Row, int First, object Value)>();
        var empty = new List<int>();
        var values = new List<object?>();

        for (var i = 0; i < rows.Count; i++)
        {
            if (rows[i].Unreadable is not null) continue;

            var value = rows[i].Fields.GetValueOrDefault(column.Name);

            if (value is null)
            {
                empty.Add(i);
                continue;
            }

            if (!ColumnTypes.TryCanonicalise(column.Type, value, out var canonical) || canonical is null)
            {
                empty.Add(i);
                continue;
            }

            var key = TextComparison.AsCompared(column.Type, canonical)!;

            if (!seen.TryAdd(key, i))
            {
                repeated.Add((i, seen[key], canonical));
                continue;
            }

            values.Add(canonical);
        }

        // One pass for the whole candidate set rather than a lookup per row (SC-9).
        var stored = _storage.MatchValues(definition.Name, column.Name, values);

        return new KeyVerdict(
            definition.Name,
            column.Name,
            repeated,
            empty,
            [.. stored.Found],
            _storage.CountNeedingAttention(definition.Name, column.Name));
    }

    /// <summary>
    /// Runs an import: classifies every row, then writes the ones that survive, in one unit of
    /// work.
    /// </summary>
    /// <exception cref="UnknownCollectionException">There is no such collection.</exception>
    /// <exception cref="UnknownColumnException">The key column is not one of its columns.</exception>
    /// <exception cref="InvalidDefinitionException">
    /// The policy cannot be carried out: <see cref="MergePolicy.UpdateExisting"/> without a
    /// natural key, which IN-7 refuses with that reason rather than silently behaving as
    /// <see cref="MergePolicy.AddAll"/>.
    /// </exception>
    public ImportReport Import(ImportRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        var definition = Require(request.CollectionName);

        if (request.Policy is MergePolicy.UpdateExisting && request.KeyColumn is null)
        {
            throw new InvalidDefinitionException(
                "merge policy",
                "Bringing records up to date needs something that says which record is which. A " +
                "fingerprint covers every value, so a row that changed has a different one and " +
                "cannot be matched to the record it changed.");
        }

        var key = request.KeyColumn is null
            ? null
            : definition.Column(request.KeyColumn)
              ?? throw new UnknownColumnException(definition.Name, request.KeyColumn);

        // The fingerprint is only the fallback. A collection with a natural key does not grow a
        // column it will never read (IN-6).
        var usesFingerprint = key is null;
        var versionChanged = false;

        if (usesFingerprint)
        {
            (definition, versionChanged) = EnsureFingerprints(definition);
        }

        var dispositions = new RowDisposition?[request.Rows.Count];
        var judged = new Dictionary<string, object?>?[request.Rows.Count];

        Read(definition, request.Rows, dispositions, judged);

        if (usesFingerprint)
        {
            Fingerprint(definition, judged);
        }

        MatchAgainstEachOther(definition, request, key, dispositions, judged);
        MatchAgainstStored(definition, request, key, dispositions, judged);
        CheckUniqueColumns(definition, key, dispositions, judged);

        Write(definition, request, key, dispositions, judged);

        return new ImportReport(
            definition.Name,
            request.Policy,
            key?.Name,
            [.. dispositions.Select(static (disposition, index) =>
                disposition ?? new RowDisposition(index, null, RowOutcome.Rejected, new RowUnreadable("Nothing happened to it.")))],
            versionChanged);
    }

    // ---- Before the transaction -------------------------------------------------------------

    /// <summary>
    /// Every row judged against the collection's columns, with no storage consulted. Types,
    /// required values and unknown columns are all decidable from the definition alone, so this
    /// costs nothing and removes most of what could have failed inside the transaction.
    /// </summary>
    private static void Read(
        CollectionDefinition definition,
        IReadOnlyList<ImportRow> rows,
        RowDisposition?[] dispositions,
        Dictionary<string, object?>?[] judged)
    {
        for (var i = 0; i < rows.Count; i++)
        {
            var row = rows[i];

            if (row.Unreadable is { } detail)
            {
                dispositions[i] = new RowDisposition(
                    i, row.LineNumber, RowOutcome.Rejected, new RowUnreadable(detail));
                continue;
            }

            if (!SharedRules.TryForCreate(definition, row.Fields, out var values, out var errors))
            {
                dispositions[i] = new RowDisposition(
                    i, row.LineNumber, RowOutcome.Rejected, new RowDoesNotFit(errors));
                continue;
            }

            judged[i] = values;
        }
    }

    private static void Fingerprint(CollectionDefinition definition, Dictionary<string, object?>?[] judged)
    {
        foreach (var values in judged)
        {
            if (values is null) continue;

            values[Fingerprints.ColumnName] = Fingerprints.Of(values, definition.Columns);
        }
    }

    /// <summary>
    /// Rows that are duplicates of an earlier row of the same file. A second import is the
    /// obvious case; a file that lists the same conference twice is the one people forget, and
    /// checking only against storage would store it twice on the first import and never again.
    /// </summary>
    private void MatchAgainstEachOther(
        CollectionDefinition definition,
        ImportRequest request,
        ColumnDefinition? key,
        RowDisposition?[] dispositions,
        Dictionary<string, object?>?[] judged)
    {
        if (request.Policy is MergePolicy.AddAll) return;

        var column = key?.Name ?? Fingerprints.ColumnName;
        var type = key?.Type ?? ColumnType.Text;
        var seen = new Dictionary<object, int>();

        for (var i = 0; i < judged.Length; i++)
        {
            if (judged[i] is not { } values) continue;

            var value = values.GetValueOrDefault(column);
            if (value is null) continue;

            var compared = TextComparison.AsCompared(type, value)!;

            if (seen.TryAdd(compared, i)) continue;

            // The later row loses, because file order is the order a person reads the file in
            // and the first of two identical rows is the one they would point at.
            dispositions[i] = new RowDisposition(
                i,
                request.Rows[i].LineNumber,
                RowOutcome.Skipped,
                new MatchedEarlierRow(Described(key), request.Rows[seen[compared]].LineNumber ?? seen[compared], value));

            judged[i] = null;
        }
    }

    /// <summary>
    /// Rows that are already stored, asked in one ordered pass over the index rather than one
    /// lookup per row (SC-9, IN-6a).
    /// </summary>
    private void MatchAgainstStored(
        CollectionDefinition definition,
        ImportRequest request,
        ColumnDefinition? key,
        RowDisposition?[] dispositions,
        Dictionary<string, object?>?[] judged)
    {
        if (request.Policy is MergePolicy.AddAll) return;

        var column = key?.Name ?? Fingerprints.ColumnName;

        var values = judged
            .Where(static row => row is not null)
            .Select(row => row!.GetValueOrDefault(column))
            .Where(static value => value is not null)
            .ToList();

        if (values.Count == 0) return;

        var stored = _storage.MatchValues(definition.Name, column, values);

        for (var i = 0; i < judged.Length; i++)
        {
            if (judged[i] is not { } row) continue;

            var value = row.GetValueOrDefault(column);
            if (value is null || stored.Holder(value) is not { } existing) continue;

            var by = Described(key);

            dispositions[i] = request.Policy switch
            {
                MergePolicy.UpdateExisting => new RowDisposition(
                    i, request.Rows[i].LineNumber, RowOutcome.Updated, null, existing),

                MergePolicy.Ask => new RowDisposition(
                    i, request.Rows[i].LineNumber, RowOutcome.Skipped, new AwaitingDecision(by, existing), existing),

                _ => new RowDisposition(
                    i,
                    request.Rows[i].LineNumber,
                    RowOutcome.Skipped,
                    new MatchedStoredRecord(by, existing, value),
                    existing)
            };

            // Only an update still has writing to do; the other two are decided.
            if (request.Policy is not MergePolicy.UpdateExisting) judged[i] = null;
        }
    }

    /// <summary>
    /// Every other unique column, asked the same way. IN-9's write conflicts, found before the
    /// transaction rather than at the write that would have rolled it back.
    /// </summary>
    private void CheckUniqueColumns(
        CollectionDefinition definition,
        ColumnDefinition? key,
        RowDisposition?[] dispositions,
        Dictionary<string, object?>?[] judged)
    {
        foreach (var column in definition.Columns)
        {
            if (!column.Unique) continue;
            if (key is not null && StorageNames.Same(column.Name, key.Name)) continue;

            var values = judged
                .Where(static row => row is not null)
                .Select(row => row!.GetValueOrDefault(column.Name))
                .Where(static value => value is not null)
                .ToList();

            if (values.Count == 0) continue;

            var stored = _storage.MatchValues(definition.Name, column.Name, values);
            var seen = new Dictionary<object, int>();

            for (var i = 0; i < judged.Length; i++)
            {
                if (judged[i] is not { } row) continue;

                var value = row.GetValueOrDefault(column.Name);
                if (value is null) continue;

                if (dispositions[i] is { Outcome: RowOutcome.Updated }) continue;

                if (stored.Holder(value) is { } holder)
                {
                    dispositions[i] = new RowDisposition(
                        i, null, RowOutcome.Rejected, new UniqueValueTaken(column.Name, value, holder));
                    judged[i] = null;
                    continue;
                }

                var compared = TextComparison.AsCompared(column.Type, value)!;

                if (!seen.TryAdd(compared, i))
                {
                    dispositions[i] = new RowDisposition(
                        i, null, RowOutcome.Rejected, new MatchedEarlierRow(column.Name, seen[compared], value));
                    judged[i] = null;
                }
            }
        }
    }

    // ---- The transaction --------------------------------------------------------------------

    /// <summary>
    /// One unit of work for everything that survived classification (SC-5). A fault inside it
    /// leaves nothing: not the inserts that had already happened, not the updates, not the
    /// fingerprint column if this import added it.
    /// </summary>
    private void Write(
        CollectionDefinition definition,
        ImportRequest request,
        ColumnDefinition? key,
        RowDisposition?[] dispositions,
        Dictionary<string, object?>?[] judged)
    {
        _storage.InUnitOfWork(() =>
        {
            for (var i = 0; i < judged.Length; i++)
            {
                if (judged[i] is not { } values) continue;

                if (dispositions[i] is { Outcome: RowOutcome.Updated, RecordId: { } existing })
                {
                    // A collection that keeps fingerprints keeps them right: the row being
                    // brought up to date has different values, so it has a different fingerprint,
                    // and leaving the old one would make the next import see a row that is no
                    // longer there.
                    if (definition.Column(Fingerprints.ColumnName) is not null)
                    {
                        values[Fingerprints.ColumnName] = Fingerprints.Of(values, definition.Columns);
                    }

                    _storage.Update(new StorageRecord(existing, definition.Name, values));
                    continue;
                }

                var record = _storage.Create(definition.Name, values);

                dispositions[i] = new RowDisposition(
                    i, request.Rows[i].LineNumber, RowOutcome.Inserted, null, record.Id);
            }
        });
    }

    // ---- The fingerprint column -------------------------------------------------------------

    /// <summary>
    /// Makes sure the collection has somewhere to keep a fingerprint, and that what is already
    /// there has one.
    ///
    /// The backfill is the part that is easy to leave out and expensive to leave out: a
    /// collection filled before any import has no fingerprints, so every row of the first import
    /// would look new and the same file imported twice afterwards would still store nothing
    /// twice - but the records from before would be duplicated once, silently. It is one pass,
    /// once, inside the import's own unit of work.
    /// </summary>
    private (CollectionDefinition Definition, bool VersionChanged) EnsureFingerprints(CollectionDefinition definition)
    {
        var recorded = definition.Metadata.TryGetValue(Fingerprints.VersionMetadataKey, out var written)
                       && int.TryParse(written, out var version)
            ? version
            : (int?)null;

        var changed = recorded is not null && recorded != Fingerprints.Version;

        if (definition.Column(Fingerprints.ColumnName) is not null && !changed && recorded is not null)
        {
            return (definition, false);
        }

        _storage.InUnitOfWork(() =>
        {
            if (definition.Column(Fingerprints.ColumnName) is null)
            {
                _storage.AddColumn(definition.Name, Fingerprints.Column());
            }

            var current = _storage.GetCollectionDefinition(definition.Name)!;

            foreach (var record in _storage.GetAll(definition.Name))
            {
                var held = record[Fingerprints.ColumnName] as string;

                // Recomputed where the normalisation moved, so that the mismatch is repaired
                // rather than reported for ever.
                if (held is not null && Fingerprints.VersionOf(held) == Fingerprints.Version) continue;

                var fields = new Dictionary<string, object?>(record.Fields, StorageNames.Comparer)
                {
                    [Fingerprints.ColumnName] = Fingerprints.Of(record.Fields, current.Columns)
                };

                _storage.Update(new StorageRecord(record.Id, current.Name, fields, record.NeedsAttention));
            }

            var metadata = new Dictionary<string, string?>(current.Metadata, StorageNames.Comparer)
            {
                [Fingerprints.VersionMetadataKey] = Fingerprints.Version.ToString()
            };

            _storage.SetMetadata(current.Name, metadata);
        });

        return (_storage.GetCollectionDefinition(definition.Name)!, changed);
    }

    private static string Described(ColumnDefinition? key) => key?.Name ?? "everything in the row";

    private CollectionDefinition Require(string collectionName) =>
        _storage.GetCollectionDefinition(collectionName)
        ?? throw new UnknownCollectionException(collectionName);
}

/// <summary>
/// Whether a column can be the natural key of a collection, and what stands in the way (IN-6).
///
/// Every part of it is evidence rather than a verdict, because the caller has to be able to say
/// why: "it repeats in your file at rows 12 and 40" and "two records you already have hold that
/// DOI" are different problems with different answers.
/// </summary>
public sealed record KeyVerdict(
    string CollectionName,
    string ColumnName,
    IReadOnlyList<(int Row, int First, object Value)> RepeatedInFile,
    IReadOnlyList<int> MissingInFile,
    IReadOnlyList<ValueMatch> AlreadyStored,
    int ValuesNeedingAttention)
{
    /// <summary>
    /// Whether this column identifies a record. It has to be unique and non-null in the file and
    /// unheld by anything already stored.
    /// </summary>
    public bool IsUsable =>
        RepeatedInFile.Count == 0 && MissingInFile.Count == 0 && AlreadyStored.Count == 0;

    /// <summary>
    /// Whether the answer is reliable. SC-6d: uniqueness in a column holding more than one type
    /// is per type, so a column with values still waiting to be converted cannot be checked
    /// across all of them, and a key accepted on one is accepted on incomplete evidence.
    /// </summary>
    public bool IsCertain => ValuesNeedingAttention == 0;

    public string Describe()
    {
        if (RepeatedInFile.Count > 0)
        {
            var (row, first, value) = RepeatedInFile[0];
            return $"'{ColumnName}' repeats: rows {first} and {row} both hold {ColumnTypes.Render(value)}.";
        }

        if (MissingInFile.Count > 0)
        {
            return $"'{ColumnName}' has no value in {MissingInFile.Count} of the rows.";
        }

        if (AlreadyStored.Count > 0)
        {
            return $"{AlreadyStored.Count} of these values are already held by records in " +
                   $"'{CollectionName}'.";
        }

        return IsCertain
            ? $"'{ColumnName}' identifies a record."
            : $"'{ColumnName}' identifies a record, as far as can be told: {ValuesNeedingAttention} " +
              $"values in it are still waiting to be made sense of.";
    }
}
