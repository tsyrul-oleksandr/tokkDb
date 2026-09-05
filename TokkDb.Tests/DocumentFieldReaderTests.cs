using TokkDb.Buffer;
using TokkDb.Documents;
using TokkDb.Documents.Path.Expressions;
using TokkDb.Documents.Serializers;
using TokkDb.Documents.Values;
using TokkDb.Values;
using Xunit;

namespace TokkDb.Tests;

//Reading one field out of a serialized document without parsing the rest.
//
//The arithmetic here has to agree exactly with what the writer put down: a width read wrong
//by one byte does not fail, it silently reads the next field as something else. So every
//test writes with the real writer and reads back with the reader.
public class DocumentFieldReaderTests {
  private static BufferSlice Write(IDocumentValue value) {
    var counting = new BufferWriter(new CountingBufferSlice());
    counting.Write(value);
    var buffer = new BufferSlice(new byte[counting.Position]);
    new BufferWriter(buffer).Write(value);
    return buffer;
  }

  private static ObjectDocumentValue Everything() {
    return new ObjectDocumentValue(new Dictionary<string, IDocumentValue> {
      ["nothing"] = new NullDocumentValue(),
      ["flag"] = new BooleanDocumentValue(true),
      ["count"] = new IntDocumentValue(-42),
      ["size"] = new UIntDocumentValue(4_000_000_000),
      ["identity"] = new UlidDocumentValue(Ulid.NewUlid()),
      ["name"] = new StringDocumentValue("Олена Ковальчук"),
      ["nested"] = new ObjectDocumentValue(new Dictionary<string, IDocumentValue> {
        ["deep"] = new StringDocumentValue("value"),
        ["deeper"] = new ObjectDocumentValue(new Dictionary<string, IDocumentValue> {
          ["deepest"] = new IntDocumentValue(1)
        })
      }),
      ["tags"] = new ArrayDocumentValue([
        new StringDocumentValue("a"),
        new ObjectDocumentValue(new Dictionary<string, IDocumentValue> { ["k"] = new IntDocumentValue(2) })
      ]),
      //Last on purpose: reaching it means every other type was stepped over correctly.
      ["last"] = new StringDocumentValue("end")
    });
  }

  private static IDocumentValue Read(BufferSlice buffer, string field) {
    return DocumentFieldReader.Read(buffer, 0, new DocumentFieldReader.FieldName(field));
  }

  [Theory]
  [InlineData("nothing")]
  [InlineData("flag")]
  [InlineData("count")]
  [InlineData("size")]
  [InlineData("identity")]
  [InlineData("name")]
  [InlineData("nested")]
  [InlineData("tags")]
  [InlineData("last")]
  public void EveryFieldIsReachableWhicheverTypesPrecedeIt(string field) {
    var document = Everything();
    var buffer = Write(document);

    var value = Read(buffer, field);

    Assert.NotNull(value);
    Assert.Equal(document.Values[field].Type, value.Type);
  }

  [Fact]
  public void TheValueReadIsTheValueWritten() {
    var document = Everything();
    var buffer = Write(document);

    Assert.Equal(-42, Assert.IsType<IntDocumentValue>(Read(buffer, "count")).Value);
    Assert.Equal(4_000_000_000u, Assert.IsType<UIntDocumentValue>(Read(buffer, "size")).Value);
    Assert.True(Assert.IsType<BooleanDocumentValue>(Read(buffer, "flag")).Value);
    Assert.Equal("Олена Ковальчук", Assert.IsType<StringDocumentValue>(Read(buffer, "name")).Value);
    Assert.Equal("end", Assert.IsType<StringDocumentValue>(Read(buffer, "last")).Value);
    Assert.Equal(((UlidDocumentValue)document.Values["identity"]).Value,
      Assert.IsType<UlidDocumentValue>(Read(buffer, "identity")).Value);
  }

  //A field that is not there is not an error. A record written before a column was added has
  //none of it, and a query over that column has to say "no" rather than throw.
  [Fact]
  public void AMissingFieldReadsAsNothing() {
    Assert.Null(Read(Write(Everything()), "absent"));
  }

