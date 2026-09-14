using TokkDb.Buffer;
using TokkDb.Pages.Managers;
using TokkDb.Pages.Records;

namespace TokkDb.Pages;

//VR-11. Every stored record carries this in front of its document body, from the first
//release, whether or not anything reads it yet.
//
//RecordId, Flags and SchemaVersion are read by every scan. VersionId is minted by
//RecordIdentity on every write (HS-7), so that it ascends with write order and names the
//image in history. Under KeepVersions, PreviousVersion addresses the history node of this
//image's own version (V-6); under None, and for a record written before its collection kept
//versions, it is zero. The fields were in the format from the first release precisely so
//that versioning arrived as a change of behaviour and not of format, which would have meant
//rewriting every record in every existing database.
public class RecordHeader {
  public const int ByteSize =
    TypesConstants.UlidByteSize * 2 +                                   //recordId, versionId
    TypesConstants.UIntByteSize + TypesConstants.UShortByteSize +       //previousVersion
    TypesConstants.ByteByteSize +                                       //flags
    TypesConstants.UShortByteSize;                                      //schemaVersion

  //The identity of the record for the whole system (D-1). It is the Ulid the document
  //serializer mints, not a second identifier beside it.
  public Ulid RecordId { get; set; }

  //Identifies this particular image of the record: its version identifier, whose timestamp is
  //the version's logical time (V-8). Minted from the same monotonic source as RecordId.
  public Ulid VersionId { get; set; }

  //V-6: the address of the history node for this image's own VersionId, where the way back to
  //its past starts. Zero when the record has no history. Checked before use and never trusted
  //blindly (HS-8).
  public DocumentAddress PreviousVersion { get; set; }

  public RecordFlags Flags { get; set; } = RecordFlags.Live;

  //The version of the collection's column set this image was written under.
  public ushort SchemaVersion { get; set; } = 1;

  public bool IsLive => Flags.HasFlag(RecordFlags.Live) && !Flags.HasFlag(RecordFlags.Deleted);

  //HS-7: the version identifier comes from RecordIdentity, never from Ulid.NewUlid(), which
  //is random within a millisecond and would put two versions of one record in random order.
  public static RecordHeader ForNewRecord(Ulid recordId, ushort schemaVersion = 1) {
    return new RecordHeader {
      RecordId = recordId,
      VersionId = RecordIdentity.Next(),
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
