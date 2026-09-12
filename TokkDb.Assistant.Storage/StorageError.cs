namespace TokkDb.Assistant.Storage;

/// <summary>
/// One reason a write was refused.
///
/// These are types rather than codes and messages, because two things read them. The
/// conversation has to turn a refusal into a sentence a person understands - "that column keeps
/// dates, and July the somethingth is not one" - and it can only do that if it can see what
/// kind of refusal it was and what the pieces were. A test has to assert that the right thing
/// was refused for the right reason, and a string comparison is a poor way to do it.
///
/// Every error names its collection and its column, because SC-3 and SC-4 both require that the
/// column be named, and because a refusal that does not say where it happened is not actionable.
/// </summary>
public abstract record StorageError(string CollectionName, string ColumnName)
{
    /// <summary>A plain sentence, for a log or a test failure. The user-facing wording is the application's.</summary>
    public abstract string Describe();

    // Sealed on purpose. Without it every derived record synthesises its own ToString and the
    // sentence Describe() wrote would never be the one a log or a test failure showed.
    public sealed override string ToString() => Describe();
}

/// <summary>
/// SC-3. The value offered is not a value of the column's type, and no conversion that cannot
/// lose anything would make it one.
/// </summary>
public sealed record ColumnTypeMismatch(
    string CollectionName,
    string ColumnName,
    ColumnType Expected,
    object? Value) : StorageError(CollectionName, ColumnName)
{
    /// <summary>What the offered value turned out to be, in words.</summary>
    public string ActualDescription => ColumnTypes.Describe(Value);

    public override string Describe() =>
        Value is null
            // Reached from a query rather than a write: a write may leave a column out, but a
            // condition compared against nothing is not a comparison, and there is an operator
            // that asks the question it was probably trying to ask.
            ? $"Column '{ColumnName}' of '{CollectionName}' keeps {Expected}, and was given nothing to " +
              $"compare against. Ask {nameof(QueryOperator.IsNothing)} instead."
            : $"Column '{ColumnName}' of '{CollectionName}' keeps {Expected}, and " +
              $"{ColumnTypes.Render(Value)} is {ActualDescription}.";
}

/// <summary>
/// SC-4. A unique column already holds this value. The error names the column and the record
/// that holds it, so that the application can show the user what they already have rather than
/// only telling them that they cannot add it.
/// </summary>
public sealed record DuplicateValue(
    string CollectionName,
    string ColumnName,
    object? Value,
    Ulid HeldBy) : StorageError(CollectionName, ColumnName)
{
    public override string Describe() =>
        $"Column '{ColumnName}' of '{CollectionName}' has to be unique, and " +
        $"{ColumnTypes.Render(Value)} is already held by record {HeldBy}.";
}

/// <summary>The write named a column the collection does not have.</summary>
public sealed record UnknownColumn(string CollectionName, string ColumnName)
    : StorageError(CollectionName, ColumnName)
{
    public override string Describe() => $"'{CollectionName}' has no column called '{ColumnName}'.";
}

/// <summary>The column is required, the write left it out, and the column has no default.</summary>
public sealed record RequiredValueMissing(string CollectionName, string ColumnName)
    : StorageError(CollectionName, ColumnName)
{
    public override string Describe() => $"Column '{ColumnName}' of '{CollectionName}' has to have a value.";
}

/// <summary>
/// An update changed a column that is written once. The old value is carried so that the
/// application can say what it would have overwritten.
/// </summary>
public sealed record ReadOnlyColumnChanged(
    string CollectionName,
    string ColumnName,
    object? WasValue,
    object? OfferedValue) : StorageError(CollectionName, ColumnName)
{
    public override string Describe() =>
        $"Column '{ColumnName}' of '{CollectionName}' is written once. It holds " +
        $"{ColumnTypes.Render(WasValue)} and cannot be changed to {ColumnTypes.Render(OfferedValue)}.";
}

/// <summary>
/// SC-7. The operator does not suit the column's type: a range asked of a true-or-false, or
/// "starts with" asked of a date.
///
/// A query error rather than a write error, and the same <see cref="StorageError"/> all the
/// same, because what a caller has to do about it is identical - a model wrote something that
/// does not fit the schema, and the reply it needs names the column and says what would fit.
/// </summary>
public sealed record OperatorNotSuitable(
    string CollectionName,
    string ColumnName,
    QueryOperator Operator,
    ColumnType ColumnType) : StorageError(CollectionName, ColumnName)
{
    public override string Describe() =>
        $"Column '{ColumnName}' of '{CollectionName}' keeps {ColumnType}, and {Operator} is not something " +
        $"that can be asked of it.";
}

/// <summary>
/// The operator was given the wrong number of values: none for a comparison, several where one
/// belongs, or one for a question that takes none.
/// </summary>
public sealed record OperandCountWrong(
    string CollectionName,
    string ColumnName,
    QueryOperator Operator,
    int Wanted,
    int Offered) : StorageError(CollectionName, ColumnName)
{
    public override string Describe()
    {
        var wanted = Wanted switch
        {
            0 => "no value",
            1 => "one value",
            _ => $"at least {Wanted} values"
        };

        return $"{Operator} on '{ColumnName}' of '{CollectionName}' takes {wanted}, and was given {Offered}.";
    }
}

/// <summary>A query asked for an order by a column the collection does not have.</summary>
public sealed record UnknownSortColumn(string CollectionName, string ColumnName)
    : StorageError(CollectionName, ColumnName)
{
    public override string Describe() =>
        $"'{CollectionName}' cannot be ordered by '{ColumnName}', which is not one of its columns.";
}
