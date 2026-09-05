namespace TokkDb.Disk;

//TX-4. The isolation this engine offers, stated: one writer at a time, any number of
//readers alongside it.
public enum TokkDbAccessMode {
  //Takes the write lock, recovers the journal on open, and may change the file.
  ReadWrite = 1,

  //Takes no lock and never writes. A reader cannot recover a journal, so it refuses to open
  //a database that an interrupted writer left behind.
  ReadOnly
}
