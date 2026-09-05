using TokkDb.Documents.Keys;
using TokkDb.Pages.Managers;
using TokkDb.Transactions;

namespace TokkDb.Pages.Indexes;

//DC-4. A B+Tree over one collection's primary index, and deliberately not a B-tree: every
//entry lives in a leaf, interior nodes hold separator keys only, and the leaves are linked
//left to right. A range scan, an ordered read and a page of results are then the same
//operation — find one leaf, then walk the chain — which is what the planner of Phase 6 needs
//and what a B-tree, whose values sit at every level, cannot give without walking the tree
//again for every step.
//
//The tree is on disk. Its pages go through the same page manager, the same buffer and the
//same journal as every other page (TX-1, TX-2), so a split half-written when the power goes
//is undone by recovery like any other change. Nothing here is rebuilt by scanning at open:
//that would cost the 500 ms of NFR-2 and lose the index to any crash.
//
//D-2: an entry points at a page and a slot, never at a byte offset, so compaction can move
//a record inside its page without the tree being touched.
public class BPlusTree {
  private readonly PageManager _pageManager;
  private readonly CollectionCatalog _catalog;
  private readonly FreeSpaceManager _freeSpace;
  private readonly TransactionManager _transactionManager;
  private readonly string _collectionName;
  private readonly IndexRoot _root;

  public BPlusTree(PageManager pageManager, CollectionCatalog catalog, FreeSpaceManager freeSpace,
      TransactionManager transactionManager, string collectionName, IndexRoot root) {
    _pageManager = pageManager;
    _catalog = catalog;
    _freeSpace = freeSpace;
    _transactionManager = transactionManager;
    _collectionName = collectionName;
    _root = root;
  }

  public string Name => _root.Name;

  //D-2 and D-4: the root is a physical pointer, so it is held in the collection's catalogue
  //document and read from there rather than remembered. Zero until the first insert.
  public uint RootPageIndex => _root.Read();

  public bool IsEmpty => RootPageIndex == default;

  public DocumentAddress? Find(byte[] key) {
    if (IsEmpty) {
      return null;
    }
    var leaf = DescendToLeaf(key);
    var position = FindEntryPosition(leaf, key);
    return position < leaf.Entries.Count && KeyComparer.Compare(leaf.Entries[position].Key, key) == 0
      ? leaf.Entries[position].Address
      : null;
  }

  //How often the tree has had to change shape. D-1 chose a time-ordered identifier so that
  //this stays small for the primary index: every insert goes to the right-hand edge, so the
  //same few pages split over and over instead of pages all over the file.
  public long LeafSplits { get; private set; }

  public long InteriorSplits { get; private set; }

  public void Insert(byte[] key, DocumentAddress address) {
    Write(key, address, replaceExisting: false);
  }

  //VR-12's copy on write moves a record to a new page and slot, and the entry has to follow
  //it. The same descent either finds the key and repoints it or does not and inserts it, so
  //an update costs one pass down the tree rather than a probe and then a pass.
  public bool Upsert(byte[] key, DocumentAddress address) {
    return Write(key, address, replaceExisting: true);
  }

  private bool Write(byte[] key, DocumentAddress address, bool replaceExisting) {
    _transactionManager.RequireTransaction();
    if (IsEmpty) {
      var firstLeaf = CreateLeaf();
      _root.Write(firstLeaf.Index);
    }
    var rootIndex = RootPageIndex;
    var inserted = true;
    var split = InsertInto(LoadNode(rootIndex), key, address, replaceExisting, ref inserted);
    if (split is { } grown) {
      GrowRoot(rootIndex, grown);
    }
    return inserted;
  }

  public bool Delete(byte[] key) {
    _transactionManager.RequireTransaction();
    if (IsEmpty) {
      return false;
    }
    var root = LoadNode(RootPageIndex);
    if (!RemoveFrom(root, key)) {
      return false;
    }
    ShrinkRoot();
    return true;
  }

  //The ordered full traversal, and the reason the leaves are linked: one descent to the
  //left-hand end and then a walk along the chain, touching each leaf once and no interior
  //node at all.
  public IEnumerable<IndexEntry> Scan() {
    return Leaves().SelectMany(leaf => leaf.Entries);
  }

  //The leaf chain itself, left to right. Every entry in the tree is in here exactly once,
  //which is what "all entries live in leaves" means when it is written down.
  public IEnumerable<IndexLeafPage> Leaves() {
    var pageIndex = LeftmostLeafIndex();
    while (pageIndex != default) {
      var leaf = LoadLeaf(pageIndex);
      yield return leaf;
      pageIndex = leaf.NextPageIndex;
    }
  }

