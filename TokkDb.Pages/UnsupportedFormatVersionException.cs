using System.Text;

namespace TokkDb.Pages;

//Thrown before anything in the file is interpreted, so an unknown file is never parsed as
//if it were a database of the current format.
public class UnsupportedFormatVersionException : Exception {
  public string FoundMagicNumber { get; }
  public string ExpectedMagicNumber { get; }
  public ushort? FoundFormatVersion { get; }
  public ushort ExpectedFormatVersion { get; }

  public UnsupportedFormatVersionException(string message, string foundMagicNumber, string expectedMagicNumber,
      ushort? foundFormatVersion, ushort expectedFormatVersion, Exception inner = null) : base(message, inner) {
    FoundMagicNumber = foundMagicNumber;
    ExpectedMagicNumber = expectedMagicNumber;
    FoundFormatVersion = foundFormatVersion;
    ExpectedFormatVersion = expectedFormatVersion;
  }

  public static UnsupportedFormatVersionException ForMagicNumber(byte[] foundBytes, string expectedMagicNumber,
      ushort expectedFormatVersion) {
    var found = Describe(foundBytes);
    var message = $"The file is not a TokkDb database: found magic number '{found}' ({ToHex(foundBytes)}), " +
      $"expected '{expectedMagicNumber}' of format version {expectedFormatVersion}.";
    return new UnsupportedFormatVersionException(message, found, expectedMagicNumber, null, expectedFormatVersion);
  }

  public static UnsupportedFormatVersionException ForFormatVersion(string magicNumber, ushort foundFormatVersion,
      ushort expectedFormatVersion) {
    var message = $"Unsupported TokkDb format version: found {foundFormatVersion}, expected {expectedFormatVersion} " +
      $"(magic number '{magicNumber}').";
    return new UnsupportedFormatVersionException(message, magicNumber, magicNumber, foundFormatVersion,
      expectedFormatVersion);
  }

  //Bytes from an unknown file are shown as text only where that text is readable.
  private static string Describe(byte[] bytes) {
    var builder = new StringBuilder(bytes.Length);
    foreach (var value in bytes) {
      builder.Append(value is >= 0x20 and < 0x7F ? (char)value : '?');
    }
    return builder.ToString();
  }

  private static string ToHex(byte[] bytes) {
    return Convert.ToHexString(bytes);
  }
}
