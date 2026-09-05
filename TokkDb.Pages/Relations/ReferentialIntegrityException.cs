namespace TokkDb.Pages.Relations;

//A write that would leave a column referring to a record that is not there. Named the way a
//unique violation is: the relation, the column and the value that has no target.
public class ReferentialIntegrityException : Exception {
  public ReferentialIntegrityException(RelationDescriptor relation, object value)
    : base($"Relation '{relation.Name}' requires {relation.SourceCollection}.{relation.SourceColumn} " +
      $"to match a {relation.TargetCollection}.{relation.TargetColumn}, and no record holds " +
      $"{(value is null ? "null" : $"'{value}'")}.") {
    Relation = relation;
    Value = value;
  }

  public RelationDescriptor Relation { get; }
  public object Value { get; }
}
