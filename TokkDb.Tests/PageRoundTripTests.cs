using TokkDb.Configuration;
using TokkDb.Disk;
using TokkDb.Pages;
using Xunit;

namespace TokkDb.Tests;

public class PageRoundTripTests {
  [Fact]
  public void DataPageHeaderAndItemsSurviveASaveAndLoad() {
    using var file = new TempDatabaseFile();
    using var disk = new DiskManager(file.Path);
    var pageManager = new PageManager(disk);

    var page = pageManager.CreateNewMemoryPage<DataPage>(PageType.Data, 1);
    page.NextPageIndex = 7;
    page.OwningCollectionId = 3;
    foreach (var marker in new[] { 11, 22, 33 }) {
      page.RegisterItem(4).WriteInt(marker, 0, out _);
    }
    var expectedFreeBytes = page.FreeBytes;
    pageManager.SavePages(page);

    var loaded = pageManager.LoadPage<DataPage>(1);
    Assert.Equal(1u, loaded.Index);
    Assert.Equal(PageType.Data, loaded.Type);
    Assert.Equal(7u, loaded.NextPageIndex);
    Assert.Equal(3u, loaded.OwningCollectionId);
    Assert.Equal(3, loaded.ItemsCount);
    Assert.Equal(expectedFreeBytes, loaded.FreeBytes);
    Assert.Equal([11, 22, 33], loaded.GetItems().Select(item => item.ReadInt(0, out _)));
  }

  [Fact]
  public void RootPageFieldsSurviveASaveAndLoad() {
    using var file = new TempDatabaseFile();
    using var disk = new DiskManager(file.Path);
    var pageManager = new PageManager(disk);
    var createdAt = new DateTime(2026, 9, 4, 18, 25, 0, DateTimeKind.Utc);

    var page = pageManager.CreateNewMemoryPage<RootPage>(PageType.Root, TokkConstants.RootPageIndex);
    page.CreatedAt = createdAt;
    page.CollectionsFirstPageId = 1;
    page.CollectionsPrimaryIndexRoot = 4;
    page.LastAllocatedPageId = 9;
    pageManager.SavePages(page);

    var loaded = pageManager.LoadPage<RootPage>(TokkConstants.RootPageIndex);
    Assert.Equal(PageType.Root, loaded.Type);
    Assert.Equal(TokkConstants.RootPageIndex, loaded.Index);
    Assert.Equal(RootPage.ExpectedMagicNumber, loaded.MagicNumber);
    Assert.Equal(RootPage.CurrentFormatVersion, loaded.FormatVersion);
    Assert.Equal(TokkConstants.DefaultPageSize, loaded.PageSize);
    Assert.Equal(createdAt, loaded.CreatedAt);
    Assert.Equal(1u, loaded.CollectionsFirstPageId);
    Assert.Equal(4u, loaded.CollectionsPrimaryIndexRoot);
    Assert.Equal(9u, loaded.LastAllocatedPageId);
  }

  [Fact]
  public void MetadataPageEntitiesSurviveASaveAndLoad() {
    using var file = new TempDatabaseFile();
    using var disk = new DiskManager(file.Path);
    var pageManager = new PageManager(disk);

    var page = pageManager.CreateNewMemoryPage<MetadataPage>(PageType.Metadata, 1);
    page.Entities.Add("Person", new MetadataEntity(1, 2, 4));
    page.Entities.Add("Tag", new MetadataEntity(2, 5, 5));
    page.EntitiesCount = (byte)page.Entities.Count;
    pageManager.SavePages(page);

    var loaded = pageManager.LoadPage<MetadataPage>(1);
    Assert.Equal(PageType.Metadata, loaded.Type);
    Assert.Equal(0u, loaded.OwningCollectionId);
    Assert.Equal(2, loaded.Entities.Count);
    Assert.Equal(1u, loaded.Entities["Person"].Id);
    Assert.Equal(2u, loaded.Entities["Person"].DataFirstPageId);
    Assert.Equal(4u, loaded.Entities["Person"].DataLastPageId);
    Assert.Equal(2u, loaded.Entities["Tag"].Id);
    Assert.Equal(5u, loaded.Entities["Tag"].DataFirstPageId);
  }

  [Fact]
  public void PagesAreWrittenToTheSlotMatchingTheirIndex() {
    using var file = new TempDatabaseFile();
    using var disk = new DiskManager(file.Path);
    var pageManager = new PageManager(disk);

    var first = pageManager.CreateNewMemoryPage<DataPage>(PageType.Data, 1);
    var second = pageManager.CreateNewMemoryPage<DataPage>(PageType.Data, 2);
    first.RegisterItem(4).WriteInt(111, 0, out _);
    second.RegisterItem(4).WriteInt(222, 0, out _);
    pageManager.SavePages(first, second);

    Assert.Equal(3, file.PageCount);
    Assert.Equal(111, pageManager.LoadPage<DataPage>(1).GetItems().Single().ReadInt(0, out _));
    Assert.Equal(222, pageManager.LoadPage<DataPage>(2).GetItems().Single().ReadInt(0, out _));
  }
}
