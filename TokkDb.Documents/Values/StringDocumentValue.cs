using TokkDb.Buffer;
using TokkDb.Values;

namespace TokkDb.Documents.Values;

public class StringDocumentValue : IDocumentValue {
  public ValueTypeEnum Type => ValueTypeEnum.String;
  public string Value { get; set; }

  public StringDocumentValue() : this(string.Empty) { }
  public StringDocumentValue(string value) {
    Value = value;
  }

  public virtual void WriteValue(BufferWriter writer) {
    writer.WriteString(Value);
  }
  public virtual void ReadValue(BufferReader reader) {
    Value = reader.ReadString();
  }
}
