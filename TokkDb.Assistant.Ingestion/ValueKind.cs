namespace TokkDb.Assistant.Ingestion;

/// <summary>
/// What a column of a file turned out to hold.
///
/// <b>Deliberately not the storage contract's <c>ColumnType</c>, although it has the same six
/// members.</b> §3.1 of the plan has this project depend on nothing: ingestion is parsing, it
/// happens before anything has been decided about where the data goes, and a file read from disk
/// is not a schema. The mapping from one to the other is a line of code and it belongs in the
/// layer that does the mapping, where the decision to store a 98%-integer column as text rather
/// than as numbers is made with the profile's evidence in front of it.
///
/// The duplication is the price of that boundary, and it is small: six names that are not going
/// to change, in a file with nothing else in it.
/// </summary>
public enum ValueKind
{
    /// <summary>Anything. The type every value fits, and the one a column widens to (IN-1b).</summary>
    Text = 1,

    /// <summary>A whole number.</summary>
    Integer,

    /// <summary>A number with a fraction.</summary>
    Decimal,

    /// <summary>True or false, as a person writes them: yes, no, y, n, true, false.</summary>
    Boolean,

    /// <summary>A day, with no time of day in it.</summary>
    Date,

    /// <summary>A moment: a day and a time.</summary>
    Timestamp
}
