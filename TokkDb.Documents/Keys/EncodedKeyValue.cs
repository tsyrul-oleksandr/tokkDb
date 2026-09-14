using TokkDb.Buffer;
using TokkDb.Values;

namespace TokkDb.Documents.Keys;

//A value known only by its encoded key (D-3).
//
//A semi-join projects the join column out of the far records and hands the keys to the near
//query as the constants of an In conjunct (RL-3a). They are kept as keys rather than as values
//because a key is what both strategies of RL-3b consume — the seek descends by it and the
//membership pass tests against it — and because a folded string key cannot be turned back into
//the text it came from. So this is a value that encodes to itself, compares as itself, and is
//never stored: the write half of IDocumentValue refuses rather than writing something partial.
public sealed class EncodedKeyValue : IDocumentValue {
  public EncodedKeyValue(EncodedKey key) {
    Key = key;
  }

  //A key as an index holds it. Every string key is folded (D-3), so a string key requires the
  //predicate to be re-checked against the record, exactly as the key it came from did.
  public EncodedKeyValue(byte[] bytes) : this(new EncodedKey(bytes, IsTruncated: false, IsFolded: IsFolded(bytes))) { }

  //Whether a key as an index holds it can find more than the value it was made from: a string
  //key is folded, and may be truncated, and nothing else is either.
  public static bool IsFolded(ReadOnlySpan<byte> key) {
    return key.Length > 0 && key[0] == KeyTag.String;
  }

  public EncodedKey Key { get; }

  //The widest type the key's tag stands for: a signed integer key is a Long whether it came
  //from an Int or a Long column, which is also what makes the two share an index.
  public ValueTypeEnum Type => KeyTag.ValueType(Key.Bytes[0]);

  public void WriteValue(BufferWriter writer) {
    throw new NotSupportedException(
      $"{nameof(EncodedKeyValue)} is a key, not a stored value, and cannot be written into a document.");
  }

  public void ReadValue(BufferReader reader) {
    throw new NotSupportedException(
      $"{nameof(EncodedKeyValue)} is a key, not a stored value, and cannot be read out of a document.");
  }

  public override string ToString() {
    return $"key {Convert.ToHexString(Key.Bytes)}";
  }
}
