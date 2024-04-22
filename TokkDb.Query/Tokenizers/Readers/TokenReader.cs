namespace TokkDb.Query.Tokenizers.Readers;

public class TokenReader {
  public string Value { get; set; }
  public int Position { get; set; }
  
  public TokenReader(string value) {
    Value = value;
  }
  
  public bool IsEnd(int index = default) {
    return Value.Length == (Position + index);
  }

  public char Read() {
    return Value[Position];
  }
  
  public char Read(int index) {
    return Value[Position + index];
  }

  public void MovePosition(int count = 1) {
    Position += count;
  }
}
