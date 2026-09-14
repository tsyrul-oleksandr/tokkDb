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

  //RL-3a: the values are the keys a relation step projects, or will project when the plan runs.
  public KeySet Keys => Values as KeySet;

  //True only when every value seeks an exact key. A string is folded and may be truncated,
  //so a seek on one narrows the records to examine but does not settle the predicate. A key set
  //not yet projected settles nothing either: what it will hold is not known until it has.
  public override bool IsExact => Keys is { } keys
    ? keys.IsProjected && !keys.RequiresRecheck
    : Values.All(value => !KeyEncoder.Encode(value).RequiresRecheck);

  //The executor visits the values in key order, one descent each, so the records come off in
  //the index's key order whatever order the query wrote the values in (OrderRules).
  public override string Describe() {
    var kind = IsUnique ? "unique index" : "index";
    if (Keys is { } keys) {
      return $"{kind} seek on {CollectionName}.{ColumnName} for {keys.Describe()}";
    }
    return Values.Count == 1
      ? $"{kind} seek on {CollectionName}.{ColumnName}"
      : $"{kind} seek on {CollectionName}.{ColumnName} for {Values.Count} values";
  }
}

//OR-2a and Q-14: a walk of a whole index in its key order, chosen for the order and not for the
//predicate, which is applied as a filter over what the walk yields. An index range with both
//bounds open is exactly this walk and needs no new executor; it has a name of its own so that a
//reader can tell a walk chosen for the order from a range chosen for a bound, because the two
//cost differently — the walk reads entries until the page is full, and with an unselective
//predicate that is the whole index (PG-3a).
public sealed class OrderedIndexWalkPath : AccessPath {
  public OrderedIndexWalkPath(string collectionName, string columnName) : base(collectionName, []) {
    ColumnName = columnName;
  }

  public string ColumnName { get; }

  //Answers no conjunct at all: every one of them is re-checked against what the walk yields.
  public override bool IsExact => false;

  public override string Describe() {
    return $"ordered walk of the index on {CollectionName}.{ColumnName}";
  }
}

//RL-3c and Q-13: the second In strategy — one sequential pass over the collection, testing each
//record's key against the projected set, instead of one descent of the tree per key. Its own
//path rather than a full scan with a filter, so that a reader cannot mistake it for a scan chosen
//because no index existed, nor for the seeks it replaced.
public sealed class MembershipPassPath : AccessPath {
  public MembershipPassPath(string collectionName, string columnName, QueryPredicate predicate, KeySet keys,
      string reason) : base(collectionName, [predicate]) {
    ColumnName = columnName;
    Predicate = predicate;
    Keys = keys;
    Reason = reason;
  }

  public string ColumnName { get; }
  public QueryPredicate Predicate { get; }
  public KeySet Keys { get; }

  //The two figures the crossover compared, so the choice can be measured rather than assumed.
  public string Reason { get; }

  //The pass yields every live record; membership is tested per record by the predicate stage.
  public override bool IsExact => false;

  public override string Describe() {
    return $"membership pass over {CollectionName}.{ColumnName} against {Keys.Count} keys ({Reason})";
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
