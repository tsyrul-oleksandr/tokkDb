namespace TokkDb.Data.Documents.Values;

public static class ValueUtilities {
  public static int GetPrimitiveValueBytes(this ValueType valueType) {
    return valueType switch {
      ValueType.Null => ValueConstants.NullByteSite,
      ValueType.Byte => ValueConstants.ByteByteSite,
      ValueType.SByte => ValueConstants.SByteByteSite,
      ValueType.Short => ValueConstants.ShortByteSite,
      ValueType.UShort => ValueConstants.UShortByteSite,
      ValueType.Int => ValueConstants.IntByteSite,
      ValueType.UInt => ValueConstants.UIntByteSite,
      ValueType.Long => ValueConstants.LongByteSite,
      ValueType.ULong => ValueConstants.ULongByteSite,
      ValueType.Float => ValueConstants.FloatByteSite,
      ValueType.Double => ValueConstants.DoubleByteSite,
      ValueType.Decimal => ValueConstants.DecimalByteSite,
      ValueType.Boolean => ValueConstants.BooleanByteSite,
      ValueType.DateTime => ValueConstants.DateTimeByteSite,
      ValueType.TimeSpan => ValueConstants.TimeSpanByteSite,
      ValueType.Guid => ValueConstants.GuidByteSite,
      ValueType.Ulid => ValueConstants.UlidByteSite,
      _ => throw new ArgumentOutOfRangeException(nameof(valueType), valueType, null)
    };
  }
  
  public static IDocumentItem GetValue(this ValueType valueType) {
    return valueType switch {
      ValueType.Null => new NullValue(),
      ValueType.Byte => throw new NotImplementedException(),
      ValueType.SByte => throw new NotImplementedException(),
      ValueType.Short => throw new NotImplementedException(),
      ValueType.UShort => throw new NotImplementedException(),
      ValueType.Int => new IntValue(),
      ValueType.UInt => throw new NotImplementedException(),
      ValueType.Long => throw new NotImplementedException(),
      ValueType.ULong => throw new NotImplementedException(),
      ValueType.Float => throw new NotImplementedException(),
      ValueType.Double => throw new NotImplementedException(),
      ValueType.Decimal =>null,
      ValueType.Boolean => throw new NotImplementedException(),
      ValueType.DateTime => throw new NotImplementedException(),
      ValueType.TimeSpan => throw new NotImplementedException(),
      ValueType.Guid => throw new NotImplementedException(),
      ValueType.Ulid => throw new NotImplementedException(),
      ValueType.String => new StringValue(),
      ValueType.Object => new ObjectValue(),
      ValueType.Array => new ArrayValue(),
      _ => throw new ArgumentOutOfRangeException(nameof(valueType), valueType, null)
    };
  }
}
