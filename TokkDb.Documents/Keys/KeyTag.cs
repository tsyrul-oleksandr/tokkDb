namespace TokkDb.Documents.Keys;

//The first byte of every encoded key. It is what makes a null sort before every value of
//every type without the comparer knowing which type it is looking at.
//
//Types that widen into one another share a tag and a width, so an Int key and a Long key of
//the same number encode identically: a column widened from Int to Long keeps the index it
//already has. Signed and unsigned integers do not share a tag, and neither does anything
//else — comparing keys of two different tags is defined but meaningless, and only ever
//happens through Null.
internal static class KeyTag {
  public const byte Null = 0x00;
  public const byte Boolean = 0x10;
  //SByte, Short, Int, Long.
  public const byte SignedInteger = 0x20;
  //Byte, UShort, UInt, ULong.
  public const byte UnsignedInteger = 0x21;
  //Float, Double. Float widens into Double exactly, so the order survives the widening.
  public const byte FloatingPoint = 0x30;
  public const byte Decimal = 0x40;
  public const byte DateTime = 0x50;
  public const byte TimeSpan = 0x51;
  public const byte Guid = 0x60;
  public const byte Ulid = 0x61;
  public const byte String = 0x70;

  //The widest type a tag stands for, for a value that is known only by its key. The tag order
  //is the cross-type order of OR-6: null, boolean, signed integer, unsigned integer, floating
  //point, decimal, date and time, time span, guid, ulid, text.
  public static TokkDb.Values.ValueTypeEnum ValueType(byte tag) {
    return tag switch {
      Null => TokkDb.Values.ValueTypeEnum.Null,
      Boolean => TokkDb.Values.ValueTypeEnum.Boolean,
      SignedInteger => TokkDb.Values.ValueTypeEnum.Long,
      UnsignedInteger => TokkDb.Values.ValueTypeEnum.ULong,
      FloatingPoint => TokkDb.Values.ValueTypeEnum.Double,
      Decimal => TokkDb.Values.ValueTypeEnum.Decimal,
      DateTime => TokkDb.Values.ValueTypeEnum.DateTime,
      TimeSpan => TokkDb.Values.ValueTypeEnum.TimeSpan,
      Guid => TokkDb.Values.ValueTypeEnum.Guid,
      Ulid => TokkDb.Values.ValueTypeEnum.Ulid,
      String => TokkDb.Values.ValueTypeEnum.String,
      _ => throw new ArgumentOutOfRangeException(nameof(tag), tag, "Not a key tag.")
    };
  }
}
