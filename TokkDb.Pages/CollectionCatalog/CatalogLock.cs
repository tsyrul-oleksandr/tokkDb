namespace TokkDb.Pages;

//Q-11 and QM-2b: what stands between a query and a schema change.
//
//A query plans against the catalogue and then reads through what it planned, and a schema
//change rewrites the catalogue those reads depend on. Comparing a version before reading is
//not enough on its own — the catalogue can move between the comparison and the read, and the
//window is the whole query. So planning and execution hold a read lease, a schema change takes
//the lock exclusively and waits for every lease to be given back, and the version is what a
//plan that left the engine through Explain is checked against when it comes back.
//
//The version moves on every schema change the lock admits, including one that turned out to
//change nothing and one that failed and was rolled back. A spurious move costs a caller one
//re-plan; a missed one would let a plan read through an index that is no longer there.
//
//Recursive, so that a schema change can run a query inside itself. The other way round — a
//query that starts a schema change while it holds its lease — throws rather than deadlocks,
//which is why QueryService raises its event only after the lease is given back.
public sealed class CatalogLock : IDisposable {
  private readonly ReaderWriterLockSlim _lock = new(LockRecursionPolicy.SupportsRecursion);
  private long _version = 1;

  public long Version => Interlocked.Read(ref _version);

  //Held for as long as a plan is made or run. The version it carries cannot move while it is
  //held, because nothing that moves it can get in.
  public CatalogLease Read() {
    _lock.EnterReadLock();
    return new CatalogLease(this, Version);
  }

  //Taken by every operation that changes what a plan depends on: collections, their columns,
  //indexes and relations, and the reload that follows a rolled-back transaction.
  public IDisposable Change() {
    _lock.EnterWriteLock();
    return new SchemaChange(this);
  }

  internal void ExitRead() {
    _lock.ExitReadLock();
  }

  public void Dispose() {
    _lock.Dispose();
  }

  //Moved before the lock is let go, so the first lease taken after a change already sees it.
  private void ExitChange() {
    Interlocked.Increment(ref _version);
    _lock.ExitWriteLock();
  }

  private sealed class SchemaChange : IDisposable {
    private readonly CatalogLock _owner;
    private bool _released;

    public SchemaChange(CatalogLock owner) {
      _owner = owner;
    }

    public void Dispose() {
      if (_released) {
        return;
      }
      _released = true;
      _owner.ExitChange();
    }
  }
}

//A read lease on the catalogue, and the version it was taken at.
public sealed class CatalogLease : IDisposable {
  private bool _released;

  internal CatalogLease(CatalogLock owner, long version) {
    Owner = owner;
    Version = version;
  }

  public long Version { get; }

  //Which catalogue the lease is on, so a plan made through one connection cannot be run
  //through another whose version happens to be the same number.
  internal CatalogLock Owner { get; }

  public void Dispose() {
    if (_released) {
      return;
    }
    _released = true;
    Owner.ExitRead();
  }
}
