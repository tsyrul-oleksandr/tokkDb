using TokkDb.Pages;
using TokkDb.Tests.Fixtures;
using Xunit;

namespace TokkDb.Tests;

//Step 0.2 of docs/versioning-requirements-and-plan.md: the database written before any versioning
//code opens under the engine as it is now, and every record in it reads. NF-7, G-9 and S-4 build
//on this file, so it is opened here exactly as they will open it — copied first, never in place.
public class PreVersioningFixtureTests {
  [Fact]
  public void TheFixtureOpensAndEveryRecordReads() {
    using var file = PreVersioningFixture.Copy();
    using var db = new TokkDbConnection(file.Path);
    Assert.True(db.IsExists());
    db.Load();

    AssertHoldsWhatTheGeneratorWrote(db);
  }

  //What the generator writes today reads the same as the checked-in file. This checks the
  //generator against the file, not the file against the generator: the file is never regenerated
  //(Fixtures/README.md), and once versioning is on by default the generator will write a different
  //file that reads the same.
  [Fact]
  public void TheGeneratorStillWritesWhatTheFixtureHolds() {
    using var file = new TempDatabaseFile();
    PreVersioningFixture.Generate(file.Path);

    using var db = new TokkDbConnection(file.Path);
    db.Load();
    AssertHoldsWhatTheGeneratorWrote(db);
  }

  private static void AssertHoldsWhatTheGeneratorWrote(TokkDbConnection db) {
    //Every conference, by a scan and again by its identity through the primary index.
    var conferences = db.Entities<PreVersioningFixture.Conference>(PreVersioningFixture.Conferences);
    var allConferences = conferences.GetAllRecords().OrderBy(record => record.Value.Id).ToList();
    Assert.Equal(PreVersioningFixture.ConferenceCount, allConferences.Count);
    Assert.Equal(Enumerable.Range(1, PreVersioningFixture.ConferenceCount),
      allConferences.Select(record => record.Value.Id));
    Assert.All(allConferences, record => {
      var expected = PreVersioningFixture.ConferenceNumbered(record.Value.Id);
      Assert.Equal(expected.Name, record.Value.Name);
      Assert.Equal(expected.City, record.Value.City);
      Assert.Equal(record.Value.Id, conferences.GetById(record.RecordId).Value.Id);
    });

    //Every expense: the deleted one is gone, the updated one reads as its last update left it,
    //and the rest read as inserted — those written before the rename through the migration log,
    //those written after it as they are.
    var expenses = db.Entities<PreVersioningFixture.Expense>(PreVersioningFixture.Expenses);
    var allExpenses = expenses.GetAllRecords().OrderBy(record => record.Value.Id).ToList();
    Assert.Equal(PreVersioningFixture.LiveExpenseCount, allExpenses.Count);
    Assert.Equal(Enumerable.Range(1, PreVersioningFixture.ExpenseCount)
        .Where(id => id != PreVersioningFixture.DeletedExpenseId),
      allExpenses.Select(record => record.Value.Id));
    Assert.All(allExpenses, record => {
      var expected = record.Value.Id == PreVersioningFixture.UpdatedExpenseId
        ? PreVersioningFixture.UpdatedExpense()
        : PreVersioningFixture.ExpenseNumbered(record.Value.Id);
      Assert.Equal(expected.ConferenceId, record.Value.ConferenceId);
      Assert.Equal(expected.Amount, record.Value.Amount);
      Assert.Equal(expected.Comment, record.Value.Comment);
      Assert.Equal(record.Value.Id, expenses.GetById(record.RecordId).Value.Id);
    });

    //The catalogue: the unique index, the relation, and the rename still pending.
    Assert.Contains(db.Indexes, index =>
      index.CollectionName == PreVersioningFixture.Conferences && index.ColumnName == "Id" && index.Unique);
    Assert.Contains(db.Relations, relation =>
      relation.Name == PreVersioningFixture.Relation
      && relation.SourceCollection == PreVersioningFixture.Expenses
      && relation.TargetCollection == PreVersioningFixture.Conferences);
    var descriptor = db.Collection(PreVersioningFixture.Expenses);
    Assert.Equal(PreVersioningFixture.ExpenseSchemaVersion, descriptor.SchemaVersion);
    var rename = Assert.Single(descriptor.Migrations);
    Assert.Equal(ColumnMigrationKind.Rename, rename.Kind);
    Assert.Equal("Note", rename.ColumnName);
    Assert.Equal("Comment", rename.NewName);
    Assert.Contains(descriptor.Columns, column => column.Name == "Comment");
    Assert.DoesNotContain(descriptor.Columns, column => column.Name == "Note");
    Assert.Equal((uint)PreVersioningFixture.LiveExpenseCount, descriptor.RecordCount);
    Assert.Equal((uint)PreVersioningFixture.ConferenceCount,
      db.Collection(PreVersioningFixture.Conferences).RecordCount);
  }
}
