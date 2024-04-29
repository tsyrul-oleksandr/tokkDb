using System.Runtime.CompilerServices;

namespace TokkDb.Core.Pages.Fields;

public class EnumBytePageField<T> : BasePageField<T> where T : struct, Enum, IConvertible {
  
  public override int Read(PageBuffer buffer, int position) {
    var numberValue = buffer.ReadByte(position);
    Value = Unsafe.As<byte, T>(ref numberValue);
    return TypesConstants.ByteByteSize;
  }

  public override int Write(PageBuffer buffer, int position) {
    var enumValue = Value;
    var numberValue = Unsafe.As<T, byte>(ref enumValue);
    buffer.WriteByte(numberValue, position);
    return TypesConstants.ByteByteSize;
  }
}
