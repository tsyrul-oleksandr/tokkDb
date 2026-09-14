namespace TokkDb.Pages.Versions;

//V-4. A versioned collection's history lives in a collection of its own, named "_history:"
//followed by the collection's identifier — reserved, so no user name can collide with it and
//no adapter lists it, and named by identifier rather than by name so that renaming a
//collection, should that ever exist, would not orphan its history.
public static class HistoryCollections {
  public const string Prefix = "_history:";

  public static string NameFor(Ulid collectionId) {
    return Prefix + collectionId;
  }

  public static bool IsHistoryName(string name) {
    return name is not null && name.StartsWith(Prefix, StringComparison.Ordinal);
  }
}
