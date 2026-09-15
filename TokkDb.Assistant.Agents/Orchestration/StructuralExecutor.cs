using TokkDb.Assistant.Agents.Changes;
using TokkDb.Assistant.Agents.Context;
using TokkDb.Assistant.Storage;
using TokkDb.Assistant.Trace;

namespace TokkDb.Assistant.Agents.Orchestration;

/// <summary>
/// Applies a classified change and journals it (SC-6, TR-2b, AG-11e): called inside the unit of
/// work of the request, so that the change and its record commit together (TR-4). The journal
/// keeps the inverse a structural change needs - and for one it cannot keep whole, the change
/// was classified irreversible before it ran, and the card said so.
/// </summary>
internal static class StructuralExecutor
{
    public static string Apply(TurnContext ctx, ClassifiedChange classified, Ulid step)
    {
        var storage = ctx.Storage;
        var request = ctx.Request.Id;
        var journal = ctx.Journal;

        switch (classified.Action)
        {
            case CreateThing create:
                storage.CreateCollection(create.Definition);
                journal.Structural(request, ChangeKind.CollectionAdded, create.Definition.Name,
                    [.. create.Definition.Columns.Select(column => FieldChange.Added(column.Name, JournalValue.Of(SchemaDigest.Kind(column.Type), PayloadLimits.Default)))],
                    Reversibility.Reversible, step);
                break;

            case AddField add:
                storage.AddColumn(add.Thing, add.Column);
                journal.Structural(request, ChangeKind.FieldAdded, add.Thing,
                    [FieldChange.Added(add.Column.Name, JournalValue.Of(SchemaDigest.Kind(add.Column.Type), PayloadLimits.Default))],
                    Reversibility.Reversible, step);
                break;

            case MakeUnique unique:
                storage.SetUnique(unique.Thing, unique.Field, unique.Unique);
                journal.Structural(request, ChangeKind.FieldChanged, unique.Thing,
                    [new FieldChange(unique.Field, JournalValue.Of(unique.Unique ? "may repeat" : "no two the same", PayloadLimits.Default), JournalValue.Of(unique.Unique ? "no two the same" : "may repeat", PayloadLimits.Default))],
                    Reversibility.Reversible, step);
                break;

            case MakeRequired:
                throw new NotSupportedException("Making a field always needed, or not, is not something that can be changed yet.");

            case AddRelation relation:
                storage.AddRelation(relation.Relation);
                journal.Structural(request, ChangeKind.RelationChanged, relation.Relation.FromCollection,
                    [FieldChange.Added(relation.Relation.Name, JournalValue.Of(relation.Relation.ToString(), PayloadLimits.Default))],
                    Reversibility.Reversible, step);
                break;

            case RemoveField remove:
            {
                var removed = journal.RemovedValues(remove.Thing, remove.Field);
                storage.RemoveColumn(remove.Thing, remove.Field);
                journal.Structural(request, ChangeKind.FieldRemoved, remove.Thing, removed,
                    removed.Count == 0 ? Reversibility.Reversible : Reversibility.ReversibleWithConditions, step);
                break;
            }

            case RenameField rename:
                storage.RenameColumn(rename.Thing, rename.Field, rename.NewName);
                journal.Structural(request, ChangeKind.FieldChanged, rename.Thing,
                    [new FieldChange("renamed", JournalValue.Of(rename.Field, PayloadLimits.Default), JournalValue.Of(rename.NewName, PayloadLimits.Default))],
                    Reversibility.Reversible, step);
                break;

            case RetypeField retype:
            {
                var before = storage.GetCollectionDefinition(retype.Thing)?.Column(retype.Field)?.Type ?? ColumnType.Text;
                storage.RetypeColumn(retype.Thing, retype.Field, retype.NewType);
                journal.Structural(request, ChangeKind.FieldChanged, retype.Thing,
                    [new FieldChange(retype.Field, JournalValue.Of(SchemaDigest.Kind(before), PayloadLimits.Default), JournalValue.Of(SchemaDigest.Kind(retype.NewType), PayloadLimits.Default))],
                    classified.Reversibility, step);
                break;
            }

            case RemoveThing removeThing:
            {
                var definition = storage.GetCollectionDefinition(removeThing.Thing) ?? throw new UnknownCollectionException(removeThing.Thing);
                storage.DeleteCollection(removeThing.Thing);
                journal.Structural(request, ChangeKind.CollectionRemoved, removeThing.Thing,
                    [.. definition.Columns.Select(column => FieldChange.Removed(column.Name, JournalValue.Of(SchemaDigest.Kind(column.Type), PayloadLimits.Default)))],
                    Reversibility.NotReversible, step);
                break;
            }

            case DeleteRecords delete:
                foreach (var id in delete.Ids)
                {
                    var effect = storage.InspectDelete(delete.Thing, id);
                    if (!effect.RecordExists) continue;

                    var heads = effect.WouldAlsoBeRemoved.ToDictionary(static reference => reference, reference => storage.HeadVersion(reference.CollectionName, reference.Id)!.Value);
                    var head = storage.HeadVersion(delete.Thing, id)!.Value;

                    var result = storage.Delete(delete.Thing, id);

                    // Each cascaded deletion is its own change (SC-8a), recorded before the record they went with.
                    foreach (var removed in result.AlsoRemoved)
                    {
                        journal.Deleted(request, removed.CollectionName, removed.Id, heads[removed], step);
                    }

                    journal.Deleted(request, delete.Thing, id, head, step);
                }

                break;

            case SetDisplayRule rule:
            {
                var before = storage.GetCollectionDefinition(rule.Thing)?.DisplayRule?.Template;
                storage.SetDisplayRule(rule.Thing, rule.Rule);
                journal.Structural(request, ChangeKind.FieldChanged, rule.Thing,
                    [new FieldChange("display rule", JournalValue.Of(before, PayloadLimits.Default), JournalValue.Of(rule.Rule?.Template, PayloadLimits.Default))],
                    Reversibility.Reversible, step);
                break;
            }

            case EraseRecords erase:
                foreach (var id in erase.Ids)
                {
                    // The journal keeps the identifier and nothing of the value (TR-2b): the change
                    // is recorded first, then the record and its history go, then the payloads of
                    // every request that ever named it - all in the one transaction.
                    if (storage.GetById(erase.Thing, id) is null) continue;
                    var head = storage.HeadVersion(erase.Thing, id) ?? Ulid.Empty;
                    journal.Deleted(request, erase.Thing, id, head, step);
                    storage.Erase(erase.Thing, id);
                    ctx.Recorder.ClearPayloadsNaming(id);
                }

                break;

            case ChangeRecord change:
            {
                var current = storage.GetById(change.Thing, change.Id);
                if (current is null) break;

                var previous = storage.HeadVersion(change.Thing, change.Id)!.Value;
                storage.Update(current.With(change.Field, change.Value));
                journal.Updated(request, change.Thing, change.Id, previous, step, "changed from the browser");
                break;
            }

            case StoreRecords:
                break;

            default:
                throw new NotSupportedException($"Nothing applies a {classified.Action.GetType().Name}.");
        }

        return classified.Description;
    }
}
