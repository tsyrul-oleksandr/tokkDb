namespace TokkDb.Documents.Delta;

public enum ArrayMatchingMode {
  ByPosition = 0,
  ByKey = 1
}

//DL-8. How the elements of one array were paired up when the delta was computed, recorded on
//the delta for every array that had an element key declared: matched by that key, or by position
//because the key could not be trusted — and then Reason says why. Arrays with no key declared
//are positional and are not listed, because listing them would cost bytes and say nothing.
public sealed record ArrayMatching(DeltaPath Path, ArrayMatchingMode Mode, string? Key, string? Reason);
