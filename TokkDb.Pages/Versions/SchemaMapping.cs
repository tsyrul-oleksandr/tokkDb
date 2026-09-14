using TokkDb.Documents;
using TokkDb.Documents.Delta;
using TokkDb.Documents.Values;
using TokkDb.Values;

namespace TokkDb.Pages.Versions;

//V-10 and RH-6. Presents a version, or a delta, as the current schema describes it, following
//the rules of V-10's table for each schema step between the version and now, oldest step first:
//
//  a column added      nothing: the version lacks the field and reads as lacking it;
//  a rename            the first path segment takes the new name;
//  a lossless retype   converted — lossless means converting back gives the same stored bytes;
//  a lossy retype      not converted, reported as Unmapped(RetypeLossy);
//  a removal           reported as Unmapped(ColumnRemoved);
//  a removal, then a new column of the same name   the old value is Unmapped(ColumnRemoved),
//                                                  never presented as the new column's.
//
//A value that passes one step is carried to the next under the name and type that step gave
//it. The first step that cannot carry a value ends its journey, and the report names that
//step, the value as it was before it, and the original. Nothing is dropped without appearing
//in the report: a mapping that lost values silently would make history lie.
public static class SchemaMapping {
  public sealed record MappedDocument(ObjectDocument Document, IReadOnlyList<Unmapped> Unmapped);

  public sealed record MappedDelta(DocumentDelta Delta, IReadOnlyList<Unmapped> Unmapped);

  public static MappedDocument MapDocument(ObjectDocument document, IEnumerable<ColumnMigration> steps) {
    ArgumentNullException.ThrowIfNull(document);
    var fields = ((ObjectDocumentValue)document.Value).Values;
    //Each field with the name it has now, its value now, and its original value.
    var carried = fields.ToDictionary(field => field.Key, field => (Value: field.Value, Original: field.Value), StringComparer.Ordinal);
    var unmapped = new List<Unmapped>();
    foreach (var step in Ordered(steps)) {
      switch (step.Kind) {
        case ColumnMigrationKind.Rename:
          if (carried.Remove(step.ColumnName, out var renamed)) {
            //A value already under the new name belongs to a column renamed away before this
            //version was written, or added and dropped since; it is not this column's.
            if (carried.Remove(step.NewName, out var orphan)) {
              unmapped.Add(new Unmapped(UnmappedReason.ColumnRemoved, step.NewName, orphan.Value, orphan.Original, step.Version));
            }
            carried[step.NewName] = renamed;
          } else if (carried.Remove(step.NewName, out var stranded)) {
            unmapped.Add(new Unmapped(UnmappedReason.ColumnRemoved, step.NewName, stranded.Value, stranded.Original, step.Version));
          }
          break;
        case ColumnMigrationKind.Retype:
          if (carried.TryGetValue(step.ColumnName, out var retyped)) {
            if (TryConvertLosslessly(retyped.Value, step.NewType, out var converted)) {
              carried[step.ColumnName] = (converted, retyped.Original);
            } else {
              carried.Remove(step.ColumnName);
              unmapped.Add(new Unmapped(UnmappedReason.RetypeLossy, step.ColumnName, retyped.Value, retyped.Original, step.Version));
            }
          }
          break;
        default:
          if (carried.Remove(step.ColumnName, out var removed)) {
            unmapped.Add(new Unmapped(UnmappedReason.ColumnRemoved, step.ColumnName, removed.Value, removed.Original, step.Version));
          }
          break;
      }
    }
    var mapped = new ObjectDocument();
    mapped.SetIdentifierValue(document.IdentifierValue);
    mapped.SetValue(new ObjectDocumentValue(carried.ToDictionary(field => field.Key, field => field.Value.Value, StringComparer.Ordinal)));
    return new MappedDocument(mapped, unmapped);
  }

