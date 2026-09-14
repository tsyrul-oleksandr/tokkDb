namespace TokkDb.Documents.Delta;

//V-2. What a delta element does at its path. Add and Remove are for an object's field, Replace for
//a value of any kind, and Insert, RemoveAt and Move for an array element. The numbers are stored,
//so they are fixed here rather than left to the compiler.
public enum DeltaOperation {
  Add = 1,
  Remove = 2,
  Replace = 3,
  Insert = 4,
  RemoveAt = 5,
  Move = 6
}
