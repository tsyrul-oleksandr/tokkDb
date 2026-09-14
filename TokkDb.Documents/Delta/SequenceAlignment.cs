namespace TokkDb.Documents.Delta;

//DL-3. The two sequence algorithms the array diff stands on: a longest common subsequence in
//linear memory, and a longest increasing subsequence.
internal static class SequenceAlignment {
  //The pairs (i, j) of a longest common subsequence of two sequences of lengths n and m, in
  //increasing order of both. Myers' O((N+M)D) algorithm in its linear-space form: the middle
  //snake of a shortest edit path is found by a forward and a backward search that meet, and each
  //half is solved the same way. The working memory is the two frontier arrays, never the D²
  //history the simple form keeps, so two arrays that share nothing cost their length, not its
  //square. A shared prefix and suffix are taken off before any search, which is why an array
  //with one insertion costs one pass.
  public static List<(int A, int B)> LongestCommonSubsequence(int n, int m, Func<int, int, bool> equal) {
    var pairs = new List<(int, int)>();
    if (n == 0 || m == 0) {
      return pairs;
    }
    var forward = new int[n + m + 5];
    var backward = new int[n + m + 5];
    Recurse(0, n, 0, m);
    return pairs;

    void Recurse(int aStart, int aEnd, int bStart, int bEnd) {
      while (aStart < aEnd && bStart < bEnd && equal(aStart, bStart)) {
        pairs.Add((aStart, bStart));
        aStart++;
        bStart++;
      }
      var suffix = 0;
      while (aStart < aEnd - suffix && bStart < bEnd - suffix && equal(aEnd - 1 - suffix, bEnd - 1 - suffix)) {
        suffix++;
      }
      var aTail = aEnd - suffix;
      var bTail = bEnd - suffix;
      if (aStart < aTail && bStart < bTail) {
        Split(aStart, aTail, bStart, bTail);
      }
      for (var t = 0; t < suffix; t++) {
        pairs.Add((aTail + t, bTail + t));
      }
    }

    void Split(int aStart, int aEnd, int bStart, int bEnd) {
      var length = aEnd - aStart;
      var width = bEnd - bStart;
      var (d, x, y, u, v) = MiddleSnake(aStart, aEnd, bStart, bEnd);
      if (d > 1) {
        Recurse(aStart, aStart + x, bStart, bStart + y);
        for (var t = 0; t < u - x; t++) {
          pairs.Add((aStart + x + t, bStart + y + t));
        }
        Recurse(aStart + u, aEnd, bStart + v, bEnd);
        return;
      }
      //At most one edit between them: the shorter side is the longer with one element left
      //out, and the common prefix says where. Unreachable after the prefix and suffix have
      //been taken off, and kept for the algorithm's own completeness.
      var shorter = Math.Min(length, width);
      var prefix = 0;
      while (prefix < shorter && equal(aStart + prefix, bStart + prefix)) {
        prefix++;
      }
      for (var t = 0; t < prefix; t++) {
        pairs.Add((aStart + t, bStart + t));
      }
      var skipA = length > width ? 1 : 0;
      var skipB = width > length ? 1 : 0;
      for (var t = prefix; t < shorter; t++) {
        pairs.Add((aStart + t + skipA, bStart + t + skipB));
      }
    }

    //The middle snake of a shortest edit path from (aStart, bStart) to (aEnd, bEnd), in
    //coordinates relative to the start: its length D, and the snake from (x, y) to (u, v).
    //Forward diagonals are k = x - y; the backward search runs on the reversed sequences,
    //where the same diagonal is delta - k with delta = n - m. The two searches overlap on a
    //diagonal when the forward frontier has reached at least as far as the backward one.
    (int D, int X, int Y, int U, int V) MiddleSnake(int aStart, int aEnd, int bStart, int bEnd) {
      var n = aEnd - aStart;
      var m = bEnd - bStart;
      var max = (n + m + 1) / 2;
      var delta = n - m;
      var odd = (delta & 1) != 0;
      var offset = max + 1;
      forward[offset + 1] = 0;
      backward[offset + 1] = 0;
      for (var d = 0; d <= max; d++) {
        for (var k = -d; k <= d; k += 2) {
          var x = k == -d || (k != d && forward[offset + k - 1] < forward[offset + k + 1])
            ? forward[offset + k + 1]
            : forward[offset + k - 1] + 1;
          var y = x - k;
          var xStart = x;
          var yStart = y;
          while (x < n && y < m && equal(aStart + x, bStart + y)) {
            x++;
            y++;
          }
          forward[offset + k] = x;
          if (odd) {
            var reverseK = delta - k;
            if (reverseK >= -(d - 1) && reverseK <= d - 1 && x + backward[offset + reverseK] >= n) {
              return (2 * d - 1, xStart, yStart, x, y);
            }
          }
        }
        for (var k = -d; k <= d; k += 2) {
          var x = k == -d || (k != d && backward[offset + k - 1] < backward[offset + k + 1])
            ? backward[offset + k + 1]
            : backward[offset + k - 1] + 1;
          var y = x - k;
          var xStart = x;
          var yStart = y;
          while (x < n && y < m && equal(aEnd - 1 - x, bEnd - 1 - y)) {
            x++;
            y++;
          }
          backward[offset + k] = x;
          if (!odd) {
            var forwardK = delta - k;
            if (forwardK >= -d && forwardK <= d && x + forward[offset + forwardK] >= n) {
              return (2 * d, n - x, m - y, n - xStart, m - yStart);
            }
          }
        }
      }
      throw new InvalidOperationException("The edit graph has no middle snake, which cannot happen.");
    }
  }

  //Which positions of a sequence form one longest strictly increasing subsequence. Patience
  //sorting with a binary search: O(n log n).
  public static bool[] LongestIncreasingSubsequence(IReadOnlyList<int> values) {
    var member = new bool[values.Count];
    if (values.Count == 0) {
      return member;
    }
    //tails[l] is the position of the smallest value that ends an increasing run of length l+1.
    var tails = new List<int>();
    var previous = new int[values.Count];
    for (var i = 0; i < values.Count; i++) {
      var low = 0;
      var high = tails.Count;
      while (low < high) {
        var middle = (low + high) / 2;
        if (values[tails[middle]] < values[i]) {
          low = middle + 1;
        } else {
          high = middle;
        }
      }
      previous[i] = low > 0 ? tails[low - 1] : -1;
      if (low == tails.Count) {
        tails.Add(i);
      } else {
        tails[low] = i;
      }
    }
    for (var position = tails[^1]; position >= 0; position = previous[position]) {
      member[position] = true;
    }
    return member;
  }
}
