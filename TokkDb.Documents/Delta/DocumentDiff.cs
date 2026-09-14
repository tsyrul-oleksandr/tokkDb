using TokkDb.Documents.Values;

namespace TokkDb.Documents.Delta;

//DL-1, DL-3, DL-5 and DL-8. Computes the delta between two document values.
//
//Objects are compared field by field in ordinal order: a field on one side only is an Add or a
//Remove, and a field on both is compared inside. Anything else that differs is a Replace of the
//whole value — a scalar, a value whose type changed, an object against a scalar. Arrays are
//aligned on the canonical bytes of their elements and, where the options declare an element key
//that both sides honour, matched by that key instead.
//
//The delta refers to the values of the documents it was computed from; it does not copy them.
public static class DocumentDiff {
  public static DocumentDelta Compute(IDocumentValue a, IDocumentValue b, DiffOptions? options = null) {
    ArgumentNullException.ThrowIfNull(a);
    ArgumentNullException.ThrowIfNull(b);
    var builder = new Builder(options ?? DiffOptions.None);
    builder.Diff(DeltaPath.Root, a, b);
    return builder.ToDelta();
  }

  public static DocumentDelta Compute(ObjectDocument a, ObjectDocument b, DiffOptions? options = null) {
    ArgumentNullException.ThrowIfNull(a);
    ArgumentNullException.ThrowIfNull(b);
    return Compute(a.Value, b.Value, options);
  }

  //A pair of elements taken as the same element on both sides: a's I and b's J. Identical pairs
  //have the same bytes; the others are compared inside.
  private sealed record Pair(int I, int J, bool Identical);

  private sealed class Builder {
    private readonly DiffOptions _options;
    private readonly List<DeltaElement> _elements = [];
    private readonly List<ArrayMatching> _matchings = [];

    public Builder(DiffOptions options) {
      _options = options;
    }

    public DocumentDelta ToDelta() {
      return new DocumentDelta(_elements, _matchings);
    }

    public void Diff(DeltaPath path, IDocumentValue a, IDocumentValue b) {
      if (a is ObjectDocumentValue objectA && b is ObjectDocumentValue objectB) {
        DiffObjects(path, objectA, objectB);
      } else if (a is ArrayDocumentValue arrayA && b is ArrayDocumentValue arrayB) {
        DiffArrays(path, arrayA, arrayB);
      } else if (!CanonicalValue.Equal(a, b)) {
        _elements.Add(DeltaElement.Replace(path, a, b));
      }
    }

    private void DiffObjects(DeltaPath path, ObjectDocumentValue a, ObjectDocumentValue b) {
      var keys = new SortedSet<string>(a.Values.Keys, StringComparer.Ordinal);
      keys.UnionWith(b.Values.Keys);
      foreach (var key in keys) {
        var inA = a.Values.TryGetValue(key, out var valueA);
        var inB = b.Values.TryGetValue(key, out var valueB);
        if (inA && inB) {
          Diff(path.Field(key), valueA!, valueB!);
        } else if (inA) {
          _elements.Add(DeltaElement.Remove(path.Field(key), valueA!));
        } else {
          _elements.Add(DeltaElement.Add(path.Field(key), valueB!));
        }
      }
    }

    private void DiffArrays(DeltaPath path, ArrayDocumentValue a, ArrayDocumentValue b) {
      var (bytesA, offsetsA) = CanonicalValue.ElementBytes(a);
      var (bytesB, offsetsB) = CanonicalValue.ElementBytes(b);
      var key = _options.ElementKeyFor(path);
      List<Pair> pairs;
      string? reason = null;
      if (key is not null && TryMatchByKey(a, b, key, bytesA, offsetsA, bytesB, offsetsB, out var keyed, out reason)) {
        _matchings.Add(new ArrayMatching(path, ArrayMatchingMode.ByKey, key, null));
        pairs = keyed;
      } else {
        if (key is not null) {
          _matchings.Add(new ArrayMatching(path, ArrayMatchingMode.ByPosition, key, reason));
        }
        pairs = MatchByPosition(a, b, bytesA, offsetsA, bytesB, offsetsB);
      }
      Emit(path, a, b, pairs);
    }

