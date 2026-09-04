using TokkDb.Documents.Path.Expressions;
using TokkDb.Documents.Path.Selectors;

namespace TokkDb.Documents.Path;

public class DocumentPathParser {
  private static readonly ISelector[] Selectors = [
    new RootSelector(),
    new PropertySelector(),
    new ContextSelector(),
    new FilterSelector()
  ];
  
  public static IExpression Parse(string value) {
    var index = 0;
    return Parse(value, ref index);
  }

  public static IExpression Parse(string value, ref int index) {
    IExpression last = null;
    for (; index < value.Length;) {
      var exp = ParseFirst(value, ref index);
      if (exp == null) {
        index++;
      } else {
        exp.Parent = last;
        last = exp;
      }
    }
    return last;
  }
  
  public static IExpression ParseFirst(string value, ref int index) {
    SkipSpace(value, ref index);
    foreach (var selector in Selectors) {
      if (selector.TryParse(value, ref index, out var exp)) {
        return exp;
      }
    }
    return null;
  }

  private static void SkipSpace(string value, ref int index) {
    while (true) {
      if (value[index] == ' ') {
        index++;
        continue;
      }
      break;
    }
  }
}
