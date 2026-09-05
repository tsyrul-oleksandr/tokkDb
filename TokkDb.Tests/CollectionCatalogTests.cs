using TokkDb.Documents;
using TokkDb.Documents.Values;
using TokkDb.Pages;
using TokkDb.Pages.Indexes;
using TokkDb.Pages.Relations;
using TokkDb.Values;
using Xunit;

namespace TokkDb.Tests;

public class CollectionCatalogTests {
  private static void CreateDatabase(TempDatabaseFile file) {
    using var db = new TokkDbConnection(file.Path);
    db.CreateDatabase(config => config.CreateEntity<Person>(description: "People"));
  }

  private static TokkDbConnection Reopen(TempDatabaseFile file) {
    var db = new TokkDbConnection(file.Path);
    db.Load();
    return db;
  }

  [Fact]
  public void EveryCollectionDefinitionIsReadBackAfterCreateCloseAndOpen() {
    using var file = new TempDatabaseFile();
    CreateDatabase(file);

    using var reopened = Reopen(file);
    var names = reopened.Collections.Select(collection => collection.Name).OrderBy(name => name).ToList();
    Assert.Equal(
      SystemCollections.All.Concat(["Person"]).OrderBy(name => name).ToList(),
      names);
  }

  [Fact]
  public void TheCatalogueDescribesItself() {
    using var file = new TempDatabaseFile();
    CreateDatabase(file);

    using var reopened = Reopen(file);
    var catalogue = reopened.Collection(SystemCollections.Collections);

    //Read out of a document in the collection it describes, not out of any hardcoded stub.
    Assert.NotEqual(default, catalogue.Id);
    Assert.NotEqual(0u, catalogue.DataFirstPage);
    Assert.Equal(SystemCollections.All.Count + 1u, catalogue.RecordCount);

    var columns = catalogue.Columns.Select(column => column.Name).ToList();
    Assert.Contains(CollectionDescriptorDocument.NameField, columns);
    Assert.Contains(CollectionDescriptorDocument.ColumnsField, columns);
    Assert.Contains(CollectionDescriptorDocument.DataFirstPageField, columns);
    Assert.Contains(CollectionDescriptorDocument.RecordCountField, columns);
    //The fields D-5 reserves are described even though nothing reads them yet.
    Assert.Contains(CollectionDescriptorDocument.HistoryCollectionIdField, columns);
    Assert.Contains(CollectionDescriptorDocument.RetentionPolicyField, columns);

    var nameColumn = catalogue.Columns.Single(column => column.Name == CollectionDescriptorDocument.NameField);
    Assert.Equal(ValueTypeEnum.String, nameColumn.Type);
    Assert.True(nameColumn.Unique);
  }

  [Fact]
  public void TheSystemCollectionsAreCreatedEmpty() {
    using var file = new TempDatabaseFile();
    CreateDatabase(file);

    using var reopened = Reopen(file);
    foreach (var name in SystemCollections.All.Where(name => name != SystemCollections.Collections)) {
      var collection = reopened.Collection(name);
      Assert.Equal(0u, collection.RecordCount);
      Assert.Equal(0u, collection.DataFirstPage);
      Assert.True(collection.IsSystem);
    }
  }

  //The system collections that hold descriptors describe their own columns, for the reason
  //_collections does: what the catalogue holds should be readable out of the catalogue and
  //not only out of the code that writes it. The ones nothing writes yet have none.
  [Fact]
  public void TheSystemCollectionsThatHoldDescriptorsDescribeTheirOwnColumns() {
    using var file = new TempDatabaseFile();
    CreateDatabase(file);

    using var reopened = Reopen(file);
    Assert.NotEmpty(reopened.Collection(SystemCollections.Collections).Columns);
    Assert.Contains(reopened.Collection(SystemCollections.Indexes).Columns,
      column => column.Name == IndexDescriptorDocument.ColumnField);
    Assert.Contains(reopened.Collection(SystemCollections.Relations).Columns,
      column => column.Name == RelationDescriptorDocument.TargetColumnField);
    Assert.Empty(reopened.Collection(SystemCollections.SemanticTypes).Columns);
  }

