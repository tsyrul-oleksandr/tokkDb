using System.Collections.Immutable;
using TokkDb.Documents.Keys;
using TokkDb.Documents.Values;
using TokkDb.Pages.Managers;

namespace TokkDb.Pages.Query;

//A matching record as the ordering stage holds it: the keys of its order columns, its identity
//and where it lives. Never the document (OR-3) — the page is materialised from the addresses
//once the order has decided which records are on it.
public readonly record struct OrderedCandidate(byte[][] Keys, Ulid RecordId, DocumentAddress Address);

//OR-1: the comparison an order is applied by. One object, shared by the ordering stage and by
//anything that later has to compare two positions in the same order.
//
//Over the encoded keys and nothing else (OR-6a). They are the keys an index over the column
//holds, so a null sorts before every value ascending and after every value descending (OR-8),
//values of different types group in the tag order of the encoding (OR-6), and a sort and an index
//walk cannot disagree about a pair a CLR comparison would get wrong: negative zero against zero,
//NaN, decimals equal at different scales, and Guid, whose CompareTo is not the order of its bytes.
//Identity is the last key, in the direction of the last declared column, which is the order the
//composite (value, identity) key of an index is already in (Q-4).
public sealed class RecordOrder : IComparer<OrderedCandidate> {
  private readonly ImmutableArray<OrderColumn> _columns;
  //The first non-null tag seen in each column, and whether a different one followed it.
  private readonly byte[] _tags;
  private readonly bool[] _mixed;

  public RecordOrder(ImmutableArray<OrderColumn> columns) {
    if (columns.IsDefaultOrEmpty) {
      throw new ArgumentException("An order names at least one column.", nameof(columns));
    }
    _columns = columns;
    _tags = new byte[columns.Length];
    _mixed = new bool[columns.Length];
  }

  public ImmutableArray<OrderColumn> Columns => _columns;

  public int Compare(OrderedCandidate left, OrderedCandidate right) {
    return Compare(left.Keys, left.RecordId, right.Keys, right.RecordId);
  }

  //Two positions in the order: the keys of the declared columns, then the identity.
  public int Compare(byte[][] leftKeys, Ulid leftId, byte[][] rightKeys, Ulid rightId) {
    for (var i = 0; i < _columns.Length; i++) {
      var comparison = KeyComparer.Compare(leftKeys[i], rightKeys[i]);
      if (comparison != 0) {
        return Directed(comparison, _columns[i].Direction);
      }
    }
    return Directed(CompareIdentity(leftId, rightId), _columns[^1].Direction);
  }

  //The keys of one record, read from the fields as they lie on the page and migrated (DC-7), so
  //they are the values an index over the same columns would hold.
  public byte[][] Keys(IFieldSource fields, string collectionName, Ulid recordId) {
    var keys = new byte[_columns.Length][];
    for (var i = 0; i < keys.Length; i++) {
      var columnName = _columns[i].ColumnName;
      //A missing field is the null key, which is where an index puts it too (IndexCatalog.ReadColumn).
      var value = fields.GetField(columnName);
      try {
        keys[i] = KeyEncoder.Encode(value).Bytes;
      } catch (NotSupportedException) {
        throw new NotSupportedException(
          $"Record {recordId} of {collectionName} holds {value.Type} in '{columnName}', which has no " +
          $"ordering, so the query cannot be ordered by that column.");
      }
      Observe(i, keys[i][0]);
    }
    return keys;
  }

  //OR-6: the columns in which more than one type was seen, nulls aside — a null belongs to
  //every column and is where the encoding puts it, not a second type.
  public IReadOnlyList<string> MixedTypeColumns =>
    _columns.Where((_, i) => _mixed[i]).Select(column => column.ColumnName).ToArray();

  //What the ordering stage accounts a candidate at (OR-3, OR-7): the keys and the arrays that
  //hold them, the identity and the address, and nothing of the record — so the figure does not
  //grow with the width of the record.
  public static long BytesOf(byte[][] keys) {
    const int arrayOverhead = 24;
    const int candidate = 16 + 8 + 8;
    long bytes = candidate + arrayOverhead + 8L * keys.Length;
    foreach (var key in keys) {
      bytes += arrayOverhead + key.Length;
    }
    return bytes;
  }

  private void Observe(int column, byte tag) {
    if (tag == 0) {
      return;
    }
    if (_tags[column] == 0) {
      _tags[column] = tag;
    } else if (_tags[column] != tag) {
      _mixed[column] = true;
    }
  }

  private static int Directed(int comparison, OrderDirection direction) {
    return direction == OrderDirection.Descending ? -Math.Sign(comparison) : comparison;
  }

  //The bytes of the identity, which is what its key is (KeyEncoder.Encode(Ulid)), rather than
  //Ulid.CompareTo.
  private static int CompareIdentity(Ulid left, Ulid right) {
    Span<byte> leftBytes = stackalloc byte[16];
    Span<byte> rightBytes = stackalloc byte[16];
    left.TryWriteBytes(leftBytes);
    right.TryWriteBytes(rightBytes);
    return KeyComparer.Compare(leftBytes, rightBytes);
  }
}
