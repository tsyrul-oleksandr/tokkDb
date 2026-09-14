using TokkDb.Documents.Values;
using TokkDb.Pages.Managers;

namespace TokkDb.Pages.Query.Pipeline;

//A record the walk handed on: where it lies, its header, and its fields as they lie on the
//page. Not a document — a candidate that the predicate rejects has cost the fields the predicate
//named and nothing else, and only one that survives to the page becomes a document.
internal readonly record struct Candidate(DataRow Row, RecordHeader Header, IFieldSource Fields);
