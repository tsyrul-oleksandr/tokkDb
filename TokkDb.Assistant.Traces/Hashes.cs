using System.Security.Cryptography;
using System.Text;

namespace TokkDb.Assistant.Trace;

/// <summary>
/// The two hashes the trace keeps, and the reason it keeps hashes rather than the things they
/// stand for.
///
/// <b>A prompt hash, never the prompt</b> (TR-3, D-8). Storing the text would put the user's
/// content in the database twice and make traces larger than the data they describe. The hash
/// still answers what the text was wanted for - whether two calls sent the same thing - which
/// AG-6a turns into an assertion, because a byte-identical prefix is what the model's cache is
/// keyed on and a reordering of two blocks is invisible in review.
///
/// <b>A content hash, so that "has this been touched since" stays answerable</b> (TR-2b, AG-11a).
/// An insert deliberately does not record what it wrote - the data is in storage, and copying it
/// into the journal would make the journal grow with the width of the rows. What it records
/// instead is fixed width and still sufficient: an undo can tell a record it created and nobody
/// has touched from one somebody has edited since.
///
/// <b>Why this is not <c>Fingerprints</c>.</b> The storage contract has a row fingerprint and it
/// answers a different question - whether a row of a file has been imported before (IN-5) - it is
/// a stored column, it is part of the data, and it is computed over the columns an import was
/// keyed on. This is computed over the fields a change actually wrote, it is part of the journal,
/// and it lives as long as the journal does. They would also have to be one file in one project
/// for the two to share code, and §3.1 has this project depending on nothing.
/// </summary>
public static class Hashes
{
    /// <summary>The first sixteen bytes of SHA-256, in hex. Fixed width, which is what TR-2b's bound rests on.</summary>
    public const int Length = 32;

    private const char FieldSeparator = (char)31;
    private const char RecordSeparator = (char)30;

    public static string Of(string text)
    {
        ArgumentNullException.ThrowIfNull(text);

        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)).AsSpan(0, Length / 2));
    }

    /// <summary>
    /// A hash of a record as it was written.
    ///
    /// Ordered by field name and rendered invariantly, so that the same record hashes the same
    /// way whoever wrote it, in whatever order the fields happened to arrive, on whatever machine.
    /// The separators are the ones a field value cannot contain.
    /// </summary>
    public static string OfFields(IReadOnlyDictionary<string, object?> fields)
    {
        ArgumentNullException.ThrowIfNull(fields);

        var text = new StringBuilder();

        foreach (var name in fields.Keys.OrderBy(static name => name, StringComparer.Ordinal))
        {
            text.Append(name).Append(FieldSeparator)
                .Append(JournalValue.TextOf(fields[name])).Append(RecordSeparator);
        }

        return Of(text.ToString());
    }
}
