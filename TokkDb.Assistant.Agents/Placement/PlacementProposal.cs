using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using TokkDb.Assistant.Agents.Changes;
using TokkDb.Assistant.Agents.Context;
using TokkDb.Assistant.Storage;
using TokkDb.Assistant.Trace;

namespace TokkDb.Assistant.Agents.Placement;

/// <summary>How an incoming field fills an existing one (AG-3).</summary>
public enum MappingKind
{
    /// <summary>The same name, the same kind.</summary>
    Exact = 1,

    /// <summary>A different name for the same kind of thing: <c>amount_eur</c> onto <c>Cost</c>.</summary>
    Renamed,

    /// <summary>The value has to change kind on the way in, losslessly, or most of it does and the rest is reported.</summary>
    Coerced
}

/// <summary>One incoming field onto one existing field, with its kind and the type on each side.</summary>
public sealed record FieldMapping(string Incoming, ColumnType IncomingKind, string Existing, ColumnType ExistingKind, MappingKind Kind)
{
    public string Describe() => Kind switch
    {
        MappingKind.Exact => $"{Incoming} → {Existing}",
        MappingKind.Renamed => $"{Incoming} → {Existing} (a different name for the same thing)",
        _ => $"{Incoming} → {Existing} (read as {SchemaDigest.Kind(ExistingKind)})"
    };
}

/// <summary>An incoming field that maps to nothing, and the field proposed for it.</summary>
public sealed record ProposedField(string Incoming, ColumnDefinition Column);

/// <summary>
/// How sure the placement is, computed in C# from observable evidence and never taken from the
/// model (AG-3a): the model's own figure, if it offers one, is ignored.
/// </summary>
/// <param name="Score">Between 0 and 1.</param>
/// <param name="Evidence">Each piece of evidence, as a line a person can read.</param>
/// <param name="RunnerUp">The score the next-best candidate would have had, or null when there was none.</param>
/// <param name="RunnerUpName">Its name.</param>
public sealed record Confidence(double Score, IReadOnlyList<string> Evidence, double? RunnerUp, string? RunnerUpName)
{
    public double Margin => RunnerUp is { } other ? Score - other : 1.0;
}

/// <summary>
/// Where incoming data belongs, as one object (AG-3): the target, the field mappings with their
/// kinds and types, the incoming fields that map to nothing and what is proposed for each, the
/// existing fields nothing fills, the structural actions implied and their classes, the
/// confidence with its evidence, and one sentence of rationale. Validation, the confirmation card
/// and the trace all read this one thing.
/// </summary>
public sealed record PlacementProposal(
    string Target,
    bool IsNew,
    string? Purpose,
    IReadOnlyList<FieldMapping> Mappings,
    IReadOnlyList<ProposedField> NewFields,
    IReadOnlyList<string> UnfilledExisting,
    IReadOnlyList<ClassifiedChange> Actions,
    Confidence Confidence,
    string Rationale,
    string? KeyColumn,
    string SchemaVersion,
    IReadOnlyList<PlacementCandidate> Shortlist)
{
    public bool NeedsConfirmation => Actions.Any(static action => action.NeedsConfirmation);

    /// <summary>The proposal as the trace's step output and the card's lines: plain words.</summary>
    public string Describe()
    {
        var text = new StringBuilder();
        text.Append(IsNew ? $"a new thing called {Target}" : $"add to {Target}");
        if (Mappings.Count > 0) text.Append("; fields: ").Append(string.Join(", ", Mappings.Select(static mapping => mapping.Describe())));
        if (NewFields.Count > 0) text.Append("; new fields: ").Append(string.Join(", ", NewFields.Select(static proposed => proposed.Column.Name)));
        if (UnfilledExisting.Count > 0) text.Append("; left empty: ").Append(string.Join(", ", UnfilledExisting));
        if (KeyColumn is not null) text.Append("; identified by ").Append(KeyColumn);
        text.Append("; confidence ").Append(Confidence.Score.ToString("0.00", CultureInfo.InvariantCulture));
        return text.ToString();
    }
}

