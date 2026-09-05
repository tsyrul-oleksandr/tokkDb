using TokkDb.Documents;
using TokkDb.Documents.Keys;
using TokkDb.Documents.Path.Expressions;
using TokkDb.Documents.Path.Normalization;
using TokkDb.Documents.Values;
using TokkDb.Pages.Indexes;
using TokkDb.Values;

namespace TokkDb.Pages.Query;

//DC-5: matches the conjuncts of a normalised query against the indexes that exist and picks
//one access path.
//
//The order is equality, then range, then a full scan, and it is a rule rather than a cost
//model on purpose. A cost model needs statistics — how many records carry each value — and
//nothing collects them yet; a rule that is written down is at least predictable, which is
//what makes the chosen path testable against an expectation. Where the rule is wrong it is
//wrong visibly, in the diagnostics line, rather than in a timing nobody attributes.
//
//Only one path is chosen. Intersecting two indexes would need the record identities from
//each, and the composite key of D-3 gives them in index order rather than in identity order,
//so a merge would have to sort. That is a Phase 8 concern if the benchmarks ask for it.
public static class QueryPlanner {

  public static QueryPlan Plan(string collectionName, NormalizedQuery query,
      IReadOnlyList<Ulid> ids, IndexCatalog indexes) {
    query ??= NormalizedQuery.Everything;
    var path = ChoosePath(collectionName, query, ids, indexes);
    //Everything the path does not settle. A path that answers a conjunct exactly takes it out
    //of the per-record work; one that only narrows by it leaves it in, which is what D-3's
    //re-check rule requires of a folded or truncated key.
    var filters = path.IsExact
      ? query.Conjuncts.Where(conjunct => !path.Answers.Contains(conjunct)).ToArray()
      : query.Conjuncts.ToArray();
    return new QueryPlan(collectionName, path, filters, query.Residual);
  }

  private static AccessPath ChoosePath(string collectionName, NormalizedQuery query,
      IReadOnlyList<Ulid> ids, IndexCatalog indexes) {
    //Identity first, and not because it is fastest by measurement: a query that names the
    //records it wants has already answered the question the access path exists to answer.
    if (ids is { Count: > 0 }) {
      return new PrimaryKeyPath(collectionName, ids);
    }
    if (query.Conjuncts.Count == 0) {
      return new FullScanPath(collectionName,
        query.Residual is null ? "no predicate" : "the predicate names no column an index could be chosen by");
    }
    return SeekPath(collectionName, query, indexes)
      ?? RangePath(collectionName, query, indexes)
      ?? new FullScanPath(collectionName, NoIndexReason(collectionName, query, indexes));
  }

  //Equality first: it names the entries it wants instead of a stretch of them, so it reads as
  //many records as there are matches and no more. A unique index is preferred over an
  //ordinary one because it matches at most one record by construction.
  private static AccessPath SeekPath(string collectionName, NormalizedQuery query, IndexCatalog indexes) {
    IndexSeekPath best = null;
    foreach (var conjunct in query.Conjuncts) {
      if (conjunct.Operator is not (ComparisonOperator.Equal or ComparisonOperator.In)) {
        continue;
      }
      if (Index(collectionName, conjunct, indexes) is not { } index || !CanEncode(conjunct)) {
        continue;
      }
      var candidate = new IndexSeekPath(collectionName, conjunct.ColumnName, conjunct,
        index.Descriptor.Unique);
      if (IsBetter(candidate, best)) {
        best = candidate;
      }
    }
    return best;
  }

  //Fewer descents and fewer records: a unique index beats an ordinary one, and among equals a
  //single value beats an IN over several.
  private static bool IsBetter(IndexSeekPath candidate, IndexSeekPath best) {
    if (best == null) {
      return true;
    }
    if (candidate.IsUnique != best.IsUnique) {
      return candidate.IsUnique;
    }
    return candidate.Values.Count < best.Values.Count;
  }

  //Then range. The bounds on one column are gathered together, so "between" — which arrives
  //as two conjuncts — is walked as one bounded stretch of leaves rather than from one bound
  //to the end of the tree.
  private static AccessPath RangePath(string collectionName, NormalizedQuery query, IndexCatalog indexes) {
    IndexRangePath best = null;
    foreach (var column in query.Conjuncts.Select(conjunct => conjunct.ColumnName).Distinct(StringComparer.Ordinal)) {
      var bounds = query.Conjuncts
        .Where(conjunct => conjunct.ColumnName == column && conjunct.Operator.IsOrdered())
        .Where(conjunct => Index(collectionName, conjunct, indexes) is not null && CanEncode(conjunct))
        .ToList();
      if (bounds.Count == 0) {
        continue;
      }
      var candidate = BuildRange(collectionName, column, bounds);
      //A bounded range reads a stretch; a half-open one reads the tail of the tree. Prefer
      //the one that is bounded at both ends.
      if (best == null || (Bounded(candidate) && !Bounded(best))) {
        best = candidate;
      }
    }
    return best;
  }

