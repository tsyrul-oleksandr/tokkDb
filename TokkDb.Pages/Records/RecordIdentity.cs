namespace TokkDb.Pages.Records;

//D-1's identifier, minted so that it is actually time-ordered.
//
//Ulid.NewUlid() is time-ordered only to the millisecond: two ids minted inside the same
//millisecond share their timestamp and differ in their random part alone, so they sort
//against each other at random. For a bulk load that is every id in the load, and the
//primary index then behaves exactly as it would under the Guid D-1 rejected — measured at
//100 000 records, 181 entries per leaf against 324 for a strictly ascending sequence.
//
//This is the ULID specification's monotonic mode: inside a millisecond the previous
//identifier is incremented instead of a new random part being drawn, so the sequence
//ascends. A carry out of the random part runs into the timestamp, which is where it belongs.
public static class RecordIdentity {
  private static readonly Lock Gate = new();
  private static Ulid _last;

  public static Ulid Next() {
    lock (Gate) {
      var candidate = Ulid.NewUlid();
      _last = candidate.CompareTo(_last) > 0 ? candidate : Increment(_last);
      return _last;
    }
  }

  private static Ulid Increment(Ulid value) {
    var bytes = value.ToByteArray();
    for (var i = bytes.Length - 1; i >= 0 && ++bytes[i] == 0; i--) {
      //A byte that wrapped to zero carries into the one before it.
    }
    return new Ulid(bytes);
  }
}