  [Fact]
  public void AUserCollectionKeepsItsColumnsAndPointersAcrossAReopen() {
    using var file = new TempDatabaseFile();
    using (var db = new TokkDbConnection(file.Path)) {
      db.CreateDatabase(config => config.CreateEntity<Person>(description: "People"));
      db.Entities<Person>().Insert(TestPeople.Ivan());
      db.Entities<Person>().Insert(TestPeople.Numbered(2));
    }

    using var reopened = Reopen(file);
    var person = reopened.Collection("Person");
    Assert.Equal("People", person.Description);
    Assert.False(person.IsSystem);
    Assert.Equal(2u, person.RecordCount);
    Assert.NotEqual(0u, person.DataFirstPage);
    Assert.Equal(person.DataFirstPage, person.DataLastPage);
    Assert.Equal(
      new[] { "Id", "Name", "Age", "Passport", "Tags" },
      person.Columns.Select(column => column.Name));
    Assert.Equal(ValueTypeEnum.Int, person.Columns.Single(column => column.Name == "Age").Type);
    Assert.Equal(ValueTypeEnum.Array, person.Columns.Single(column => column.Name == "Tags").Type);
    Assert.Equal(ValueTypeEnum.Object, person.Columns.Single(column => column.Name == "Passport").Type);
  }

  [Fact]
  public void EveryCollectionHasItsOwnIdentifiers() {
    using var file = new TempDatabaseFile();
    CreateDatabase(file);

    using var reopened = Reopen(file);
    var collections = reopened.Collections.ToList();
    Assert.Equal(collections.Count, collections.Select(collection => collection.Id).Distinct().Count());
    Assert.Equal(collections.Count, collections.Select(collection => collection.OwningCollectionId).Distinct().Count());
    Assert.DoesNotContain(collections, collection => collection.OwningCollectionId == 0);
  }

  [Fact]
  public void AUserCollectionNamedWithTheReservedPrefixIsRejected() {
    using var file = new TempDatabaseFile();
    using var db = new TokkDbConnection(file.Path);

    var exception = Assert.Throws<ReservedCollectionNameException>(
      () => db.CreateDatabase(config => config.CreateEntity<Person>("_x")));
    Assert.Equal("_x", exception.CollectionName);
    Assert.Contains("_x", exception.Message);
  }

  [Fact]
  public void TheReservedPrefixIsRefusedOnTheCatalogueItself() {
    using var file = new TempDatabaseFile();
    CreateDatabase(file);

    using var reopened = Reopen(file);
    //Reaching past the configuration API does not get around it either.
    Assert.Throws<ReservedCollectionNameException>(() => reopened.CreateCollection("_events"));
    Assert.Throws<ReservedCollectionNameException>(() => reopened.CreateCollection(SystemCollections.Settings));
  }

  [Fact]
  public void RecordsStillReadBackWhenTheirCollectionCameFromTheCatalogue() {
    using var file = new TempDatabaseFile();
    using (var db = new TokkDbConnection(file.Path)) {
      db.CreateDatabase(config => config.CreateEntity<Person>());
      var entities = db.Entities<Person>();
      for (var i = 0; i < 300; i++) {
        entities.Insert(TestPeople.Numbered(i));
      }
    }

    using var reopened = Reopen(file);
    Assert.Equal(300, reopened.Entities<Person>().GetAll().Count());
    Assert.Equal(300u, reopened.Collection("Person").RecordCount);
    //Several data pages, so the chain pointers in the catalogue were maintained too.
    Assert.NotEqual(reopened.Collection("Person").DataFirstPage, reopened.Collection("Person").DataLastPage);
  }

  [Fact]
  public void ACollectionAddedAfterOpeningIsThereOnTheNextOpen() {
    using var file = new TempDatabaseFile();
    CreateDatabase(file);

    using (var db = Reopen(file)) {
      db.CreateCollection<Tag>(description: "Tags");
      db.Entities<Tag>().Insert(new Tag("first"));
    }

    using var reopened = Reopen(file);
    var tag = reopened.Collection("Tag");
    Assert.Equal("Tags", tag.Description);
    Assert.Equal(1u, tag.RecordCount);
    Assert.Equal("first", Assert.Single(reopened.Entities<Tag>().GetAll()).Name);
  }

  [Fact]
  public void ACatalogueThatOutgrowsOnePageStillReadsBackWhole() {
    using var file = new TempDatabaseFile();
    using (var db = new TokkDbConnection(file.Path)) {
      db.CreateDatabase(config => config.CreateEntity<Person>());
      for (var i = 0; i < 120; i++) {
        db.CreateCollection<Person>($"Collection{i}");
      }
    }

    using var reopened = Reopen(file);
    Assert.Equal(SystemCollections.All.Count + 121, reopened.Collections.Count);
    for (var i = 0; i < 120; i++) {
      Assert.Equal($"Collection{i}", reopened.Collection($"Collection{i}").Name);
    }
    //The catalogue outgrew its first page, so its own chain pointers had to be maintained
    //while it was being written to.
    var catalogue = reopened.Collection(SystemCollections.Collections);
    Assert.NotEqual(catalogue.DataFirstPage, catalogue.DataLastPage);
  }

