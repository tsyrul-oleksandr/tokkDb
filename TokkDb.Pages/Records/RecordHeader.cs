using TokkDb.Buffer;
using TokkDb.Pages.Managers;

namespace TokkDb.Pages;

//VR-11. Every stored record carries this in front of its document body, from the first
//release, whether or not anything reads it yet.
//
//In this pass only RecordId, Flags and SchemaVersion are read. VersionId is a fresh Ulid on
//every write and PreviousVersion is written as zero — they are written unread precisely so
//that turning versioning on later (D-5) is a change of behaviour and not a change of format,
//which would mean rewriting every record in every existing database.
public class RecordHeader {
  public const int ByteSize =
    TypesConstants.UlidByteSize * 2 +                                   //recordId, versionId
    TypesConstants.UIntByteSize + TypesConstants.UShortByteSize +       //previousVersion
    TypesConstants.ByteByteSize +                                       //flags
    TypesConstants.UShortByteSize;                                      //schemaVersion

  //The identity of the record for the whole system (D-1). It is the Ulid the document
  //serializer mints, not a second identifier beside it.
  public Ulid RecordId { get; set; }

  //Identifies this particular image of the record. Unread until versioning exists.
  public Ulid VersionId { get; set; }

  //Where the image this one replaced lives. Zero throughout this pass.
  public DocumentAddress PreviousVersion { get; set; }

  public RecordFlags Flags { get; set; } = RecordFlags.Live;

  //The version of the collection's column set this image was written under.
  public ushort SchemaVersion { get; set; } = 1;

  public bool IsLive => Flags.HasFlag(RecordFlags.Live) && !Flags.HasFlag(RecordFlags.Deleted);

  public static RecordHeader ForNewRecord(Ulid recordId, ushort schemaVersion = 1) {
    return new RecordHeader {
      RecordId = recordId,
      VersionId = Ulid.NewUlid(),
      PreviousVersion = default,
      Flags = RecordFlags.Live,
      SchemaVersion = schemaVersion
    };
  }

  public void Write(BufferWriter writer) {
    writer.WriteBytes(RecordId.ToByteArray());
    writer.WriteBytes(VersionId.ToByteArray());
    writer.WriteUInt(PreviousVersion.PageIndex);
    writer.WriteUShort(PreviousVersion.SlotIndex);
    writer.WriteByte((byte)Flags);
    writer.WriteUShort(SchemaVersion);
  }

  public static RecordHeader Read(BufferReader reader) {
    return new RecordHeader {
      RecordId = new Ulid(reader.ReadBytes(TypesConstants.UlidByteSize)),
      VersionId = new Ulid(reader.ReadBytes(TypesConstants.UlidByteSize)),
      PreviousVersion = new DocumentAddress(reader.ReadUInt(), reader.ReadUShort()),
      Flags = (RecordFlags)reader.ReadByte(),
      SchemaVersion = reader.ReadUShort()
    };
  }
}
