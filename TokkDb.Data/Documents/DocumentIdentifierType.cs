using ValueType = TokkDb.Data.Documents.Values.ValueType;

namespace TokkDb.Data.Documents;

public enum DocumentIdentifierType {
  UInt = ValueType.UInt,
  ULong = ValueType.ULong,
  String = ValueType.String,
  Guid = ValueType.Guid,
  Ulid = ValueType.Ulid,
}