  private static bool Bounded(IndexRangePath path) {
    return path.From != null && path.To != null;
  }

  private static IndexRangePath BuildRange(string collectionName, string column,
      IReadOnlyList<QueryPredicate> bounds) {
    byte[] from = null;
    byte[] to = null;
    var described = new List<string>();
    foreach (var bound in bounds) {
      var key = KeyEncoder.Encode(bound.Constant);
      switch (bound.Operator) {
        //A composite entry for one value sorts at or above the value's prefix and below the
        //bound just past it, so an inclusive lower bound starts at the prefix and an
        //exclusive one starts past it (D-3).
        case ComparisonOperator.GreaterOrEqual:
          from = Tighter(from, CompositeKey.ValuePrefix(key), higher: true);
          break;
        case ComparisonOperator.Greater:
          from = Tighter(from, CompositeKey.AboveValuePrefix(key), higher: true);
          break;
        case ComparisonOperator.LessOrEqual:
          to = Tighter(to, CompositeKey.AboveValuePrefix(key), higher: false);
          break;
        case ComparisonOperator.Less:
          to = Tighter(to, CompositeKey.ValuePrefix(key), higher: false);
          break;
      }
      described.Add($"{bound.Operator} {Describe(bound.Constant)}");
    }
    return new IndexRangePath(collectionName, column, bounds, from, to, string.Join(", ", described));
  }

  //Two bounds of the same direction on one column narrow to the tighter of them. It happens
  //when a query says both "after 2000" and "after 2010", and reading from the looser one
  //would be correct but would walk leaves that cannot match.
  private static byte[] Tighter(byte[] current, byte[] candidate, bool higher) {
    if (current == null || candidate == null) {
      return candidate ?? current;
    }
    var comparison = KeyComparer.Compare(current, candidate);
    return (higher ? comparison < 0 : comparison > 0) ? candidate : current;
  }

  private static SecondaryIndex Index(string collectionName, QueryPredicate conjunct, IndexCatalog indexes) {
    //A conjunct an index cannot answer is skipped rather than used wrongly. The Phase 4
    //finding is what makes this necessary: four column types are stored as text, and "250"
    //sorts below "40" as text, so an ordered comparison over one of them must not become a
    //range.
    if (!conjunct.IsIndexable) {
      return null;
    }
    return indexes?.Find(collectionName, conjunct.ColumnName);
  }

  //An index key is a scalar. A column holding an object or an array has no index to begin
  //with (IndexCatalog refuses one), but a predicate could still name one.
  private static bool CanEncode(QueryPredicate conjunct) {
    return conjunct.Constants.Count > 0
      && conjunct.Constants.All(constant => constant is null or NullDocumentValue
        || constant.Type is not (ValueTypeEnum.Object or ValueTypeEnum.Array));
  }

  //Why the scan happened, in the terms the reader can act on: an index that does not exist is
  //a different problem from a predicate no index could answer.
  private static string NoIndexReason(string collectionName, NormalizedQuery query, IndexCatalog indexes) {
    var unindexed = query.Conjuncts
      .Where(conjunct => indexes?.Find(collectionName, conjunct.ColumnName) is null)
      .Select(conjunct => conjunct.ColumnName)
      .Distinct(StringComparer.Ordinal)
      .ToList();
    if (unindexed.Count > 0) {
      return $"no index on {string.Join(", ", unindexed)}";
    }
    if (query.Conjuncts.Any(conjunct => !conjunct.IsIndexable)) {
      return "the indexed columns are compared with an operator their stored form does not order by";
    }
    return "no conjunct an index can answer";
  }

  private static string Describe(IDocumentValue value) {
    return value switch {
      StringDocumentValue text => $"'{text.Value}'",
      IntDocumentValue number => number.Value.ToString(),
      UIntDocumentValue number => number.Value.ToString(),
      BooleanDocumentValue flag => flag.Value ? "true" : "false",
      UlidDocumentValue identifier => identifier.Value.ToString(),
      _ => "null"
    };
  }
}
