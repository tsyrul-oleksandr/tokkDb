using TokkDb.Disk;
using TokkDb.Pages;
using TokkDb.Pages.Indexes;
using TokkDb.Pages.Managers;
using TokkDb.Pages.Relations;
using TokkDb.Transactions;
using Xunit;

namespace TokkDb.Tests;

//D-4 said the reserved list would grow — "(later: _events, _versions)" — and the assistant's
//request traces are the growth D-2 calls for. The promise that goes with it is that a database
//written before a name existed gains the collection on open rather than needing a migration,
//and this is what holds the engine to it.
//
//The older database is written by a catalogue that knows only the older names, which is what the
//engine was before the change, and then opened by the engine as it is now. Nothing is checked
//in: a file that has to predate a change cannot be built by the code that made it, and a
//catalogue that creates fewer collections is a closer likeness than a binary would be anyway,
//because it is the same code writing the same format.
public class SystemCollectionGrowthTests {
  //The reserved collections as they stood before the traces were added.
  private static readonly string[] Older = [
    SystemCollections.Collections, SystemCollections.Indexes, SystemCollections.Relations,
    SystemCollections.SemanticTypes, SystemCollections.DisplayRules, SystemCollections.Settings,
    SystemCollections.Conversations, SystemCollections.ConversationEntries
  ];

  private static readonly string[] Added = [
    SystemCollections.Traces, SystemCollections.TraceSteps, SystemCollections.DataChanges
  ];

  [Fact]
  public void ADatabaseWrittenBeforeTheTraceCollectionsExistedGainsThemOnOpen() {
    using var file = new TempDatabaseFile();

    //What the older engine left behind: the eight it knew about, and none of the three. It
    //cannot be counted by opening the file, because opening the file is the thing that adds
    //them — so the writer says what it wrote.
    var written = WriteOlderDatabase(file.Path);
    Assert.Equal(Older.OrderBy(name => name), written.OrderBy(name => name));
    Assert.DoesNotContain(SystemCollections.Traces, written);

    using var reopened = new TokkDbConnection(file.Path);
    reopened.Load();

    var names = reopened.Collections.Select(collection => collection.Name).ToList();
    Assert.Equal(SystemCollections.All.OrderBy(name => name), names.OrderBy(name => name));
    foreach (var name in Added) {
      Assert.Contains(name, names);
    }
  }

  [Fact]
  public void TheCollectionsItGainedAreThereAgainAfterAnotherCloseAndOpen() {
    using var file = new TempDatabaseFile();
    WriteOlderDatabase(file.Path);

    using (var upgraded = new TokkDbConnection(file.Path)) {
      upgraded.Load();
    }

    using var reopened = new TokkDbConnection(file.Path);
    reopened.Load();

    //Written to the catalogue rather than added to the cache each time: the descriptor is a
    //document like any other and was committed when it was created.
    foreach (var name in Added) {
      var descriptor = reopened.Collection(name);
      Assert.Equal(SystemCollections.Descriptions[name], descriptor.Description);
      Assert.Equal(0u, descriptor.RecordCount);
    }
  }

  //Opening twice more must not create them twice, and must not renumber the ones that were
  //already there — a reissued owning id would let a new collection claim another's pages.
  [Fact]
  public void GainingThemHappensOnceHoweverOftenTheDatabaseIsOpened() {
    using var file = new TempDatabaseFile();
    WriteOlderDatabase(file.Path);

    var owningIds = new List<uint>();

    for (var open = 0; open < 3; open++) {
      using var connection = new TokkDbConnection(file.Path);
      connection.Load();

      Assert.Equal(SystemCollections.All.Count, connection.Collections.Count);
      owningIds.Add(connection.Collection(SystemCollections.Traces).OwningCollectionId);
    }

    Assert.Single(owningIds.Distinct());
  }

  [Fact]
  public void TheUsersOwnCollectionsAndTheirRecordsAreUntouchedByTheUpgrade() {
    using var file = new TempDatabaseFile();
    WriteOlderDatabase(file.Path);

    Ulid recordId;

    using (var older = new TokkDbConnection(file.Path)) {
      older.Load();
      older.CreateCollection("Person", [new ColumnDescriptor("Name", Values.ValueTypeEnum.String)]);
      recordId = older.Entities<Person>("Person").Insert(new Person { Name = "Anna" });
    }

    using var reopened = new TokkDbConnection(file.Path);
    reopened.Load();

    Assert.Contains(SystemCollections.Traces, reopened.Collections.Select(collection => collection.Name));
    Assert.Equal("Anna", reopened.Entities<Person>("Person").GetById(recordId).Value.Name);
  }

  //A database written by the engine as it was before the trace collections were named: the same
  //code, the same format, and a catalogue that creates eight collections instead of ten.
  private static IReadOnlyList<string> WriteOlderDatabase(string path) {
    using var disk = new DiskManager(path);
    var pages = new PageManager(disk);
    var transactions = new TransactionManager(pages);
    var root = new RootPageManager(pages, transactions);
    var catalogue = new OlderCatalog(root, transactions);
    var freeSpace = new FreeSpaceManager(pages, root, catalogue, transactions);
    var data = new DataPageManager(pages, catalogue, freeSpace, transactions);
    catalogue.SetDataPageManager(data);

    var transaction = transactions.CreateTransaction();
    try {
      root.Initialize();
      catalogue.Initialize();
      transaction.Commit();
    } catch {
      transaction.Rollback();
      throw;
    }

    return catalogue.Descriptors.Select(descriptor => descriptor.Name).ToList();
  }

  //The catalogue the engine had before D-2's change: it creates the reserved collections it
  //knows about, and it does not know about the traces.
  private sealed class OlderCatalog(RootPageManager root, TransactionManager transactions)
    : CollectionCatalog(root, transactions) {

    protected override void CreateNewCatalog() {
      foreach (var name in Older) {
        CreateCollectionCore(name, ColumnsFor(name), SystemCollections.Descriptions[name]);
      }
    }

    private static List<ColumnDescriptor> ColumnsFor(string name) {
      return name switch {
        SystemCollections.Collections => CollectionDescriptorDocument.CreateSelfColumns(),
        SystemCollections.Indexes => IndexDescriptorDocument.CreateColumns(),
        SystemCollections.Relations => RelationDescriptorDocument.CreateColumns(),
        SystemCollections.DisplayRules => DisplayRuleDocument.CreateColumns(),
        SystemCollections.Settings => SettingsDocument.CreateColumns(),
        _ => []
      };
    }
  }

  private sealed class Person {
    public string Name { get; set; } = string.Empty;
  }
}
