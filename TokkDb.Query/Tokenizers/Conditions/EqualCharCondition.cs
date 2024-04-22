using TokkDb.Query.Tokenizers.Readers;
using TokkDb.Query.Tokenizers.Tokens;

namespace TokkDb.Query.Tokenizers.Conditions;

public class EqualCharCondition : ITokenCondition {
  public char Value { get; }
  public TokenType Type { get; }

  public EqualCharCondition(char value, TokenType type) {
    Value = value;
    Type = type;
  }

  public bool Match(TokenReader reader, out string result) {
    if (reader.IsEnd()) {
      result = null;
      return false;
    }
    var ch = reader.Read();
    if (ch != Value) {
      result = null;
      return false;
    }
    result = ch.ToString();
    reader.MovePosition();
    return true;
  }
}