  //Every node, walked from the root through the child pointers rather than along the chain.
  //For diagnostics and for checking the shape against what is actually on the pages.
  public IEnumerable<BaseIndexPage> Nodes() {
    var pending = new Stack<uint>();
    if (!IsEmpty) {
      pending.Push(RootPageIndex);
    }
    while (pending.Count > 0) {
      var node = LoadNode(pending.Pop());
      yield return node;
      if (node is IndexInteriorPage interior) {
        for (var position = 0; position < interior.ChildCount; position++) {
          pending.Push(interior.ChildAt(position));
        }
      }
    }
  }

  //Half-open: from is included, to is not, and a null end is open. Descends once to find
  //where to start and then walks the same chain Scan does.
  public IEnumerable<IndexEntry> Range(byte[] from, byte[] to) {
    if (IsEmpty) {
      yield break;
    }
    var leaf = from == null ? LoadLeafOrNull(LeftmostLeafIndex()) : DescendToLeaf(from);
    var position = from == null ? 0 : FindEntryPosition(leaf, from);
    while (leaf != null) {
      for (var i = position; i < leaf.Entries.Count; i++) {
        var entry = leaf.Entries[i];
        if (to != null && KeyComparer.Compare(entry.Key, to) >= 0) {
          yield break;
        }
        yield return entry;
      }
      leaf = LoadLeafOrNull(leaf.NextPageIndex);
      position = 0;
    }
  }

  //What the tests and the page-read check of NFR-2 need to see: how far a lookup descends.
  public int Height() {
    var height = 0;
    for (var pageIndex = RootPageIndex; pageIndex != default; height++) {
      var node = LoadNode(pageIndex);
      if (node is IndexLeafPage) {
        return height + 1;
      }
      pageIndex = ((IndexInteriorPage)node).FirstChildPageIndex;
    }
    return height;
  }

  private SplitResult? InsertInto(BaseIndexPage node, byte[] key, DocumentAddress address,
      bool replaceExisting, ref bool inserted) {
    if (node is IndexLeafPage leaf) {
      return InsertIntoLeaf(leaf, key, address, replaceExisting, ref inserted);
    }
    var interior = (IndexInteriorPage)node;
    var childPosition = FindChildPosition(interior, key);
    var split = InsertInto(LoadNode(interior.ChildAt(childPosition)), key, address, replaceExisting,
      ref inserted);
    if (split is not { } grown) {
      return null;
    }
    //The child at childPosition just split. Its right half becomes the child after it, and
    //the separator between them goes here.
    interior.Entries.Insert(childPosition, new IndexSeparator(grown.SeparatorKey, grown.RightPageIndex));
    Track(interior);
    return interior.IsOverfull ? SplitInterior(interior) : null;
  }

  private SplitResult? InsertIntoLeaf(IndexLeafPage leaf, byte[] key, DocumentAddress address,
      bool replaceExisting, ref bool inserted) {
    var position = FindEntryPosition(leaf, key);
    if (position < leaf.Entries.Count && KeyComparer.Compare(leaf.Entries[position].Key, key) == 0) {
      if (!replaceExisting) {
        throw new DuplicateIndexKeyException(_collectionName, key);
      }
      //D-2 again: only the address changes. The key is the record identity and does not move
      //when the record does.
      leaf.Entries[position] = leaf.Entries[position] with { Address = address };
      Track(leaf);
      inserted = false;
      return null;
    }
    leaf.Entries.Insert(position, new IndexEntry(key, address));
    Track(leaf);
    return leaf.IsOverfull ? SplitLeaf(leaf, position) : null;
  }

  //The upper half moves to a new leaf and the chain is relinked through it. The separator
  //handed up is a copy of the new leaf's first key: in a B+Tree it is a signpost, and the
  //entry it was copied from stays where it is.
  private SplitResult SplitLeaf(IndexLeafPage leaf, int insertedPosition) {
    LeafSplits++;
    //Appending to the right-hand edge, which is what a time-ordered identifier does on every
    //insert (D-1). Splitting such a leaf down the middle would leave it half empty for ever,
    //because nothing will ever be inserted into it again: the whole left half is below every
    //key still to come. So the new entry goes on alone and the full leaf stays full.
    //
    //Only when the new entry is the last one, because then taking it back out again leaves
    //exactly the leaf that fitted before the insert.
    var appendedAtTheEdge = leaf.NextPageIndex == default && insertedPosition == leaf.Entries.Count - 1;
    var half = appendedAtTheEdge
      ? leaf.Entries.Count - 1
      : HalfPoint(leaf.Entries.Count, i => IndexLeafPage.SizeOf(leaf.Entries[i]));
    var right = CreateLeaf();
    right.Entries = leaf.Entries.GetRange(half, leaf.Entries.Count - half);
    leaf.Entries.RemoveRange(half, leaf.Entries.Count - half);
    right.NextPageIndex = leaf.NextPageIndex;
    leaf.NextPageIndex = right.Index;
    Track(leaf, right);
    return new SplitResult(right.Entries[0].Key, right.Index);
  }

