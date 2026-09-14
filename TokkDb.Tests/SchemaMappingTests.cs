using TokkDb.Documents;
using TokkDb.Documents.Delta;
using TokkDb.Documents.Values;
using TokkDb.Pages;
using TokkDb.Pages.Versions;
using TokkDb.Values;
using Xunit;
using static TokkDb.Tests.Delta.Values;

namespace TokkDb.Tests;

//RH-6 and V-10: each row of the mapping table, step by step, oldest first, with nothing dropped
//silently.
public class SchemaMappingTests {
  private static ObjectDocument Document(params (string Name, IDocumentValue Value)[] fields) {
    var document = new ObjectDocument();
    document.SetIdentifierValue(new UlidDocumentValue(Ulid.NewUlid()));
    document.SetValue(Obj(fields));
    return document;
  }

  private static Dictionary<string, IDocumentValue> Fields(SchemaMapping.MappedDocument mapped) {
    return ((ObjectDocumentValue)mapped.Document.Value).Values;
  }

  [Fact]
  public void AColumnAddedChangesNothing() {
    var mapped = SchemaMapping.MapDocument(Document(("a", Int(1))), []);
    Assert.Equal(["a"], Fields(mapped).Keys);
    Assert.Empty(mapped.Unmapped);
  }

  [Fact]
  public void ARenameGivesTheFirstPathSegmentTheNewName() {
    var mapped = SchemaMapping.MapDocument(Document(("Year", Int(2000)), ("Title", Str("t"))), [ColumnMigration.Rename(2, "Year", "Published")]);
    Assert.Equal(2000, Assert.IsType<IntDocumentValue>(Fields(mapped)["Published"]).Value);
    Assert.False(Fields(mapped).ContainsKey("Year"));
    Assert.Empty(mapped.Unmapped);

    var delta = new DocumentDelta([DeltaElement.Replace(DeltaPath.Parse("Year"), Int(2000), Int(2001)),
      DeltaElement.Replace(DeltaPath.Parse("Author.Year"), Int(1), Int(2))]);
    var mappedDelta = SchemaMapping.MapDelta(delta, [ColumnMigration.Rename(2, "Year", "Published")]);
    Assert.Equal("Published", mappedDelta.Delta.Elements[0].Path.Render());
    Assert.Equal("Author.Year", mappedDelta.Delta.Elements[1].Path.Render());
    Assert.Empty(mappedDelta.Unmapped);
  }

  [Fact]
  public void ALosslessRetypeConverts() {
    var mapped = SchemaMapping.MapDocument(Document(("Year", Int(2000))), [ColumnMigration.Retype(2, "Year", ValueTypeEnum.Long)]);
    Assert.Equal(2000L, Assert.IsType<LongDocumentValue>(Fields(mapped)["Year"]).Value);
    Assert.Empty(mapped.Unmapped);
    Assert.True(SchemaMapping.TryConvertLosslessly(Str("42"), ValueTypeEnum.Int, out var number));
    Assert.Equal(42, Assert.IsType<IntDocumentValue>(number).Value);
  }

  [Fact]
  public void ALossyRetypeIsReportedAndNotConverted() {
    var mapped = SchemaMapping.MapDocument(Document(("Year", Str("about 2000"))), [ColumnMigration.Retype(2, "Year", ValueTypeEnum.Int)]);
    Assert.False(Fields(mapped).ContainsKey("Year"));
    var report = Assert.Single(mapped.Unmapped);
    Assert.Equal(UnmappedReason.RetypeLossy, report.Reason);
    Assert.Equal("Year", report.Column);
    Assert.Equal("about 2000", Assert.IsType<StringDocumentValue>(report.Value).Value);
    Assert.Equal("about 2000", Assert.IsType<StringDocumentValue>(report.Original).Value);
    Assert.Equal(2, report.SchemaVersion);
    //A retype that rounds is lossy too: 1.5 as an Int is not 1.5.
    Assert.False(SchemaMapping.TryConvertLosslessly(Dec(1.5m), ValueTypeEnum.Int, out _));
  }

  [Fact]
  public void ARemovalIsReported() {
    var mapped = SchemaMapping.MapDocument(Document(("Note", Str("n")), ("Title", Str("t"))), [ColumnMigration.Remove(2, "Note")]);
    Assert.Equal(["Title"], Fields(mapped).Keys);
    var report = Assert.Single(mapped.Unmapped);
    Assert.Equal(UnmappedReason.ColumnRemoved, report.Reason);
    Assert.Equal("Note", report.Column);
    Assert.Equal("n", Assert.IsType<StringDocumentValue>(report.Value).Value);
  }

  [Fact]
  public void ARemovalFollowedByANewColumnOfTheSameNameNeverPresentsTheOldValueAsTheNew() {
    //Version 2 removed Note; version 3 added a column called Note again (no step: an addition
    //is not a migration). The old value is reported as removed and is not the new column's.
    var mapped = SchemaMapping.MapDocument(Document(("Note", Str("old"))), [ColumnMigration.Remove(2, "Note")]);
    Assert.False(Fields(mapped).ContainsKey("Note"));
    Assert.Equal(UnmappedReason.ColumnRemoved, Assert.Single(mapped.Unmapped).Reason);
  }

  [Fact]
  public void ARenameThenALosslessThenALossyRetypeIsReportedOnceNamingTheThirdStep() {
    var steps = new[] {
      ColumnMigration.Rename(2, "Year", "Published"),
      ColumnMigration.Retype(3, "Published", ValueTypeEnum.Long),
      ColumnMigration.Retype(4, "Published", ValueTypeEnum.Boolean)
    };
    var mapped = SchemaMapping.MapDocument(Document(("Year", Int(2000))), steps);
    Assert.False(Fields(mapped).ContainsKey("Published"));
    Assert.False(Fields(mapped).ContainsKey("Year"));
    var report = Assert.Single(mapped.Unmapped);
    Assert.Equal(UnmappedReason.RetypeLossy, report.Reason);
    Assert.Equal(4, report.SchemaVersion);
    Assert.Equal("Published", report.Column);
    //The value as the second step left it, and the original as stored.
    Assert.Equal(2000L, Assert.IsType<LongDocumentValue>(report.Value).Value);
    Assert.Equal(2000, Assert.IsType<IntDocumentValue>(report.Original).Value);
  }

  [Fact]
  public void AnElementInsideARetypedOrRemovedColumnIsReported() {
    var delta = new DocumentDelta([
      DeltaElement.Replace(DeltaPath.Parse("Author.Name"), Str("a"), Str("b")),
      DeltaElement.Replace(DeltaPath.Parse("Note"), Str("n"), Str("m")),
      DeltaElement.Replace(DeltaPath.Parse("Title"), Str("t"), Str("u"))
    ]);
    var mapped = SchemaMapping.MapDelta(delta, [ColumnMigration.Retype(2, "Author", ValueTypeEnum.String), ColumnMigration.Remove(3, "Note")]);
    Assert.Equal("Title", Assert.Single(mapped.Delta.Elements).Path.Render());
    Assert.Equal(2, mapped.Unmapped.Count);
    Assert.Equal(UnmappedReason.RetypeLossy, mapped.Unmapped[0].Reason);
    Assert.Equal("Author.Name", mapped.Unmapped[0].Element.Path.Render());
    Assert.Equal(UnmappedReason.ColumnRemoved, mapped.Unmapped[1].Reason);
    Assert.Equal("m", Assert.IsType<StringDocumentValue>(mapped.Unmapped[1].Value).Value);
  }
}
