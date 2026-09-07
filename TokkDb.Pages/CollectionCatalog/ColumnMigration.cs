using TokkDb.Values;

namespace TokkDb.Pages;

public enum ColumnMigrationKind {
  Rename,
  Retype,
  Remove
}

//One schema change a record written before it has to be read through.
//
//DC-7's lazy migration: a change to a column is recorded rather than applied, the collection's
//schemaVersion moves, and records keep the version they were written under (VR-11). A read
//replays every step above the record's version, so a collection can serve records written
//under any number of past schemas without any of them having been rewritten.
//
//Adding a column is not here. A record that lacks a field simply lacks it, and the column's
//declared default is what a read makes of that — nothing about the stored bytes has to change,
//which is what makes adding a column metadata-only.
public class ColumnMigration {
  //The schemaVersion this change produced. A record at version N is brought up to date by
  //replaying every step whose Version is greater than N, in order.
  public ushort Version { get; set; }
  public ColumnMigrationKind Kind { get; set; }

  //The column as the record on the page still calls it.
  public string ColumnName { get; set; } = string.Empty;

  //Rename only.
  public string NewName { get; set; } = string.Empty;

  //Retype only: the type the column now declares, which is what the stored value has to be
  //read as.
  public ValueTypeEnum NewType { get; set; } = ValueTypeEnum.Null;

  public static ColumnMigration Rename(ushort version, string columnName, string newName) {
    return new ColumnMigration {
      Version = version, Kind = ColumnMigrationKind.Rename, ColumnName = columnName, NewName = newName
    };
  }

  public static ColumnMigration Retype(ushort version, string columnName, ValueTypeEnum newType) {
    return new ColumnMigration {
      Version = version, Kind = ColumnMigrationKind.Retype, ColumnName = columnName, NewType = newType
    };
  }

  public static ColumnMigration Remove(ushort version, string columnName) {
    return new ColumnMigration {
      Version = version, Kind = ColumnMigrationKind.Remove, ColumnName = columnName
    };
  }

  public override string ToString() {
    return Kind switch {
      ColumnMigrationKind.Rename => $"v{Version}: rename {ColumnName} to {NewName}",
      ColumnMigrationKind.Retype => $"v{Version}: retype {ColumnName} as {NewType}",
      _ => $"v{Version}: remove {ColumnName}"
    };
  }
}
