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
      ValueTypeEnum.UInt => new UIntDocumentValue(),
      ValueTypeEnum.Boolean => new BooleanDocumentValue(),
      ValueTypeEnum.String => new StringDocumentValue(),
      ValueTypeEnum.Object => new ObjectDocumentValue(),
      ValueTypeEnum.Array => new ArrayDocumentValue(),
      ValueTypeEnum.Ulid => new UlidDocumentValue(),
      ValueTypeEnum.Long => new LongDocumentValue(),
      ValueTypeEnum.Decimal => new DecimalDocumentValue(),
      ValueTypeEnum.DateTime => new DateTimeDocumentValue(),
      ValueTypeEnum.Guid => new GuidDocumentValue(),
      _ => throw new NotSupportedException(
        $"The document format has no value for {type}.")
    };
  }
}
