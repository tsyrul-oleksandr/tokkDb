namespace TokkDb.Assistant.Storage;

/// <summary>
/// The assistant's storage.
///
/// SC-1 and D-1: this is the assistant's own contract, defined without reference to anything in
/// <c>TokkDb.LLM.*</c> and without reference to the engine. Two things implement it - an
/// in-memory one for tests (1.2) and one over <c>TokkDbConnection</c> (1.3) - and one suite of
/// contract tests runs against both, because the finding §2.2 of the engine plan recorded was
/// that two backends will drift apart on exactly the questions nobody wrote down.
///
/// <b>The questions SC-4 settles, and the answers.</b> These are stated here rather than left to
/// an implementation, so that the two implementations cannot differ on them and so that the
/// answer is readable without reading either.
///
/// <list type="number">
/// <item>
/// <b>Record identity is a <see cref="Ulid"/>, assigned by the storage, and it is not a
/// column.</b> <see cref="Create"/> returns the record it made, carrying the identity it gave
/// it; a caller cannot choose one. It is not a column because a column can be renamed, retyped
/// and removed (SC-6), and an identity that could be removed is not an identity - and because
/// the mapping step, which proposes columns, must not be able to propose that one. See
/// <see cref="StorageRecord.Id"/> for why a Ulid rather than a number.
/// </item>
///
/// <item>
/// <b>Field validation happens on write.</b> A value that is not a value of its column's type is
/// refused by <see cref="Create"/> and <see cref="Update"/> with a
/// <see cref="StorageValidationException"/> carrying a <see cref="ColumnTypeMismatch"/> that
/// names the column. Nothing is coerced except by conversions that cannot lose anything and
/// cannot fail; nothing is interpreted on the way out. The consequence, which is the point of
/// SC-3, is that no write can make a record unreadable: if it got in, it comes back as what it
/// was.
/// </item>
///
/// <item>
/// <b>A duplicate in a unique column is refused, and the refusal names the column and the
/// record that already holds the value.</b> A <see cref="DuplicateValue"/> carries both. Naming
/// only the column would leave the application able to say "you already have that" and unable
/// to show it, which is the answer the user actually wants.
/// </item>
///
/// <item>
/// <b>Names are trimmed when they are created and compared ordinally</b> - collection names and
/// column names alike, by the one rule in <see cref="StorageNames"/>. Two spellings are two
/// names. Deciding that a model meant an existing name is the mapping step's job, not the
/// storage's: it has the schema in front of it and it can ask the user, and a storage that
/// guesses cannot be asked to stop.
/// </item>
///
/// <item>
/// <b><see cref="GetAll"/> promises no order.</b> Whatever order it happens to return today is
/// not part of this contract, and a caller that wants an order sorts. Promising insertion order
/// would bind every future implementation to whatever the first one's storage layout happened to
/// do, and the promise would be broken by the first index that made a query faster. What replaces
/// the promise is <see cref="StorageRecord.Id"/>: Ulids sort by creation time, so "oldest first"
/// is available to any caller who wants it, and is asked for rather than assumed.
/// </item>
/// </list>
///
/// <b>Four more questions SC-4 did not name, settled here for the same reason.</b> Each is one
/// the two implementations would otherwise have answered separately, and the contract suite
/// would then have been testing two contracts.
///
/// <list type="bullet">
/// <item>
/// <b>A column left out and a column given nothing are different things.</b> Left out means the
/// record has no value there; given nothing means it has one and it is nothing. So a required
/// column cannot be given nothing, and the default does not step in when a caller explicitly
/// said nothing - they said nothing, not "whatever you like".
/// </item>
/// <item>
/// <b>A default is what a record starts with, not what it returns to.</b> Defaults apply on
/// <see cref="Create"/> and not on <see cref="Update"/>. Applying them again would resurrect a
/// value the caller had deliberately cleared, and a column with a default could then never be
/// cleared at all.
/// </item>
/// <item>
/// <b><see cref="Update"/> carries the whole record.</b> It replaces the values rather than
/// merging them, so a column left out of an update is a column cleared. The path this is shaped
/// for is <see cref="GetById"/>, change, <see cref="Update"/>, which carries everything without
/// the caller thinking about it.
/// </item>
/// <item>
/// <b>Absence is not a value, so two records with nothing in a unique column are not
/// duplicates.</b> Uniqueness is about values, and "I do not know yet" is not one. A collection
/// that needs the value present says so with <see cref="ColumnDefinition.Required"/>, which is a
/// separate statement and stays separate.
/// </item>
/// </list>
///
/// </summary>
public interface IStorage
{
    /// <summary>
    /// Runs <paramref name="work"/> as one unit: everything it writes is applied together, or
    /// nothing is.
    ///
    /// SC-5 asks for this in the first version rather than later, and the reason is arithmetic.
    /// An import of five hundred records without it is five hundred commits, each paying the
    /// durability cost of the whole commit protocol. Worse than slow, it is unrecoverable: a
    /// failure at record three hundred leaves two hundred and ninety-nine records behind and
    /// nothing to say which they were. Inside a unit of work, a failure leaves the storage as it
    /// was before the unit began.
    ///
    /// A unit of work inside a unit of work joins the outer one, so a method that batches
    /// internally can be called from a caller that is already batching. Only the outermost one
    /// commits.
    /// </summary>
    void InUnitOfWork(Action work);

