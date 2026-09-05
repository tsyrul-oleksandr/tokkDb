using System.Globalization;
using TokkDb.Documents.Values;
using TokkDb.Values;

namespace TokkDb.Documents.Keys;

//A stored value compared as the type its column declares.
//
//Four of the types ValueTypeEnum declares have no IDocumentValue behind them — Long,
//Decimal, DateTime and Guid — so whatever stores them writes invariant text instead. Text
//does not order the way a number does: "250" is below "40" as a string and above it as a
//decimal. So a comparison cannot be made against the stored form alone; it needs the type
//the column declares, and that is what this puts back.
//
//The encoding is D-3's, which means a comparison here and a range over an index are the
//same order by construction rather than by agreement.
public static class TypedKey {
  //Whether values of this type are stored as themselves or as text standing in for them.
  public static bool IsTextEncoded(ValueTypeEnum type) {
    return type is ValueTypeEnum.Long or ValueTypeEnum.Decimal or ValueTypeEnum.DateTime
      or ValueTypeEnum.Guid;
  }

  //Null when the value cannot be compared as that type at all: a document value of the wrong
  //shape, or text that does not parse. A predicate over such a value is simply not satisfied,
  //which is what makes a wrong-typed record invisible to a query rather than fatal to it.
  public static EncodedKey? Encode(ValueTypeEnum type, IDocumentValue value) {
    if (value is null or NullDocumentValue) {
      return KeyEncoder.EncodeNull();
    }
    if (!IsTextEncoded(type)) {
      try {
        return KeyEncoder.Encode(value);
      } catch (NotSupportedException) {
        return null;
      }
    }
    if (value is not StringDocumentValue text) {
      return null;
    }
    return type switch {
      ValueTypeEnum.Long => long.TryParse(text.Value, NumberStyles.Integer, CultureInfo.InvariantCulture,
        out var number) ? KeyEncoder.Encode(number) : null,
      ValueTypeEnum.Decimal => decimal.TryParse(text.Value, NumberStyles.Number, CultureInfo.InvariantCulture,
        out var number) ? KeyEncoder.Encode(number) : null,
      ValueTypeEnum.DateTime => DateTime.TryParse(text.Value, CultureInfo.InvariantCulture,
        DateTimeStyles.RoundtripKind, out var moment) ? KeyEncoder.Encode(moment) : null,
      ValueTypeEnum.Guid => Guid.TryParse(text.Value, out var identifier) ? KeyEncoder.Encode(identifier) : null,
      _ => null
    };
  }
}
