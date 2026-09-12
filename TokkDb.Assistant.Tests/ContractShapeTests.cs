using System.Reflection;
using TokkDb.Assistant.Storage;

namespace TokkDb.Assistant.Tests;

/// <summary>
/// SC-1 and SC-2, asserted rather than trusted.
///
/// SC-1 wants a build-time check or a test that the contract references nothing from
/// <c>TokkDb.LLM.*</c>. SC-2 wants a reflection test over the definition type listing exactly
/// its logical members, and it forbids anything physical being <i>reachable</i> from it - which
/// a list of the definition's own members cannot show on its own, because the physical thing
/// would arrive one hop away, on a column or inside a display rule. So both are here: the exact
/// member lists, and a walk of everything the definitions can reach.
/// </summary>
public sealed class ContractShapeTests
{
    private static readonly Assembly Contract = typeof(IStorage).Assembly;

    /// <summary>
    /// SC-1. The assistant's contract stands alone: not on <c>TokkDb.LLM.*</c>, which D-1
    /// forbids, and not on the engine either, which is what makes an in-memory implementation
    /// and a schema digest for a model possible without either of them dragging in a page file.
    /// </summary>
    [Fact]
    public void The_contract_references_no_other_TokkDb_assembly()
    {
        var referenced = Contract.GetReferencedAssemblies()
            .Select(static assembly => assembly.Name ?? "")
            .Where(static name => name.StartsWith("TokkDb", StringComparison.Ordinal))
            .ToArray();

        Assert.Empty(referenced);
    }

    /// <summary>SC-2, the list. These five members are the collection definition, and nothing else is.</summary>
    [Fact]
    public void A_collection_definition_carries_exactly_the_logical_members()
    {
        Assert.Equal(
            ["Columns", "DisplayRule", "Metadata", "Name", "Purpose"],
            PublicProperties(typeof(CollectionDefinition)));
    }

    [Fact]
    public void A_column_definition_carries_exactly_the_logical_members()
    {
        Assert.Equal(
            ["DefaultValue", "Name", "Purpose", "ReadOnly", "Required", "Type", "Unique"],
            PublicProperties(typeof(ColumnDefinition)));
    }

    [Fact]
    public void A_record_carries_its_identity_its_collection_and_its_values_and_nothing_else()
    {
        Assert.Equal(
            ["CollectionName", "Fields", "Id"],
            PublicProperties(typeof(StorageRecord)));
    }

    /// <summary>
    /// SC-2's real demand: nothing physical is <i>reachable</i>. Everything a definition can
    /// reach is either part of this contract or part of the framework. A page, a chain pointer,
    /// an index root and a record count all live in the engine's own assemblies, so a graph with
    /// no type from any other <c>TokkDb</c> assembly in it is a graph with none of them in it -
    /// and the test does not have to guess at their names to say so.
    /// </summary>
    [Theory]
    [InlineData(typeof(CollectionDefinition))]
    [InlineData(typeof(ColumnDefinition))]
    [InlineData(typeof(StorageRecord))]
    [InlineData(typeof(DisplayRule))]
    [InlineData(typeof(IStorage))]
    public void Nothing_outside_the_contract_is_reachable_from(Type root)
    {
        var visited = new HashSet<Type>();
        var strangers = new List<string>();

        Walk(root, visited, strangers);

        Assert.Empty(strangers);
    }

