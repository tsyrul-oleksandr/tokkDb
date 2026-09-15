namespace TokkDb.Assistant.Storage;

/// <summary>
/// The base of everything this contract throws on purpose.
///
/// The distinction worth keeping is between a caller that asked for something impossible and a
/// storage that broke. Everything below is the first kind: it is a normal outcome of a request
/// that a model composed, and the application is expected to catch it and say something. A
/// caller can catch this one type to mean "the request did not fit", and nothing that escapes
/// this hierarchy means that.
/// </summary>
public abstract class StorageException : Exception
{
    protected StorageException(string message) : base(message)
    {
    }

    protected StorageException(string message, Exception inner) : base(message, inner)
    {
    }
}

/// <summary>
/// A definition could not be built: a name that is not a name, two columns with one name, a
/// default value of the wrong type, a display rule that does not parse.
///
/// This is thrown from the definitions' own constructors rather than reported later, so that a
/// <see cref="CollectionDefinition"/> that exists is a definition that makes sense on its own
/// terms. What it cannot check alone - that its display rule names columns that exist - is
/// checked by <see cref="IStorage.CreateCollection"/>, which can see the columns.
///
/// It is not an <see cref="ArgumentException"/> on purpose. Most of these definitions are built
/// from what a model proposed, so a rejected definition is an expected branch of the ingestion
/// path and not a programming mistake, and the application has to be able to catch it without
/// also catching its own bugs.
/// </summary>
public sealed class InvalidDefinitionException : StorageException
{
    public InvalidDefinitionException(string member, string message) : base(message)
    {
        Member = member;
    }

    /// <summary>What was wrong: "collection name", "column name", "default value", "display rule".</summary>
    public string Member { get; }
}

/// <summary>There is no collection by that name.</summary>
public sealed class UnknownCollectionException : StorageException
{
    public UnknownCollectionException(string collectionName)
        : base($"There is no collection called '{collectionName}'.")
    {
        CollectionName = collectionName;
    }

    public string CollectionName { get; }
}

/// <summary>There is already a collection by that name. Names are compared as <see cref="StorageNames"/> describes.</summary>
public sealed class CollectionAlreadyExistsException : StorageException
{
    public CollectionAlreadyExistsException(string collectionName)
        : base($"There is already a collection called '{collectionName}'.")
    {
        CollectionName = collectionName;
    }

    public string CollectionName { get; }
}

/// <summary>There is no column by that name on that collection.</summary>
public sealed class UnknownColumnException : StorageException
{
    public UnknownColumnException(string collectionName, string columnName)
        : base($"'{collectionName}' has no column called '{columnName}'.")
    {
        CollectionName = collectionName;
        ColumnName = columnName;
    }

    public string CollectionName { get; }

    public string ColumnName { get; }
}

/// <summary>There is already a column by that name on that collection.</summary>
public sealed class ColumnAlreadyExistsException : StorageException
{
    public ColumnAlreadyExistsException(string collectionName, string columnName)
        : base($"'{collectionName}' already has a column called '{columnName}'.")
    {
        CollectionName = collectionName;
        ColumnName = columnName;
    }

    public string CollectionName { get; }

    public string ColumnName { get; }
}

/// <summary>
/// A write was refused. Every reason is reported, not only the first, because a model that got
/// one column wrong usually got two, and asking it again once is cheaper than asking it twice.
/// </summary>
public sealed class StorageValidationException : StorageException
{
    public StorageValidationException(IReadOnlyList<StorageError> errors)
        : base(Summarise(errors))
    {
        Errors = errors;
    }

    public IReadOnlyList<StorageError> Errors { get; }

    private static string Summarise(IReadOnlyList<StorageError> errors) =>
        errors.Count switch
        {
            0 => "The write was refused.",
            1 => errors[0].Describe(),
            _ => $"The write was refused for {errors.Count} reasons: " +
                 string.Join(" ", errors.Select(static error => error.Describe()))
        };
}

/// <summary>There is no relation by that name.</summary>
public sealed class UnknownRelationException : StorageException
{
    public UnknownRelationException(string relationName)
        : base($"There is no relation called '{relationName}'.")
    {
        RelationName = relationName;
    }

    public string RelationName { get; }
}

/// <summary>There is already a relation by that name.</summary>
public sealed class RelationAlreadyExistsException : StorageException
{
    public RelationAlreadyExistsException(string relationName)
        : base($"There is already a relation called '{relationName}'.")
    {
        RelationName = relationName;
    }

    public string RelationName { get; }
}

/// <summary>
/// A delete was refused because other records refer to the record being deleted, under a
/// relation whose integrity action says to refuse (SC-8).
///
/// It carries the referring records rather than a count, because the answer the user wants is
/// "these four expenses are for that conference" and an application that has only a number
/// cannot show them. <see cref="IStorage.InspectDelete"/> answers the same question without
/// attempting the delete, which is what the confirmation card is built from.
/// </summary>
public sealed class IntegrityRefusedException : StorageException
{
    public IntegrityRefusedException(
        RelationDefinition relation,
        RecordReference target,
        IReadOnlyList<RecordReference> referring)
        : base(Summarise(relation, referring))
    {
        Relation = relation;
        Target = target;
        Referring = referring;
    }

    public RelationDefinition Relation { get; }

    /// <summary>The record that was to be deleted.</summary>
    public RecordReference Target { get; }

    /// <summary>The records that refer to it.</summary>
    public IReadOnlyList<RecordReference> Referring { get; }

    private static string Summarise(RelationDefinition relation, IReadOnlyList<RecordReference> referring)
    {
        var what = relation.Purpose is { } purpose ? purpose : relation.Name;

        return referring.Count == 1
            ? $"One record of '{relation.FromCollection}' still refers to this one ({what}): " +
              $"{referring[0].Id}."
            : $"{referring.Count} records of '{relation.FromCollection}' still refer to this one ({what}): " +
              string.Join(", ", referring.Take(5).Select(static reference => reference.Id)) +
              (referring.Count > 5 ? ", and others." : ".");
    }
}

/// <summary>There is no conversation with that identity (SC-10).</summary>
public sealed class UnknownConversationException : StorageException
{
    public UnknownConversationException(Ulid id)
        : base($"There is no conversation with the identity {id}.")
    {
        Id = id;
    }

    public Ulid Id { get; }
}

/// <summary>
/// The storage is open for writing somewhere else (AG-8a).
///
/// D-11's one file means one writer, and this is the single-writer lock at the moment it bites:
/// a second instance of the application, or a second copy of it, opening the same database. It
/// is refused here rather than allowed to corrupt the first, and the message is the application's
/// to phrase - "it is already open" - because the person who sees it opened the same thing twice.
/// </summary>
public sealed class StorageInUseException : StorageException
{
    public StorageInUseException(string location, Exception inner)
        : base($"The storage at '{location}' is already open for writing somewhere else.", inner)
    {
        Location = location;
    }

    public string Location { get; }
}
