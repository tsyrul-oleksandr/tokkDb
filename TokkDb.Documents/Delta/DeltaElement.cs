namespace TokkDb.Documents.Delta;

//DL-1 and V-2. One change: (path, operation, oldValue, newValue). Both values are kept, so a delta
//can be audited without reconstructing anything and inverted by swapping them (DL-4).
//
//Which sides are present follows from the operation: Add and Insert have only a new value, Remove
//and RemoveAt only an old one, Replace both, and Move the same value on both sides. A Move also
//carries MoveTo, the position the element takes in the array once it has been taken out of the
//position its path names; V-2's four-tuple has no room for a second index, and a Move without it
//would not say where the element went.
public sealed class DeltaElement {
  private DeltaElement(DeltaPath path, DeltaOperation operation, DeltaValue oldValue, DeltaValue newValue,
      int moveTo) {
    Path = path;
    Operation = operation;
    OldValue = oldValue;
    NewValue = newValue;
    MoveTo = moveTo;
  }

  public DeltaPath Path { get; }
  public DeltaOperation Operation { get; }
  public DeltaValue OldValue { get; }
  public DeltaValue NewValue { get; }

  //Move only: the index the element is inserted at, in the array as it is after the element has
  //been removed from Path's index. -1 for every other operation.
  public int MoveTo { get; }

  public static DeltaElement Add(DeltaPath path, IDocumentValue value) {
    RequireField(path, DeltaOperation.Add);
    return new DeltaElement(path, DeltaOperation.Add, DeltaValue.Absent, DeltaValue.Of(value), -1);
  }

  public static DeltaElement Remove(DeltaPath path, IDocumentValue oldValue) {
    RequireField(path, DeltaOperation.Remove);
    return new DeltaElement(path, DeltaOperation.Remove, DeltaValue.Of(oldValue), DeltaValue.Absent, -1);
  }

  //The path may be the root: a document replaced whole is a Replace with no segments.
  public static DeltaElement Replace(DeltaPath path, IDocumentValue oldValue, IDocumentValue newValue) {
    ArgumentNullException.ThrowIfNull(path);
    return new DeltaElement(path, DeltaOperation.Replace, DeltaValue.Of(oldValue), DeltaValue.Of(newValue), -1);
  }

  public static DeltaElement Insert(DeltaPath path, IDocumentValue value) {
    RequireIndex(path, DeltaOperation.Insert);
    return new DeltaElement(path, DeltaOperation.Insert, DeltaValue.Absent, DeltaValue.Of(value), -1);
  }

  public static DeltaElement RemoveAt(DeltaPath path, IDocumentValue oldValue) {
    RequireIndex(path, DeltaOperation.RemoveAt);
    return new DeltaElement(path, DeltaOperation.RemoveAt, DeltaValue.Of(oldValue), DeltaValue.Absent, -1);
  }

  public static DeltaElement Move(DeltaPath path, IDocumentValue value, int moveTo) {
    RequireIndex(path, DeltaOperation.Move);
    ArgumentOutOfRangeException.ThrowIfNegative(moveTo);
    var moved = DeltaValue.Of(value);
    return new DeltaElement(path, DeltaOperation.Move, moved, moved, moveTo);
  }

  //Assembles an element read back from storage, checking the same shape rules as the factories.
  public static DeltaElement Of(DeltaPath path, DeltaOperation operation, DeltaValue oldValue, DeltaValue newValue,
      int moveTo = -1) {
    return operation switch {
      DeltaOperation.Add => Add(path, Require(newValue, operation, "new")),
      DeltaOperation.Remove => Remove(path, Require(oldValue, operation, "old")),
      DeltaOperation.Replace => Replace(path, Require(oldValue, operation, "old"), Require(newValue, operation, "new")),
      DeltaOperation.Insert => Insert(path, Require(newValue, operation, "new")),
      DeltaOperation.RemoveAt => RemoveAt(path, Require(oldValue, operation, "old")),
      DeltaOperation.Move => Move(path, Require(oldValue, operation, "old"), moveTo),
      _ => throw new ArgumentOutOfRangeException(nameof(operation), operation, "Not a delta operation.")
    };
  }

  //DL-4: the element that undoes this one. Add and Remove swap, Insert and RemoveAt swap, a
  //Replace swaps its values, and a Move goes back from where it went to where it came from: after
  //a forward Move the element sits at MoveTo, and taking it out of there leaves exactly the array
  //the forward Move saw once it had taken the element out of Path's index, so Path's index is
  //where it goes back in.
  public DeltaElement Invert() {
    return Operation switch {
      DeltaOperation.Add => Remove(Path, NewValue.Value),
      DeltaOperation.Remove => Add(Path, OldValue.Value),
      DeltaOperation.Replace => Replace(Path, NewValue.Value, OldValue.Value),
      DeltaOperation.Insert => RemoveAt(Path, NewValue.Value),
      DeltaOperation.RemoveAt => Insert(Path, OldValue.Value),
      DeltaOperation.Move => Move(Path.Parent.At(MoveTo), OldValue.Value, Path.Last.Index),
      _ => throw new InvalidOperationException($"{Operation} cannot be inverted.")
    };
  }

  public override string ToString() {
    return Operation switch {
      DeltaOperation.Add => $"Add {Path} = {NewValue}",
      DeltaOperation.Remove => $"Remove {Path} (was {OldValue})",
      DeltaOperation.Replace => $"Replace {Path}: {OldValue} -> {NewValue}",
      DeltaOperation.Insert => $"Insert {Path} = {NewValue}",
      DeltaOperation.RemoveAt => $"RemoveAt {Path} (was {OldValue})",
      _ => $"Move {Path} -> [{MoveTo}]"
    };
  }

  private static IDocumentValue Require(DeltaValue value, DeltaOperation operation, string side) {
    return value.IsPresent
      ? value.Value
      : throw new ArgumentException($"A {operation} element needs a {side} value.");
  }

  private static void RequireField(DeltaPath path, DeltaOperation operation) {
    ArgumentNullException.ThrowIfNull(path);
    if (path.IsRoot || !path.Last.IsField) {
      throw new ArgumentException($"A {operation} element applies to a field, and {path} does not name one.");
    }
  }

  private static void RequireIndex(DeltaPath path, DeltaOperation operation) {
    ArgumentNullException.ThrowIfNull(path);
    if (path.IsRoot || !path.Last.IsIndex) {
      throw new ArgumentException($"A {operation} element applies to an array position, and {path} does not name one.");
    }
  }
}
