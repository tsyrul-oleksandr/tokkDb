namespace TokkDb.Pages;

//V-1 and V-13, which settled D-5. What becomes of a record image once it stops being the
//current one. Persisted
//per collection in its catalogue document (HS-1) and set through
//TokkDbConnection.SetRetentionPolicy.
public enum RetentionPolicy {
  //The retired image's space returns to the free list at once and nothing of it is kept.
  None = 1,

  //Every write records a version in the collection's history collection (V-4): a node with the
  //delta from the version before it, and the full image where the keyframe rule keeps one
  //(V-1). The retired image's space returns to the free list exactly as under None; the live
  //image's previousVersion addresses the node of its own version (V-6).
  KeepVersions
}

//Why an image is being retired. It is what a version store will branch on when it takes over
//the seam.
public enum RemoveReason {
  //The record itself is going.
  Deleted = 1,

  //A newer image of the same record replaced this one.
  Superseded
}
