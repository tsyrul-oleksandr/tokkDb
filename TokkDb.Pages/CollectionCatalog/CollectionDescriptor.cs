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

  //The number every data page of this collection carries in its header. The page header
  //holds a uint, the catalogue holds the Ulid; this is what ties the two together.
  public uint OwningCollectionId { get; set; }

  public uint DataFirstPage { get; set; }
  public uint DataLastPage { get; set; }
  public uint PrimaryIndexRoot { get; set; }
  public List<uint> SecondaryIndexRoots { get; set; } = [];
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
