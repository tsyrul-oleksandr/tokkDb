using TokkDb.Query.Tokenizers.Readers;
using TokkDb.Query.Tokenizers.Tokens;

namespace TokkDb.Query.Tokenizers.Conditions;

public interface ITokenCondition {
  TokenType Type { get; }
  bool Match(TokenReader reader, out string value);
}
