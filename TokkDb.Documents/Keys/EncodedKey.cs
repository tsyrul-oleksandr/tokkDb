namespace TokkDb.Documents.Keys;

//An index key, and whether finding it is the same thing as satisfying the predicate.
//
//Two encodings lose information on purpose. A long string is prefix-truncated so a node can
//hold a useful number of keys, and every string is case- and diacritic-folded (D-3) so the
//comparison never depends on the machine's culture. Both make the key a filter rather than
//an answer: a match narrows the search to candidate records, and the predicate is re-checked
//against the record itself before the record is returned.
public readonly record struct EncodedKey(byte[] Bytes, bool IsTruncated, bool IsFolded) {
  public EncodedKey(byte[] bytes) : this(bytes, false, false) { }

  //False only when the key is the whole truth about the value — every type but String.
  public bool RequiresRecheck => IsTruncated || IsFolded;
}
