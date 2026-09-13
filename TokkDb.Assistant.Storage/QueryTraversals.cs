namespace TokkDb.Assistant.Storage;

/// <summary>
/// Turning "the expenses for a conference in Lviv" into a condition on the expenses.
///
/// SC-7's internal model supports relation traversal, and this is how it is served without the
/// engine growing a join it does not have: the far side is asked first, its answers become the
/// values of an <see cref="QueryOperator.In"/> on the near column, and the near query is then an
/// ordinary query that an index can answer. Two reads instead of one, and both of them narrow.
///
/// Shared between the implementations for the reason everything in this assembly is shared: a
/// traversal that meant one thing in memory and another on the engine would be a disagreement
/// the contract suite could not see, because each would be self-consistent.
///
/// <b>The far query is projected to the one column that matters.</b> Reading whole records to
/// take one field from each is what the browser's table does and what this must not: the far
/// side can be ten thousand records, and only its key is wanted.
/// </summary>
internal static class QueryTraversals
{
    /// <summary>
    /// Resolves every traversal of <paramref name="query"/> into conditions on its own columns.
    ///
    /// Returns false when a traversal matched nothing at all, which is not an error and not an
    /// empty condition: no conference in Lviv means no expenses for one, and the caller answers
    /// with no records rather than with every record. An <see cref="QueryOperator.In"/> of no
    /// values would be refused by validation, and rightly - it is a caller mistake everywhere
    /// except here.
    /// </summary>
    public static bool TryResolve(
        StorageQuery query,
        Func<string, RelationDefinition?> relations,
        Func<StorageQuery, StorageQueryResult> run,
        out StorageQuery resolved)
    {
        resolved = query;

        if (query.Traversals.Count == 0) return true;

        var conditions = new List<QueryCondition>(query.Traversals.Count);

        foreach (var traversal in query.Traversals)
        {
            var relation = relations(traversal.RelationName)
                ?? throw new UnknownRelationException(traversal.RelationName);

            if (!StorageNames.Same(relation.FromCollection, query.CollectionName))
            {
                throw new InvalidDefinitionException(
                    "traversal",
                    $"'{relation.Name}' is a relation of '{relation.FromCollection}', and this query reads " +
                    $"'{query.CollectionName}'.");
            }

            if (!StorageNames.Same(traversal.Target.CollectionName, relation.ToCollection))
            {
                throw new InvalidDefinitionException(
                    "traversal",
                    $"'{relation.Name}' refers to '{relation.ToCollection}', and the other side of this " +
                    $"traversal reads '{traversal.Target.CollectionName}'.");
            }

            var far = run(traversal.Target.Selecting(relation.ToColumn).Continuing(null));

            var values = far.Records
                .Select(record => record[relation.ToColumn])
                .Where(static value => value is not null)
                .Distinct()
                .ToList();

            if (values.Count == 0) return false;

            conditions.Add(new QueryCondition(relation.FromColumn, QueryOperator.In, values));
        }

        resolved = query.Resolved(conditions);
        return true;
    }
}
