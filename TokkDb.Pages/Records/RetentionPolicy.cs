namespace TokkDb.Pages;

//D-5. What becomes of a record image once it stops being the current one.
public enum RetentionPolicy {
  //The only policy implemented in this pass: the retired image's space returns to the free
  //list at once and nothing of it is kept.
  None = 1,

  //Reserved. The retired image is kept and the image replacing it links back to it through
  //previousVersion. Declared now so that turning versioning on is a change of policy rather
  //than a change of format.
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
