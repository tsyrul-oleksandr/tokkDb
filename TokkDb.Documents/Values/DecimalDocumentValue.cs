using TokkDb.Buffer;
using TokkDb.Values;

namespace TokkDb.Documents.Values;

//Stored as the four ints decimal.GetBits gives, so the scale survives: 1.50 comes back as
//1.50 rather than as 1.5. The two are equal to CompareTo and distinguishable to ToString, and
//a currency column that lost its trailing zero would be a different number to a reader.
public class DecimalDocumentValue : IDocumentValue {
  public ValueTypeEnum Type => ValueTypeEnum.Decimal;
  public decimal Value { get; set; }

  public DecimalDocumentValue() { }
  public DecimalDocumentValue(decimal value) {
    Value = value;
  }

  public virtual void WriteValue(BufferWriter writer) {
    writer.WriteDecimal(Value);
  }
  public virtual void ReadValue(BufferReader reader) {
    Value = reader.ReadDecimal();
  }
}
