using TokkDb.Pages.Managers;

namespace TokkDb.Pages.Indexes;

//A leaf entry: the encoded key of D-3, and where the record it points at lives.
//
//D-2: the pointer is a page and a slot, never a byte offset. Compaction may slide the
//record down inside its page without a single index entry being rewritten.
public readonly record struct IndexEntry(byte[] Key, DocumentAddress Address);

//An interior entry: a separator key and the child holding the keys at or above it. The
//child below the first separator is the node's FirstChildPageIndex, so a node with n
//separators has n + 1 children.
public readonly record struct IndexSeparator(byte[] Key, uint ChildPageIndex);
