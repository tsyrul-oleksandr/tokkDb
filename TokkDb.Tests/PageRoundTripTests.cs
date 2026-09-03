using TokkDb.Disk;
using TokkDb.Pages;
using Xunit;

namespace TokkDb.Tests;

public class PageRoundTripTests {
  [Fact]
  public void DataPageHeaderAndItemsSurviveASaveAndLoad() {
    using var file = new TempDatabaseFile();
    var pageManager = new PageManager(new DiskManager(file.Path));

    var page = pageManager.CreateNewMemoryPage<DataPage>(PageType.Data, 1);
    page.NextPageIndex = 7;
    foreach (var marker in new[] { 11, 22, 33 }) {
      page.RegisterItem(4).WriteInt(marker, 0, out _);
    }
    var expectedFreeBytes = page.FreeBytes;
    pageManager.SavePages(page);

    var loaded = pageManager.LoadPage<DataPage>(1);
    Assert.Equal(1u, loaded.Index);
    Assert.Equal(PageType.Data, loaded.Type);
    Assert.Equal(7u, loaded.NextPageIndex);
    Assert.Equal(3, loaded.ItemsCount);
    Assert.Equal(expectedFreeBytes, loaded.FreeBytes);
    Assert.Equal([11, 22, 33], loaded.GetItems().Select(item => item.ReadInt(0, out _)));
  }

  [Fact]
  public void MetadataPageEntitiesSurviveASaveAndLoad() {
    using var file = new TempDatabaseFile();
    var pageManager = new PageManager(new DiskManager(file.Path));
    var createdAt = new DateTime(2025, 3, 24, 18, 25, 0, DateTimeKind.Utc);

    var page = pageManager.CreateNewMemoryPage<MetadataPage>(PageType.Metadata, 0);
    page.CreatedAt = createdAt;
    page.LastPageId = 9;
    page.Entities.Add("Person", new MetadataEntity(1, 4));
    page.Entities.Add("Tag", new MetadataEntity(5, 5));
    page.EntitiesCount = (byte)page.Entities.Count;
    pageManager.SavePages(page);

    var loaded = pageManager.LoadPage<MetadataPage>(0);
    Assert.Equal(PageType.Metadata, loaded.Type);
    Assert.Equal(createdAt, loaded.CreatedAt);
    Assert.Equal(9u, loaded.LastPageId);
    Assert.Equal(2, loaded.Entities.Count);
    Assert.Equal(1u, loaded.Entities["Person"].DataFirstPageId);
    Assert.Equal(4u, loaded.Entities["Person"].DataLastPageId);
    Assert.Equal(5u, loaded.Entities["Tag"].DataFirstPageId);
  }

  [Fact]
  public void PagesAreWrittenToTheSlotMatchingTheirIndex() {
    using var file = new TempDatabaseFile();
    var pageManager = new PageManager(new DiskManager(file.Path));

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
