using TokkDb.Core.Reader;

namespace TokkDb.Data.Documents.Values;

public class ValueReader {
  public TokkBinaryReader Reader { get; }

  public ValueReader(TokkBinaryReader reader) {
    Reader = reader;
  }
}
