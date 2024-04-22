using TokkDb.Query.Tokenizers.Readers;
using TokkDb.Query.Tokenizers.Tokens;

namespace TokkDb.Query.Tokenizers.Conditions;

public class EqualStringCondition : ITokenCondition {
  public TokenType Type { get; }
  public string Value { get; set; }
  
  public EqualStringCondition(string value, TokenType type) {
    Type = type;
    Value = value;
  }
  
  public bool Match(TokenReader reader, out string result) {
    var index = 0;
    while (!reader.IsEnd()) {
      var ch = reader.Read(index);
      if (Value.Length <= index || !Value[index].Equals(ch)) {
        break;
      }
      index++;
    }
    result = Value;
    return Value.Length == index;
  }
}
