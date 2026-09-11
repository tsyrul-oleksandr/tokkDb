using TokkDb.Documents.Path.Expressions;
using TokkDb.Documents.Values;
using TokkDb.Values;

namespace TokkDb.Documents.Path.Normalization;

//One comparison in the shape the planner can act on: a column of the collection under
//filter, an operator, and constants. Nothing here refers to the record, so a conjunct can
//be matched against an index without looking at any data.
public sealed record QueryPredicate(
  string ColumnName,
  ComparisonOperator Operator,
  ValueTypeEnum ColumnType,
  IReadOnlyList<IDocumentValue> Constants) {

  public IDocumentValue Constant => Constants[0];

  //Whether an index over this column could answer the predicate at all. Every scalar has an
  //order-preserving encoding (D-3), so what is left is the shapes that have no order: an
  //object and an array cannot be an index key, and a predicate with no constant has nothing
  //to seek by.
  //
  //Ordered comparisons over Long, Decimal, DateTime and Guid used to be excluded here,
  //because those four had no document value and were stored as invariant text — and "250"
  //sorts below "40" as text. They have their own values now, so the exclusion is gone.
  public bool IsIndexable =>
    Constants.Count > 0
    && Constants.All(constant => constant is null or NullDocumentValue
      || constant.Type is not (ValueTypeEnum.Object or ValueTypeEnum.Array));

  public override string ToString() {
    return $"{ColumnName} {Operator} {string.Join(", ", Constants.Select(Describe))}";
  }

  private static string Describe(IDocumentValue value) {
    return value switch {
      StringDocumentValue text => $"'{text.Value}'",
      IntDocumentValue number => number.Value.ToString(),
      UIntDocumentValue number => number.Value.ToString(),
      BooleanDocumentValue flag => flag.Value ? "true" : "false",
      UlidDocumentValue identifier => identifier.Value.ToString(),
      LongDocumentValue number => number.Value.ToString(),
      DecimalDocumentValue number => number.Value.ToString(System.Globalization.CultureInfo.InvariantCulture),
      DateTimeDocumentValue moment => moment.Value.ToString("O", System.Globalization.CultureInfo.InvariantCulture),
      GuidDocumentValue identifier => identifier.Value.ToString("D"),
      NullDocumentValue => "null",
      _ => value?.Type.ToString() ?? "null"
    };
  }
}

//A predicate split into the part a planner can use and the part it cannot.
//
//The two halves are conjoined: a record satisfies the query when it satisfies every conjunct
//and the residual. That is what lets the planner narrow with the conjuncts — through an
//index where one covers the column — and re-check the rest per record. Nothing is lost and
//nothing is assumed, because the split is exact rather than approximate.
public sealed record NormalizedQuery(
  IReadOnlyList<QueryPredicate> Conjuncts,
  IExpression Residual) {

  public static readonly NormalizedQuery Everything = new([], null);

  //True when the whole predicate came out as conjuncts, so nothing has to be re-checked
  //against the record beyond what the access path already guarantees.
  public bool IsFullyNormalized => Residual is null;

  public bool IsEverything => Conjuncts.Count == 0 && Residual is null;

  public override string ToString() {
    var parts = Conjuncts.Select(conjunct => conjunct.ToString()).ToList();
    if (Residual is not null) {
      parts.Add($"residual({Residual.GetType().Name})");
    }
    return parts.Count == 0 ? "everything" : string.Join(" AND ", parts);
  }
}
