using TokkDb.Buffer;
using TokkDb.Configuration;
using TokkDb.Disk;
using TokkDb.Documents;
using TokkDb.Documents.Serializers;
using TokkDb.Pages;
using Xunit;
using Xunit.Abstractions;

namespace TokkDb.Tests;

//A record whose body is far past what one page holds.
public class LargeDocument {
  public int Id { get; set; }
  public string Title { get; set; } = string.Empty;
  public string Text { get; set; } = string.Empty;
  public Section[] Sections { get; set; } = [];

  //Deterministic content, so "byte identical" means something when it is read back.
  public static LargeDocument OfSize(int id, int textLength, int sections = 8) {
    var text = new char[textLength];
    for (var i = 0; i < textLength; i++) {
      //Non-ASCII on purpose: the length in bytes must not be assumed to be the length in chars.
      text[i] = (i % 97) == 0 ? 'Ї' : (char)('a' + i % 26);
    }
    return new LargeDocument {
      Id = id,
      Title = $"Document {id}",
      Text = new string(text),
      Sections = Enumerable.Range(0, sections)
        .Select(i => new Section { Heading = $"Section {i}", Body = new string(text, 0, textLength / 32) })
        .ToArray()
    };
  }
}

public class Section {
  public string Heading { get; set; } = string.Empty;
  public string Body { get; set; } = string.Empty;
}

public class OverflowTests {
  //Comfortably past a page, and past what a ushort could have counted.
  private const int OneMegabyte = 1024 * 1024;

  private readonly ITestOutputHelper _output;

  public OverflowTests(ITestOutputHelper output) {
    _output = output;
  }

  private static void AssertSame(LargeDocument expected, LargeDocument actual) {
    Assert.Equal(expected.Id, actual.Id);
    Assert.Equal(expected.Title, actual.Title);
    Assert.Equal(expected.Text, actual.Text);
    Assert.Equal(expected.Sections.Length, actual.Sections.Length);
    for (var i = 0; i < expected.Sections.Length; i++) {
      Assert.Equal(expected.Sections[i].Heading, actual.Sections[i].Heading);
      Assert.Equal(expected.Sections[i].Body, actual.Sections[i].Body);
    }
  }

  //ST-5's acceptance criterion and this step's done-when.
  [Fact]
  public void AMegabyteDocumentInsertsReadsBackUpdatesAndDeletes() {
    var original = LargeDocument.OfSize(1, OneMegabyte);
    var replacement = LargeDocument.OfSize(2, OneMegabyte + 4096);
    var documentBytes = ObjectDocumentUtilities.GetBytesLength(
      new DocumentSerializer<LargeDocument>().Create(original, Ulid.NewUlid()));
    _output.WriteLine($"document is {documentBytes:N0} bytes, page size {TokkConstants.DefaultPageSize}");
    Assert.True(documentBytes > OneMegabyte, "the test document is not actually a megabyte");

    using var file = new TempDatabaseFile();
    Ulid recordId;
    using (var db = new TokkDbConnection(file.Path)) {
      db.CreateDatabase(config => config.CreateEntity<LargeDocument>());
      var entities = db.Entities<LargeDocument>();
      entities.Insert(original);
      recordId = Assert.Single(entities.GetAllRecords()).RecordId;
    }

    //Read back after a close and reopen, so the chain is followed off the file rather than
    //out of anything still in memory.
    using (var reopened = new TokkDbConnection(file.Path)) {
      reopened.Load();
      AssertSame(original, Assert.Single(reopened.Entities<LargeDocument>().GetAll()));

      reopened.Entities<LargeDocument>().Update(recordId, replacement);
      AssertSame(replacement, Assert.Single(reopened.Entities<LargeDocument>().GetAll()));
    }

    using (var reopened = new TokkDbConnection(file.Path)) {
      reopened.Load();
      AssertSame(replacement, Assert.Single(reopened.Entities<LargeDocument>().GetAll()));
      reopened.Entities<LargeDocument>().Delete(recordId);
      Assert.Empty(reopened.Entities<LargeDocument>().GetAll());
      Assert.Equal(0u, reopened.Collection(nameof(LargeDocument)).RecordCount);
    }

    using var check = new TokkDbConnection(file.Path);
    check.Load();
    Assert.Empty(check.Entities<LargeDocument>().GetAll());
  }

