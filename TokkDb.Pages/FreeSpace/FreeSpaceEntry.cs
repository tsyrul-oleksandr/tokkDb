namespace TokkDb.Pages;

//One page as the free-space structure records it.
//
//ReclaimableBytes is everything the page could give up: the contiguous tail plus what sits
//in freed slots. A page whose reclaimable bytes are enough is worth loading; whether it can
//take the record as it stands, or only after compaction, is for the page itself to say.
public record struct FreeSpaceEntry(uint PageIndex, ushort ReclaimableBytes, BlockState State) {
  public const int ByteSize = 4 + 2 + 1;

  public bool CanHoldRecords => State is BlockState.Free or BlockState.Occupied;
}
