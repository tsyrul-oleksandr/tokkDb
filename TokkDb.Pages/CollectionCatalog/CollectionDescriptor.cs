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

  //On the _collections descriptor only: the greatest identifier RecordIdentity had issued when
  //the last commit that moved it ran (HS-7, V-8). Loaded into RecordIdentity at open and never
  //lowered, so that a restart with the clock set behind the last write cannot mint an
  //identifier below one already stored. Stamped at the moment the descriptor is written, after
  //its own header's version identifier has been minted, so the mark covers that one too.
  public Ulid LastIdentifier { get; set; }

  public uint DataFirstPage { get; set; }
  public uint DataLastPage { get; set; }
  public uint PrimaryIndexRoot { get; set; }

  //On a history collection's descriptor only: the root of its version index (V-5, HS-4).
  public uint VersionIndexRoot { get; set; }

  //Index name to root page. A bare list could not say which root belonged to which index,
  //and the descriptors in _indexes name themselves.
  public Dictionary<string, uint> SecondaryIndexRoots { get; set; } = new(StringComparer.Ordinal);
  public uint FreeSpaceRoot { get; set; }
  public uint RecordCount { get; set; }

  //V-4: the identifier of the collection's history collection, named by HistoryCollections,
  //or default while the collection keeps no versions. Set and cleared only by the version
  //store's lifecycle operations (HS-2).
  public Ulid HistoryCollectionId { get; set; }

  //HS-1 and V-13: the one source of truth for what becomes of a retired image, read by the
  //write seam. Stored by name; a database written before versioning existed holds an empty
  //name here, which reads as None.
  public RetentionPolicy RetentionPolicy { get; set; } = RetentionPolicy.None;

  //V-1: the keyframe interval k and the large-delta ratio, per collection. Their defaults
  //are I-1 and I-2, decided at step 5.1; until then these stand in. Read only under
  //KeepVersions.
  public int SnapshotInterval { get; set; } = DefaultSnapshotInterval;
  public double LargeDeltaRatio { get; set; } = DefaultLargeDeltaRatio;

  public const int DefaultSnapshotInterval = 8;
  public const double DefaultLargeDeltaRatio = 0.5;

  //Where this descriptor's own document lives, once it has been written.
  public DocumentAddress? Address { get; set; }

  public bool IsSystem => SystemCollections.IsReservedName(Name);
}
