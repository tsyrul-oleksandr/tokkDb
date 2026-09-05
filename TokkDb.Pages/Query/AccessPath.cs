using TokkDb.Documents;
using TokkDb.Documents.Keys;
using TokkDb.Documents.Path.Normalization;

namespace TokkDb.Pages.Query;

//DC-5: how a query reaches its records. One of these is chosen per query, and it is the only
//thing that decides which records are read at all — everything else the predicate says is
//re-checked against the records this hands back.
public abstract class AccessPath {
  protected AccessPath(string collectionName, IReadOnlyList<QueryPredicate> answers) {
    CollectionName = collectionName;
    Answers = answers;
  }

  public string CollectionName { get; }

  //The conjuncts this path was chosen for. They are still re-checked unless the path answers
  //them exactly — see IsExact.
  public IReadOnlyList<QueryPredicate> Answers { get; }

  //Whether a record this path hands back is guaranteed to satisfy the conjuncts it answers.
  //D-3 is why this is not simply true for an equality seek: a folded or truncated key
  //matches more than the value it was made from, and the predicate has to be re-checked
  //against the record when it does.
  public abstract bool IsExact { get; }

  //What the diagnostics report (UI-4). Short enough to read in a log line and specific
  //enough to tell one plan from another.
  public abstract string Describe();

  public override string ToString() {
    return Describe();
  }
}

//The fallback: every live record of the collection, page by page. Chosen when no conjunct
//names an indexed column, which is the case DC-5 exists to make visible rather than to hide.
public sealed class FullScanPath : AccessPath {
  public FullScanPath(string collectionName, string reason) : base(collectionName, []) {
    Reason = reason;
  }

  //Why no index was used. A full scan that was chosen deliberately and one that happened
  //because a column was not indexed look identical in a timing, and only one of them is a
  //problem the reader can fix.
  public string Reason { get; }

  public override bool IsExact => false;

  public override string Describe() {
    return $"full scan of {CollectionName} ({Reason})";
  }
}

//A lookup by record identity through the primary index. Identity is not a column, so this
//comes from the query's id list rather than from a conjunct (D-1, D-2).
public sealed class PrimaryKeyPath : AccessPath {
  public PrimaryKeyPath(string collectionName, IReadOnlyList<Ulid> ids) : base(collectionName, []) {
    Ids = ids;
  }

  public IReadOnlyList<Ulid> Ids { get; }

  //A Ulid key is fixed width and never folded, so the entry the tree returns is the record
  //that was asked for.
  public override bool IsExact => true;

  public override string Describe() {
    return Ids.Count == 1
      ? $"primary index lookup on {CollectionName} by id"
      : $"primary index lookup on {CollectionName} by {Ids.Count} ids";
  }
}

//An equality seek into a secondary index: one descent per value, then the entries for that
//value, which sit together (D-3's composite key).
public sealed class IndexSeekPath : AccessPath {
  public IndexSeekPath(string collectionName, string columnName, QueryPredicate predicate, bool unique)
      : base(collectionName, [predicate]) {
    ColumnName = columnName;
    Predicate = predicate;
    IsUnique = unique;
  }

  public string ColumnName { get; }
  public QueryPredicate Predicate { get; }
  public bool IsUnique { get; }

  public IReadOnlyList<IDocumentValue> Values => Predicate.Constants;

  //True only when every value seeks an exact key. A string is folded and may be truncated,
  //so a seek on one narrows the records to examine but does not settle the predicate.
  public override bool IsExact =>
    Values.All(value => !KeyEncoder.Encode(value).RequiresRecheck);

  public override string Describe() {
    var kind = IsUnique ? "unique index" : "index";
    return Values.Count == 1
      ? $"{kind} seek on {CollectionName}.{ColumnName}"
      : $"{kind} seek on {CollectionName}.{ColumnName} for {Values.Count} values";
  }
}

//A range walk of a secondary index: one descent to the lower bound and then a walk of the
//linked leaves, which is what the B+Tree of Phase 5 was built to make sequential.
public sealed class IndexRangePath : AccessPath {
  public IndexRangePath(string collectionName, string columnName, IReadOnlyList<QueryPredicate> answers,
      byte[] from, byte[] to, string bounds) : base(collectionName, answers) {
    ColumnName = columnName;
    From = from;
    To = to;
    Bounds = bounds;
  }

  public string ColumnName { get; }

  //Half-open, and either end may be null for an open one — the same convention BPlusTree.Range
  //takes.
  public byte[] From { get; }
  public byte[] To { get; }

  //The bounds as they read in the query, for the diagnostics line.
  public string Bounds { get; }

  //A range is never exact: it is chosen precisely because it covers more than the predicate.
  public override bool IsExact => false;

  public override string Describe() {
    return $"index range on {CollectionName}.{ColumnName} [{Bounds}]";
  }
}
