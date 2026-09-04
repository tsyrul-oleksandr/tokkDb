namespace TokkDb.Buffer;

//CRC-32 (IEEE 802.3), written out rather than taken from a package: the values are
//persisted in pages and in the journal, so they have to stay identical across runtimes and
//framework versions forever.
public static class Crc32 {
  private const uint Polynomial = 0xEDB88320;
  private const uint Seed = 0xFFFFFFFF;
  private static readonly uint[] Table = CreateTable();

  public static uint Compute(ReadOnlySpan<byte> bytes) {
    var crc = Seed;
    foreach (var value in bytes) {
      crc = Table[(crc ^ value) & 0xFF] ^ (crc >> 8);
    }
    return crc ^ Seed;
  }

  private static uint[] CreateTable() {
    var table = new uint[256];
    for (uint i = 0; i < table.Length; i++) {
      var entry = i;
      for (var bit = 0; bit < 8; bit++) {
        entry = (entry & 1) == 1 ? (entry >> 1) ^ Polynomial : entry >> 1;
      }
      table[i] = entry;
    }
    return table;
  }
}
