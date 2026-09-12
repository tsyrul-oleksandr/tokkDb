namespace TokkDb.Assistant.Storage;

/// <summary>
/// The logical type of a column.
///
/// SC-3 says a value is written on the way in and never interpreted on the way out, so this set
/// is the set of things the storage is willing to store *as themselves*. It is deliberately
/// small, for two reasons. A model choosing between six types is right more often than one
/// choosing between twenty (D-4), and every extra type is a retype waiting to happen the first
/// time the model chooses the neighbouring one.
///
/// Three choices inside it were made on purpose rather than inherited:
///
/// <list type="bullet">
/// <item>
/// There is one integer type, not two. A personal storage has no use for the four bytes that
/// distinguishing 32-bit from 64-bit would save, and having both means that the day a column
/// inferred as 32-bit meets a larger number, the whole collection needs a structural change.
/// </item>
/// <item>
/// <see cref="Date"/> and <see cref="Timestamp"/> are different types. A receipt has a date; a
/// message has a moment. Storing a date as midnight in some time zone is a claim the storage
/// cannot support, and one that the browser and the retrieval step would then have to
/// un-make. Keeping them apart costs one enum member and settles the question once.
/// </item>
/// <item>
/// There is no binary floating point type. Money is the commonest number this application will
/// meet, and <see cref="Decimal"/> holds it exactly. A caller with a measurement that is
/// genuinely a double has to say what precision it wants, which is a better conversation than
/// silently storing 0.1 as 0.1000000000000000055511151231257827.
/// </item>
/// </list>
///
/// <see cref="ColumnTypes"/> holds the CLR types each one accepts on the way in and the one CLR
/// type it hands back.
/// </summary>
public enum ColumnType
{
    /// <summary>
    /// Text of any length. Accepts and returns <see cref="string"/>.
    ///
    /// Stored exactly and compared loosely: a comparison drops accents, folds case, and looks at
    /// the first 128 characters. So a search for <c>euro</c> finds <c>EuroPython</c>, and a
    /// unique column treats <c>R-0001</c> and <c>r-0001</c> as one value. That is the engine's
    /// rule for its string index keys, taken rather than fought, and it is the right one for
    /// someone searching their own data. <c>TextComparison</c> states it in full.
    /// </summary>
    Text = 1,

    /// <summary>A whole number. Accepts any signed or unsigned integer that fits; returns <see cref="long"/>.</summary>
    Integer,

    /// <summary>An exact decimal number. Accepts integers and <see cref="decimal"/>; returns <see cref="decimal"/>.</summary>
    Decimal,

    /// <summary>True or false. Accepts and returns <see cref="bool"/>.</summary>
    Boolean,

    /// <summary>A calendar date with no time and no zone. Accepts and returns <see cref="DateOnly"/>.</summary>
    Date,

    /// <summary>A moment in time, held in UTC. Accepts <see cref="DateTime"/> and <see cref="DateTimeOffset"/>; returns a UTC <see cref="DateTime"/>.</summary>
    Timestamp
}
