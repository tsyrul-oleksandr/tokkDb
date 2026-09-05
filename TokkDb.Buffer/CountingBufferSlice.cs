namespace TokkDb.Buffer;

//A buffer that stores nothing and only counts what would have been written to it.
//
//Measuring a document by serializing it into a page-sized scratch buffer both allocated a
//page per measurement and silently capped what could be measured at one page. Every write
//in BufferSlice funnels through WriteByte and WriteBytes, so overriding those two is enough
//to make the whole of it free.
public class CountingBufferSlice : BufferSlice {
  public CountingBufferSlice() : base(Memory<byte>.Empty) { }

  public override void WriteByte(byte value, int index) {
    //Counted by the caller through writeBytes; there is nowhere to put it.
  }

  public override void WriteBytes(byte[] values, int index, out int writeBytes) {
    writeBytes = TypesConstants.ByteByteSize * values.Length;
  }
}