/// <summary>What the floor and the gap are (AG-3b), and the higher bar for a new thing.</summary>
/// <param name="Floor">Below this the best candidate is not applied without asking.</param>
/// <param name="Gap">A margin below this over the runner-up is a question, however high both score.</param>
/// <param name="NewFloor">How sure "nothing here fits" has to be before a new thing is made without asking.</param>
public sealed record PlacementThresholds(double Floor, double Gap, double NewFloor)
{
    /// <summary>
    /// Provisional (§10, item 6): they come out of running N-1 and N-2 over a corpus of real
    /// files, and these are the starting points the tests were written against.
    /// </summary>
    public static readonly PlacementThresholds Default = new(Floor: 0.6, Gap: 0.2, NewFloor: 0.75);
}

/// <summary>Whether a proposal applies without asking, or the person is asked with the candidates shown (AG-3b).</summary>
public enum PlacementDecision
{
    ApplySilently = 1,
    Ask
}

/// <summary>The rule of AG-3b, applied to a proposal.</summary>
public static class PlacementDecisions
{
    public static PlacementDecision Decide(PlacementProposal proposal, PlacementThresholds thresholds)
    {
        ArgumentNullException.ThrowIfNull(proposal);
        ArgumentNullException.ThrowIfNull(thresholds);

        if (proposal.NeedsConfirmation) return PlacementDecision.Ask;

        if (proposal.IsNew)
        {
            return proposal.Confidence.Score >= thresholds.NewFloor ? PlacementDecision.ApplySilently : PlacementDecision.Ask;
        }

        return proposal.Confidence.Score >= thresholds.Floor && proposal.Confidence.Margin >= thresholds.Gap
            ? PlacementDecision.ApplySilently
            : PlacementDecision.Ask;
    }

    /// <summary>Why it was asked, in a sentence for the card (N-1).</summary>
    public static string WhyAsked(PlacementProposal proposal, PlacementThresholds thresholds)
    {
        if (proposal.NeedsConfirmation) return "part of this needs a yes before it happens";
        if (proposal.IsNew) return "nothing you have stored looks quite like this, and it is not certain enough that none does";
        if (proposal.Confidence.Margin < thresholds.Gap && proposal.Confidence.RunnerUpName is { } other)
        {
            return $"this could belong to {proposal.Target} or to {other}, and neither is clearly the one";
        }

        return $"it is not certain enough that this belongs to {proposal.Target}";
    }
}

/// <summary>
/// A proposal that has been validated, and cannot be changed (AG-3d): the confirmation card is
/// rendered from it, the executor re-checks its hash before applying, and an unvalidated
/// candidate cannot reach execution because there is no way to make one of these without
/// <see cref="ValidatedProposal.Validate"/>.
///
/// The hash covers the proposal and the rows it would write, so that substituting either between
/// the question and the answer is caught (N-12). It is also the idempotency key of the resolved
/// action (AG-8a).
/// </summary>
public sealed record ValidatedProposal
{
    private ValidatedProposal(PlacementProposal proposal, IReadOnlyList<ImportRow> rows, MergePolicy policy, string hash)
    {
        Proposal = proposal;
        Rows = rows;
        Policy = policy;
        Hash = hash;
    }

    public PlacementProposal Proposal { get; }

    /// <summary>The rows as the importer will take them: by the target's field names, typed.</summary>
    public IReadOnlyList<ImportRow> Rows { get; }

    public MergePolicy Policy { get; }

    /// <summary>AG-3d's content hash: the proposal and the rows, canonically rendered.</summary>
    public string Hash { get; }

    public int RowCount => Rows.Count;

    /// <summary>
    /// Validates a candidate against the storage as it is now: the target exists or is new, every
    /// mapped field exists on the target with the type the mapping says, no two mappings fill one
    /// field, and every new field is a name the target does not have. Returns the errors instead
    /// of a proposal when it does not fit.
    /// </summary>
    public static ValidatedProposal Validate(IStorage storage, PlacementProposal proposal, IReadOnlyList<ImportRow> rows, MergePolicy policy)
    {
        ArgumentNullException.ThrowIfNull(storage);
        ArgumentNullException.ThrowIfNull(proposal);
        ArgumentNullException.ThrowIfNull(rows);

        var problems = Check(storage, proposal);
        if (problems.Count > 0)
        {
            throw new InvalidProposalException(problems);
        }

        return new ValidatedProposal(proposal, rows, policy, Hashes.Of(Canonical(proposal, rows, policy)));
    }

