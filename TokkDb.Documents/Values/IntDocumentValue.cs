using System.Text.Json;
using System.Text.Json.Nodes;
using TokkDb.Buffer;
using TokkDb.Values;

namespace TokkDb.Documents.Values;

public class IntDocumentValue : JsonNode, IDocumentValue {
  public ValueTypeEnum Type => ValueTypeEnum.Int;
  public int Value { get; set; }

  public virtual void WriteValue(BufferWriter writer) {
    this.va
    writer.WriteInt(Value);
  }
  public virtual void ReadValue(BufferReader reader) {
    Value = reader.ReadInt();
  }

  public override void WriteTo(Utf8JsonWriter writer, JsonSerializerOptions options = null) {
    throw new NotImplementedException();
  }
}