    //DL-3. The pairs of a positional matching: the longest common subsequence of the elements'
    //bytes, then the gaps between its anchors — a gap of equal length on both sides pairs its
    //elements one to one to be compared inside, an unequal gap leaves its elements unmatched —
    //and then, among the unmatched, an element removed with the same bytes as one inserted is
    //paired as moved.
    private static List<Pair> MatchByPosition(ArrayDocumentValue a, ArrayDocumentValue b,
        byte[] bytesA, int[] offsetsA, byte[] bytesB, int[] offsetsB) {
      var n = a.Values.Length;
      var m = b.Values.Length;
      var hashesA = Hashes(bytesA, offsetsA);
      var hashesB = Hashes(bytesB, offsetsB);
      bool Equal(int i, int j) {
        return hashesA[i] == hashesB[j] && Slice(bytesA, offsetsA, i).SequenceEqual(Slice(bytesB, offsetsB, j));
      }
      var common = SequenceAlignment.LongestCommonSubsequence(n, m, Equal);
      var pairs = new List<Pair>(common.Count);
      var removed = new List<int>();
      var inserted = new List<int>();
      var previousI = -1;
      var previousJ = -1;
      foreach (var (i, j) in common.Append((n, m))) {
        var gapA = i - previousI - 1;
        var gapB = j - previousJ - 1;
        if (gapA == gapB) {
          for (var t = 1; t <= gapA; t++) {
            pairs.Add(new Pair(previousI + t, previousJ + t, Identical: false));
          }
        } else {
          for (var t = previousI + 1; t < i; t++) {
            removed.Add(t);
          }
          for (var t = previousJ + 1; t < j; t++) {
            inserted.Add(t);
          }
        }
        if (i < n) {
          pairs.Add(new Pair(i, j, Identical: true));
        }
        previousI = i;
        previousJ = j;
      }
      if (removed.Count > 0 && inserted.Count > 0) {
        var byHash = new Dictionary<int, Queue<int>>();
        foreach (var i in removed) {
          if (!byHash.TryGetValue(hashesA[i], out var queue)) {
            byHash[hashesA[i]] = queue = new Queue<int>();
          }
          queue.Enqueue(i);
        }
        foreach (var j in inserted) {
          if (!byHash.TryGetValue(hashesB[j], out var queue)) {
            continue;
          }
          //The first removed element with these bytes, so that identical elements pair up in order.
          var count = queue.Count;
          for (var attempt = 0; attempt < count; attempt++) {
            var i = queue.Dequeue();
            if (Equal(i, j)) {
              pairs.Add(new Pair(i, j, Identical: true));
              break;
            }
            queue.Enqueue(i);
          }
        }
      }
      pairs.Sort((left, right) => left.I.CompareTo(right.I));
      return pairs;
    }

    //DL-8. Pairs by the element key, when every element on both sides is an object holding a
    //distinct, non-null scalar at the key. Otherwise nothing, with the reason.
    private static bool TryMatchByKey(ArrayDocumentValue a, ArrayDocumentValue b, string key,
        byte[] bytesA, int[] offsetsA, byte[] bytesB, int[] offsetsB, out List<Pair> pairs, out string? reason) {
      pairs = [];
      if (!TryKeys(a, key, "left", out var keysA, out reason) || !TryKeys(b, key, "right", out var keysB, out reason)) {
        return false;
      }
      var positionsA = new Dictionary<string, int>(keysA.Length, StringComparer.Ordinal);
      for (var i = 0; i < keysA.Length; i++) {
        positionsA[keysA[i]] = i;
      }
      for (var j = 0; j < keysB.Length; j++) {
        if (positionsA.TryGetValue(keysB[j], out var i)) {
          var identical = Slice(bytesA, offsetsA, i).SequenceEqual(Slice(bytesB, offsetsB, j));
          pairs.Add(new Pair(i, j, identical));
        }
      }
      pairs.Sort((left, right) => left.I.CompareTo(right.I));
      return true;
    }

