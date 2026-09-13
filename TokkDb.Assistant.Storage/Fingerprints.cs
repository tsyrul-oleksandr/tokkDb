using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace TokkDb.Assistant.Storage;

/// <summary>
/// "Have I seen this row before", for rows that carry nothing that identifies them.
///
/// IN-6: identity rests on a natural key where one exists, and on a fingerprint over normalised
/// values where none does. IN-6a makes it a <b>stored, indexed field</b> rather than something
/// computed during an import, which is what lets a whole import be checked in one ordered pass
/// (SC-9) instead of one lookup per row.
///
/// <b>The version is in the value, and that is the point of it.</b> A fingerprint means nothing
/// on its own; it means "the values of this row, normalised the way version 1 normalises them".
/// Change the normalisation and every stored fingerprint becomes a fingerprint of something else,
/// and every row of the next import looks new. Carrying the version makes that visible: the
/// importer compares what it computes with what the collection was written under, and reports a
/// mismatch rather than reading it as "not a duplicate" and storing everything twice.
///
/// <b>What it does not catch, written down rather than discovered.</b> A fingerprint covers every
/// value, so a row that came back with one field corrected is a different row to it. That is the
/// limit IN-6 states and the reason IN-7 refuses <c>update existing</c> without a natural key:
/// there is nothing to match the changed row to.
/// </summary>
public static class Fingerprints
{
    /// <summary>
    /// The normalisation this version of the code computes. Bumping it is a decision with a
    /// cost: every stored fingerprint was computed under the old one, and the mismatch is
    /// reported rather than silently ignored.
    /// </summary>
    public const int Version = 1;

    /// <summary>
    /// The column a fingerprint is stored in. A name and not a secret: it is an ordinary column
    /// of the collection, visible in the browser like any other, because a hidden field that
    /// governs whether an import is stored is worse than a visible one nobody looks at.
    /// </summary>
    public const string ColumnName = "rowFingerprint";

    /// <summary>Where the collection records which normalisation its fingerprints were computed under.</summary>
    public const string VersionMetadataKey = "fingerprintVersion";

    // The unit separator, so that two columns' values cannot run together into a third meaning.
    private const char Separator = (char)31;

    /// <summary>The column, ready to be added to a collection that is about to be imported into.</summary>
    /// <summary>
    /// The column, ready to be added to a collection that is about to be imported into.
    ///
    /// <b>Ordinary in every way, including editable.</b> It is tempting to make it write-once,
    /// and that would be wrong twice over: a record stored before the column existed has to be
    /// given one, and a record brought up to date by an import has to have it recomputed. A
    /// person who edits it by hand has made their row look new to the next import, which is
    /// visible and recoverable, where a column nothing can write would make the import machinery
    /// unable to do its own job.
    /// </summary>
    public static ColumnDefinition Column() =>
        new(ColumnName,
            ColumnType.Text,
            "A short form of this record's values, used to notice the same row arriving twice.");

    /// <summary>
    /// The fingerprint of one row: the version, then a hash of every value in column order.
    ///
    /// Normalisation is the contract's own comparison rules rather than a new set - text folded
    /// as <see cref="TextComparison"/> folds it, numbers and moments in the invariant text every
    /// other part of this assembly round-trips through. So two rows that a unique column would
    /// call the same are the same row here too, and the two cannot disagree.
    /// </summary>
    public static string Of(IReadOnlyDictionary<string, object?> fields, IReadOnlyList<ColumnDefinition> columns)
    {
        ArgumentNullException.ThrowIfNull(fields);
        ArgumentNullException.ThrowIfNull(columns);

        var material = new StringBuilder();

        foreach (var column in columns)
        {
            // The fingerprint is not part of what it is a fingerprint of.
            if (StorageNames.Same(column.Name, ColumnName)) continue;

            material.Append(column.Name).Append(Separator);
            material.Append(Normalise(fields.GetValueOrDefault(column.Name))).Append(Separator);
        }

        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(material.ToString()));

        // Sixteen bytes of a SHA-256, which is what a Ulid is and what a Guid is: a collision
        // needs a number of rows nobody has. The rest would be paid for on every index page.
        return $"{Version}:{Convert.ToHexStringLower(hash.AsSpan(0, 16))}";
    }

    /// <summary>The version a stored fingerprint was computed under, or null if it is not one of ours.</summary>
    public static int? VersionOf(string? fingerprint)
    {
        if (fingerprint is null) return null;

        var separator = fingerprint.IndexOf(':');

        return separator > 0
               && int.TryParse(
                   fingerprint[..separator], NumberStyles.None, CultureInfo.InvariantCulture, out var version)
            ? version
            : null;
    }

    private static string Normalise(object? value) => value switch
    {
        null => string.Empty,
        string text => TextComparison.Fold(text),
        long whole => whole.ToString(CultureInfo.InvariantCulture),
        // A decimal keeps its scale as a value and must not keep it here: 5 and 5.00 are one
        // amount, and a row re-exported with another scale is not a new row.
        decimal exact => Trimmed(exact).ToString(CultureInfo.InvariantCulture),
        bool flag => flag ? "true" : "false",
        DateOnly day => day.ToString("O", CultureInfo.InvariantCulture),
        DateTime moment => moment.ToString("O", CultureInfo.InvariantCulture),
        _ => value.ToString() ?? string.Empty
    };

    /// <summary>Trailing zeros removed, so that 5.00 and 5 fingerprint alike.</summary>
    private static decimal Trimmed(decimal value) => value / 1.000000000000000000000000000000m;
}
