namespace TokkDb.Pages.Versions;

//V-12 and HS-9. Who made a change and why, delivered once for a whole unit of work and stamped
//on its operation rather than repeated on every node. Cause is a Ulid: the assistant's request
//identifier today, an event identifier once the event log exists; default when unknown. A
//comment is caller text and must not carry record values, because erasure does not scrub
//operations (V-17).
public sealed record VersionAttribution(string Author, Ulid Cause, string Comment = "") {
  public static readonly VersionAttribution None = new(string.Empty, default, string.Empty);
}
