using System.Reflection;
using TokkDb.Assistant.Agents.Models;

namespace TokkDb.Assistant.Agents.Operations;

/// <summary>
/// The operations the assistant makes (D-4: more models, each doing less), one declaration each,
/// with their budgets from §6.2 and their egress from NF-4a.
///
/// <b>None of them takes a tool</b>, and that is measured rather than tidy (D-5, R-1): the first
/// tool costs 252 prompt tokens and a read tool invited a loop measured at a median of twenty-two
/// calls, while a digest in the system message cost 181 tokens and terminated. So the schema
/// reaches the model inlined, and the answer is a structured output rather than a tool call. The
/// catalogue and the scoping exist for the operation that one day needs a tool that <i>acts</i>.
///
/// <b>How the catalogue is found.</b> Every <c>public static readonly OperationDeclaration</c>
/// field of any type in this assembly - and of any assembly a caller includes - is an operation.
/// Adding one is adding a field in one file; the orchestrator dispatches by the declaration it is
/// handed and never by a list of names (EX-2).
/// </summary>
public static class Operations
{
    private static readonly List<Assembly> Included = [typeof(Operations).Assembly];
    private static readonly object Gate = new();

    // ---- The vocabulary the answers share -----------------------------------------------------------

    /// <summary>The kinds of value, in the words a model is asked to use and the digest uses.</summary>
    public const string KindWords = "\"text\",\"whole number\",\"number\",\"date\",\"date and time\",\"true or false\"";

    // ---- The declarations ---------------------------------------------------------------------------

    /// <summary>What the person wants done, when it cannot be told without asking (AG-2).</summary>
    public static readonly OperationDeclaration Intent = new(
        Name: "intent",
        StepName: "what was meant",
        Instructions:
            """
            You read one message a person sent to their personal storage and say what they want.
            store: they are giving you something to keep. find: they are asking what is stored.
            correct: they say a stored value is wrong. remove: they want something deleted.
            restructure: they want to change what a thing keeps, such as dropping a field.
            undo: they want a recent change taken back. other: none of these.
            Answer with the intent only.
            """,
        Tools: [],
        ContextBudget: 600,
        OutputBudget: 64,
        Egress: EgressClass.Nothing,
        OutputSchema:
            """
            {"type":"object","properties":{"intent":{"type":"string","enum":["store","find","correct","remove","restructure","undo","other"]}},"required":["intent"],"additionalProperties":false}
            """);

    /// <summary>Prose into candidate records (D-4's extraction model).</summary>
    public static readonly OperationDeclaration Extraction = new(
        Name: "extraction",
        StepName: "what is in the text",
        Instructions:
            $$"""
            You turn a piece of text a person wants to keep into records. Each record is a list of
            fields: a short field name, the value as it appears in the text, and the kind of value it
            is, one of {{KindWords}}. Use the same field names for every record. Write amounts as
            plain numbers without currency words, and dates as day month year. Say what kind of
            things these are in "kind", as a plural noun such as "conferences" or "expenses".
            Leave out nothing the text says and add nothing it does not.
            """,
        Tools: [],
        ContextBudget: 4_000,
        // Ten records of seven fields are about 2,500 tokens of the answer's JSON; a cap that cut
        // a pasted table's records short was the first thing a person met (2026-09-15).
        OutputBudget: 6_000,
        Egress: EgressClass.RawText,
        OutputSchema:
            $$$$"""
            {"type":"object","properties":{"kind":{"type":"string"},"records":{"type":"array","items":{"type":"object","properties":{"fields":{"type":"array","items":{"type":"object","properties":{"name":{"type":"string"},"value":{"type":"string"},"kind":{"type":"string","enum":[{{{{KindWords}}}}]}},"required":["name","value","kind"],"additionalProperties":false}}},"required":["fields"],"additionalProperties":false}}},"required":["kind","records"],"additionalProperties":false}
            """);

