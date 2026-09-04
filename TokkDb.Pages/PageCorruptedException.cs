namespace TokkDb.Pages;

//A page whose stored checksum does not match its content. It names the page by index so a
//damaged file can be pointed at rather than guessed about.
public class PageCorruptedException : Exception {
  public uint PageIndex { get; }
  public uint StoredChecksum { get; }
  public uint ComputedChecksum { get; }

  public PageCorruptedException(uint pageIndex, uint storedChecksum, uint computedChecksum, Exception inner = null)
      : base($"Page {pageIndex} is damaged: its content checksums to 0x{computedChecksum:X8}, " +
        $"but 0x{storedChecksum:X8} is stored in its control area.", inner) {
    PageIndex = pageIndex;
    StoredChecksum = storedChecksum;
    ComputedChecksum = computedChecksum;
  }
}
