namespace TokkDb.Pages.Indexes;

//Where a tree keeps the page it starts at. D-2 puts every physical pointer in the
//collection's catalogue document, and a tree that read its root from anywhere else would
//have to be found by a scan at open.
//
//It is a parameter rather than a rule inside the tree because the primary index and a
//secondary index are the same structure over different pointers, and only their roots and
//their keys differ.
public abstract class IndexRoot {
  public abstract string Name { get; }
  public abstract uint Read();
  public abstract void Write(uint pageIndex);
}

//The one index every collection has: keyed by record identity (D-1).
public sealed class PrimaryIndexRoot : IndexRoot {
  private readonly CollectionCatalog _catalog;
  private readonly string _collectionName;

  public PrimaryIndexRoot(CollectionCatalog catalog, string collectionName) {
    _catalog = catalog;
    _collectionName = collectionName;
  }

  public override string Name => "primary";

  public override uint Read() {
    return _catalog.Get(_collectionName).PrimaryIndexRoot;
  }

  public override void Write(uint pageIndex) {
    _catalog.SetPrimaryIndexRoot(_collectionName, pageIndex);
  }
}

//One of the collection's secondary indexes, named by the descriptor that defines it in
//_indexes and rooted in the same catalogue document as everything else.
public sealed class SecondaryIndexRoot : IndexRoot {
  private readonly CollectionCatalog _catalog;
  private readonly string _collectionName;

  public SecondaryIndexRoot(CollectionCatalog catalog, string collectionName, string indexName) {
    _catalog = catalog;
    _collectionName = collectionName;
    Name = indexName;
  }

  public override string Name { get; }

  public override uint Read() {
    return _catalog.Get(_collectionName).SecondaryIndexRoots.GetValueOrDefault(Name);
  }

  public override void Write(uint pageIndex) {
    _catalog.SetSecondaryIndexRoot(_collectionName, Name, pageIndex);
  }
}
