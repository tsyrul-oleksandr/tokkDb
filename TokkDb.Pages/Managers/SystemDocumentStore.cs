using TokkDb.Documents;
using TokkDb.Pages.Records;
using TokkDb.Transactions;

namespace TokkDb.Pages.Managers;

//D-4's machinery, on its own: read, write and remove documents in a system collection.
//
//This is the whole of "no new storage mechanism is needed". A system collection is an
//ordinary collection, its documents are ordinary documents, and they go through the same
//pages, journal and transaction as user data — so what is left for a caller that wants to
//keep something in the catalogue is deciding what its document looks like. Nothing here
//knows what any of these documents mean; the layer that defines them does.
public class SystemDocumentStore {
  private readonly TransactionManager _transactionManager;
  private DataPageManager _dataPageManager;

  public SystemDocumentStore(TransactionManager transactionManager) {
    _transactionManager = transactionManager;
  }

  public void SetDataPageManager(DataPageManager dataPageManager) {
    _dataPageManager = dataPageManager;
  }

  //Every live document of the collection, with the identity it is stored under (D-1).
  public IEnumerable<(Ulid Id, ObjectDocument Document)> ReadAll(string collectionName) {
    Require(collectionName);
    foreach (var row in _dataPageManager.GetAllRows(collectionName)) {
      var record = StoredRecordUtilities.FromBuffer(_dataPageManager.ReadRecordBuffer(row));
      if (record.Header.IsLive) {
        yield return (record.Header.RecordId, record.Document);
      }
    }
  }

  //Writes the document under this identity, replacing whatever was there.
  //
  //A document that still fits where it lies is written there; one that has outgrown its slot
  //is retired and written again. The second path costs more but takes an overflow chain when
  //it has to (ST-5), so a document is not capped at a page — which the in-place-or-move path
  //a catalogue descriptor takes is, because growing a record where it lies is ST-6 and unbuilt.
  public void Write(string collectionName, Ulid id, ObjectDocument document) {
    Require(collectionName);
    _transactionManager.RequireTransaction();
    var header = RecordHeader.ForNewRecord(id);
    var row = _dataPageManager.FindLiveRow(collectionName, id);
    if (row is null) {
      _dataPageManager.WriteRecord(collectionName, header, document);
      return;
    }
    if (_dataPageManager.CanUpdateRowInPlace(row.Value.Address, header, document)) {
      _dataPageManager.UpdateRow(row.Value.Address, header, document);
      return;
    }
    //Retired before the new image exists, so "the current document" means one thing (VR-12).
    _dataPageManager.RetireRow(collectionName, row.Value.Address, RecordFlags.Superseded,
      RetentionPolicy.None);
    _dataPageManager.WriteRecord(collectionName, header, document);
  }

  public bool Delete(string collectionName, Ulid id) {
    Require(collectionName);
    _transactionManager.RequireTransaction();
    if (_dataPageManager.FindLiveRow(collectionName, id) is not { } row) {
      return false;
    }
    _dataPageManager.RetireRow(collectionName, row.Address, RecordFlags.Deleted, RetentionPolicy.None);
    return true;
  }

  //Only the reserved collections. A caller that wants to write user records has DbEntities,
  //which serializes a type; this exists for the catalogue, whose documents are not a type.
  private static void Require(string collectionName) {
    if (!SystemCollections.IsReservedName(collectionName)) {
      throw new ArgumentException(
        $"'{collectionName}' is not a system collection. Use Entities to write records of a user collection.",
        nameof(collectionName));
    }
  }
}
