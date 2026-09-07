using TokkDb.Documents;
using TokkDb.Documents.Values;
using TokkDb.LLM.Core;
using TokkDb.Pages;
using TokkDb.Values;

namespace TokkDb.LLM.Storage.Engine;

/// <summary>
/// D-4: a semantic type as a document in <c>_semanticTypes</c>.
///
/// The mapping lives here rather than in the engine because a semantic type is an
/// application concept — a base type, a hierarchy, rules a value must satisfy — and the
/// engine stores documents without knowing what any of them mean. It is written out field by
/// field rather than serialized as an opaque blob for the reason D-4 gives: a field added to
/// a definition should be a field added to a document, readable by anything that reads
/// documents, not an opaque string only this class can open.
/// </summary>
public static class SemanticTypeDocument
{
    public const string IdField = "id";
    public const string NameField = "name";
    public const string DisplayNameField = "displayName";
    public const string DescriptionField = "description";
    public const string BaseTypeField = "baseType";
    public const string ParentTypeField = "parentType";
    public const string AliasesField = "aliases";
    public const string ExamplesField = "examples";
    public const string ValidationPatternField = "validationPattern";
    public const string ValidationPatternsField = "validationPatterns";
    public const string NormalizationRulesField = "normalizationRules";
    public const string ValidationsField = "validations";

    public const string ValidationKindField = "kind";
    public const string ValidationPatternValueField = "pattern";
    public const string ValidationLengthField = "length";
    public const string ValidationValueField = "value";

    /// <summary>
    /// What a document of this collection looks like, so that the catalogue describes it the
    /// way it describes its own (DC-7) rather than the shape being readable only here.
    /// </summary>
    public static List<ColumnDescriptor> CreateColumns() =>
    [
        new(IdField, ValueTypeEnum.Ulid, "Identifier of the semantic type", unique: true, readOnly: true),
        new(NameField, ValueTypeEnum.String, "Name the type is referred to by", unique: true),
        new(DisplayNameField, ValueTypeEnum.String, "Name shown to a reader"),
        new(DescriptionField, ValueTypeEnum.String, "What values of this type mean"),
        new(BaseTypeField, ValueTypeEnum.String, "The column type it refines"),
        new(ParentTypeField, ValueTypeEnum.String, "The semantic type it derives from, if any"),
        new(AliasesField, ValueTypeEnum.Array, "Other names that resolve to this type"),
        new(ExamplesField, ValueTypeEnum.Array, "Example values"),
        new(ValidationPatternField, ValueTypeEnum.String, "Single pattern, kept for definitions written that way"),
        new(ValidationPatternsField, ValueTypeEnum.Array, "Patterns a value must match"),
        new(NormalizationRulesField, ValueTypeEnum.Array, "Rules applied to a value before it is stored"),
        new(ValidationsField, ValueTypeEnum.Array, "The canonical rule list")
    ];

    public static ObjectDocument Write(Ulid id, SemanticTypeDefinition definition)
    {
        var document = new ObjectDocument();
        document.SetIdentifierValue(new UlidDocumentValue(id));
        document.SetValue(new ObjectDocumentValue(new Dictionary<string, IDocumentValue>
        {
            [IdField] = new UlidDocumentValue(id),
            [NameField] = new StringDocumentValue(definition.Name),
            [DisplayNameField] = new StringDocumentValue(definition.DisplayName),
            [DescriptionField] = new StringDocumentValue(definition.Description),
            // The name rather than the number, so renumbering ColumnType cannot silently
            // retype every semantic type in an existing database.
            [BaseTypeField] = new StringDocumentValue(definition.BaseType.ToString()),
            [ParentTypeField] = new StringDocumentValue(definition.ParentType ?? string.Empty),
            [AliasesField] = Strings(definition.Aliases),
            [ExamplesField] = Strings(definition.Examples),
            [ValidationPatternField] = new StringDocumentValue(definition.ValidationPattern ?? string.Empty),
            [ValidationPatternsField] = Strings(definition.ValidationPatterns),
            [NormalizationRulesField] = Strings(definition.NormalizationRules),
            [ValidationsField] = new ArrayDocumentValue((definition.Validations ?? [])
                .Select(WriteValidation).ToArray())
        }));
        return document;
    }

