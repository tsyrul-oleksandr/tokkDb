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
      long number => new LongDocumentValue(number),
      decimal number => new DecimalDocumentValue(number),
      DateTime moment => new DateTimeDocumentValue(moment),
      Guid identifier => new GuidDocumentValue(identifier),
      _ => throw new NotSupportedException(
        $"{value.GetType().Name} has no document value, so it cannot be stored or looked up.")
    };
  }
}
