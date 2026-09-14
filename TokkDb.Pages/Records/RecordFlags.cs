namespace TokkDb.Pages;

//The state of a stored image. A new image is Live; RetireRow marks the image an update
//replaces Superseded and the one a delete removes Deleted, before it frees the slot, so that
//keeping the image instead is a matter of not freeing it rather than of writing something
//different (VR-12).
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
