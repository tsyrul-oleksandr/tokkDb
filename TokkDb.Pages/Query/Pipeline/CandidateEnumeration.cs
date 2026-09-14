using TokkDb.Documents.Keys;
using TokkDb.Pages.Indexes;
using TokkDb.Pages.Managers;

namespace TokkDb.Pages.Query.Pipeline;

//DG-4, stage one: walks the access path.
//
//Owns pages read, records examined and index probes, and nothing else counts them. The walk is
//lazy: a page is read when the walk reaches it and not before, so a Take that stops the walk
//stops the reading (PG-1). The cancellation token is observed here, per record, because a single
//walk can be the whole cost of a query (NF-4).
internal sealed class CandidateEnumeration {
  private readonly DataPageManager _data;
  private readonly IndexCatalog _indexes;
  private readonly PageManager _pages;
  private long? _pagesAtStart;
  private long? _pagesRead;

  public CandidateEnumeration(DataPageManager data, IndexCatalog indexes, PageManager pages) {
    _data = data;
    _indexes = indexes;
    _pages = pages;
  }

  //Every page the execution read from the moment the walk began, the pages a document was read
  //from included: a record the ordering stage kept by address is read again when the page is
  //materialised, and that read is the walk's read deferred. Closed by the pipeline once the last
  //document has been read; until then it is the count so far, which is what a partial report
  //carries (DG-3a).
  public long PagesRead => _pagesRead ?? (_pagesAtStart is { } start ? _pages.PageReadCount - start : 0);

  public int RecordsExamined { get; private set; }

  //NF-3: descents of a tree. One per distinct value of a seek, one per id of a lookup, one for a
  //range or an ordered walk, none for a scan or a membership pass.
  public int IndexProbes { get; private set; }

  public void Close() {
    _pagesRead = PagesRead;
  }

  public IEnumerable<Candidate> Walk(string collectionName, AccessPath path, CancellationToken cancellation) {
    _pagesAtStart ??= _pages.PageReadCount;
    foreach (var row in Rows(path)) {
      cancellation.ThrowIfCancellationRequested();
      RecordsExamined++;
      //An overflowed record is put back together here and nowhere else; one that fits its page
      //is read where it lies, with nothing copied. A record written under an older schema is
      //read through the migration on the way (DC-7), one field at a time like any other.
      var fields = _data.ReadFields(collectionName, row, out var header);
      yield return new Candidate(row, header, fields);
    }
  }

  private IEnumerable<DataRow> Rows(AccessPath path) {
    return path switch {
      PrimaryKeyPath primary => PrimaryKeyRows(primary),
      IndexSeekPath seek => SeekRows(seek),
      IndexRangePath range => RangeRows(range),
      OrderedIndexWalkPath walk => OrderedWalkRows(walk),
      FullScanPath scan => ScanRows(scan.CollectionName),
      MembershipPassPath pass => ScanRows(pass.CollectionName),
      _ => throw new NotSupportedException($"Access path '{path.GetType().Name}' cannot be executed.")
    };
  }

  private IEnumerable<DataRow> PrimaryKeyRows(PrimaryKeyPath path) {
    foreach (var id in path.Ids) {
      IndexProbes++;
      if (_data.FindLiveRow(path.CollectionName, id) is { } row) {
        yield return row;
      }
    }
  }

  //One descent per distinct key, in key order. Visiting the keys in the order of the encoding is
  //what makes an In seek yield the records in the index's key order, so that OR-2 can take an
  //order from it (OrderRules.KeyOrderOf); visiting each key once is what stops a folded string
  //key that two values share from yielding its records twice.
  private IEnumerable<DataRow> SeekRows(IndexSeekPath path) {
    var index = RequireIndex(path.CollectionName, path.ColumnName);
    foreach (var key in SeekKeys(path)) {
      IndexProbes++;
      foreach (var entry in index.Tree.Range(CompositeKey.ValuePrefix(key), CompositeKey.AboveValuePrefix(key))) {
        if (_data.LiveRowAt(entry.Address) is { } row) {
          yield return row;
        }
      }
    }
  }

  private static IEnumerable<EncodedKey> SeekKeys(IndexSeekPath path) {
    if (path.Keys is { } keys) {
      return keys.SortedKeys().Select(bytes => new EncodedKey(bytes));
    }
    var encoded = path.Values.Select(KeyEncoder.Encode).ToList();
    encoded.Sort((left, right) => KeyComparer.Compare(left.Bytes, right.Bytes));
    return Distinct(encoded);
  }

  private static IEnumerable<EncodedKey> Distinct(List<EncodedKey> sorted) {
    for (var i = 0; i < sorted.Count; i++) {
      if (i == 0 || KeyComparer.Compare(sorted[i - 1].Bytes, sorted[i].Bytes) != 0) {
        yield return sorted[i];
      }
    }
  }

  private IEnumerable<DataRow> RangeRows(IndexRangePath path) {
    var index = RequireIndex(path.CollectionName, path.ColumnName);
    IndexProbes++;
    //One descent to the lower bound and then a walk of the linked leaves — the sequential
    //read the B+Tree of Phase 5 exists to make possible.
    foreach (var entry in index.Tree.Range(path.From, path.To)) {
      if (_data.LiveRowAt(entry.Address) is { } row) {
        yield return row;
      }
    }
  }

  //OR-2a: the whole index, in key order. A range with both bounds open, and nothing more.
  private IEnumerable<DataRow> OrderedWalkRows(OrderedIndexWalkPath path) {
    var index = RequireIndex(path.CollectionName, path.ColumnName);
    IndexProbes++;
    foreach (var entry in index.Tree.Range(null, null)) {
      if (_data.LiveRowAt(entry.Address) is { } row) {
        yield return row;
      }
    }
  }

  private IEnumerable<DataRow> ScanRows(string collectionName) {
    foreach (var row in _data.GetAllRows(collectionName)) {
      //Dead images are on the page until compaction takes them; only the header is read to
      //recognise one.
      if (StoredRecordUtilities.ReadHeader(row.Buffer).IsLive) {
        yield return row;
      }
    }
  }

  private SecondaryIndex RequireIndex(string collectionName, string columnName) {
    return _indexes?.Find(collectionName, columnName)
      ?? throw new InvalidOperationException(
        $"The plan reads {collectionName}.{columnName} through an index that no longer exists.");
  }
}