    /// <summary>
    /// <see cref="InUnitOfWork(Action)"/>, for work that produces something - the records an
    /// import created, for instance, which the trace step describing the import then refers to.
    /// </summary>
    T InUnitOfWork<T>(Func<T> work);

    /// <summary>
    /// Creates a collection.
    /// </summary>
    /// <exception cref="CollectionAlreadyExistsException">A collection of that name exists.</exception>
    /// <exception cref="InvalidDefinitionException">
    /// The definition does not fit together: its display rule names a column it does not have.
    /// The checks a definition can make alone were made when it was built.
    /// </exception>
    void CreateCollection(CollectionDefinition definition);

    /// <summary>The definition of that collection, or null if there is none.</summary>
    CollectionDefinition? GetCollectionDefinition(string collectionName);

    /// <summary>Every collection's definition. Like <see cref="GetAll"/>, in no promised order.</summary>
    IReadOnlyCollection<CollectionDefinition> GetCollectionDefinitions();

    /// <summary>
    /// Removes a collection and everything in it, returning false if there was no such
    /// collection. Destructive, so D-7 has the application ask the user first, with counts, and
    /// this is what it calls once they answer.
    /// </summary>
    bool DeleteCollection(string collectionName);

    /// <summary>
    /// Creates a record and returns it, carrying the identity the storage gave it and the
    /// defaults it filled in.
    ///
    /// Values are judged here, against the collection's columns: see answers 2 and 3 above. A
    /// column left out gets its default if it has one; a required column with neither a value
    /// nor a default is a <see cref="RequiredValueMissing"/>. A column the collection does not
    /// have is an <see cref="UnknownColumn"/> rather than something quietly kept, so that a
    /// model's invented column is reported instead of stored.
    ///
    /// The identity is the storage's to give and cannot be offered here. Nothing named after it
    /// is special either: a collection is free to have a column called <c>id</c>, and it is an
    /// ordinary column with no relation to <see cref="StorageRecord.Id"/>.
    /// </summary>
    /// <exception cref="UnknownCollectionException">There is no such collection.</exception>
    /// <exception cref="StorageValidationException">
    /// One or more values did not fit. Every reason is reported, not only the first.
    /// </exception>
    StorageRecord Create(string collectionName, IReadOnlyDictionary<string, object?> fields);

    /// <summary>The record with that identity, or null if the collection does not hold one.</summary>
    /// <exception cref="UnknownCollectionException">There is no such collection.</exception>
    StorageRecord? GetById(string collectionName, Ulid id);

