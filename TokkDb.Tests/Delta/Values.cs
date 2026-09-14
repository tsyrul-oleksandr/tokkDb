using TokkDb.Documents;
using TokkDb.Documents.Values;

namespace TokkDb.Tests.Delta;

//Short builders for document values, so a test reads as the document it describes.
internal static class Values {
  public static ObjectDocumentValue Obj(params (string Name, IDocumentValue Value)[] fields) {
    var values = new Dictionary<string, IDocumentValue>(StringComparer.Ordinal);
    foreach (var (name, value) in fields) {
      values[name] = value;
    }
    return new ObjectDocumentValue(values);
  }

  public static ArrayDocumentValue Arr(params IDocumentValue[] items) => new(items);
  public static ArrayDocumentValue Ints(params int[] items) => new(items.Select(Int).ToArray());
  public static IDocumentValue Int(int value) => new IntDocumentValue(value);
  public static IDocumentValue Long(long value) => new LongDocumentValue(value);
  public static IDocumentValue Str(string value) => new StringDocumentValue(value);
  public static IDocumentValue Dec(decimal value) => new DecimalDocumentValue(value);
  public static IDocumentValue Bool(bool value) => new BooleanDocumentValue(value);
  public static IDocumentValue Null() => new NullDocumentValue();
  public static IDocumentValue Moment(DateTime value) => new DateTimeDocumentValue(value);

  //A record-like document of the given number of int fields, named field00, field01, ...
  public static ObjectDocumentValue Record(int fields, int changedField = -1, int changedTo = 0) {
    var values = new Dictionary<string, IDocumentValue>(StringComparer.Ordinal);
    for (var i = 0; i < fields; i++) {
      values[$"field{i:D2}"] = i == changedField ? Int(changedTo) : Int(i * 7);
    }
    return new ObjectDocumentValue(values);
  }
}
