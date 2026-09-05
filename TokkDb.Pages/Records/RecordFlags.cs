namespace TokkDb.Pages;

//The state of a stored image. Only Live is written in this pass — nothing supersedes or
//deletes anything yet — but the byte is there so that a version store can start setting it
//without the format changing.
[Flags]
public enum RecordFlags : byte {
  None = 0,
  Live = 1,
  Superseded = 2,
  Deleted = 4,

  //The record did not fit one page: what follows its header on the page is a pointer to the
  //overflow chain holding the body (ST-5).
  HasOverflow = 8
}