    /// <summary>
    /// Replaces the values of an existing record with the ones this record carries, returning
    /// false if there is no record with its identity.
    ///
    /// The record says which collection and which identity, so there is nothing to pass twice
    /// and nothing to pass inconsistently. Values are judged exactly as they are on create, with
    /// two differences: a column declared <see cref="ColumnDefinition.ReadOnly"/> whose value
    /// would change is a <see cref="ReadOnlyColumnChanged"/>, and defaults do not apply again -
    /// see the list above.
    /// </summary>
    /// <exception cref="UnknownCollectionException">There is no such collection.</exception>
    /// <exception cref="StorageValidationException">One or more values did not fit.</exception>
    bool Update(StorageRecord record);

    /// <summary>
    /// Removes a record, returning false if there was none with that identity. Destructive; see
    /// <see cref="DeleteCollection"/> on D-7.
    /// </summary>
    /// <exception cref="UnknownCollectionException">There is no such collection.</exception>
    bool Delete(string collectionName, Ulid id);

    /// <summary>
    /// Every record in the collection, <b>in no promised order</b> - see answer 5 above. Sort by
    /// <see cref="StorageRecord.Id"/> for oldest first.
    ///
    /// This is the whole of reading until SC-7's query type arrives, and it is not what the
    /// application should end up using: D-6 has the model write a query that C# runs, and a
    /// retrieval that reads everything and filters in memory is the thing that query exists to
    /// avoid.
    /// </summary>
    /// <exception cref="UnknownCollectionException">There is no such collection.</exception>
    IReadOnlyCollection<StorageRecord> GetAll(string collectionName);

    /// <summary>
    /// Runs a query, and says how it reached what it returned.
    ///
    /// SC-7, and the operation D-6 is built around: a model writes one of these, C# checks it
    /// against the schema and runs it, and the records are rendered by the application. They do
    /// not go back to a model, so a thousand-row answer costs no more of the token budget than a
    /// one-row answer, and this is the method that makes that true.
    ///
    /// <b>Validated before anything runs.</b> Every column the query names is resolved against
    /// the collection, every operator is checked against its column's type, and every value is
    /// judged by the same rule a write is judged by. A query that does not fit is refused with
    /// every reason it did not, each naming its column - and nothing has been read.
    ///
    /// <b>A column with no value satisfies nothing but <see cref="QueryOperator.IsNothing"/>.</b>
    /// A record that has never been given a city is not a record whose city is other than
    /// Prague. Three-valued logic would be the other answer and it is the wrong one here: the
    /// person asking has no idea their data has nulls in it, and a query that silently means
    /// something other than what it says cannot be explained in the diagram.
    ///
    /// <b>Text is compared with its accents dropped and its case folded</b>, on the first 128
    /// characters, which is the form the engine holds its string index keys in. A value is still
    /// stored exactly as it was given; this is only what counts as the same text when one is
    /// being looked for. See <see cref="ColumnType.Text"/>.
    ///
    /// <b>Ordering, skipping and taking are applied to what came back</b>, not pushed into the
    /// access path, so asking for ten records of a thousand still reads the thousand. The cost
    /// is in <see cref="StorageQueryResult.Cost"/> rather than hidden, and an ordering that
    /// matters for speed is a reason to raise an index, not a reason to make this quietly
    /// approximate.
    /// </summary>
    /// <exception cref="UnknownCollectionException">There is no such collection.</exception>
    /// <exception cref="StorageValidationException">
    /// The query does not fit the schema. Every reason is reported, each naming what did not fit.
    /// </exception>
    StorageQueryResult ExecuteQuery(StorageQuery query);

