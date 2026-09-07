namespace TokkDb.LLM.Application;

public enum StorageBackend
{
    //Kept for tests and for a run that should leave nothing behind. It loses everything when
    //the application exits, which is the whole reason it is no longer the default.
    Memory,

    //The engine. The default since Phase 7: the application's collections, records, schema,
    //semantic types, display rules and conversations all live in one database file and are
    //still there when it is opened again.
    TokkDb
}
