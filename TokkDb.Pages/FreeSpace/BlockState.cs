namespace TokkDb.Pages;

//ST-1. What a block of the file is being used for. Recorded per page in the owning
//collection's free-space structure.
public enum BlockState : byte {
  //Allocated to the collection and holding no live record: the whole page is available.
  Free = 1,

  //Holding at least one live record, possibly with room for more.
  Occupied,

  //Its checksum did not verify. Never handed out, so a damaged page cannot swallow a write.
  Damaged,

  //Allocated but not available for records — the free-space pages and the index pages.
  Reserved,

  //An index page a merge emptied. It is not Free, because Free with nothing reclaimable is
  //how a spare overflow page is found and a record must never land on a tree node; the
  //index takes these back itself before it allocates. Recorded rather than remembered, so a
  //crash between the merge and the next split does not leak the page.
  Retired
}