  //An interior node splits differently: the middle separator is not copied but moved up, and
  //the child it pointed at becomes the new node's first child. A key in an interior node is
  //only a signpost, so nothing is lost by taking it out of this level.
  private SplitResult SplitInterior(IndexInteriorPage node) {
    InteriorSplits++;
    var half = HalfPoint(node.Entries.Count, i => IndexInteriorPage.SizeOf(node.Entries[i]));
    //One separator has to be left on each side of the one that moves up.
    half = Math.Clamp(half, 1, node.Entries.Count - 1);
    var middle = node.Entries[half];
    var right = CreateInterior();
    right.FirstChildPageIndex = middle.ChildPageIndex;
    right.Entries = node.Entries.GetRange(half + 1, node.Entries.Count - half - 1);
    node.Entries.RemoveRange(half, node.Entries.Count - half);
    Track(node, right);
    return new SplitResult(middle.Key, right.Index);
  }

  //The only place the tree gets taller, and it gets taller at the root rather than at the
  //leaves, which is what keeps every leaf the same distance from the top.
  private void GrowRoot(uint oldRootIndex, SplitResult split) {
    var root = CreateInterior();
    root.FirstChildPageIndex = oldRootIndex;
    root.Entries.Add(new IndexSeparator(split.SeparatorKey, split.RightPageIndex));
    Track(root);
    _root.Write(root.Index);
  }

  private bool RemoveFrom(BaseIndexPage node, byte[] key) {
    if (node is IndexLeafPage leaf) {
      var position = FindEntryPosition(leaf, key);
      if (position >= leaf.Entries.Count || KeyComparer.Compare(leaf.Entries[position].Key, key) != 0) {
        return false;
      }
      leaf.Entries.RemoveAt(position);
      Track(leaf);
      return true;
    }
    var interior = (IndexInteriorPage)node;
    var childPosition = FindChildPosition(interior, key);
    var child = LoadNode(interior.ChildAt(childPosition));
    if (!RemoveFrom(child, key)) {
      return false;
    }
    //A separator that is now larger than nothing in its subtree is still a correct signpost,
    //so removing the smallest key of a leaf costs the parent nothing. Only an underfull node
    //has to be dealt with.
    if (child.IsUnderfull) {
      Rebalance(interior, childPosition, child);
    }
    return true;
  }

  //Borrow one entry from a sibling if that leaves both of them legal; otherwise merge the
  //two into one and drop a separator out of the parent.
  private void Rebalance(IndexInteriorPage parent, int position, BaseIndexPage child) {
    if (child is IndexLeafPage leaf) {
      RebalanceLeaf(parent, position, leaf);
    } else {
      RebalanceInterior(parent, position, (IndexInteriorPage)child);
    }
  }

  private void RebalanceLeaf(IndexInteriorPage parent, int position, IndexLeafPage leaf) {
    var left = position > 0 ? LoadLeaf(parent.ChildAt(position - 1)) : null;
    if (left is { Entries.Count: > 0 } && CanMove(left, leaf, IndexLeafPage.SizeOf(left.Entries[^1]))) {
      var moved = left.Entries[^1];
      left.Entries.RemoveAt(left.Entries.Count - 1);
      leaf.Entries.Insert(0, moved);
      //The separator in front of a leaf is its smallest key, and that has just changed.
      parent.Entries[position - 1] = parent.Entries[position - 1] with { Key = leaf.Entries[0].Key };
      Track(left, leaf, parent);
      return;
    }
    var right = position < parent.ChildCount - 1 ? LoadLeaf(parent.ChildAt(position + 1)) : null;
    if (right is { Entries.Count: > 1 } && CanMove(right, leaf, IndexLeafPage.SizeOf(right.Entries[0]))) {
      var moved = right.Entries[0];
      right.Entries.RemoveAt(0);
      leaf.Entries.Add(moved);
      parent.Entries[position] = parent.Entries[position] with { Key = right.Entries[0].Key };
      Track(right, leaf, parent);
      return;
    }
    if (left != null && Fits(left, leaf)) {
      MergeLeaves(parent, position - 1, left, leaf);
    } else if (right != null && Fits(leaf, right)) {
      MergeLeaves(parent, position, leaf, right);
    }
    //Neither borrowing nor merging fits only when the keys are long enough that two
    //half-empty nodes would overflow one page. The node is left underfull, which costs space
    //and no correctness: every invariant the search relies on still holds.
  }

