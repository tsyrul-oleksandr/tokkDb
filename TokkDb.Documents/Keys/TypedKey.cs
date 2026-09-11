using TokkDb.Documents.Values;
using TokkDb.Values;

namespace TokkDb.Documents.Keys;

//A stored value compared as the type its column declares.
//
//Every type ValueTypeEnum declares now has an IDocumentValue behind it, so a value is
//normally already the type its column says and this encodes it as it stands. What it is still
//for is the two cases where a record and its column disagree: a record written before the
//column was retyped, whose value is whatever the column used to mean, and a record written
//before Long, Decimal, DateTime and Guid had a document value of their own, whose value is
//the invariant text they used to be stored as. Both are read as the column reads them now.
//
//The encoding is D-3's, which means a comparison here and a range over an index are the same
//order by construction rather than by agreement.
public static class TypedKey {
  //Null when the value cannot be read as that type at all: a document value of the wrong
  //shape, or text that does not parse. A predicate over such a value is simply not satisfied,
  //which is what makes a wrong-typed record invisible to a query rather than fatal to it.
  public static EncodedKey? Encode(ValueTypeEnum type, IDocumentValue value) {
    if (value is null or NullDocumentValue) {
      return KeyEncoder.EncodeNull();
    }
    var typed = ValueMigration.To(type, value);
    if (typed is NullDocumentValue) {
      return null;
    }
    try {
      return KeyEncoder.Encode(typed);
    } catch (NotSupportedException) {
      //An object or an array: no ordering, so no key.
      return null;
    }
  }
}
