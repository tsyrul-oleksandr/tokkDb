using TokkDb.Documents.Path.Expressions;

namespace TokkDb.Documents.Path.Selectors;

public interface ISelector {
  bool TryParse(string value, ref int index, out IExpression expression);
}