    /// <summary>
    /// The same prohibition said the other way round, and scoped the way SC-2 scopes it: not
    /// over the whole assembly, but over everything a definition can reach. SC-2 names four
    /// things; the rest below are the same mistake wearing other words.
    ///
    /// <b>A query result is deliberately not a definition</b> and is deliberately not walked
    /// here. <c>QueryCost</c> reports pages read and records examined on purpose - UI-4 and TR-3
    /// want exactly those numbers, and a caller who can read the records without being able to
    /// say what reading them cost cannot tell a query that will still work at ten thousand
    /// records from one that will not. The prohibition is on a definition carrying a page
    /// number, not on anything ever mentioning one: a definition's truth would expire, and a
    /// measurement's is about the moment it was taken.
    /// </summary>
    [Fact]
    public void Nothing_reachable_from_a_definition_is_named_after_anything_physical()
    {
        string[] physical =
        [
            "page", "pointer", "chain", "indexroot", "rootpage", "recordcount", "count",
            "offset", "slot", "address", "pageid", "lsn", "checksum", "bytes", "length",
            "capacity", "freespace", "segment", "extent", "btree", "node", "schemaversion"
        ];

        var reachable = new HashSet<Type>();
        Walk(typeof(CollectionDefinition), reachable, []);
        Walk(typeof(ColumnDefinition), reachable, []);
        Walk(typeof(StorageRecord), reachable, []);
        Walk(typeof(DisplayRule), reachable, []);

        var offenders = new List<string>();

        foreach (var type in reachable.Where(static type => type.Assembly == Contract))
        {
            foreach (var member in type.GetMembers(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly))
            {
                if (member is MethodBase { IsSpecialName: true }) continue;

                var squashed = member.Name.Replace("_", "", StringComparison.Ordinal).ToLowerInvariant();
                foreach (var word in physical)
                {
                    // MaxTemplateLength is a limit on a string the user typed, not on a buffer,
                    // and Segments are pieces of a template. Everything else is fair game.
                    if (squashed.Contains(word, StringComparison.Ordinal) &&
                        !(type == typeof(DisplayRule) && member.Name is "MaxTemplateLength" or "Segments"))
                    {
                        offenders.Add($"{type.Name}.{member.Name} contains '{word}'");
                    }
                }
            }
        }

        Assert.Empty(offenders);
        Assert.Contains(typeof(ColumnDefinition), reachable);
        Assert.Contains(typeof(DisplaySegment), reachable);
    }

    /// <summary>
    /// A definition is immutable once built. Not a requirement in as many words, but the reason
    /// SC-2's prohibition holds over time: a definition nobody can reach into is a definition
    /// that cannot acquire a record count in a later phase by someone adding a setter.
    /// </summary>
    [Theory]
    [InlineData(typeof(CollectionDefinition))]
    [InlineData(typeof(ColumnDefinition))]
    [InlineData(typeof(StorageRecord))]
    [InlineData(typeof(DisplayRule))]
    public void A_definition_cannot_be_changed_after_it_is_built(Type definition)
    {
        var settable = definition
            .GetProperties(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
            .Where(static property => property.CanWrite)
            .Select(static property => property.Name)
            .ToArray();

        Assert.Empty(settable);

        Assert.Empty(definition.GetFields(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly));
    }

    private static string[] PublicProperties(Type type) =>
        type.GetProperties(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
            .Where(static property => property.GetIndexParameters().Length == 0)
            .Select(static property => property.Name)
            .Order(StringComparer.Ordinal)
            .ToArray();

    private static void Walk(Type type, HashSet<Type> visited, List<string> strangers)
    {
        if (type == typeof(void) || type.IsGenericParameter) return;

        if (type.IsByRef || type.IsPointer || type.IsArray)
        {
            Walk(type.GetElementType()!, visited, strangers);
            return;
        }

        if (Nullable.GetUnderlyingType(type) is { } underlying)
        {
            Walk(underlying, visited, strangers);
            return;
        }

        if (type.IsGenericType)
        {
            // A dictionary of definitions is a way of reaching a definition, so the arguments
            // are walked as well as the container.
            foreach (var argument in type.GetGenericArguments()) Walk(argument, visited, strangers);
            type = type.GetGenericTypeDefinition();
        }

        if (!visited.Add(type)) return;

        if (type.IsPrimitive || type.IsEnum) return;

        var assembly = type.Assembly.GetName().Name ?? "";

        if (type.Assembly != Contract)
        {
            // A type from another TokkDb assembly is the failure this test exists for: a page, a
            // chain pointer, an index root and a record count all live in one of them, so the
            // test does not have to guess at their names to catch them.
            if (assembly.StartsWith("TokkDb", StringComparison.Ordinal))
            {
                strangers.Add($"{type.FullName} from {assembly}");
            }

            // Framework types are where the walk stops. The contract is allowed to speak in
            // strings and dictionaries, and following System.String inwards proves nothing.
            return;
        }

        foreach (var property in type.GetProperties(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly))
        {
            Walk(property.PropertyType, visited, strangers);
            foreach (var parameter in property.GetIndexParameters()) Walk(parameter.ParameterType, visited, strangers);
        }

        foreach (var method in type.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly))
        {
            Walk(method.ReturnType, visited, strangers);
            foreach (var parameter in method.GetParameters()) Walk(parameter.ParameterType, visited, strangers);
        }

        foreach (var constructor in type.GetConstructors(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly))
        {
            foreach (var parameter in constructor.GetParameters()) Walk(parameter.ParameterType, visited, strangers);
        }

        foreach (var nested in type.GetNestedTypes(BindingFlags.Public)) Walk(nested, visited, strangers);
    }
}
