namespace TokkDb.Pages;

public enum PageType : byte {
  Root = 1,
  Data = 2,
  FreeSpace = 3,
  Overflow = 4,

  //The two node kinds of a B+Tree. They are separate types because they hold different
  //things: an interior node holds keys and children only, and every entry lives in a leaf.
  IndexInterior = 5,
  IndexLeaf = 6,
}
