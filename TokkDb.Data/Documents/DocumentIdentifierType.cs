using TokkDb.Data.Documents.Values;

namespace TokkDb.Data.Documents;

public enum DocumentIdentifierType {
  UInt = DocumentValueType.UInt,
  ULong = DocumentValueType.ULong,
  String = DocumentValueType.String,
  Guid = DocumentValueType.Guid,
  Ulid = DocumentValueType.Ulid,
}