  [Fact]
  public void TheCatalogueLivesOnOrdinaryDataPages() {
    using var file = new TempDatabaseFile();
    CreateDatabase(file);

    using var disk = new TokkDb.Disk.DiskManager(file.Path);
    var pageManager = new PageManager(disk);
    pageManager.SetPageSize(RootPage.ReadPrefix(pageManager.ReadPrefix(RootPage.PrefixByteSize)).PageSize);
    var rootPage = pageManager.LoadPage<RootPage>(TokkDb.Configuration.TokkConstants.RootPageIndex);

    var cataloguePage = pageManager.LoadPage<DataPage>(rootPage.CollectionsFirstPageId);
    Assert.Equal(PageType.Data, cataloguePage.Type);
    //Every descriptor is an ordinary record: the VR-11 header, then a document body that the
    //ordinary document reader understands. Read along the chain rather than off the first
    //page — the catalogue is an ordinary collection, so it spills onto a second page like any
    //other, and it does once the system collections describe their own columns.
    var records = new List<StoredRecord>();
    for (var page = cataloguePage; page is not null;
        page = page.NextPageIndex == default ? null : pageManager.LoadPage<DataPage>(page.NextPageIndex)) {
      Assert.Equal(PageType.Data, page.Type);
      records.AddRange(page.GetItems().Select(StoredRecordUtilities.FromBuffer));
    }
    Assert.Equal(SystemCollections.All.Count + 1, records.Count);
    Assert.All(records, record => Assert.True(record.Header.IsLive));
    Assert.All(records, record => Assert.NotEqual(default, record.Header.RecordId));
    var names = records
      .Select(record => ((StringDocumentValue)((ObjectDocumentValue)record.Document.Value)
        [CollectionDescriptorDocument.NameField]).Value)
      .ToList();
    Assert.Contains(SystemCollections.Collections, names);
    Assert.Contains("Person", names);
  }

  [Fact]
  public void ADefinitionLookupCostsNoPageRead() {
    using var file = new TempDatabaseFile();
    CreateDatabase(file);

    using var reopened = Reopen(file);
    //Everything the catalogue holds was read at open; from here on it is memory only.
    var readsAfterOpen = reopened.PageReadCount;

    for (var i = 0; i < 100; i++) {
      foreach (var name in SystemCollections.All) {
        var collection = reopened.Collection(name);
        _ = collection.DataFirstPage;
        _ = collection.RecordCount;
        _ = collection.Columns.Count;
      }
      _ = reopened.Collection("Person").OwningCollectionId;
      _ = reopened.Collections.Count;
    }

    Assert.Equal(readsAfterOpen, reopened.PageReadCount);
  }

  [Fact]
  public void TheWholeCatalogueIsInMemoryBeforeTheFirstLookup() {
    using var file = new TempDatabaseFile();
    using (var db = new TokkDbConnection(file.Path)) {
      db.CreateDatabase(config => config.CreateEntity<Person>());
      for (var i = 0; i < 60; i++) {
        db.CreateCollection<Person>($"Collection{i}");
      }
    }

    using var reopened = Reopen(file);
    var readsAfterOpen = reopened.PageReadCount;
    //A catalogue spanning several pages, every definition of it already in hand.
    Assert.Equal(SystemCollections.All.Count + 61, reopened.Collections.Count);
    Assert.All(reopened.Collections, collection => Assert.NotEqual(default, collection.Id));
    Assert.Equal(readsAfterOpen, reopened.PageReadCount);
  }

  [Fact]
  public void AWrittenDefinitionIsInTheCacheAndInTheCatalogueAtOnce() {
    using var file = new TempDatabaseFile();
    CreateDatabase(file);

    using (var db = Reopen(file)) {
      var created = db.CreateCollection<Tag>(description: "Tags");
      //In the cache straight away, without going back to the pages.
      var readsAfterCreate = db.PageReadCount;
      Assert.Same(created, db.Collection("Tag"));
      Assert.Equal("Tags", db.Collection("Tag").Description);
      Assert.Equal(readsAfterCreate, db.PageReadCount);
    }

    //And in _collections, in the same operation that put it in the cache.
    using var reopened = Reopen(file);
    Assert.Equal("Tags", reopened.Collection("Tag").Description);
  }

