using TokkDb.Buffer;
using TokkDb.Values;

namespace TokkDb.Documents.Values;

//Stored through DateTime.ToBinary, which carries the Kind as well as the ticks. A value read
//back as Unspecified when it was written as Utc is a different moment to anything that later
//converts it, and the difference is invisible until it is too late to tell.
public class DateTimeDocumentValue : IDocumentValue {
  public ValueTypeEnum Type => ValueTypeEnum.DateTime;
  public DateTime Value { get; set; }

  public DateTimeDocumentValue() { }
  public DateTimeDocumentValue(DateTime value) {
    Value = value;
  }

  public virtual void WriteValue(BufferWriter writer) {
    writer.WriteDateTime(Value);
  }
  public virtual void ReadValue(BufferReader reader) {
    Value = reader.ReadDateTime();
  }
}
