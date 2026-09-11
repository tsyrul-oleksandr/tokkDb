using System.Globalization;
using TokkDb.Values;

namespace TokkDb.Documents.Values;

//A stored value read as a type its column did not declare when the value was written.
//
//What a retype means, in other words: the column says the values mean something else from now
//on, and a record written before it said so has to be read that way. Conversion goes through
//the invariant text of the value, which is what makes an Int32 column widened to Int64 keep
//its numbers and a number retyped to a string keep its digits.
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
    //Already the type asked for: nothing to reinterpret, and a decimal's scale or a
    //DateTime's kind survives untouched rather than going through text and back.
    if (value.Type == type) {
      return value;
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
      ValueTypeEnum.Long => long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var number)
        ? new LongDocumentValue(number) : Null(),
      ValueTypeEnum.Decimal => decimal.TryParse(text, NumberStyles.Number, CultureInfo.InvariantCulture,
          out var number)
        ? new DecimalDocumentValue(number) : Null(),
      ValueTypeEnum.DateTime => DateTime.TryParse(text, CultureInfo.InvariantCulture,
          DateTimeStyles.RoundtripKind, out var moment)
        ? new DateTimeDocumentValue(moment) : Null(),
      ValueTypeEnum.Guid => Guid.TryParse(text, out var guid) ? new GuidDocumentValue(guid) : Null(),
      ValueTypeEnum.Ulid => Ulid.TryParse(text, out var identifier)
        ? new UlidDocumentValue(identifier) : Null(),
      //An object or an array is not a scalar and has no text to be read as one. A column
      //retyped to or from one of them keeps nothing.
      _ => Null()
    };
  }

  //The stored value as text, in the invariant form each type parses back from.
  private static string AsText(IDocumentValue value) {
    return value switch {
      StringDocumentValue text => text.Value,
      BooleanDocumentValue flag => flag.Value ? "True" : "False",
      IntDocumentValue number => number.Value.ToString(CultureInfo.InvariantCulture),
      UIntDocumentValue number => number.Value.ToString(CultureInfo.InvariantCulture),
      LongDocumentValue number => number.Value.ToString(CultureInfo.InvariantCulture),
      DecimalDocumentValue number => number.Value.ToString(CultureInfo.InvariantCulture),
      DateTimeDocumentValue moment => moment.Value.ToString("O", CultureInfo.InvariantCulture),
      GuidDocumentValue identifier => identifier.Value.ToString("D"),
      UlidDocumentValue identifier => identifier.Value.ToString(),
      _ => null
    };
  }

  private static IDocumentValue Null() {
    return new NullDocumentValue();
  }
}
