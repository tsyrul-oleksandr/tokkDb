using System.Text;
using TokkDb.Configuration;
using TokkDb.Disk;
using TokkDb.Pages;
using Xunit;

namespace TokkDb.Tests;

public class FileFormatTests {
  //Offsets inside page 0: the page index and page type of every page come first.
  private const int MagicNumberPosition = 5;
  private const int FormatVersionPosition = MagicNumberPosition + RootPage.MagicNumberByteSize;
  private const int PageSizePosition = FormatVersionPosition + 2;

  private static void CreateDatabase(TempDatabaseFile file) {
    using var db = new TokkDbConnection(file.Path);
    db.CreateDatabase(config => config.CreateEntity<Person>());
  }

  private static void PatchByte(TempDatabaseFile file, int position, byte value) {
    var bytes = File.ReadAllBytes(file.Path);
    bytes[position] = value;
    File.WriteAllBytes(file.Path, bytes);
  }

  [Fact]
  public void ANewFileStartsWithTheMagicNumberVersionAndPageSize() {
    using var file = new TempDatabaseFile();
    CreateDatabase(file);

    var bytes = File.ReadAllBytes(file.Path);
    Assert.Equal(RootPage.ExpectedMagicNumber,
      Encoding.ASCII.GetString(bytes, MagicNumberPosition, RootPage.MagicNumberByteSize));
    Assert.Equal(RootPage.CurrentFormatVersion, BitConverter.ToUInt16(bytes, FormatVersionPosition));
    Assert.Equal(TokkConstants.DefaultPageSize, BitConverter.ToUInt16(bytes, PageSizePosition));
    Assert.Equal((byte)PageType.Root, bytes[4]);
  }

  [Fact]
  public void ANewDatabaseRoundTripsThroughCreateCloseAndOpen() {
    using var file = new TempDatabaseFile();
    using (var db = new TokkDbConnection(file.Path)) {
      db.CreateDatabase(config => config.CreateEntity<Person>());
      db.Entities<Person>().Insert(TestPeople.Ivan());
    }

    using var reopened = new TokkDbConnection(file.Path);
    reopened.Load();
    Assert.Equal("Ivan", Assert.Single(reopened.Entities<Person>().GetAll()).Name);
  }

  [Fact]
  public void TheReopenedFileKeepsItsRootPage() {
    using var file = new TempDatabaseFile();
    CreateDatabase(file);

    using var disk = new DiskManager(file.Path);
    var pageManager = new PageManager(disk);
    var prefix = RootPage.ReadPrefix(pageManager.ReadPrefix(RootPage.PrefixByteSize));
    Assert.Equal(RootPage.ExpectedMagicNumber, prefix.MagicNumber);
    Assert.Equal(RootPage.CurrentFormatVersion, prefix.FormatVersion);
    Assert.Equal(TokkConstants.DefaultPageSize, prefix.PageSize);

    var rootPage = pageManager.LoadPage<RootPage>(TokkConstants.RootPageIndex);
    //Page 0 is the root, page 1 the collections catalogue it points at.
    Assert.Equal(1u, rootPage.CollectionsFirstPageId);
    Assert.Equal(0u, rootPage.CollectionsPrimaryIndexRoot);
    Assert.Equal(1u, rootPage.LastAllocatedPageId);
    Assert.InRange(rootPage.CreatedAt, DateTime.UtcNow.AddMinutes(-5), DateTime.UtcNow.AddMinutes(5));
  }

  [Fact]
  public void AFileWithABumpedFormatVersionIsRefusedByNameAndVersion() {
    using var file = new TempDatabaseFile();
    CreateDatabase(file);
    PatchByte(file, FormatVersionPosition, (byte)(RootPage.CurrentFormatVersion + 1));

    using var reopened = new TokkDbConnection(file.Path);
    var exception = Assert.Throws<UnsupportedFormatVersionException>(reopened.Load);

    Assert.Equal(RootPage.ExpectedMagicNumber, exception.FoundMagicNumber);
    Assert.Equal((ushort)(RootPage.CurrentFormatVersion + 1), exception.FoundFormatVersion);
    Assert.Equal(RootPage.CurrentFormatVersion, exception.ExpectedFormatVersion);
    Assert.Contains(RootPage.ExpectedMagicNumber, exception.Message);
    Assert.Contains($"{RootPage.CurrentFormatVersion + 1}", exception.Message);
    Assert.Contains($"expected {RootPage.CurrentFormatVersion}", exception.Message);
  }

  [Fact]
  public void AFileWithAnUnknownMagicNumberIsRefused() {
    using var file = new TempDatabaseFile();
    CreateDatabase(file);
    PatchByte(file, MagicNumberPosition + RootPage.MagicNumberByteSize - 1, (byte)'9');

    using var reopened = new TokkDbConnection(file.Path);
    var exception = Assert.Throws<UnsupportedFormatVersionException>(reopened.Load);

    Assert.Equal("TOKKDB09", exception.FoundMagicNumber);
    Assert.Equal(RootPage.ExpectedMagicNumber, exception.ExpectedMagicNumber);
    Assert.Null(exception.FoundFormatVersion);
    Assert.Contains("TOKKDB09", exception.Message);
    Assert.Contains(RootPage.ExpectedMagicNumber, exception.Message);
  }

  [Fact]
  public void AFileThatIsNotADatabaseIsRefusedInsteadOfParsed() {
    using var file = new TempDatabaseFile();
    File.WriteAllBytes(file.Path, Enumerable.Repeat((byte)0xFF, TokkConstants.DefaultPageSize).ToArray());

    using var connection = new TokkDbConnection(file.Path);
    var exception = Assert.Throws<UnsupportedFormatVersionException>(connection.Load);
    Assert.Contains(RootPage.ExpectedMagicNumber, exception.Message);
  }

  [Fact]
  public void AFileTooShortToCarryARootPageIsRefused() {
    using var file = new TempDatabaseFile();
    File.WriteAllBytes(file.Path, "TOKK"u8.ToArray());

    using var connection = new TokkDbConnection(file.Path);
    Assert.Throws<UnsupportedFormatVersionException>(connection.Load);
  }
}
