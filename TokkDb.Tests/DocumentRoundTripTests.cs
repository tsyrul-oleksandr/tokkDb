using TokkDb.Buffer;
using TokkDb.Configuration;
using TokkDb.Documents;
using TokkDb.Documents.Serializers;
using TokkDb.Documents.Values;
using TokkDb.Pages;
using TokkDb.Pages.Managers;
using TokkDb.Values;
using Xunit;

namespace TokkDb.Tests;

public class DocumentRoundTripTests {
  private static BufferSlice NewSlice() {
    return new BufferSlice(new byte[TokkConstants.DefaultPageSize]);
  }

  private static IDocumentValue RoundTrip(IDocumentValue value) {
    var slice = NewSlice();
    var writer = new BufferWriter(slice);
    writer.Write(value);
    return new BufferReader(slice).Read();
  }

  [Fact]
  public void NullValueRoundTrips() {
    Assert.Equal(ValueTypeEnum.Null, RoundTrip(new NullDocumentValue()).Type);
  }

  [Theory]
  [InlineData(int.MinValue)]
  [InlineData(0)]
  [InlineData(29)]
  [InlineData(int.MaxValue)]
  public void IntValueRoundTrips(int value) {
    var read = Assert.IsType<IntDocumentValue>(RoundTrip(new IntDocumentValue(value)));
    Assert.Equal(value, read.Value);
  }

  [Theory]
  [InlineData("")]
  [InlineData("ST-111111")]
  [InlineData("Олександр")]
  public void StringValueRoundTrips(string value) {
    var read = Assert.IsType<StringDocumentValue>(RoundTrip(new StringDocumentValue(value)));
    Assert.Equal(value, read.Value);
  }

  [Fact]
  public void UlidValueRoundTrips() {
    var value = Ulid.NewUlid();
    var read = Assert.IsType<UlidDocumentValue>(RoundTrip(new UlidDocumentValue(value)));
    Assert.Equal(value, read.Value);
  }

  [Fact]
  public void EmptyArrayValueRoundTrips() {
    var read = Assert.IsType<ArrayDocumentValue>(RoundTrip(new ArrayDocumentValue()));
    Assert.Empty(read.Values);
  }

  [Fact]
  public void EmptyObjectValueRoundTrips() {
    var read = Assert.IsType<ObjectDocumentValue>(RoundTrip(new ObjectDocumentValue()));
    Assert.Empty(read.Values);
  }

  [Fact]
  public void NestedObjectAndArrayValuesRoundTrip() {
    var value = new ObjectDocumentValue(new Dictionary<string, IDocumentValue> {
      ["Name"] = new StringDocumentValue("Ivan"),
      ["Age"] = new IntDocumentValue(29),
      ["Passport"] = new ObjectDocumentValue(new Dictionary<string, IDocumentValue> {
        ["Code"] = new StringDocumentValue("ST-111111")
      }),
      ["Missing"] = new NullDocumentValue(),
      ["Tags"] = new ArrayDocumentValue([
        new ObjectDocumentValue(new Dictionary<string, IDocumentValue> { ["Name"] = new StringDocumentValue("tag1") }),
        new ObjectDocumentValue(new Dictionary<string, IDocumentValue> { ["Name"] = new StringDocumentValue("tag2") })
      ])
    });

    var read = Assert.IsType<ObjectDocumentValue>(RoundTrip(value));
    Assert.Equal("Ivan", Assert.IsType<StringDocumentValue>(read["Name"]).Value);
    Assert.Equal(29, Assert.IsType<IntDocumentValue>(read["Age"]).Value);
    Assert.Equal(ValueTypeEnum.Null, read["Missing"].Type);
    var passport = Assert.IsType<ObjectDocumentValue>(read["Passport"]);
    Assert.Equal("ST-111111", Assert.IsType<StringDocumentValue>(passport["Code"]).Value);
    var tags = Assert.IsType<ArrayDocumentValue>(read["Tags"]);
    Assert.Equal(2, tags.Values.Length);
    Assert.Equal("tag2", Assert.IsType<StringDocumentValue>(((ObjectDocumentValue)tags.Values[1])["Name"]).Value);
  }

  [Fact]
  public void ObjectDocumentRoundTripsThroughABuffer() {
    var serializer = new DocumentSerializer<Person>();
    var identifier = Ulid.NewUlid();
    var document = serializer.Create(TestPeople.Ivan(), identifier);

    var slice = NewSlice();
    ObjectDocumentUtilities.ToBuffer(document, slice);
    var read = ObjectDocumentUtilities.FromBuffer(slice);

    Assert.Equal(identifier, Assert.IsType<UlidDocumentValue>(read.IdentifierValue).Value);
    var person = serializer.Deserialize(read);
    Assert.Equal("Ivan", person.Name);
    Assert.Equal(29, person.Age);
    Assert.Equal("ST-111111", person.Passport.Code);
    Assert.Equal(["tag1", "tag2"], person.Tags.Select(tag => tag.Name));
  }

