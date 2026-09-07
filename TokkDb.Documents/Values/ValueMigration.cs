using System.Globalization;
using TokkDb.Values;

namespace TokkDb.Documents.Values;

//A stored value read as a type its column did not declare when the value was written.
//
//Four of the types ValueTypeEnum declares have no IDocumentValue behind them — Long, Decimal,
//DateTime and Guid — so whatever stores them writes invariant text (see TypedKey). That
//convention is what a retype has to produce as well as consume: turning an Int32 column into
//an Int64 one means the value a record still holds as an IntDocumentValue has to read as the
//text a value of the new column is written as, or the two would not compare.
//
//A value that cannot be read as the new type becomes null rather than an error. A retype is a
//statement about what the column means from now on, and a record whose old value has no
//meaning under it has no value for that column — which is the same situation as a record
//written before the column existed, and is already what a read makes of that.
public static class ValueMigration {
  public static IDocumentValue To(ValueTypeEnum type, IDocumentValue value) {
    if (value is null or NullDocumentValue) {
      return new NullDocumentValue();
    }
    var text = AsText(value);
    if (text is null) {
      return new NullDocumentValue();
    }
    return type switch {
      ValueTypeEnum.String => new StringDocumentValue(text),
      ValueTypeEnum.Boolean => bool.TryParse(text, out var flag)
        ? new BooleanDocumentValue(flag) : Null(),
      ValueTypeEnum.Int => int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var number)
        ? new IntDocumentValue(number) : Null(),
      ValueTypeEnum.UInt => uint.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var number)
        ? new UIntDocumentValue(number) : Null(),
      ValueTypeEnum.Ulid => Ulid.TryParse(text, out var identifier)
        ? new UlidDocumentValue(identifier) : Null(),
      //The four with no value type of their own: stored as the invariant text of themselves,
      //parsed first so that text which is not one of them does not survive as though it were.
      ValueTypeEnum.Long => long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var number)
        ? new StringDocumentValue(number.ToString(CultureInfo.InvariantCulture)) : Null(),
      ValueTypeEnum.Decimal => decimal.TryParse(text, NumberStyles.Number, CultureInfo.InvariantCulture,
          out var number)
        ? new StringDocumentValue(number.ToString(CultureInfo.InvariantCulture)) : Null(),
      ValueTypeEnum.DateTime => DateTime.TryParse(text, CultureInfo.InvariantCulture,
          DateTimeStyles.RoundtripKind, out var moment)
        ? new StringDocumentValue(moment.ToString("O", CultureInfo.InvariantCulture)) : Null(),
      ValueTypeEnum.Guid => Guid.TryParse(text, out var identifier)
        ? new StringDocumentValue(identifier.ToString("D")) : Null(),
      //An object or an array is not a scalar and has no text to be read as one. A column
      //retyped to or from one of them keeps nothing.
      _ => Null()
    };
  }

  //The stored value as text, in the invariant form the four text-encoded types are written in
  //— so a value already stored as text is left exactly as it is and passes straight through a
  //retype between two of them.
  private static string AsText(IDocumentValue value) {
    return value switch {
      StringDocumentValue text => text.Value,
      BooleanDocumentValue flag => flag.Value ? "True" : "False",
      IntDocumentValue number => number.Value.ToString(CultureInfo.InvariantCulture),
      UIntDocumentValue number => number.Value.ToString(CultureInfo.InvariantCulture),
      UlidDocumentValue identifier => identifier.Value.ToString(),
      _ => null
    };
  }

  private static IDocumentValue Null() {
    return new NullDocumentValue();
  }
}
