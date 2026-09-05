using TokkDb.Documents;
using TokkDb.Documents.Keys;
using TokkDb.Pages.Managers;

namespace TokkDb.Pages.Indexes;

//DC-4: an index over one column of one collection. The same B+Tree as the primary index over
//a different root and a different key — D-3's composite (encodedValue, recordId), which is
//what lets a column with repeated values be indexed without a posting list hanging off each
//distinct value. All the entries for one value are simply neighbours in the tree.
public class SecondaryIndex {
  private readonly BPlusTree _tree;

  public SecondaryIndex(IndexDescriptor descriptor, BPlusTree tree) {
    Descriptor = descriptor;
    _tree = tree;
  }

  public IndexDescriptor Descriptor { get; }

  public BPlusTree Tree => _tree;

  //The entry carries the record's address as well as its identity, so a lookup by an indexed
  //value reads the tree and then the data page, without a second descent through the primary
  //index. It stays correct because a record that moves is removed from here and put back
  //(VR-12 rewrites rather than mutates), and a record that only moves inside its page keeps
  //its slot (D-2).
  public void Add(IDocumentValue value, Ulid recordId, DocumentAddress address) {
    _tree.Upsert(CompositeKey.Create(KeyEncoder.Encode(value), KeyEncoder.Encode(recordId)), address);
  }

  public void Remove(IDocumentValue value, Ulid recordId) {
    _tree.Delete(CompositeKey.Create(KeyEncoder.Encode(value), KeyEncoder.Encode(recordId)));
  }

  //Every record carrying this value, in identity order — the entries for one value are
  //contiguous, so this is a descent and then a walk of as many entries as there are matches.
  public IEnumerable<(Ulid RecordId, DocumentAddress Address)> Find(IDocumentValue value) {
    var key = KeyEncoder.Encode(value);
    foreach (var entry in _tree.Range(CompositeKey.ValuePrefix(key), CompositeKey.AboveValuePrefix(key))) {
      yield return (CompositeKey.ReadRecordId(entry.Key), entry.Address);
    }
  }

  //The record already holding this value, if it is not the one being written. Uniqueness
  //cannot be a duplicate key in the tree, because the identity in the composite makes every
  //entry distinct on purpose; it is the range of one value holding anything at all.
  public Ulid? FindConflict(IDocumentValue value, Ulid recordId) {
    foreach (var (existing, _) in Find(value)) {
      if (existing != recordId) {
        return existing;
      }
    }
    return null;
  }
}
