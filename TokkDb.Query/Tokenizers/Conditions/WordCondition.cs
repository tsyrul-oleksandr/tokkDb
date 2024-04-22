using TokkDb.Query.Tokenizers.Readers;
using TokkDb.Query.Tokenizers.Tokens;

namespace TokkDb.Query.Tokenizers.Conditions;

public class WordCondition : ITokenCondition {
  public TokenType Type => TokenType.Word;
  public string Value { get; protected set; } = string.Empty;
  
  public bool Match(TokenReader reader, out string result) {
    while (!reader.IsEnd()) {
      var ch = reader.Read();
      if (char.IsLetter(ch)) {
        reader.MovePosition();
        Value += ch;
      } else {
        break;
      }
    }
    result = Value;
    return Value != string.Empty;
  }
}
