namespace TokkDb.Documents.Keys;

//The comparison the encoder is written against: unsigned byte order, shorter-is-smaller on a
//tie. Nothing in an index may compare keys any other way, or the ordering the encoding was
//built to guarantee stops being the ordering the tree is sorted by.
public sealed class KeyComparer : IComparer<byte[]> {
  public static readonly KeyComparer Instance = new();

  public int Compare(byte[]? left, byte[]? right) {
    return Compare(left.AsSpan(), right.AsSpan());
  }

  public static int Compare(ReadOnlySpan<byte> left, ReadOnlySpan<byte> right) {
    return left.SequenceCompareTo(right);
  }
}
