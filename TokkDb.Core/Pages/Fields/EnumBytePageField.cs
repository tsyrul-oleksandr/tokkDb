using System.Runtime.CompilerServices;

namespace TokkDb.Core.Pages.Fields;

public class EnumBytePageField<T> : BasePageField<T> where T : struct, Enum, IConvertible {

  public EnumBytePageField() : base(TypesConstants.ByteByteSize) { }
  public override void Read(PageBuffer buffer, int position) {
    var numberValue = buffer.ReadByte(position);
    Value = Unsafe.As<byte, T>(ref numberValue);
  }

  public override void Write(PageBuffer buffer, int position) {
    var enumValue = Value;
    var numberValue = Unsafe.As<T, byte>(ref enumValue);
    buffer.WriteByte(numberValue, position);
  }
}
