using TokkDb.Core;

namespace TokkDb.Data.Documents.Values;

public static class ValueUtilities {
  public static int GetPrimitiveValueBytes(this DocumentValueType valueType) {
    return valueType switch {
      DocumentValueType.Null => TypesConstants.NullByteSize,
      DocumentValueType.Byte => TypesConstants.ByteByteSize,
      DocumentValueType.SByte => TypesConstants.SByteByteSize,
      DocumentValueType.Short => TypesConstants.ShortByteSize,
      DocumentValueType.UShort => TypesConstants.UShortByteSize,
      DocumentValueType.Int => TypesConstants.IntByteSize,
      DocumentValueType.UInt => TypesConstants.UIntByteSize,
      DocumentValueType.Long => TypesConstants.LongByteSize,
      DocumentValueType.ULong => TypesConstants.ULongByteSize,
      DocumentValueType.Float => TypesConstants.FloatByteSize,
      DocumentValueType.Double => TypesConstants.DoubleByteSize,
      DocumentValueType.Decimal => TypesConstants.DecimalByteSize,
      DocumentValueType.Boolean => TypesConstants.BooleanByteSize,
      DocumentValueType.DateTime => TypesConstants.DateTimeByteSize,
      DocumentValueType.TimeSpan => TypesConstants.TimeSpanByteSize,
      DocumentValueType.Guid => TypesConstants.GuidByteSize,
      DocumentValueType.Ulid => TypesConstants.UlidByteSize,
      _ => throw new ArgumentOutOfRangeException(nameof(valueType), valueType, null)
    };
  }
  
  public static IDocumentValue GetValue(this DocumentValueType valueType) {
    return valueType switch {
      DocumentValueType.Null => new NullValue(),
      DocumentValueType.Byte => throw new NotImplementedException(),
      DocumentValueType.SByte => throw new NotImplementedException(),
      DocumentValueType.Short => throw new NotImplementedException(),
      DocumentValueType.UShort => throw new NotImplementedException(),
      DocumentValueType.Int => new IntValue(),
      DocumentValueType.UInt => throw new NotImplementedException(),
      DocumentValueType.Long => throw new NotImplementedException(),
      DocumentValueType.ULong => throw new NotImplementedException(),
      DocumentValueType.Float => throw new NotImplementedException(),
      DocumentValueType.Double => throw new NotImplementedException(),
      DocumentValueType.Decimal =>null,
      DocumentValueType.Boolean => throw new NotImplementedException(),
      DocumentValueType.DateTime => throw new NotImplementedException(),
      DocumentValueType.TimeSpan => throw new NotImplementedException(),
      DocumentValueType.Guid => throw new NotImplementedException(),
      DocumentValueType.Ulid => throw new NotImplementedException(),
      DocumentValueType.String => new StringValue(),
      DocumentValueType.Object => new ObjectValue(),
      DocumentValueType.Array => new ArrayValue(),
      _ => throw new ArgumentOutOfRangeException(nameof(valueType), valueType, null)
    };
  }
}
