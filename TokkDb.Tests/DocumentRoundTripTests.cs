using TokkDb.Buffer;
using TokkDb.Configuration;
using TokkDb.Documents;
using TokkDb.Documents.Serializers;
using TokkDb.Documents.Values;
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
