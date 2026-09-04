using TokkDb.Configuration;
using TokkDb.Disk;
using TokkDb.Pages;
using Xunit;

namespace TokkDb.Tests;

public class PageIntegrityTests {
  //Page 0 is the root, page 1 the collections catalogue, so user data starts here.
  private const uint FirstDataPageIndex = 2;

  private static void CreateDatabaseWithPeople(TempDatabaseFile file, int count = 3) {
    using var db = new TokkDbConnection(file.Path);
    db.CreateDatabase(config => config.CreateEntity<Person>());
    var entities = db.Entities<Person>();
    for (var i = 0; i < count; i++) {
      entities.Insert(TestPeople.Numbered(i));
    }
  }

  private static void FlipByte(TempDatabaseFile file, uint pageIndex, int offsetInPage) {
    var bytes = File.ReadAllBytes(file.Path);
    bytes[pageIndex * TokkConstants.DefaultPageSize + offsetInPage] ^= 0xFF;
    File.WriteAllBytes(file.Path, bytes);
  }

  [Fact]
  public void FlippingAByteInADataPageIsReportedInsteadOfReturningWrongData() {
    using var file = new TempDatabaseFile();
    CreateDatabaseWithPeople(file);
    //A byte in the record area, which is exactly where a silent wrong answer would come from.
    FlipByte(file, FirstDataPageIndex, BasePage.StartContentBufferPosition + 8);

    using var reopened = new TokkDbConnection(file.Path);
    reopened.Load();
    var exception = Assert.Throws<PageCorruptedException>(() => reopened.Entities<Person>().GetAll().ToList());

    Assert.Equal(FirstDataPageIndex, exception.PageIndex);
    Assert.NotEqual(exception.StoredChecksum, exception.ComputedChecksum);
    Assert.Contains($"Page {FirstDataPageIndex}", exception.Message);
  }

  [Fact]
  public void FlippingAByteInTheSlotDirectoryIsReported() {
    using var file = new TempDatabaseFile();
    CreateDatabaseWithPeople(file);
    FlipByte(file, FirstDataPageIndex, TokkConstants.DefaultPageSize - BasePage.ControlAreaByteSize - 1);

    using var reopened = new TokkDbConnection(file.Path);
    reopened.Load();
    var exception = Assert.Throws<PageCorruptedException>(() => reopened.Entities<Person>().GetAll().ToList());
    Assert.Equal(FirstDataPageIndex, exception.PageIndex);
  }

  [Fact]
  public void FlippingAByteInTheControlAreaItselfIsReported() {
    using var file = new TempDatabaseFile();
    CreateDatabaseWithPeople(file);
    FlipByte(file, FirstDataPageIndex, TokkConstants.DefaultPageSize - 1);

    using var reopened = new TokkDbConnection(file.Path);
    reopened.Load();
    var exception = Assert.Throws<PageCorruptedException>(() => reopened.Entities<Person>().GetAll().ToList());
    Assert.Equal(FirstDataPageIndex, exception.PageIndex);
  }

  [Fact]
  public void ADamagedCataloguePageIsReportedWhenTheDatabaseIsOpened() {
    using var file = new TempDatabaseFile();
    CreateDatabaseWithPeople(file);
    FlipByte(file, 1, BasePage.StartContentBufferPosition + 2);

    using var reopened = new TokkDbConnection(file.Path);
    var exception = Assert.Throws<PageCorruptedException>(reopened.Load);
    Assert.Equal(1u, exception.PageIndex);
  }

  [Fact]
  public void ADamagedRootPageIsReportedWhenTheDatabaseIsOpened() {
    using var file = new TempDatabaseFile();
    CreateDatabaseWithPeople(file);
    //Past the prefix, so the file still names itself correctly and the damage is what is left.
    FlipByte(file, TokkConstants.RootPageIndex, RootPage.PrefixByteSize + 1);

    using var reopened = new TokkDbConnection(file.Path);
    var exception = Assert.Throws<PageCorruptedException>(reopened.Load);
    Assert.Equal(TokkConstants.RootPageIndex, exception.PageIndex);
  }

  [Fact]
  public void AnUndamagedDatabaseReadsBackEveryRecord() {
    using var file = new TempDatabaseFile();
    CreateDatabaseWithPeople(file, 200);

    using var reopened = new TokkDbConnection(file.Path);
    reopened.Load();
    Assert.Equal(200, reopened.Entities<Person>().GetAll().Count());
  }

  [Fact]
  public void DataPagesCarryTheirCollectionAndSystemPagesCarryNone() {
    using var file = new TempDatabaseFile();
    CreateDatabaseWithPeople(file);

    using var disk = new DiskManager(file.Path);
    var pageManager = new PageManager(disk);
    pageManager.SetPageSize(RootPage.ReadPrefix(pageManager.ReadPrefix(RootPage.PrefixByteSize)).PageSize);

    Assert.Equal(0u, pageManager.LoadPage<RootPage>(TokkConstants.RootPageIndex).OwningCollectionId);
    var catalogue = pageManager.LoadPage<MetadataPage>(1);
    Assert.Equal(0u, catalogue.OwningCollectionId);

    var collectionId = catalogue.Entities["Person"].Id;
    Assert.NotEqual(0u, collectionId);
    Assert.Equal(collectionId, pageManager.LoadPage<DataPage>(FirstDataPageIndex).OwningCollectionId);
  }

  [Fact]
  public void TheChecksumIsStableAcrossRuns() {
    //A persisted value: it must not depend on anything that varies between processes.
    Assert.Equal(0xCBF43926u, PageChecksum.Compute("123456789"u8));
    Assert.Equal(0u, PageChecksum.Compute(ReadOnlySpan<byte>.Empty));
  }
}
