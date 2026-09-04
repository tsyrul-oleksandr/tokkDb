using TokkDb.Buffer;

namespace TokkDb.Pages.Managers;

//Where a document lives: the page it is on and its slot in that page's directory. The slot
//is the indirection, so a record may move inside its page without this changing (D-2).
public readonly record struct DocumentAddress(uint PageIndex, ushort SlotIndex);

public readonly record struct DataRow(DocumentAddress Address, BufferSlice Buffer);
