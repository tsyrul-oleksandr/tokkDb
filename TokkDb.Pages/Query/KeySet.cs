using System.Collections;
using TokkDb.Documents;
using TokkDb.Documents.Keys;

namespace TokkDb.Pages.Query;

//RL-6 and Q-9: the keys a semi-join projected, held compactly and capped by what holding them
//actually allocates.
//
//Three arrays and nothing per key: every key's bytes back to back in one arena, an int per key
//to find them, and an open-addressed table of ints to look one up. A HashSet<byte[]> would cost
//an object header, a length, a reference and a bucket for every key, which on a 9-byte integer
//key is more than the key itself — so a cap over the sum of the keys, called a memory cap, would
//under-count by that multiple. The cap here is compared against the bytes of the three arrays.
//
//Also the constants of the In conjunct the rewrite produces (RL-3a). The set is handed to the
//planner as a list of values, each an EncodedKeyValue over its key, so the seek strategy seeks by
//the key as it is and the membership strategy tests the key as it is, and neither has to turn a
//folded string key back into text it cannot recover.
//
//A plan carries a deferred set — one that will be projected when the plan runs — so that the
//plan can name the path the rewritten conjunct takes before the inner query has run (QM-2).
public sealed class KeySet : IReadOnlyList<IDocumentValue> {
  private const int InitialTableSize = 32;
  private const int InitialArenaBytes = 256;
  //What the runtime charges for an array before its elements: header and length.
  private const int ArrayOverheadBytes = 24;

  private byte[] _arena = [];
  //Where key i begins. Key i ends where key i + 1 begins, and _starts[Count] is the end of the
  //last key, so the array is always one longer than the count.
  private int[] _starts = [];
  //Slot to key index plus one, zero for an empty slot. A power of two, never more than half full.
  private int[] _table = [];
  private int _used;

  public KeySet(string origin, long capBytes) {
    ArgumentException.ThrowIfNullOrWhiteSpace(origin);
    Origin = origin;
    CapBytes = capBytes;
    IsProjected = true;
  }

  private KeySet(string origin) {
    Origin = origin;
    IsProjected = false;
  }

  //The placeholder a plan carries for a step it has resolved but not run.
  public static KeySet Deferred(string origin) {
    return new KeySet(origin);
  }

  //The relation the keys were projected across, for the cap's message and the plan's line.
  public string Origin { get; }

  public long CapBytes { get; }

  //False for the placeholder in a plan. It holds nothing and answers nothing; the executor
  //projects a set of its own in its place.
  public bool IsProjected { get; }

  public int Count { get; private set; }

  //D-3: whether any key is a folded string key, which finds more than the value it came from and
  //so leaves the conjunct to be re-checked against the record. Kept as keys arrive, so the
  //question costs nothing at the time it is asked.
  public bool RequiresRecheck { get; private set; }

  //The sum of the keys, which is not the memory they take.
  public long KeyBytes => _used;

  //What the set has allocated: the three arrays as they stand, headers included. This is the
  //figure the cap is compared against.
  public long AllocatedBytes =>
    Allocated(_arena.Length) + Allocated(sizeof(int) * (long)_starts.Length) + Allocated(sizeof(int) * (long)_table.Length);

  private static long Allocated(long elementBytes) {
    return elementBytes == 0 ? 0 : elementBytes + ArrayOverheadBytes;
  }

  //True when the key was not there before. The same key twice is one member (RL-3a's
  //deduplication), which is what stops a repeated far value seeking twice.
  public bool Add(ReadOnlySpan<byte> key) {
    if (!IsProjected) {
      throw new InvalidOperationException(
        $"The key set of {Origin} is a placeholder in a plan; the executor projects a set of its own.");
    }
    if (_table.Length == 0) {
      _table = new int[InitialTableSize];
      _starts = new int[InitialTableSize];
      _arena = new byte[InitialArenaBytes];
      RequireUnderCap();
    }
    var mask = _table.Length - 1;
    var slot = Hash(key) & mask;
    while (_table[slot] != 0) {
      if (KeyAtSpan(_table[slot] - 1).SequenceEqual(key)) {
        return false;
      }
      slot = (slot + 1) & mask;
    }
    Append(key);
    RequiresRecheck |= EncodedKeyValue.IsFolded(key);
    _table[slot] = Count;
    if (Count * 2 > _table.Length) {
      Rehash(_table.Length * 2);
    }
    RequireUnderCap();
    return true;
  }

  public bool Contains(ReadOnlySpan<byte> key) {
    if (_table.Length == 0) {
      return false;
    }
    var mask = _table.Length - 1;
    var slot = Hash(key) & mask;
    while (_table[slot] != 0) {
      if (KeyAtSpan(_table[slot] - 1).SequenceEqual(key)) {
        return true;
      }
      slot = (slot + 1) & mask;
    }
    return false;
  }

  public byte[] KeyAt(int index) {
    return KeyAtSpan(index).ToArray();
  }

  //Every key in the order of the encoding (D-3), which is the order an index holds them in. The
  //seek strategy visits them in this order, so that the records it yields come off in the index's
  //key order — which is what lets OR-2 take an order from an In seek.
  public IEnumerable<byte[]> SortedKeys() {
    var keys = new byte[Count][];
    for (var i = 0; i < Count; i++) {
      keys[i] = KeyAt(i);
    }
    Array.Sort(keys, KeyComparer.Instance);
    return keys;
  }

  public IDocumentValue this[int index] => index >= 0 && index < Count
    ? new EncodedKeyValue(KeyAt(index))
    : throw new ArgumentOutOfRangeException(nameof(index));

  public IEnumerator<IDocumentValue> GetEnumerator() {
    for (var i = 0; i < Count; i++) {
      yield return this[i];
    }
  }

  IEnumerator IEnumerable.GetEnumerator() {
    return GetEnumerator();
  }

  //What a plan or a report calls the set.
  public string Describe() {
    return IsProjected
      ? $"{Count} keys projected across {Origin}"
      : $"the keys projected across {Origin}";
  }

  public override string ToString() {
    return Describe();
  }

  private ReadOnlySpan<byte> KeyAtSpan(int index) {
    return _arena.AsSpan(_starts[index], _starts[index + 1] - _starts[index]);
  }

  private void Append(ReadOnlySpan<byte> key) {
    if (_used + key.Length > _arena.Length) {
      var grown = new byte[Math.Max(_arena.Length * 2, _used + key.Length)];
      _arena.AsSpan(0, _used).CopyTo(grown);
      _arena = grown;
    }
    if (Count + 1 >= _starts.Length) {
      Array.Resize(ref _starts, _starts.Length * 2);
    }
    key.CopyTo(_arena.AsSpan(_used));
    _starts[Count] = _used;
    _used += key.Length;
    Count++;
    _starts[Count] = _used;
  }

  private void Rehash(int size) {
    var table = new int[size];
    var mask = size - 1;
    for (var i = 0; i < Count; i++) {
      var slot = Hash(KeyAtSpan(i)) & mask;
      while (table[slot] != 0) {
        slot = (slot + 1) & mask;
      }
      table[slot] = i + 1;
    }
    _table = table;
  }

  private void RequireUnderCap() {
    if (AllocatedBytes > CapBytes) {
      throw new QueryCapExceededException(QueryCap.KeySet, Origin, AllocatedBytes, CapBytes);
    }
  }

  private static int Hash(ReadOnlySpan<byte> key) {
    var hash = new HashCode();
    hash.AddBytes(key);
    return hash.ToHashCode();
  }
}
