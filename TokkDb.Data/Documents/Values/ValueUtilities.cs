using TokkDb.Core;
using TokkDb.Values;

namespace TokkDb.Data.Documents.Values;

public static class ValueUtilities {
  public static int GetPrimitiveValueBytes(this ValueTypeEnum valueType) {
    return valueType switch {
      ValueTypeEnum.Null => TypesConstants.NullByteSize,
      ValueTypeEnum.Byte => TypesConstants.ByteByteSize,
      ValueTypeEnum.SByte => TypesConstants.SByteByteSize,
      ValueTypeEnum.Short => TypesConstants.ShortByteSize,
      ValueTypeEnum.UShort => TypesConstants.UShortByteSize,
      ValueTypeEnum.Int => TypesConstants.IntByteSize,
      ValueTypeEnum.UInt => TypesConstants.UIntByteSize,
      ValueTypeEnum.Long => TypesConstants.LongByteSize,
      ValueTypeEnum.ULong => TypesConstants.ULongByteSize,
      ValueTypeEnum.Float => TypesConstants.FloatByteSize,
      ValueTypeEnum.Double => TypesConstants.DoubleByteSize,
      ValueTypeEnum.Decimal => TypesConstants.DecimalByteSize,
      ValueTypeEnum.Boolean => TypesConstants.BooleanByteSize,
      ValueTypeEnum.DateTime => TypesConstants.DateTimeByteSize,
      ValueTypeEnum.TimeSpan => TypesConstants.TimeSpanByteSize,
      ValueTypeEnum.Guid => TypesConstants.GuidByteSize,
      ValueTypeEnum.Ulid => TypesConstants.UlidByteSize,
      _ => throw new ArgumentOutOfRangeException(nameof(valueType), valueType, null)
    };
  }
}
