using TokkDb.Assistant.Storage;

namespace TokkDb.Assistant.Tests;

/// <summary>
/// <c>RecordIdentity</c> is what makes SC-4's first answer worth having: a Ulid that carries its
/// creation time is only useful for "in the order they arrived" if two identities issued in the
/// same millisecond still order correctly. A plain <c>Ulid.NewUlid()</c> does not promise that.
/// </summary>
public sealed class RecordIdentityTests
{
    [Fact]
    public void Identities_issued_as_fast_as_possible_still_increase()
    {
        var issued = new Ulid[10_000];
        for (var i = 0; i < issued.Length; i++)
        {
            issued[i] = RecordIdentity.Next();
        }

        for (var i = 1; i < issued.Length; i++)
        {
            Assert.True(
                issued[i].CompareTo(issued[i - 1]) > 0,
                $"identity {i} ({issued[i]}) is not after identity {i - 1} ({issued[i - 1]})");
        }
    }

    [Fact]
    public void Identities_are_distinct()
    {
        var issued = Enumerable.Range(0, 10_000).Select(static _ => RecordIdentity.Next()).ToArray();

        Assert.Equal(issued.Length, issued.Distinct().Count());
    }

    /// <summary>
    /// The identity still carries a usable creation time. If it did not, "when did this arrive"
    /// would have to become a column, and the increment would have bought order at the cost of
    /// the thing it was ordering by.
    /// </summary>
    [Fact]
    public void An_identity_carries_the_time_it_was_issued()
    {
        var before = DateTimeOffset.UtcNow.AddSeconds(-1);
        var identity = RecordIdentity.Next();
        var after = DateTimeOffset.UtcNow.AddSeconds(1);

        Assert.InRange(identity.Time, before, after);
    }

    /// <summary>
    /// Issued from several threads at once, which is how a unit of work on a background thread
    /// and a trace step on another will reach it.
    /// </summary>
    [Fact]
    public void Identities_issued_from_several_threads_at_once_are_still_distinct()
    {
        var issued = new System.Collections.Concurrent.ConcurrentBag<Ulid>();

        Parallel.For(0, 8, _ =>
        {
            for (var i = 0; i < 1_000; i++) issued.Add(RecordIdentity.Next());
        });

        Assert.Equal(8_000, issued.Count);
        Assert.Equal(8_000, issued.Distinct().Count());
    }
}
