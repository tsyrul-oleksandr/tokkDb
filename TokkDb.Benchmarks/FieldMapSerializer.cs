using TokkDb.Documents;
using TokkDb.Documents.Serializers;
using TokkDb.Documents.Values;

namespace TokkDb.Benchmarks;

//A record as a map of field names to document values, for workloads that write documents with
//exactly the fields a schema names, and whose shape is decided at run time.
public sealed class FieldMapSerializer : DocumentSerializer<Dictionary<string, IDocumentValue>> {
  protected override IDocumentValue Serialize(object value, Type type) {
    return value is Dictionary<string, IDocumentValue> fields
      ? new ObjectDocumentValue(new Dictionary<string, IDocumentValue>(fields, StringComparer.Ordinal))
      : base.Serialize(value, type);
  }

  protected override object Deserialize(IDocumentValue value, Type type) {
    return type == typeof(Dictionary<string, IDocumentValue>) && value is ObjectDocumentValue fields
      ? new Dictionary<string, IDocumentValue>(fields.Values, StringComparer.Ordinal)
      : base.Deserialize(value, type);
  }
}