  private void MergeLeaves(IndexInteriorPage parent, int separatorPosition, IndexLeafPage left,
      IndexLeafPage right) {
    left.Entries.AddRange(right.Entries);
    //The chain closes over the gap, or a scan would walk into a retired page.
    left.NextPageIndex = right.NextPageIndex;
    //The separator and the pointer to the right node are one entry, so dropping it drops both.
    parent.Entries.RemoveAt(separatorPosition);
    Track(left, parent);
    Retire(right);
  }

  private void RebalanceInterior(IndexInteriorPage parent, int position, IndexInteriorPage node) {
    var left = position > 0 ? LoadInterior(parent.ChildAt(position - 1)) : null;
    if (left is { Entries.Count: > 0 }
        && CanMove(left, node, IndexInteriorPage.SizeOf(parent.Entries[position - 1]))) {
      //A rotation through the parent: the separator above comes down into this node in front
      //of its first child, and the sibling's last separator goes up to replace it.
      node.Entries.Insert(0, new IndexSeparator(parent.Entries[position - 1].Key, node.FirstChildPageIndex));
      node.FirstChildPageIndex = left.Entries[^1].ChildPageIndex;
      parent.Entries[position - 1] = parent.Entries[position - 1] with { Key = left.Entries[^1].Key };
      left.Entries.RemoveAt(left.Entries.Count - 1);
      Track(left, node, parent);
      return;
    }
    var right = position < parent.ChildCount - 1 ? LoadInterior(parent.ChildAt(position + 1)) : null;
    if (right is { Entries.Count: > 0 }
        && CanMove(right, node, IndexInteriorPage.SizeOf(parent.Entries[position]))) {
      node.Entries.Add(new IndexSeparator(parent.Entries[position].Key, right.FirstChildPageIndex));
      right.FirstChildPageIndex = right.Entries[0].ChildPageIndex;
      parent.Entries[position] = parent.Entries[position] with { Key = right.Entries[0].Key };
      right.Entries.RemoveAt(0);
      Track(right, node, parent);
      return;
    }
    if (left != null && Fits(left, node, parent.Entries[position - 1])) {
      MergeInteriors(parent, position - 1, left, node);
    } else if (right != null && Fits(node, right, parent.Entries[position])) {
      MergeInteriors(parent, position, node, right);
    }
  }

  private void MergeInteriors(IndexInteriorPage parent, int separatorPosition, IndexInteriorPage left,
      IndexInteriorPage right) {
    //The separator comes down between the two sets of children, because the right node's
    //first child has no key in front of it until it has one here.
    left.Entries.Add(new IndexSeparator(parent.Entries[separatorPosition].Key, right.FirstChildPageIndex));
    left.Entries.AddRange(right.Entries);
    parent.Entries.RemoveAt(separatorPosition);
    Track(left, parent);
    Retire(right);
  }

  //A root that has run out of separators has one child and decides nothing, so the child
  //takes its place and the tree gets shorter — the mirror of GrowRoot.
  private void ShrinkRoot() {
    while (LoadNode(RootPageIndex) is IndexInteriorPage { Entries.Count: 0 } root) {
      var child = root.FirstChildPageIndex;
      _root.Write(child);
      Retire(root);
    }
  }

  //Whether the donor can give that many bytes away and the receiver can take them.
  private static bool CanMove(BaseIndexPage donor, BaseIndexPage receiver, int bytes) {
    return donor.ContentByteSize - bytes >= donor.MinimumBytes
      && receiver.ContentByteSize + bytes <= receiver.UsableBytes;
  }

  private static bool Fits(BaseIndexPage left, BaseIndexPage right, IndexSeparator pulledDown) {
    return left.ContentByteSize + right.ContentByteSize + IndexInteriorPage.SizeOf(pulledDown)
      <= left.UsableBytes;
  }

  private static bool Fits(BaseIndexPage left, BaseIndexPage right) {
    return left.ContentByteSize + right.ContentByteSize <= left.UsableBytes;
  }