  [Fact]
  public void TheHeaderOfAnOverflowedRecordStaysOnItsDataPage() {
    using var file = new TempDatabaseFile();
    using var db = new TokkDbConnection(file.Path);
    db.CreateDatabase(config => config.CreateEntity<LargeDocument>());
    db.Entities<LargeDocument>().Insert(LargeDocument.OfSize(1, 200_000));
    var recordId = Assert.Single(db.Entities<LargeDocument>().GetAllRecords()).RecordId;

    var headers = ReadDataPageHeaders(file, nameof(LargeDocument));
    var header = Assert.Single(headers);
    //VR-13 and ST-5 together: a scan reads the flags and the identifier off the page, and
    //only follows the chain when it wants the body.
    Assert.Equal(recordId, header.RecordId);
    Assert.True(header.IsLive);
    Assert.True(header.Flags.HasFlag(RecordFlags.HasOverflow));
  }

  [Fact]
  public void ADeletedChainIsUsedAgainRatherThanGrowingTheFile() {
    using var file = new TempDatabaseFile();
    using var db = new TokkDbConnection(file.Path);
    db.CreateDatabase(config => config.CreateEntity<LargeDocument>());
    var entities = db.Entities<LargeDocument>();

    entities.Insert(LargeDocument.OfSize(1, 300_000));
    var pagesAfterFirst = file.PageCount;

    for (var round = 0; round < 4; round++) {
      var record = Assert.Single(entities.GetAllRecords());
      entities.Delete(record.RecordId);
      entities.Insert(LargeDocument.OfSize(round + 2, 300_000));
    }

    _output.WriteLine($"pages after the first document: {pagesAfterFirst}, after four replacements: {file.PageCount}");
    Assert.Single(entities.GetAll());
    //Four documents' worth of chain passed through the file without it growing by four.
    Assert.True(file.PageCount <= pagesAfterFirst + 2,
      $"the file grew from {pagesAfterFirst} to {file.PageCount} pages replacing one large document");
  }

  [Fact]
  public void RecordsEitherSideOfTheLimitBothRoundTrip() {
    using var file = new TempDatabaseFile();
    using var db = new TokkDbConnection(file.Path);
    db.CreateDatabase(config => config.CreateEntity<LargeDocument>());
    var entities = db.Entities<LargeDocument>();

    //One that fits a page and one that does not, so the boundary itself is exercised.
    var small = LargeDocument.OfSize(1, 1000, sections: 1);
    var large = LargeDocument.OfSize(2, 9000, sections: 1);
    entities.Insert(small);
    entities.Insert(large);

    var read = entities.GetAll().OrderBy(document => document.Id).ToList();
    Assert.Equal(2, read.Count);
    AssertSame(small, read[0]);
    AssertSame(large, read[1]);
  }

  //Measuring a document must not allocate a page, nor stop counting at one.
  [Fact]
  public void ADocumentIsMeasuredWithoutAPageSizedBufferAndWithoutACap() {
    var document = new DocumentSerializer<LargeDocument>()
      .Create(LargeDocument.OfSize(1, OneMegabyte), Ulid.NewUlid());

    var measured = ObjectDocumentUtilities.GetBytesLength(document);
    Assert.True(measured > ushort.MaxValue, $"{measured} bytes would have fitted the old ushort");

    //The count is what writing it actually consumes.
    var slice = new BufferSlice(new byte[measured]);
    var writer = new BufferWriter(slice);
    document.Write(writer);
    Assert.Equal(measured, writer.Position);
  }

  [Fact]
  public void TheCountingBufferAgreesWithARealOne() {
    var document = new DocumentSerializer<Person>().Create(TestPeople.Ivan(), Ulid.NewUlid());
    var counted = ObjectDocumentUtilities.GetBytesLength(document);

    var slice = new BufferSlice(new byte[TokkConstants.DefaultPageSize]);
    var writer = new BufferWriter(slice);
    document.Write(writer);

    Assert.Equal(writer.Position, counted);
  }

  private static IReadOnlyList<RecordHeader> ReadDataPageHeaders(TempDatabaseFile file, string collectionName) {
    using var reader = new TokkDbConnection(file.Path, TokkDbAccessMode.ReadOnly);
    reader.Load();
    var descriptor = reader.Collection(collectionName);

    using var disk = new DiskManager(file.Path, accessMode: TokkDbAccessMode.ReadOnly);
    var pageManager = new PageManager(disk);
    pageManager.SetPageSize(RootPage.ReadPrefix(pageManager.ReadPrefix(RootPage.PrefixByteSize)).PageSize);

    var headers = new List<RecordHeader>();
    var next = descriptor.DataFirstPage;
    while (next != default) {
      var page = pageManager.LoadPage<DataPage>(next);
      headers.AddRange(page.GetItems().Select(StoredRecordUtilities.ReadHeader));
      next = page.NextPageIndex;
    }
    return headers;
  }
}
