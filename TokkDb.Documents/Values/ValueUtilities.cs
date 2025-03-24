using TokkDb.Buffer;
using TokkDb.Values;

namespace TokkDb.Documents.Values;

public static class ValueUtilities {
  
  public static IDocumentValue Read(this BufferReader bufferReader) {
    var type = (ValueTypeEnum)bufferReader.ReadByte();
    var value = CreateValueType(type);
    value.ReadValue(bufferReader);
    return value;
  }
  
  public static void Write(this BufferWriter bufferWriter, IDocumentValue value) {
    bufferWriter.WriteByte((byte)value.Type);
    value.WriteValue(bufferWriter);
  }

  private static IDocumentValue CreateValueType(ValueTypeEnum type) {
    return type switch {
      ValueTypeEnum.Null => new NullDocumentValue(),
      ValueTypeEnum.Int => new IntDocumentValue(),
      ValueTypeEnum.String => new StringDocumentValue(),
      ValueTypeEnum.Object => new ObjectDocumentValue(),
      ValueTypeEnum.Array => new ArrayDocumentValue(),
      _ => throw new NotImplementedException()
    };
  }
}
