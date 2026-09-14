namespace TokkDb.Pages.Versions;

//HS-5 and V-9. What a version node records. Baseline is the head of a record that was written
//before its collection kept versions, captured at the first change after switch-on (WV-4);
//Delete is a tombstone; Restore is a child of the version it restored.
public enum VersionKind {
  Baseline = 1,
  Insert = 2,
  Update = 3,
  Delete = 4,
  Restore = 5
}
