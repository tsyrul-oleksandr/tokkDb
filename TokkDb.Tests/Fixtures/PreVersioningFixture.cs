using TokkDb.Pages;
using TokkDb.Values;

namespace TokkDb.Tests.Fixtures;

//Step 0.2 of docs/versioning-requirements-and-plan.md: a database written by the engine before any
//versioning code existed, checked in as Fixtures/pre-versioning.db and never regenerated
//(Fixtures/README.md). NF-7, G-9 and S-4 open a copy of it to show that a later engine reads it
//unchanged and that switching versioning on changes nothing already stored.
//
//What the file holds, in the order Generate wrote it:
//  1. Conference: 50 records, Id 1 to 50. Id is declared unique, which is the unique index.
//  2. Expense, and the relation ExpenseConference from Expense.ConferenceId to Conference.Id.
//  3. 45 expenses, Id 1 to 45, under schema version 1, whose text column was called Note.
//  4. Expense 7 updated three times: copy-on-write, so its first three images are retired.
//  5. Note renamed Comment through SetColumns, without Rewrite: the collection is at schema
//     version 2 with the rename in its migration log, and the 45 records stay at version 1 and
//     are read through the log (DC-7).
//  6. Five more expenses, Id 46 to 50, under schema version 2.
//  7. Expense 13 deleted.
//So 50 conferences and 49 expenses are live, one of them at the end of three updates.
public static class PreVersioningFixture {
  public const string FileName = "pre-versioning.db";
  public const string Conferences = "Conference";
  public const string Expenses = "Expense";
  public const string Relation = "ExpenseConference";
  public const int ConferenceCount = 50;
  public const int ExpenseCount = 50;
  public const int ExpensesBeforeRename = 45;
  public const int UpdatedExpenseId = 7;
  public const int DeletedExpenseId = 13;
  public const int LiveExpenseCount = ExpenseCount - 1;
  public const ushort ExpenseSchemaVersion = 2;

  //Declared with the names the collection has now. A record written before the rename comes
  //back through these too, because a read replays the migration log.
  public class Conference {
    public int Id { get; set; }
    public string Name { get; set; }
    public string City { get; set; }
  }

  public class Expense {
    public int Id { get; set; }
    public int ConferenceId { get; set; }
    public int Amount { get; set; }
    public string Comment { get; set; }
  }

  //The shape of an expense as it was written before the rename. Only Generate uses it: the
  //serializer names fields after properties, and the 45 early records have to carry Note.
  private class ExpenseBeforeRename {
    public int Id { get; set; }
    public int ConferenceId { get; set; }
    public int Amount { get; set; }
    public string Note { get; set; }
  }

  //Where the build copied the checked-in file to (TokkDb.Tests.csproj).
  public static string Path => System.IO.Path.Combine(AppContext.BaseDirectory, "Fixtures", FileName);

  //A copy a test may open and write to. The checked-in file is never opened in place: opening it
  //takes a write lock beside it and can leave a journal, and a test that changed it would defeat
  //its purpose.
  public static TempDatabaseFile Copy() {
    var file = new TempDatabaseFile();
    File.Copy(Path, file.Path);
    return file;
  }

  public static Conference ConferenceNumbered(int id) {
    return new Conference { Id = id, Name = $"Conference {id}", City = id % 2 == 0 ? "Kyiv" : "Lviv" };
  }

  //An expense as inserted. Expense 7's Amount and Comment were then changed by its three updates,
  //which ExpenseAfterUpdates gives.
  public static Expense ExpenseNumbered(int id) {
    return new Expense {
      Id = id, ConferenceId = (id - 1) % ConferenceCount + 1, Amount = id * 10, Comment = $"expense {id}"
    };
  }

  public static Expense ExpenseAfterUpdates(int id, int update) {
    var expense = ExpenseNumbered(id);
    expense.Amount += update * 100;
    expense.Comment = $"expense {id}, update {update}";
    return expense;
  }

  public static Expense UpdatedExpense() {
    return ExpenseAfterUpdates(UpdatedExpenseId, 3);
  }

  private static List<ColumnDescriptor> ExpenseColumns(string textColumn) {
    return [
      new ColumnDescriptor("Id", ValueTypeEnum.Int),
      new ColumnDescriptor("ConferenceId", ValueTypeEnum.Int),
      new ColumnDescriptor("Amount", ValueTypeEnum.Int),
      new ColumnDescriptor(textColumn, ValueTypeEnum.String)
    ];
  }

  //Writes the fixture to a path that does not exist yet. It was run once, against the engine at
  //the commit README.md names, to produce the checked-in file; it stays so that the file's
  //contents are stated in code rather than remembered, and so that a test can check that what it
  //writes today still reads the same as the file.
  public static void Generate(string path) {
    using var db = new TokkDbConnection(path);
    db.Load();
    db.CreateCollection(Conferences, [
      new ColumnDescriptor("Id", ValueTypeEnum.Int, unique: true),
      new ColumnDescriptor("Name", ValueTypeEnum.String),
      new ColumnDescriptor("City", ValueTypeEnum.String)
    ]);
    db.CreateCollection(Expenses, ExpenseColumns("Note"));
    //The relation is checked against Conference.Id's unique index on every expense written.
    db.CreateRelation(Relation, Expenses, "ConferenceId", Conferences, "Id");

    var conferences = db.Entities<Conference>(Conferences);
    db.InTransaction(() => {
      for (var id = 1; id <= ConferenceCount; id++) {
        conferences.Insert(ConferenceNumbered(id));
      }
    });

    var early = db.Entities<ExpenseBeforeRename>(Expenses);
    var recordIds = new Dictionary<int, Ulid>();
    db.InTransaction(() => {
      for (var id = 1; id <= ExpensesBeforeRename; id++) {
        var expense = ExpenseNumbered(id);
        recordIds[id] = early.Insert(new ExpenseBeforeRename {
          Id = expense.Id, ConferenceId = expense.ConferenceId, Amount = expense.Amount, Note = expense.Comment
        });
      }
    });
    //Three updates, each its own transaction, as an application would make them.
    for (var update = 1; update <= 3; update++) {
      var expense = ExpenseAfterUpdates(UpdatedExpenseId, update);
      early.Update(recordIds[UpdatedExpenseId], new ExpenseBeforeRename {
        Id = expense.Id, ConferenceId = expense.ConferenceId, Amount = expense.Amount, Note = expense.Comment
      });
    }

    //SetColumns stamps the migration with the version it produces, so the version given here
    //does not matter. Rewrite is deliberately not called.
    db.SetColumns(Expenses, ExpenseColumns("Comment"), [ColumnMigration.Rename(0, "Note", "Comment")]);

    var late = db.Entities<Expense>(Expenses);
    db.InTransaction(() => {
      for (var id = ExpensesBeforeRename + 1; id <= ExpenseCount; id++) {
        recordIds[id] = late.Insert(ExpenseNumbered(id));
      }
    });
    late.Delete(recordIds[DeletedExpenseId]);
  }
}
