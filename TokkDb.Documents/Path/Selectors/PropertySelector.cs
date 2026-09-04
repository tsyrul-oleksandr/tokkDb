using TokkDb.Documents.Path.Expressions;

namespace TokkDb.Documents.Path.Selectors;

public class PropertySelector : ISelector {
  public static readonly char[] SpecialChars = ['$', '.'];

  public bool TryParse(string value, ref int index, out IExpression expression) {
    if (value[index] != '.') {
      expression = null;
      return false;
    }
    index++;
    var propertyName = GetWord(value, ref index);
    expression = new PropertyExpression(propertyName);
    return true;
  }
  
  private static string GetWord(string exp, ref int index) {
    var startIndex = index;
    for (; index < exp.Length; index++) {
      if (SpecialChars.Contains(exp[index])) {
        break;
      }
    }
    var word = exp.Substring(startIndex, index - startIndex);
    //index--;
    return word;
  }
}