  [Fact]
  public void GetBytesLengthMatchesWhatWritingActuallyConsumes() {
    var document = new DocumentSerializer<Person>().Create(TestPeople.Ivan(), Ulid.NewUlid());
    var length = ObjectDocumentUtilities.GetBytesLength(document);

    var slice = NewSlice();
    var writer = new BufferWriter(slice);
    document.Write(writer);

    Assert.Equal(writer.Position, length);
  }

  //VR-11: the header every stored record carries, from the first release.
  [Fact]
  public void TheRecordHeaderRoundTripsWithItsDocument() {
    var serializer = new DocumentSerializer<Person>();
    var recordId = Ulid.NewUlid();
    var document = serializer.Create(TestPeople.Ivan(), recordId);
    var header = RecordHeader.ForNewRecord(recordId, schemaVersion: 7);

    var slice = NewSlice();
    StoredRecordUtilities.ToBuffer(header, document, slice);
    var read = StoredRecordUtilities.FromBuffer(slice);

    Assert.Equal(recordId, read.Header.RecordId);
    Assert.Equal(header.VersionId, read.Header.VersionId);
    Assert.Equal(RecordFlags.Live, read.Header.Flags);
    Assert.Equal(7, read.Header.SchemaVersion);
    //Written as zero in this pass, but written, so that versioning is not a format break.
    Assert.Equal(default, read.Header.PreviousVersion);

    var person = serializer.Deserialize(read.Document);
    Assert.Equal("Ivan", person.Name);
    Assert.Equal(29, person.Age);
    Assert.Equal("ST-111111", person.Passport.Code);
    Assert.Equal(["tag1", "tag2"], person.Tags.Select(tag => tag.Name));
  }

  [Fact]
  public void TheRecordIdentifierIsTheDocumentIdentifierAndIsStoredOnce() {
    var recordId = Ulid.NewUlid();
    var document = new DocumentSerializer<Person>().Create(TestPeople.Ivan(), recordId);
    var header = RecordHeader.ForNewRecord(recordId);

    var slice = NewSlice();
    StoredRecordUtilities.ToBuffer(header, document, slice);
    var read = StoredRecordUtilities.FromBuffer(slice);

    //D-1: one identity for the whole system, carried by the header and handed back to the
    //document rather than written a second time beside it.
    Assert.Equal(recordId, Assert.IsType<UlidDocumentValue>(read.Document.IdentifierValue).Value);
    Assert.NotEqual(read.Header.RecordId, read.Header.VersionId);

    var bodyOnly = ObjectDocumentUtilities.GetBytesLength(document);
    var withHeader = StoredRecordUtilities.GetBytesLength(header, document);
    //The header costs 41 bytes and gives back the 17 the duplicated identifier took.
    Assert.Equal(RecordHeader.ByteSize, 41);
    Assert.Equal(bodyOnly + RecordHeader.ByteSize - (TypesConstants.UlidByteSize + 1), withHeader);
  }

  [Fact]
  public void EveryPartOfTheHeaderSurvivesIndependently() {
    var header = new RecordHeader {
      RecordId = Ulid.NewUlid(),
      VersionId = Ulid.NewUlid(),
      PreviousVersion = new DocumentAddress(4242, 17),
      Flags = RecordFlags.Superseded | RecordFlags.Deleted,
      SchemaVersion = ushort.MaxValue
    };
    var document = new DocumentSerializer<Person>().Create(TestPeople.Ivan(), header.RecordId);

    var slice = NewSlice();
    StoredRecordUtilities.ToBuffer(header, document, slice);
    var read = StoredRecordUtilities.FromBuffer(slice).Header;

    //previousVersion is unread by the engine in this pass, but it has to survive a write and
    //a read or the door it holds open would not be there when versioning arrives.
    Assert.Equal(4242u, read.PreviousVersion.PageIndex);
    Assert.Equal((ushort)17, read.PreviousVersion.SlotIndex);
    Assert.Equal(header.VersionId, read.VersionId);
    Assert.Equal(RecordFlags.Superseded | RecordFlags.Deleted, read.Flags);
    Assert.Equal(ushort.MaxValue, read.SchemaVersion);
    Assert.False(read.IsLive);
  }

  [Fact]
  public void AFreshVersionIdentifierIsMintedOnEveryWrite() {
    var recordId = Ulid.NewUlid();
    var first = RecordHeader.ForNewRecord(recordId);
    var second = RecordHeader.ForNewRecord(recordId);

    Assert.Equal(first.RecordId, second.RecordId);
    Assert.NotEqual(first.VersionId, second.VersionId);
    Assert.Equal(RecordFlags.Live, first.Flags);
  }

  [Fact]
  public void DeserializeRestoresNullReferences() {
    var serializer = new DocumentSerializer<Person>();
    var source = new Person { Id = 3, Name = "Nobody", Age = 0, Passport = null, Tags = [] };
    var document = serializer.Create(source, Ulid.NewUlid());

    var slice = NewSlice();
    ObjectDocumentUtilities.ToBuffer(document, slice);
    var person = serializer.Deserialize(ObjectDocumentUtilities.FromBuffer(slice));

    Assert.Null(person.Passport);
    Assert.Empty(person.Tags);
    Assert.Equal("Nobody", person.Name);
  }
}
