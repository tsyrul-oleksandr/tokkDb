using TokkDb.Documents.Path.Expressions;

namespace TokkDb.Documents.Path.Selectors;

public class ContextSelector : ISelector {

  public bool TryParse(string value, ref int index, out IExpression expression) {
    if (value[index] != '@') {
      expression = null;
      return false;
    }
    index++;
    expression = new ContextExpression();
    return true;
  }
}
