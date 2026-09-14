using TokkDb.Pages.Versions;

namespace TokkDb;

//RH-9 and V-11. The columns and relations of a versioned collection as declared at a moment of
//logical time, from the schema and relation nodes in memory: the last schema node at or before
//the moment, and every relation whose last node at or before the moment created it. Before the
//first schema node there is no recorded history to answer from. Shared by SchemaAsOf and by the
//related restore, which takes a record's relations as they stood at the moment (V-16).
internal static class SchemaSnapshots {
  public static SchemaSnapshot At(IVersionStore store, string collectionName, DateTimeOffset moment) {
    var schema = store.SchemaNodes(collectionName)
      .Where(node => node.LogicalTime <= moment)
      .MaxBy(node => node.Id);
    if (schema is null) {
      return new SchemaSnapshot { Moment = moment, BeforeRecordedHistory = true };
    }
    var relations = store.RelationNodes(collectionName)
      .Where(node => node.LogicalTime <= moment)
      .GroupBy(node => node.Relation.Name, StringComparer.Ordinal)
      .Select(group => group.MaxBy(node => node.Id)!)
      .Where(node => node.Kind == RelationNodeKind.Created)
      .Select(node => node.Relation)
      .ToList();
    return new SchemaSnapshot { Moment = moment, Schema = schema, Relations = relations };
  }
}
