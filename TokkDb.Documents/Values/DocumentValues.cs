using TokkDb.Values;

namespace TokkDb.Documents.Values;

//A CLR value as the document format holds it, for callers that have a value in hand rather
//than a typed object to serialise — a lookup by an indexed column, say.
public static class DocumentValues {
  public static IDocumentValue From(object value) {
    return value switch {
      null => new NullDocumentValue(),
      IDocumentValue already => already,
      bool flag => new BooleanDocumentValue(flag),
      int number => new IntDocumentValue(number),
      uint number => new UIntDocumentValue(number),
      string text => new StringDocumentValue(text),
      Ulid identifier => new UlidDocumentValue(identifier),
      //The four ValueTypeEnum declares with nothing implementing them: Long, Decimal,
      //DateTime and Guid. A column of one of those cannot be stored, so it cannot be looked
      //up either, and saying so here is better than encoding it to something it is not.
      _ => throw new NotSupportedException(
        $"{value.GetType().Name} has no document value, so it cannot be stored or looked up. " +
        $"Only {string.Join(", ", ValueTypeEnum.Boolean, ValueTypeEnum.Int, ValueTypeEnum.UInt,
          ValueTypeEnum.String, ValueTypeEnum.Ulid)} are implemented.")
    };
  }
}