  //The skipping is the part that can be wrong without failing. Walking the field names is the
  //same arithmetic as skipping to one, so if the widths are wrong this comes back mangled.
  [Fact]
  public void SteppingOverEveryFieldLandsOnEveryName() {
    var document = Everything();
    Assert.Equal(document.Values.Keys, DocumentFieldReader.FieldNames(Write(document), 0));
  }

  //A name that is a prefix of another must not match it: the length is compared, not just the
  //bytes.
  [Fact]
  public void AFieldNameIsMatchedWholeRatherThanByPrefix() {
    var buffer = Write(new ObjectDocumentValue(new Dictionary<string, IDocumentValue> {
      ["Name"] = new StringDocumentValue("short"),
      ["NameSuffix"] = new StringDocumentValue("long")
    }));

    Assert.Equal("short", Assert.IsType<StringDocumentValue>(Read(buffer, "Name")).Value);
    Assert.Equal("long", Assert.IsType<StringDocumentValue>(Read(buffer, "NameSuffix")).Value);
    Assert.Null(Read(buffer, "Nam"));
  }

  //Non-ASCII names are compared as their stored UTF-8 bytes, so a name whose characters are
  //more than one byte each has to match on the bytes rather than on a character count.
  [Fact]
  public void AFieldNameOutsideAsciiIsMatchedOnItsStoredBytes() {
    var buffer = Write(new ObjectDocumentValue(new Dictionary<string, IDocumentValue> {
      ["Ім'я"] = new StringDocumentValue("Олена"),
      ["Вік"] = new IntDocumentValue(31)
    }));

    Assert.Equal("Олена", Assert.IsType<StringDocumentValue>(Read(buffer, "Ім'я")).Value);
    Assert.Equal(31, Assert.IsType<IntDocumentValue>(Read(buffer, "Вік")).Value);
  }

  [Fact]
  public void AnEmptyObjectHasNoFields() {
    Assert.Null(Read(Write(new ObjectDocumentValue()), "anything"));
  }

  //The buffer-backed view and a parsed object have to be the same thing to a predicate, or
  //the query would mean one thing on the page and another in memory.
  [Fact]
  public void TheBufferedViewAnswersExactlyAsTheParsedObjectDoes() {
    var document = Everything();
    var buffered = new BufferedObjectValue(Write(document), 0);

    foreach (var field in document.Values.Keys) {
      Assert.Equal(document.GetField(field).Type, buffered.GetField(field).Type);
    }
    Assert.Null(buffered.GetField("absent"));
    Assert.Equal(ValueTypeEnum.Object, buffered.Type);
  }

  //The point of the type: a comparison written against a document runs unchanged against the
  //buffer, because both are a field source.
  [Fact]
  public void APredicateEvaluatesAgainstTheBufferAsItWouldAgainstTheDocument() {
    var document = Everything();
    var comparison = new ComparisonExpression(
      new PropertyExpression("count") { Parent = new RootExpression() },
      ComparisonOperator.Equal, new ConstantExpression(new IntDocumentValue(-42)), ValueTypeEnum.Int);

    var onTheDocument = comparison.Execute(document, document);
    var buffered = new BufferedObjectValue(Write(document), 0);
    var onTheBuffer = comparison.Execute(buffered, buffered);

    Assert.True(Assert.IsType<BooleanDocumentValue>(onTheDocument).Value);
    Assert.True(Assert.IsType<BooleanDocumentValue>(onTheBuffer).Value);
  }

  //It is a view of somebody else's bytes, so the write half of the interface refuses rather
  //than doing something half-right.
  [Fact]
  public void TheBufferedViewCannotBeWritten() {
    var buffered = new BufferedObjectValue(Write(Everything()), 0);
    Assert.Throws<NotSupportedException>(() => buffered.WriteValue(new BufferWriter(new CountingBufferSlice())));
  }
}
