namespace TokkDb.Assistant.Storage;

/// <summary>
/// Where a record's identity comes from. Both implementations of <see cref="IStorage"/> call
/// this, so that neither can issue an identity the other would not.
///
/// A plain <c>Ulid.NewUlid()</c> is 48 bits of millisecond timestamp and 80 bits of randomness,
/// which means two records created in the same millisecond sort in a random order relative to
/// each other. That is not good enough for the claim <see cref="IStorage.GetAll"/> rests on. An
/// import of five hundred spreadsheet rows (SC-5) happens well inside one millisecond, and if
/// sorting by identity scrambled them, the answer to "show me what I just imported, in order"
/// would be gone and the only way back would be storing a row number - a physical detail in a
/// logical definition, which SC-2 forbids.
///
/// So identities are issued strictly increasing: the timestamp when the clock has moved, and the
/// previous identity plus one when it has not. That keeps every property a Ulid had - unique
/// without asking the storage anything, and carrying its creation time - and adds the one it was
/// missing.
/// </summary>
internal static class RecordIdentity
{
    private static readonly object Gate = new();

    private static Ulid _last;

    /// <summary>The next identity. Strictly greater than every identity this process has issued.</summary>
    public static Ulid Next()
    {
        lock (Gate)
        {
            var next = Ulid.NewUlid();
            if (next.CompareTo(_last) <= 0)
            {
                next = Increment(_last);
            }

            _last = next;
            return next;
        }
    }

    /// <summary>
    /// The smallest Ulid greater than <paramref name="identity"/>: the 80 random bits as a
    /// big-endian number plus one, carrying into the timestamp if they are all ones.
    /// </summary>
    private static Ulid Increment(Ulid identity)
    {
        var bytes = identity.ToByteArray();

        for (var i = bytes.Length - 1; i >= 0; i--)
        {
            if (bytes[i] != byte.MaxValue)
            {
                bytes[i]++;
                return new Ulid(bytes);
            }

            bytes[i] = 0;
        }

        // Every bit was set, which is the year 10889 and eighty ones. There is no next one.
        throw new InvalidOperationException("There is no identity after the last one.");
    }
}
