namespace TokkDb.Disk;

//The journal itself does not read back. Recovery may discard it, but it must never be
//guessed at: a half-read journal is worse than no journal.
public class JournalCorruptedException : Exception {
  public JournalCorruptedException(string message, Exception inner = null) : base(message, inner) { }
}