    /// <summary>Where incoming data belongs, chosen from a shortlist C# produced (AG-3, AG-3c).</summary>
    public static readonly OperationDeclaration Mapping = new(
        Name: "mapping",
        StepName: "where it belongs",
        Instructions:
            """
            You place incoming data into a personal storage. Everything the storage already holds is
            listed in the digest below, one thing per line with what it keeps and its fields. The
            message offers a shortlist of the things the data might belong to, with the incoming
            fields. Choose one of the offered names when the data is the same kind of thing, and
            "none" when it is not. For every incoming field, name the existing field it fills, or
            leave "existing" empty when nothing fits and the field would have to be added. When
            you choose "none", propose a plural name for a new thing in "newName" and one sentence
            in "purpose" saying what it keeps.
            """,
        Tools: [],
        ContextBudget: 3_000,
        OutputBudget: 600,
        Egress: EgressClass.BoundedSample,
        OutputSchema:
            """
            {"type":"object","properties":{"choice":{"type":"string"},"newName":{"type":"string"},"purpose":{"type":"string"},"fields":{"type":"array","items":{"type":"object","properties":{"incoming":{"type":"string"},"existing":{"type":"string"}},"required":["incoming","existing"],"additionalProperties":false}}},"required":["choice","newName","purpose","fields"],"additionalProperties":false}
            """);

    /// <summary>A change to what a thing keeps, from a sentence (AG-4).</summary>
    public static readonly OperationDeclaration Structure = new(
        Name: "structure",
        StepName: "what would change",
        Instructions:
            $$"""
            A person wants to change how their storage is organised. The digest below lists every
            thing stored with its fields. Say which action they are asking for - remove-field,
            rename-field, retype-field, add-field, remove-thing, set-required, set-unique, or none
            when the message asks for no such change - and which thing and field it is about,
            using the names exactly as listed. For rename-field give the new name in "newName"; for
            retype-field and add-field give the kind in "kind", one of {{KindWords}}; otherwise leave
            those empty.
            """,
        Tools: [],
        ContextBudget: 2_000,
        OutputBudget: 200,
        Egress: EgressClass.SchemaDigest,
        OutputSchema:
            $$$$"""
            {"type":"object","properties":{"action":{"type":"string","enum":["remove-field","rename-field","retype-field","add-field","remove-thing","set-required","set-unique","none"]},"thing":{"type":"string"},"field":{"type":"string"},"newName":{"type":"string"},"kind":{"type":"string","enum":[{{{{KindWords}}}},""]}},"required":["action","thing","field","newName","kind"],"additionalProperties":false}
            """);

    /// <summary>A question into a query the storage runs (D-6, QR-1). The answer is the query, not a tool call.</summary>
    public static readonly OperationDeclaration Query = new(
        Name: "query",
        StepName: "what to look for",
        Instructions:
            """
            You turn a question about a personal storage into a query. The digest below lists every
            thing stored, with its fields and their kinds. Answer with the thing to look in, using
            its name exactly as listed, and the conditions: each names a field exactly as listed, an
            operator - is, is not, before, after, at least, at most, contains, one of, has nothing,
            has something - and its values as text (dates as year-month-day, amounts as plain
            numbers). Set "total" to the field to add up when the question asks how much in all,
            and to an empty string otherwise. Set "orderBy" to a field when an order is asked for,
            "largestFirst" to true for the largest or latest first, and "limit" to how many are
            wanted, or 0 for all. Leave "where" empty when the question asks for everything.
            Read a period by the calendar: "last year" is the whole calendar year before today's
            (at least January 1 and before January 1 of this year), "this year" is today's calendar
            year, "last month" the whole previous calendar month; only "the last N months" or
            "the past year" count back from today. For example, when today is 2026-09-15, "last
            year" is date at least 2025-01-01 and date before 2026-01-01 - never after 2025-09-15.
            "orderBy", "largestFirst", "limit" and "total" are answers of their own, never
            conditions inside "where"; a condition's field is always one the digest lists. A
            question for the most, largest, highest, latest or newest one orders by that field
            with "largestFirst" true and "limit" 1; the least, smallest, cheapest, earliest or
            oldest one orders by it with "largestFirst" false and "limit" 1.
            """,
        Tools: [],
        ContextBudget: 2_500,
        OutputBudget: 400,
        Egress: EgressClass.SchemaDigest,
        OutputSchema: QuerySchema);

    /// <summary>A follow-up as a refinement of the previous query (QR-3's second layer).</summary>
    public static readonly OperationDeclaration Refine = new(
        Name: "refine",
        StepName: "narrowing it down",
        Instructions:
            """
            A person asked a question, was shown the records a query found, and now asks a follow-up
            about them. The message gives the previous query and the follow-up. Answer with the
            query the follow-up means: the previous one, narrowed, reordered or re-totalled as the
            follow-up asks, in the same shape and with the names exactly as listed in the digest
            below.
            """,
        Tools: [],
        ContextBudget: 2_500,
        OutputBudget: 400,
        Egress: EgressClass.SchemaDigest,
        OutputSchema: QuerySchema);

