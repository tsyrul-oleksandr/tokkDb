namespace TokkDb.Pages;

//The reserved collections and the prefix that keeps user names out of their way.
public static class SystemCollections {
  public const char ReservedPrefix = '_';

  public const string Collections = "_collections";
  public const string Indexes = "_indexes";
  public const string Relations = "_relations";
  public const string SemanticTypes = "_semanticTypes";
  public const string DisplayRules = "_displayRules";
  public const string Settings = "_settings";
  public const string Conversations = "_conversations";
  public const string ConversationEntries = "_conversationEntries";

  //The assistant's traces, and the journal of what they changed.
  //
  //Three, and the number is a retention decision rather than a layout one. The assistant keeps
  //four things here — diagnostics, the change journal, the state of a request, and conversations
  //— and they do not all live as long. Two things with different lifetimes cannot share a
  //collection, because a purge removes documents and a document is either removed or it is not:
  //the diagnostics are prunable on a stated window, the change journal is on a longer one of its
  //own and no purge of the first may touch it, and conversations are on no window at all because
  //they go when the user says so. A request's state is the fourth thing and needs no collection
  //— it lives on its own trace document, because a request that is waiting for an answer is
  //exactly the request whose diagram is being looked at.
  //
  //_traces and _traceSteps split for the reason _conversations and _conversationEntries split:
  //the steps of one request are read together and are far more numerous than the requests, so
  //they are their own collection rather than an array inside one.
  //
  //D-4 said this list would grow — "(later: _events, _versions)" — and this is the growth. The
  //engine stores these and interprets none of them: what a step is, and what may be in one, is
  //defined a layer up and declared through DescribeSystemCollection — which is also where the
  //five the assistant now owns are listed against their three retentions.
  public const string Traces = "_traces";
  public const string TraceSteps = "_traceSteps";
  public const string DataChanges = "_dataChanges";

  //_collections comes first: it has to exist before anything can be described in it.
  public static readonly IReadOnlyList<string> All = [
    Collections, Indexes, Relations, SemanticTypes, DisplayRules, Settings, Conversations,
    ConversationEntries, Traces, TraceSteps, DataChanges
  ];

  public static readonly IReadOnlyDictionary<string, string> Descriptions = new Dictionary<string, string> {
    [Collections] = "The catalogue of collections. It describes itself.",
    [Indexes] = "Index descriptors.",
    [Relations] = "Relation definitions between collections.",
    [SemanticTypes] = "The semantic type registry.",
    [DisplayRules] = "Display rules for collections and columns.",
    [Settings] = "Per-collection application and AI settings.",
    [Conversations] = "Conversations: title and when they were started and last touched (CX-2).",
    [ConversationEntries] = "The events of a conversation, one document each (CX-2).",
    [Traces] = "One document per request the assistant handled, and the state it is in (TR-1, D-15).",
    [TraceSteps] = "The steps of a request and the edges between them, one document each (TR-1).",
    [DataChanges] = "What each request changed: the audit record, kept longer than the trace (TR-2, TR-8)."
  };

  public static bool IsReservedName(string name) {
    return !string.IsNullOrEmpty(name) && name[0] == ReservedPrefix;
  }
}
