using System.Globalization;
using System.Text;

namespace TokkDb.Documents.Keys;

//D-3: string keys are compared ordinally over an explicitly normalised form, folded once
//here at write time and stored folded, rather than compared with a culture-sensitive
//collation at read time. An implicit collation would make the same query return different
//results on two machines, which for Ukrainian text is not a theoretical risk.
public static class KeyNormalization {
  //Decompose, drop the combining marks, recompose, then upper-case with the invariant
  //culture — invariant so that a Turkish locale cannot make "I" and "i" stop folding.
  //
  //This is a deliberate loss. In Ukrainian, "й" decomposes to "и" plus a combining breve and
  //folds to "и"; "ї" folds to "і". So "мий" and "мии" share a key. That is what makes the
  //ordering machine-independent, and it is why EncodedKey.IsFolded is set for every string:
  //the index narrows the search and the record settles it.
  public static string Normalize(string value) {
    var decomposed = value.Normalize(NormalizationForm.FormD);
    var builder = new StringBuilder(decomposed.Length);
    foreach (var character in decomposed) {
      if (CharUnicodeInfo.GetUnicodeCategory(character) != UnicodeCategory.NonSpacingMark) {
        builder.Append(character);
      }
    }
    return builder.ToString().Normalize(NormalizationForm.FormC).ToUpperInvariant();
  }
}