  //Where the entries divide so that each half carries about the same number of bytes. A
  //count would do for keys that are all the same length; bytes are what actually fill a page.
  private static int HalfPoint(int count, Func<int, int> sizeOf) {
    var total = 0;
    for (var i = 0; i < count; i++) {
      total += sizeOf(i);
    }
    var accumulated = 0;
    for (var i = 0; i < count; i++) {
      accumulated += sizeOf(i);
      if (accumulated * 2 >= total) {
        //At least one entry stays behind, and at least one moves.
        return Math.Clamp(i + 1, 1, count - 1);
      }
    }
    return count - 1;
  }

  private IndexLeafPage DescendToLeaf(byte[] key) {
    var node = LoadNode(RootPageIndex);
    while (node is IndexInteriorPage interior) {
      node = LoadNode(interior.ChildAt(FindChildPosition(interior, key)));
    }
    return (IndexLeafPage)node;
  }

  private uint LeftmostLeafIndex() {
    var pageIndex = RootPageIndex;
    while (pageIndex != default && LoadNode(pageIndex) is IndexInteriorPage interior) {
      pageIndex = interior.FirstChildPageIndex;
    }
    return pageIndex;
  }

  //The first entry whose key is not below the one being looked for — where an equal key
  //would be found, and where a new one belongs.
  private static int FindEntryPosition(IndexLeafPage leaf, byte[] key) {
    var low = 0;
    var high = leaf.Entries.Count;
    while (low < high) {
      var middle = (low + high) / 2;
      if (KeyComparer.Compare(leaf.Entries[middle].Key, key) < 0) {
        low = middle + 1;
      } else {
        high = middle;
      }
    }
    return low;
  }

  //The child to descend into: the number of separators at or below the key, which is the
  //position of the child that covers it.
  private static int FindChildPosition(IndexInteriorPage node, byte[] key) {
    var low = 0;
    var high = node.Entries.Count;
    while (low < high) {
      var middle = (low + high) / 2;
      if (KeyComparer.Compare(node.Entries[middle].Key, key) <= 0) {
        low = middle + 1;
      } else {
        high = middle;
      }
    }
    return low;
  }

  private IndexLeafPage CreateLeaf() {
    return CreateNode<IndexLeafPage>(PageType.IndexLeaf);
  }

  private IndexInteriorPage CreateInterior() {
    return CreateNode<IndexInteriorPage>(PageType.IndexInterior);
  }

  private T CreateNode<T>(PageType type) where T : BaseIndexPage, new() {
    //A page a merge retired comes back before the file is grown.
    var pageIndex = _freeSpace.TakeRetiredIndexPage(_collectionName) ?? _catalog.AllocatePageIndex();
    var page = _pageManager.CreateNewMemoryPage<T>(type, pageIndex);
    page.OwningCollectionId = _catalog.GetOwningCollectionId(_collectionName);
    _freeSpace.RecordIndexPage(_collectionName, pageIndex, inUse: true);
    Track(page);
    return page;
  }

  private void Retire(BaseIndexPage page) {
    _freeSpace.RecordIndexPage(_collectionName, page.Index, inUse: false);
  }

  //The identity map first: a node this transaction has already changed must not be read back
  //from disk and changed again through a second object.
  private BaseIndexPage LoadNode(uint pageIndex) {
    return _transactionManager.FindTrackedPage<BaseIndexPage>(pageIndex)
      ?? _pageManager.LoadPage<BaseIndexPage>(pageIndex,
        type => type == PageType.IndexLeaf ? new IndexLeafPage() : new IndexInteriorPage());
  }

  private IndexLeafPage LoadLeaf(uint pageIndex) {
    return (IndexLeafPage)LoadNode(pageIndex);
  }

  private IndexLeafPage LoadLeafOrNull(uint pageIndex) {
    return pageIndex == default ? null : LoadLeaf(pageIndex);
  }

  private IndexInteriorPage LoadInterior(uint pageIndex) {
    return (IndexInteriorPage)LoadNode(pageIndex);
  }

  //Every page the tree changes is tracked, which is the whole of its part in TX-1 and TX-2:
  //the transaction journals it, writes it and undoes it exactly as it does a data page.
  private void Track(params BaseIndexPage[] pages) {
    foreach (var page in pages) {
      _transactionManager.Track(page);
    }
  }

  //What a split hands to the level above: the key that separates the two halves and the page
  //holding the right one.
  private readonly record struct SplitResult(byte[] SeparatorKey, uint RightPageIndex);
}