    public static SemanticTypeDefinition Read(ObjectDocument document)
    {
        var value = (ObjectDocumentValue)document.Value;
        return new SemanticTypeDefinition(
            ReadString(value, NameField),
            ReadString(value, DisplayNameField),
            ReadString(value, DescriptionField),
            Enum.TryParse<ColumnType>(ReadString(value, BaseTypeField), out var baseType)
                ? baseType
                : ColumnType.String,
            NullIfEmpty(ReadString(value, ParentTypeField)),
            ReadStrings(value, AliasesField),
            ReadStrings(value, ExamplesField),
            NullIfEmpty(ReadString(value, ValidationPatternField)),
            ReadStrings(value, ValidationPatternsField),
            ReadStrings(value, NormalizationRulesField),
            ReadArray(value, ValidationsField).OfType<ObjectDocumentValue>().Select(ReadValidation).ToArray());
    }

    public static Ulid ReadId(ObjectDocument document) =>
        ((ObjectDocumentValue)document.Value).Values.GetValueOrDefault(IdField) is UlidDocumentValue id
            ? id.Value
            : default;

    public static string ReadName(ObjectDocument document) =>
        ReadString((ObjectDocumentValue)document.Value, NameField);

    // Length is absent rather than zero when the rule does not use it: zero is a length a
    // MinLength rule could legitimately carry, so writing it always would make "no minimum"
    // and "a minimum of nothing" the same document.
    private static IDocumentValue WriteValidation(SemanticValidation validation)
    {
        var fields = new Dictionary<string, IDocumentValue>
        {
            [ValidationKindField] = new StringDocumentValue(validation.Kind.ToString()),
            [ValidationPatternValueField] = new StringDocumentValue(validation.Pattern ?? string.Empty),
            [ValidationValueField] = new StringDocumentValue(validation.Value ?? string.Empty)
        };
        if (validation.Length is { } length)
        {
            fields[ValidationLengthField] = new IntDocumentValue(length);
        }

        return new ObjectDocumentValue(fields);
    }

    private static SemanticValidation ReadValidation(ObjectDocumentValue value) =>
        new(Enum.TryParse<SemanticValidationKind>(ReadString(value, ValidationKindField), out var kind)
                ? kind
                : SemanticValidationKind.Regex,
            NullIfEmpty(ReadString(value, ValidationPatternValueField)),
            value.Values.GetValueOrDefault(ValidationLengthField) is IntDocumentValue length
                ? length.Value
                : null,
            NullIfEmpty(ReadString(value, ValidationValueField)));

    private static ArrayDocumentValue Strings(IReadOnlyCollection<string>? values) =>
        new((values ?? []).Select(IDocumentValue (item) => new StringDocumentValue(item)).ToArray());

    // DC-7: a field the writer of the document did not know about reads as its default, which
    // is what lets a definition gain one without a migration.
    private static string ReadString(ObjectDocumentValue value, string field) =>
        value.Values.GetValueOrDefault(field) is StringDocumentValue text ? text.Value : string.Empty;

    private static IDocumentValue[] ReadArray(ObjectDocumentValue value, string field) =>
        value.Values.GetValueOrDefault(field) is ArrayDocumentValue array ? array.Values : [];

    private static string[] ReadStrings(ObjectDocumentValue value, string field) =>
        ReadArray(value, field).OfType<StringDocumentValue>().Select(item => item.Value).ToArray();

    private static string? NullIfEmpty(string text) => string.IsNullOrEmpty(text) ? null : text;
}
