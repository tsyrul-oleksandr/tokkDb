using TokkDb.Pages.Managers;

namespace TokkDb.Pages.Query.Pipeline;

//DG-4, stage five: only a surviving record of the page becomes a document. Owns documents
//materialised, which is the figure NF-2 is checked by.
internal sealed class MaterialisationStage {
  private readonly DataPageManager _data;

  public MaterialisationStage(DataPageManager data) {
    _data = data;
  }

  public int DocumentsMaterialised { get; private set; }

  //From the row the walk has in hand, for a plan whose walk is the order.
  public QueryMatch Materialise(string collectionName, DataRow row) {
    DocumentsMaterialised++;
    return new QueryMatch(row.Address, _data.ReadRecord(collectionName, row));
  }

  //By address, for a record the ordering stage kept: the rows of the walk were not held, because
  //holding them would hold every page the walk passed through. Nothing when the record has gone
  //since the walk saw it.
  public QueryMatch MaterialiseAt(string collectionName, DocumentAddress address) {
    if (_data.LiveRowAt(address) is not { } row) {
      return null;
    }
    return Materialise(collectionName, row);
  }
}