    /// <summary>A follow-up answered from a digest of the result, never the rows (QR-3's third layer, D-6).</summary>
    public static readonly OperationDeclaration Digest = new(
        Name: "digest",
        StepName: "answering from the summary",
        Instructions:
            """
            A person was shown some records and asks a question about them. You are given a short
            summary of those records - how many, which fields, the smallest and largest values, a
            few examples - and never the records themselves. Answer the question from the summary
            in one or two plain sentences, and say when the summary cannot answer it.
            """,
        Tools: [],
        ContextBudget: 1_500,
        OutputBudget: 300,
        Egress: EgressClass.BoundedSample,
        OutputSchema:
            """
            {"type":"object","properties":{"answer":{"type":"string"}},"required":["answer"],"additionalProperties":false}
            """);

    /// <summary>The outcome of a request put into a sentence. Templated in C# for the common cases (AG-1e); this is for the rest.</summary>
    public static readonly OperationDeclaration Phrase = new(
        Name: "phrase",
        StepName: "putting it into words",
        Instructions:
            """
            You write one or two plain sentences telling a person what was just done with their
            data, from the facts given. Use their words: things they have stored, not collections;
            fields, not columns. Never mention databases, schemas, queries or indexes.
            """,
        Tools: [],
        ContextBudget: 1_200,
        OutputBudget: 300,
        Egress: EgressClass.BoundedSample,
        OutputSchema:
            """
            {"type":"object","properties":{"reply":{"type":"string"}},"required":["reply"],"additionalProperties":false}
            """);

    /// <summary>What a person wants corrected in a record they were shown (QR-4).</summary>
    public static readonly OperationDeclaration Correction = new(
        Name: "correction",
        StepName: "what to correct",
        Instructions:
            """
            A person was shown a numbered list of records and says one of them is wrong. The
            message gives the records - their number, what they are called and their fields - and
            what the person said. Answer with which record they mean, as its number, the field
            that is wrong, named exactly as listed, and the value it should be, as plain text.
            """,
        Tools: [],
        ContextBudget: 1_500,
        OutputBudget: 200,
        Egress: EgressClass.BoundedSample,
        OutputSchema:
            """
            {"type":"object","properties":{"which":{"type":"integer"},"field":{"type":"string"},"value":{"type":"string"}},"required":["which","field","value"],"additionalProperties":false}
            """);

    private const string QuerySchema =
        """
        {"type":"object","properties":{"thing":{"type":"string"},"where":{"type":"array","items":{"type":"object","properties":{"field":{"type":"string"},"operator":{"type":"string","enum":["is","is not","before","after","at least","at most","contains","one of","has nothing","has something"]},"values":{"type":"array","items":{"type":"string"}}},"required":["field","operator","values"],"additionalProperties":false}},"orderBy":{"type":"string"},"largestFirst":{"type":"boolean"},"limit":{"type":"integer"},"total":{"type":"string"}},"required":["thing","where","orderBy","largestFirst","limit","total"],"additionalProperties":false}
        """;

    // ---- The catalogue ------------------------------------------------------------------------------

    /// <summary>Makes an assembly's declarations part of the catalogue: the tests add one this way.</summary>
    public static void Include(Assembly assembly)
    {
        ArgumentNullException.ThrowIfNull(assembly);
        lock (Gate)
        {
            if (!Included.Contains(assembly)) Included.Add(assembly);
        }
    }

    /// <summary>Every operation there is, found by reflection so that adding one is one file (EX-2).</summary>
    public static IReadOnlyList<OperationDeclaration> All
    {
        get
        {
            Assembly[] assemblies;
            lock (Gate) assemblies = [.. Included];

            return
            [
                .. assemblies
                    .SelectMany(static assembly => assembly.GetTypes())
                    .SelectMany(static type => type.GetFields(BindingFlags.Public | BindingFlags.Static))
                    .Where(static member => member.FieldType == typeof(OperationDeclaration) && member.IsInitOnly)
                    .Select(static member => (OperationDeclaration)member.GetValue(null)!)
                    .OrderBy(static operation => operation.Name, StringComparer.Ordinal)
            ];
        }
    }

    /// <summary>The operation by name, or null.</summary>
    public static OperationDeclaration? Named(string name) =>
        All.FirstOrDefault(operation => string.Equals(operation.Name, name, StringComparison.Ordinal));
}
