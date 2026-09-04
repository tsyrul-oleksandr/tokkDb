namespace TokkDb.Pages;

//The reserved collections and the prefix that keeps user names out of their way.
public static class SystemCollections {
  public const char ReservedPrefix = '_';

  public const string Collections = "_collections";
  public const string Indexes = "_indexes";
  public const string Relations = "_relations";
  public const string SemanticTypes = "_semanticTypes";
  public const string DisplayRules = "_displayRules";
  public const string Settings = "_settings";

  //_collections comes first: it has to exist before anything can be described in it.
  public static readonly IReadOnlyList<string> All =
    [Collections, Indexes, Relations, SemanticTypes, DisplayRules, Settings];

  public static readonly IReadOnlyDictionary<string, string> Descriptions = new Dictionary<string, string> {
    [Collections] = "The catalogue of collections. It describes itself.",
    [Indexes] = "Index descriptors.",
    [Relations] = "Relation definitions between collections.",
    [SemanticTypes] = "The semantic type registry.",
    [DisplayRules] = "Display rules for collections and columns.",
    [Settings] = "Per-collection application and AI settings."
  };

  public static bool IsReservedName(string name) {
    return !string.IsNullOrEmpty(name) && name[0] == ReservedPrefix;
  }
}