    private static bool TryKeys(ArrayDocumentValue array, string key, string side, out string[] keys, out string? reason) {
      keys = new string[array.Values.Length];
      var seen = new Dictionary<string, int>(StringComparer.Ordinal);
      for (var i = 0; i < array.Values.Length; i++) {
        if (array.Values[i] is not ObjectDocumentValue element) {
          reason = $"element [{i}] on the {side} is not an object";
          return false;
        }
        var value = element.Values.GetValueOrDefault(key);
        if (value is null) {
          reason = $"element [{i}] on the {side} has no '{key}'";
          return false;
        }
        if (value is NullDocumentValue) {
          reason = $"element [{i}] on the {side} has a null '{key}'";
          return false;
        }
        if (value is ObjectDocumentValue or ArrayDocumentValue) {
          reason = $"'{key}' of element [{i}] on the {side} is {CanonicalValue.Describe(value)}, which cannot be a key";
          return false;
        }
        var encoded = Convert.ToHexString(CanonicalValue.Bytes(value));
        if (seen.TryGetValue(encoded, out var first)) {
          reason = $"'{key}' {CanonicalValue.Describe(value)} is held by elements [{first}] and [{i}] on the {side}";
          return false;
        }
        seen[encoded] = i;
        keys[i] = encoded;
      }
      reason = null;
      return true;
    }

    //Turns a matching into elements whose indices are valid at the moment each is applied, in
    //list order. The rule, which ApplyTo relies on and Invert preserves by reversing it:
    //  1. RemoveAt for every unmatched element of a, in ascending order, each at the position the
    //     element has once the ones before it are gone;
    //  2. Move for every matched element that is out of order — the ones outside a longest
    //     increasing subsequence of the b positions, taken in ascending b order — from the
    //     element's current position to the position right after the last settled element that
    //     precedes it in b, where settled means in that subsequence or already moved;
    //  3. Insert for every unmatched element of b, in ascending order, at its final position,
    //     which is right because everything before it in b is already in place;
    //  4. the changes inside matched pairs whose bytes differ, at the pair's final position in b.
    private void Emit(DeltaPath path, ArrayDocumentValue a, ArrayDocumentValue b, List<Pair> pairs) {
      var matchedA = new bool[a.Values.Length];
      var matchedB = new bool[b.Values.Length];
      foreach (var pair in pairs) {
        matchedA[pair.I] = true;
        matchedB[pair.J] = true;
      }
      var removed = 0;
      for (var i = 0; i < a.Values.Length; i++) {
        if (!matchedA[i]) {
          _elements.Add(DeltaElement.RemoveAt(path.At(i - removed), a.Values[i]));
          removed++;
        }
      }
      var settled = SequenceAlignment.LongestIncreasingSubsequence(pairs.Select(pair => pair.J).ToList());
      var settledPairs = new HashSet<Pair>(ReferenceEqualityComparer.Instance);
      for (var t = 0; t < pairs.Count; t++) {
        if (settled[t]) {
          settledPairs.Add(pairs[t]);
        }
      }
      var working = new List<Pair>(pairs);
      foreach (var mover in pairs.Where(pair => !settledPairs.Contains(pair)).OrderBy(pair => pair.J)) {
        var from = working.IndexOf(mover);
        working.RemoveAt(from);
        var to = 0;
        for (var t = 0; t < working.Count; t++) {
          if (settledPairs.Contains(working[t]) && working[t].J < mover.J) {
            to = t + 1;
          }
        }
        working.Insert(to, mover);
        settledPairs.Add(mover);
        _elements.Add(DeltaElement.Move(path.At(from), a.Values[mover.I], to));
      }
      for (var j = 0; j < b.Values.Length; j++) {
        if (!matchedB[j]) {
          _elements.Add(DeltaElement.Insert(path.At(j), b.Values[j]));
        }
      }
      foreach (var pair in pairs.Where(pair => !pair.Identical).OrderBy(pair => pair.J)) {
        Diff(path.At(pair.J), a.Values[pair.I], b.Values[pair.J]);
      }
    }

    private static int[] Hashes(byte[] bytes, int[] offsets) {
      var hashes = new int[offsets.Length - 1];
      for (var i = 0; i < hashes.Length; i++) {
        hashes[i] = CanonicalValue.Hash(Slice(bytes, offsets, i));
      }
      return hashes;
    }

    private static ReadOnlySpan<byte> Slice(byte[] bytes, int[] offsets, int i) {
      return bytes.AsSpan(offsets[i], offsets[i + 1] - offsets[i]);
    }
  }
}