    /// <summary>What does not fit, or nothing.</summary>
    public static IReadOnlyList<string> Check(IStorage storage, PlacementProposal proposal)
    {
        var problems = new List<string>();
        var target = storage.GetCollectionDefinition(proposal.Target);

        if (proposal.IsNew)
        {
            if (target is not null) problems.Add($"'{proposal.Target}' already exists, and the proposal makes a new one.");
            if (proposal.Mappings.Count > 0) problems.Add("a new thing has nothing to map onto.");
        }
        else
        {
            if (target is null)
            {
                problems.Add($"'{proposal.Target}' is not something stored.");
                return problems;
            }

            var filled = new HashSet<string>(Names.Comparer);
            foreach (var mapping in proposal.Mappings)
            {
                var column = target.Column(mapping.Existing);
                if (column is null) problems.Add($"'{target.Name}' has no field called '{mapping.Existing}'.");
                else if (column.Type != mapping.ExistingKind) problems.Add($"'{mapping.Existing}' keeps {SchemaDigest.Kind(column.Type)}, not {SchemaDigest.Kind(mapping.ExistingKind)}.");
                if (!filled.Add(mapping.Existing)) problems.Add($"two incoming fields would fill '{mapping.Existing}'.");
            }

            foreach (var proposed in proposal.NewFields)
            {
                if (target.Column(proposed.Column.Name) is not null) problems.Add($"'{target.Name}' already has a field called '{proposed.Column.Name}'.");
            }
        }

        return problems;
    }

    /// <summary>The canonical text the hash is of: stable order, invariant rendering, no incidental detail.</summary>
    public static string Canonical(PlacementProposal proposal, IReadOnlyList<ImportRow> rows, MergePolicy policy)
    {
        var json = new JsonObject
        {
            ["target"] = proposal.Target,
            ["new"] = proposal.IsNew,
            ["purpose"] = proposal.Purpose,
            ["schema"] = proposal.SchemaVersion,
            ["key"] = proposal.KeyColumn,
            ["policy"] = policy.ToString(),
            ["mappings"] = new JsonArray([.. proposal.Mappings
                .OrderBy(static mapping => mapping.Incoming, StringComparer.Ordinal)
                .Select(mapping => (JsonNode)$"{mapping.Incoming}>{mapping.Existing}:{mapping.ExistingKind}:{mapping.Kind}")]),
            ["fields"] = new JsonArray([.. proposal.NewFields
                .OrderBy(static proposed => proposed.Column.Name, StringComparer.Ordinal)
                .Select(proposed => (JsonNode)$"{proposed.Column.Name}:{proposed.Column.Type}:{proposed.Column.Required}:{proposed.Column.Unique}")]),
            ["actions"] = new JsonArray([.. proposal.Actions.Select(action => (JsonNode)$"{action.Description}|{action.Class}|{action.Reversibility}")]),
            ["rows"] = rows.Count,
            ["rowsHash"] = RowsHash(rows)
        };

        return json.ToJsonString(new JsonSerializerOptions { WriteIndented = false });
    }

    private static string RowsHash(IReadOnlyList<ImportRow> rows)
    {
        var text = new StringBuilder();

        foreach (var row in rows)
        {
            foreach (var (name, value) in row.Fields.OrderBy(static entry => entry.Key, StringComparer.Ordinal))
            {
                text.Append(name).Append('=').Append(JournalValue.TextOf(value)).Append((char)31);
            }

            text.Append(row.Unreadable).Append((char)30);
        }

        return Hashes.Of(text.ToString());
    }
}

/// <summary>A candidate proposal did not fit the storage as it is (AG-3d): the reasons, named.</summary>
public sealed class InvalidProposalException : Exception
{
    public InvalidProposalException(IReadOnlyList<string> problems)
        : base("The proposal does not fit what is stored: " + string.Join(" ", problems))
    {
        Problems = problems;
    }

    public IReadOnlyList<string> Problems { get; }
}
