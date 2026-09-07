using TokkDb.Pages.Managers;

namespace TokkDb.Pages;

//One collection as the catalogue records it. Adding a field here means adding a field to a
//document: no binary reader changes, no migration.
public class CollectionDescriptor {
  public Ulid Id { get; set; }
  public string Name { get; set; } = string.Empty;
  public string Description { get; set; } = string.Empty;
  public ushort SchemaVersion { get; set; } = 1;
  public List<ColumnDescriptor> Columns { get; set; } = [];

  //DC-7. The schema changes a record written under an older version has to be read through,
  //oldest first. Empty for a collection that has never had a column renamed, retyped or
  //removed, which is most of them.
  //
  //It is kept here rather than in a collection of its own because it is part of what the
  //column set means: the columns say what a record looks like now, and these say how to read
  //one that was written when they said something else. Rewrite empties it, so it grows with
  //the number of changes since the last rewrite rather than for the life of the database.
  public List<ColumnMigration> Migrations { get; set; } = [];

  //The number every data page of this collection carries in its header. The page header
  //holds a uint, the catalogue holds the Ulid; this is what ties the two together.
  public uint OwningCollectionId { get; set; }

  //On the _collections descriptor only: the highest owning id ever issued. A dropped
  //collection leaves its pages in the file (there is no global free-page list to return them
  //to), and those pages carry its owning id in their headers, so reissuing that id would let
  //a new collection claim them. Taking the maximum of the collections that currently exist is
  //not enough, because dropping the newest one lowers it.
  public uint LastOwningCollectionId { get; set; }

  public uint DataFirstPage { get; set; }
  public uint DataLastPage { get; set; }
  public uint PrimaryIndexRoot { get; set; }

  //Index name to root page. A bare list could not say which root belonged to which index,
  //and the descriptors in _indexes name themselves.
  public Dictionary<string, uint> SecondaryIndexRoots { get; set; } = new(StringComparer.Ordinal);
  public uint FreeSpaceRoot { get; set; }
  public uint RecordCount { get; set; }

  //Written with default values and never read in this pass. They exist so that versioning
  //(D-5) arrives as a later addition rather than a later format break.
  public Ulid HistoryCollectionId { get; set; }
  public string RetentionPolicy { get; set; } = string.Empty;

  //Where this descriptor's own document lives, once it has been written.
  public DocumentAddress? Address { get; set; }

  public bool IsSystem => SystemCollections.IsReservedName(Name);
}