    // ---- Structural change (SC-6) ----------------------------------------------------------
    //
    // Six changes, of which two are already above: CreateCollection adds one and
    // DeleteCollection removes one. The four here are the column ones.
    //
    // None of them rewrites a record. A collection whose column was retyped serves records
    // written on both sides of the change by reading the older ones through what changed, and
    // Converge is what makes them agree - offered rather than imposed, because bringing a large
    // collection up to date is a cost the caller schedules.
    //
    // D-7 divides them: adding a column happens and is shown in the trace; renaming, retyping
    // and removing can lose values, so the application asks first, with counts, in the user's
    // words. None of that judgement is here. This does what it is told and says plainly what
    // each one costs, so that the application has something true to put on the card.

    /// <summary>
    /// Adds a column. Records that already exist gain no value for it - not even its default,
    /// which is what a record starts with, and these records started without it.
    /// </summary>
    /// <exception cref="UnknownCollectionException">There is no such collection.</exception>
    /// <exception cref="ColumnAlreadyExistsException">The collection already has a column by that name.</exception>
    void AddColumn(string collectionName, ColumnDefinition column);

    /// <summary>
    /// Renames a column, keeping everything else about it and every value in it.
    ///
    /// The display rule is rewritten with it, because it names columns and one naming a column
    /// that no longer exists is broken. Only its references move; literal text that happens to
    /// contain the old name is not a reference.
    ///
    /// A rename is not a remove followed by an add. The two look identical in a diff and mean
    /// opposite things to a record written before either, which is why this is its own
    /// operation rather than something an implementation works out.
    /// </summary>
    /// <exception cref="UnknownCollectionException">There is no such collection.</exception>
    /// <exception cref="UnknownColumnException">There is no such column.</exception>
    /// <exception cref="ColumnAlreadyExistsException">The new name is taken.</exception>
    void RenameColumn(string collectionName, string columnName, string newName);

    /// <summary>
    /// Changes what a column's values mean from now on.
    ///
    /// Values already stored are read as the new type: through their invariant text, so a
    /// number retyped to text keeps its digits and text retyped to a number keeps its value.
    /// <b>A value that cannot be read as the new type becomes nothing.</b> It is not an error,
    /// and the retype is not refused - a record whose old value has no meaning under the new
    /// type has no value for that column, which is the same situation as a record written
    /// before the column existed. This is the loss D-7 has the application count and ask about.
    ///
    /// The column's declared default is converted by the same rule, so one that has no meaning
    /// under the new type leaves the column without a default.
    ///
    /// Retyping a column to the type it already has changes nothing.
    /// </summary>
    /// <exception cref="UnknownCollectionException">There is no such collection.</exception>
    /// <exception cref="UnknownColumnException">There is no such column.</exception>
    void RetypeColumn(string collectionName, string columnName, ColumnType newType);

    /// <summary>
    /// Removes a column and every value in it.
    ///
    /// Refused while the collection's display rule names the column, because removing a column
    /// the collection is displayed by is two decisions and the second one is the caller's. An
    /// implementation that quietly dropped the rule would be making it for them, and D-7's
    /// confirmation card cannot describe a loss it was not told about.
    /// </summary>
    /// <exception cref="UnknownCollectionException">There is no such collection.</exception>
    /// <exception cref="UnknownColumnException">There is no such column.</exception>
    /// <exception cref="InvalidDefinitionException">The display rule names the column.</exception>
    void RemoveColumn(string collectionName, string columnName);

    /// <summary>
    /// Brings every record of the collection up to the current schema, so that nothing is left
    /// to read through. Returns how many records it had to bring up, which is zero when there
    /// was nothing to do - so calling it twice does the work once, and calling it on a
    /// collection that never changed does nothing at all.
    ///
    /// Nothing requires it. A retyped collection serves records from both sides of the change
    /// whether this has run or not, and the only difference it makes is that reads afterwards
    /// have nothing to replay. It is exposed rather than automatic because the cost belongs to
    /// whoever schedules it: converging a large collection rewrites every record in it, and
    /// doing that inside the change would make every structural change as expensive as the
    /// collection is large.
    /// </summary>
    /// <exception cref="UnknownCollectionException">There is no such collection.</exception>
    int Converge(string collectionName);
}
