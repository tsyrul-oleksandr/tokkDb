using TokkDb.Documents.Path.Expressions;

namespace TokkDb.Documents.Path.Selectors;

public class FilterSelector : ISelector {

  public bool TryParse(string value, ref int index, out IExpression expression) {
    if (value[index] != '[') {
      expression = null;
      return false;
    }
    index++;
    expression = new ConditionExpression();
    while (value[index] != ']') {
      var left = DocumentPathParser.ParseFirst(value, ref index);
      var operation = DocumentPathParser.ParseFirst(value, ref index);
      var right = DocumentPathParser.ParseFirst(value, ref index);
    }
    return true;
  }
}
