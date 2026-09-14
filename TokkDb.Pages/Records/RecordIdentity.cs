namespace TokkDb.Pages.Records;

//D-1's identifier, minted so that it is actually time-ordered — and, since HS-7, the one source
//of every identifier the engine mints: record identities, version identifiers, catalogue
//descriptors, operations, schema and relation nodes.
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
//The same rule covers a clock that has stepped back (V-8): a candidate below the last
//identifier is never issued, so logical time — the identifier's timestamp — never runs
//backwards, and is at least the recorded time the clock gave.
//
//Within a process the rule is enough. Across a restart it is not: the last identifier is
//forgotten with the process, and a clock set behind the last write would mint identifiers
//below ones already stored. So the greatest identifier issued is kept as a high-water mark on
//the _collections descriptor, written by every outermost commit that moved it, and RaiseTo
//brings this source up to it at open — never down.
public static class RecordIdentity {
  private static readonly Lock Gate = new();
  private static Ulid _last;

  public static Ulid Next() {
    lock (Gate) {
      var candidate = Ulid.NewUlid(EngineClock.Now);
      _last = candidate.CompareTo(_last) > 0 ? candidate : Increment(_last);
      return _last;
    }
  }

  //HS-7: the identifier of a new version of a record, which has to sort after the record's
  //latest one whatever the source has issued meanwhile: the greater of the next identifier and
  //the successor of the latest. The source moves up with it, so nothing issued later falls
  //between the two.
  public static Ulid NextAfter(Ulid latest) {
    lock (Gate) {
      var candidate = Ulid.NewUlid(EngineClock.Now);
      _last = candidate.CompareTo(_last) > 0 ? candidate : Increment(_last);
      if (_last.CompareTo(latest) <= 0) {
        _last = Increment(latest);
      }
      return _last;
    }
  }

  //The greatest identifier issued so far: the high-water mark a commit records (HS-7).
  public static Ulid Last {
    get {
      lock (Gate) {
        return _last;
      }
    }
  }

  //Raises the source to an identifier the database already holds, so that nothing minted from
  //now on is below it. Never lowers it: a second database opened in the same process, or one
  //whose mark is behind, changes nothing.
  public static void RaiseTo(Ulid mark) {
    lock (Gate) {
      if (mark.CompareTo(_last) > 0) {
        _last = mark;
      }
    }
  }

  //Forgets the last identifier, as a new process would. For tests that stand in for a restart;
  //nothing in the engine calls it.
  public static void ResetForTests() {
    lock (Gate) {
      _last = default;
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
