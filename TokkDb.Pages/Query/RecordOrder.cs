using System.Collections.Immutable;
using TokkDb.Documents.Keys;
using TokkDb.Documents.Values;
using TokkDb.Pages.Managers;

namespace TokkDb.Pages.Query;

//A matching record as the ordering holds it: the keys of its order columns, its identity and
//where it lives. Never the document (OR-3) — the page is materialised from the addresses once the
//order has decided which records are on it.
internal readonly record struct OrderedCandidate(byte[][] Keys, Ulid RecordId, DocumentAddress Address);

//OR-1: the comparison an order is applied by.
//
//Over the encoded keys and nothing else (OR-6a). They are the keys an index over the column
//holds, so a null sorts before every value (OR-8), values of different types group in the tag
//order of the encoding (OR-6), and a sort and an index walk cannot disagree about a pair a CLR
//comparison would get wrong. Identity is the last key, in the direction of the last declared
//column, which is the order the composite (value, identity) key of an index is already in (Q-4).
internal sealed class RecordOrder : IComparer<OrderedCandidate> {
  private readonly ImmutableArray<OrderColumn> _columns;

  public RecordOrder(ImmutableArray<OrderColumn> columns) {
    _columns = columns;
  }

  public int Compare(OrderedCandidate left, OrderedCandidate right) {
    for (var i = 0; i < _columns.Length; i++) {
      var comparison = KeyComparer.Compare(left.Keys[i], right.Keys[i]);
      if (comparison != 0) {
        return Directed(comparison, _columns[i].Direction);
      }
    }
    return Directed(CompareIdentity(left.RecordId, right.RecordId), _columns[^1].Direction);
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
    }
    return keys;
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