  [Fact]
  public void ADatabaseWithAThousandCollectionsOpensCorrectly() {
    using var file = new TempDatabaseFile();
    const int count = 1000;
    using (var db = new TokkDbConnection(file.Path)) {
      db.CreateDatabase(config => config.CreateEntity<Person>());
      for (var i = 0; i < count; i++) {
        db.CreateCollection<Person>($"Collection{i}");
      }
    }

    using var reopened = Reopen(file);
    //One pass over the pages the catalogue occupies, not one read per definition.
    var readsAfterOpen = reopened.PageReadCount;
    Assert.InRange(readsAfterOpen, 1, count / 4);

    Assert.Equal(SystemCollections.All.Count + count + 1, reopened.Collections.Count);
    for (var i = 0; i < count; i++) {
      var collection = reopened.Collection($"Collection{i}");
      Assert.Equal($"Collection{i}", collection.Name);
      Assert.Equal(5, collection.Columns.Count);
      Assert.NotEqual(default, collection.Id);
    }
    Assert.Equal(count + 1, reopened.Collections.Count(collection => !collection.IsSystem));
    //A thousand lookups later the file has not been touched again.
    Assert.Equal(readsAfterOpen, reopened.PageReadCount);
  }

  [Fact]
  public void AFieldTheWriterDidNotKnowAboutReadsAsItsDefault() {
    //DC-7: adding a metadata field must not need a reader change or a migration, so a
    //document written without a field has to load rather than fail.
    var document = new ObjectDocument();
    document.SetIdentifierValue(new UlidDocumentValue(Ulid.NewUlid()));
    document.SetValue(new ObjectDocumentValue(new Dictionary<string, IDocumentValue> {
      [CollectionDescriptorDocument.NameField] = new StringDocumentValue("Legacy"),
      [CollectionDescriptorDocument.DataFirstPageField] = new UIntDocumentValue(7)
    }));

    var descriptor = CollectionDescriptorDocument.Read(document);
    Assert.Equal("Legacy", descriptor.Name);
    Assert.Equal(7u, descriptor.DataFirstPage);
    Assert.Equal(string.Empty, descriptor.RetentionPolicy);
    Assert.Equal(default, descriptor.HistoryCollectionId);
    Assert.Empty(descriptor.Columns);
    Assert.Empty(descriptor.SecondaryIndexRoots);
  }

  [Fact]
  public void ADescriptorRoundTripsThroughItsDocument() {
    var descriptor = new CollectionDescriptor {
      Id = Ulid.NewUlid(),
      Name = "Orders",
      Description = "Замовлення",
      SchemaVersion = 3,
      OwningCollectionId = 9,
      DataFirstPage = 11,
      DataLastPage = 14,
      PrimaryIndexRoot = 21,
      //Named, because a root has to say which index it belongs to (DC-4).
      SecondaryIndexRoots = new Dictionary<string, uint> { ["doi"] = 31, ["year"] = 32 },
      FreeSpaceRoot = 41,
      RecordCount = 512,
      Columns = [
        new ColumnDescriptor("Code", ValueTypeEnum.String, "Order code", unique: true, readOnly: true,
          defaultValue: new StringDocumentValue("none")),
        new ColumnDescriptor("Total", ValueTypeEnum.Int)
      ]
    };

    var read = CollectionDescriptorDocument.Read(CollectionDescriptorDocument.Write(descriptor));

    Assert.Equal(descriptor.Id, read.Id);
    Assert.Equal("Orders", read.Name);
    Assert.Equal("Замовлення", read.Description);
    Assert.Equal(3, read.SchemaVersion);
    Assert.Equal(9u, read.OwningCollectionId);
    Assert.Equal(11u, read.DataFirstPage);
    Assert.Equal(14u, read.DataLastPage);
    Assert.Equal(21u, read.PrimaryIndexRoot);
    Assert.Equal(new Dictionary<string, uint> { ["doi"] = 31, ["year"] = 32 }, read.SecondaryIndexRoots);
    Assert.Equal(41u, read.FreeSpaceRoot);
    Assert.Equal(512u, read.RecordCount);

    var code = read.Columns[0];
    Assert.Equal("Code", code.Name);
    Assert.Equal(ValueTypeEnum.String, code.Type);
    Assert.True(code.Unique);
    Assert.True(code.ReadOnly);
    Assert.Equal("Order code", code.Description);
    Assert.Equal("none", Assert.IsType<StringDocumentValue>(code.DefaultValue).Value);
    Assert.Equal(ValueTypeEnum.Null, read.Columns[1].DefaultValue.Type);
  }
}
