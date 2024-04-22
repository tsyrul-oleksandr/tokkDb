namespace TokkDb.Core.Buffer;

public interface IBufferAddress {
  ushort Position { get; }
  ushort Length { get; }
  public bool IsEmpty();
}
