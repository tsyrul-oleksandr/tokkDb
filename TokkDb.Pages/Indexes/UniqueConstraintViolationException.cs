namespace TokkDb.Pages.Indexes;

//DC-4: a unique index names what went wrong. The column and the record already holding the
//value are both in the message, because "duplicate key" on its own tells the caller nothing
//it can act on.
public class UniqueConstraintViolationException : Exception {
  public UniqueConstraintViolationException(string collectionName, string columnName, object value,
      Ulid conflictingRecordId)
    : base($"Column '{columnName}' of collection '{collectionName}' is unique, and record " +
      $"{conflictingRecordId} already holds the value {Describe(value)}.") {
    CollectionName = collectionName;
    ColumnName = columnName;
    Value = value;
    ConflictingRecordId = conflictingRecordId;
  }

  public string CollectionName { get; }
  public string ColumnName { get; }
  public object Value { get; }
  public Ulid ConflictingRecordId { get; }

  private static string Describe(object value) {
    return value is null ? "null" : $"'{value}'";
  }
}
