namespace TokkDb.Assistant.Storage;

/// <summary>
/// What happens to the records that refer to a thing when the thing they refer to goes away.
///
/// SC-8: a relation has to state this when it is created, rather than have it discovered at the
/// first delete. The four are the whole set, and there is no fifth called "whatever the engine
/// does", because the engine does not do any of them - it refuses a write whose reference has no
/// target and says nothing about a delete on the other side. That gap is what SC-8 was written
/// for, and this is the part the assistant supplies.
/// </summary>
public enum RelationIntegrity
{
    /// <summary>
    /// The delete is refused while anything refers to the record, and the refusal names what
    /// does. <b>The default</b>, because it is the only one of the four that loses nothing and
    /// the only one a person can be asked about afterwards.
    /// </summary>
    Restrict = 1,

    /// <summary>
    /// The records that refer to it go with it. Never a default and never silent (SC-8a): the
    /// count of what would go is evidence on a confirmation card, and each deletion is recorded
    /// separately so that undoing the request can put every one of them back.
    /// </summary>
    Cascade,

    /// <summary>
    /// The records that refer to it keep existing and stop referring to anything. Refused at
    /// creation on a required column, because a column that has to have a value and a rule that
    /// empties it cannot both hold, and the delete is the wrong moment to find that out.
    /// </summary>
    SetEmpty,

    /// <summary>
    /// The delete is refused until the caller says what the referring records should point at
    /// instead. Restrict with a way forward, for the case where the record is being replaced
    /// rather than removed.
    /// </summary>
    RequireReplacement
}

/// <summary>
/// One thing referring to another: a column of one collection holds a value that some record of
/// another collection carries in a column of its own.
///
/// SC-8. Relations were being traversed by the browser, classified when they were added, and
/// relied on by compensation - which restores a deleted record under its original identity so
/// that relations survive - before anything in the plan created one or said what a delete does
/// to them. This is that, in the same logical vocabulary the rest of the contract uses: two
/// collections, two columns, and what happens when the target goes.
///
/// <b>The direction is the direction of the reference.</b> <see cref="FromCollection"/> holds the
/// value and <see cref="ToCollection"/> is what it refers to - expenses refer to a conference,
/// so the relation is from expenses to conferences. The integrity action is about deleting the
/// conference, which is the end that has no column saying it is referred to.
///
/// Nothing here says an index exists. One does, underneath, because a reference cannot be
/// checked without one, and that is the storage's business exactly as SC-2 says.
/// </summary>
public sealed record RelationDefinition
{
    /// <param name="name">
    /// What this relation is called. Names are trimmed and compared as <see cref="StorageNames"/>
    /// describes, and two relations cannot share one.
    /// </param>
    /// <param name="fromCollection">The collection whose records refer.</param>
    /// <param name="fromColumn">The column holding the reference.</param>
    /// <param name="toCollection">The collection referred to.</param>
    /// <param name="toColumn">
    /// The column of the target that the reference matches. It has to be unique there - a
    /// reference that matches three records refers to nothing in particular - and
    /// <see cref="IStorage.AddRelation"/> refuses one that is not.
    /// </param>
    /// <param name="integrity">What a delete of the target does. Restrict unless said otherwise.</param>
    /// <param name="purpose">
    /// What the relation means, in the user's words: "the conference this expense was for". BR-7
    /// shows the related records under this rather than under the columns that implement them.
    /// </param>
    public RelationDefinition(
        string name,
        string fromCollection,
        string fromColumn,
        string toCollection,
        string toColumn,
        RelationIntegrity integrity = RelationIntegrity.Restrict,
        string? purpose = null)
    {
        Name = StorageNames.Normalise(name, "relation name");
        FromCollection = StorageNames.Normalise(fromCollection, "collection name");
        FromColumn = StorageNames.Normalise(fromColumn, "column name");
        ToCollection = StorageNames.Normalise(toCollection, "collection name");
        ToColumn = StorageNames.Normalise(toColumn, "column name");

        if (!Enum.IsDefined(integrity))
        {
            throw new InvalidDefinitionException(
                "integrity action",
                $"Relation '{Name}' was given no usable answer to what a delete does.");
        }

        Integrity = integrity;
        Purpose = ColumnDefinition.Sentence(purpose);
    }

    public string Name { get; }

    public string FromCollection { get; }

    public string FromColumn { get; }

    public string ToCollection { get; }

    public string ToColumn { get; }

    public RelationIntegrity Integrity { get; }

    /// <summary>What this relation means, in the user's words, or null.</summary>
    public string? Purpose { get; }

    public override string ToString() =>
        $"{FromCollection}.{FromColumn} -> {ToCollection}.{ToColumn} ({Integrity})";
}
