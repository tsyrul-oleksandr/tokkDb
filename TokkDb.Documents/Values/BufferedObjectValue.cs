using TokkDb.Buffer;
using TokkDb.Documents.Serializers;
using TokkDb.Values;

namespace TokkDb.Documents.Values;

//A record as it lies on the page, answering for its fields without being parsed.
//
//This is what Phase 6 evaluates predicates against. It looks like an object to the
//expression tree — the same IFieldSource an ObjectDocumentValue is — so one evaluator serves
//both, and a query that names two columns reads two fields instead of building the whole
//document. A field is parsed the first time it is asked for and kept, because a predicate
//naming the same column twice is common and re-scanning the record for it is not free.
public sealed class BufferedObjectValue : IDocumentValue, IFieldSource {
  private readonly BufferSlice _buffer;
  private readonly int _position;
  private Dictionary<string, IDocumentValue> _read;

  public BufferedObjectValue(BufferSlice buffer, int position) {
    _buffer = buffer;
    _position = position;
  }

  public ValueTypeEnum Type => ValueTypeEnum.Object;

  public IDocumentValue GetField(string name) {
    _read ??= new Dictionary<string, IDocumentValue>(StringComparer.Ordinal);
    if (_read.TryGetValue(name, out var cached)) {
      return cached;
    }
    var value = DocumentFieldReader.Read(_buffer, _position, new DocumentFieldReader.FieldName(name));
    _read[name] = value;
    return value;
  }

  //A view over bytes somebody else wrote. It is never written back — the record it reads is
  //rewritten from its document, not from this (VR-12) — so the write half of IDocumentValue
  //says so rather than silently doing something partial.
  public void WriteValue(BufferWriter writer) {
    throw new NotSupportedException(
      $"{nameof(BufferedObjectValue)} is a read-only view of a stored record and cannot be written.");
  }

  public void ReadValue(BufferReader reader) {
    throw new NotSupportedException(
      $"{nameof(BufferedObjectValue)} reads fields on demand and is not filled by a reader.");
  }
}
