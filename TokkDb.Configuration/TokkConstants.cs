namespace TokkDb.Configuration;

public class TokkConstants {
  public const uint RootPageIndex = 0;

  //The page size of an existing database is read from its root page. This value is only
  //the size a brand new file is created with.
  public const ushort DefaultPageSize = 8192;
}