  //The same rules on a delta's elements, by the column each path begins with. Paths stay
  //positional, so the mapped delta still applies (V-2). An element inside a retyped column,
  //where the column has become a scalar, cannot be carried; one whose column was removed
  //cannot either. Each is reported with its values.
  public static MappedDelta MapDelta(DocumentDelta delta, IEnumerable<ColumnMigration> steps) {
    ArgumentNullException.ThrowIfNull(delta);
    var elements = new List<(DeltaElement Element, DeltaElement Original)>(delta.Elements.Select(element => (element, element)));
    var unmapped = new List<Unmapped>();
    foreach (var step in Ordered(steps)) {
      var kept = new List<(DeltaElement, DeltaElement)>();
      foreach (var (element, original) in elements) {
        if (element.Path.IsRoot || !element.Path.Segments[0].IsField) {
          kept.Add((element, original));
          continue;
        }
        var column = element.Path.Segments[0].Name!;
        switch (step.Kind) {
          case ColumnMigrationKind.Rename when column == step.ColumnName:
            kept.Add((WithColumn(element, step.NewName), original));
            break;
          case ColumnMigrationKind.Rename when column == step.NewName:
            unmapped.Add(Report(UnmappedReason.ColumnRemoved, column, element, original, step.Version));
            break;
          case ColumnMigrationKind.Retype when column == step.ColumnName && element.Path.Length == 1
            && TryRetype(element, step.NewType, out var converted):
            kept.Add((converted, original));
            break;
          case ColumnMigrationKind.Retype when column == step.ColumnName:
            unmapped.Add(Report(UnmappedReason.RetypeLossy, column, element, original, step.Version));
            break;
          case ColumnMigrationKind.Remove when column == step.ColumnName:
            unmapped.Add(Report(UnmappedReason.ColumnRemoved, column, element, original, step.Version));
            break;
          default:
            kept.Add((element, original));
            break;
        }
      }
      elements = kept;
    }
    return new MappedDelta(new DocumentDelta(elements.Select(pair => pair.Element), delta.Matchings), unmapped);
  }

  private static IEnumerable<ColumnMigration> Ordered(IEnumerable<ColumnMigration> steps) {
    return steps.OrderBy(step => step.Version);
  }

  //Converting to the new type and back gives the same stored bytes: nothing was lost.
  public static bool TryConvertLosslessly(IDocumentValue value, ValueTypeEnum type, out IDocumentValue converted) {
    converted = ValueMigration.To(type, value);
    if (value is NullDocumentValue) {
      return true;
    }
    if (converted is NullDocumentValue) {
      return false;
    }
    var back = ValueMigration.To(value.Type, converted);
    return CanonicalValue.Equal(back, value);
  }

  private static bool TryRetype(DeltaElement element, ValueTypeEnum type, out DeltaElement converted) {
    converted = null;
    IDocumentValue oldValue = null;
    IDocumentValue newValue = null;
    if (element.OldValue.IsPresent && !TryConvertLosslessly(element.OldValue.Value, type, out oldValue)) {
      return false;
    }
    if (element.NewValue.IsPresent && !TryConvertLosslessly(element.NewValue.Value, type, out newValue)) {
      return false;
    }
    converted = DeltaElement.Of(element.Path, element.Operation,
      oldValue is null ? DeltaValue.Absent : DeltaValue.Of(oldValue),
      newValue is null ? DeltaValue.Absent : DeltaValue.Of(newValue), element.MoveTo);
    return true;
  }

  private static DeltaElement WithColumn(DeltaElement element, string column) {
    var segments = element.Path.Segments.ToArray();
    segments[0] = DeltaSegment.Field(column);
    return DeltaElement.Of(DeltaPath.Of(segments), element.Operation, element.OldValue, element.NewValue, element.MoveTo);
  }

  private static Unmapped Report(UnmappedReason reason, string column, DeltaElement element, DeltaElement original,
      ushort version) {
    var value = element.NewValue.IsPresent ? element.NewValue.Value : element.OldValue.Value;
    var originalValue = original.NewValue.IsPresent ? original.NewValue.Value : original.OldValue.Value;
    return new Unmapped(reason, column, value, originalValue, version) { Element = original };
  }
}
