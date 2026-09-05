using TokkDb.Documents;
using TokkDb.Documents.Values;
using TokkDb.Pages.Records;
using TokkDb.Transactions;
using TokkDb.Values;

namespace TokkDb.Pages.Managers;

//D-4. What a collection carries besides its structure: how a record of it is displayed, and
//whatever key/value settings the application keeps about it.
//
//These are separate documents in separate system collections rather than fields of the
//catalogue document, and deliberately so. A display rule and an AI-derived note change often
//and for reasons that have nothing to do with the schema; putting them in the structural
//descriptor would make every such change rewrite the document that the data pages, the index
//roots and the free-space root all live in, and would tie a note about a collection to the
//version of its column set.
//
//Neither is interpreted here. A display rule is a string and a setting is a pair of strings;
//what they mean belongs to the application, which is the only thing that can say.
public class CollectionSettingsCatalog {
  private readonly TransactionManager _transactionManager;
  private DataPageManager _dataPageManager;

  private readonly Dictionary<string, DisplayRuleEntry> _displayRules = new(StringComparer.Ordinal);
  private readonly Dictionary<string, SettingsEntry> _settings = new(StringComparer.Ordinal);

  public CollectionSettingsCatalog(TransactionManager transactionManager) {
    _transactionManager = transactionManager;
  }

  public void SetDataPageManager(DataPageManager dataPageManager) {
    _dataPageManager = dataPageManager;
  }

  public void Initialize() {
    _displayRules.Clear();
    _settings.Clear();
    foreach (var document in ReadLive(SystemCollections.DisplayRules)) {
      var entry = DisplayRuleDocument.Read(document);
      _displayRules[entry.CollectionName] = entry;
    }
    foreach (var document in ReadLive(SystemCollections.Settings)) {
      var entry = SettingsDocument.Read(document);
      _settings[entry.CollectionName] = entry;
    }
  }

  //The template, or null when the collection has none. Never parsed here.
  public string GetDisplayRule(string collectionName) {
    return _displayRules.GetValueOrDefault(collectionName)?.Template;
  }

  public void SetDisplayRule(string collectionName, string template) {
    _transactionManager.RequireTransaction();
    if (template is null) {
      RemoveDisplayRule(collectionName);
      return;
    }
    if (_displayRules.TryGetValue(collectionName, out var existing)) {
      var previous = existing.Template;
      existing.Template = template;
      //The cache is wound back if the write does not happen. The transaction restores the
      //pages, and a cache still holding the value that was refused would answer with something
      //the file does not contain until the next reopen disagreed with it.
      try {
        Rewrite(SystemCollections.DisplayRules, existing.Id, DisplayRuleDocument.Write(existing));
      } catch {
        existing.Template = previous;
        throw;
      }
      return;
    }
    var entry = new DisplayRuleEntry {
      Id = RecordIdentity.Next(), CollectionName = collectionName, Template = template
    };
    _displayRules[collectionName] = entry;
    _dataPageManager.WriteRecord(SystemCollections.DisplayRules, RecordHeader.ForNewRecord(entry.Id),
      DisplayRuleDocument.Write(entry));
  }

  public IReadOnlyDictionary<string, string> GetMetadata(string collectionName) {
    return _settings.GetValueOrDefault(collectionName)?.Values
      ?? new Dictionary<string, string>(StringComparer.Ordinal);
  }

  public void SetMetadata(string collectionName, IReadOnlyDictionary<string, string> metadata) {
    _transactionManager.RequireTransaction();
    var values = new Dictionary<string, string>(metadata ?? new Dictionary<string, string>(),
      StringComparer.Ordinal);
    if (_settings.TryGetValue(collectionName, out var existing)) {
      var previous = existing.Values;
      existing.Values = values;
      try {
        Rewrite(SystemCollections.Settings, existing.Id, SettingsDocument.Write(existing));
      } catch {
        existing.Values = previous;
        throw;
      }
      return;
    }
    if (values.Count == 0) {
      return;
    }
    var entry = new SettingsEntry {
      Id = RecordIdentity.Next(), CollectionName = collectionName, Values = values
    };
    _settings[collectionName] = entry;
    try {
      _dataPageManager.WriteRecord(SystemCollections.Settings, RecordHeader.ForNewRecord(entry.Id),
        SettingsDocument.Write(entry));
    } catch {
      _settings.Remove(collectionName);
      throw;
    }
  }

  //Both documents of a collection that is going away, so nothing describes a collection that
  //no longer exists.
  public void Remove(string collectionName) {
    _transactionManager.RequireTransaction();
    RemoveDisplayRule(collectionName);
    if (_settings.Remove(collectionName, out var settings)) {
      Retire(SystemCollections.Settings, settings.Id);
    }
  }

  private void RemoveDisplayRule(string collectionName) {
    if (_displayRules.Remove(collectionName, out var entry)) {
      Retire(SystemCollections.DisplayRules, entry.Id);
    }
  }

  //A settings document grows and shrinks as entries are added, so it takes the same
  //in-place-or-move path a catalogue descriptor does. One that outgrows a whole page is
  //refused by the storage layer: growing a record into an overflow chain is ST-6 and not
  //implemented, so the settings of one collection have to fit a page.
  private void Rewrite(string collectionName, Ulid id, ObjectDocument document) {
    var row = _dataPageManager.FindLiveRow(collectionName, id);
    if (row is null) {
      _dataPageManager.WriteRecord(collectionName, RecordHeader.ForNewRecord(id), document);
      return;
    }
    var header = RecordHeader.ForNewRecord(id);
    if (_dataPageManager.CanUpdateRowInPlace(row.Value.Address, header, document)) {
      _dataPageManager.UpdateRow(row.Value.Address, header, document);
      return;
    }
    _dataPageManager.RewriteRow(collectionName, row.Value.Address, header, document);
  }

  private void Retire(string collectionName, Ulid id) {
    if (_dataPageManager.FindLiveRow(collectionName, id) is { } row) {
      _dataPageManager.RetireRow(collectionName, row.Address, RecordFlags.Deleted, RetentionPolicy.None);
    }
  }

  private IEnumerable<ObjectDocument> ReadLive(string collectionName) {
    foreach (var row in _dataPageManager.GetAllRows(collectionName)) {
      var record = StoredRecordUtilities.FromBuffer(_dataPageManager.ReadRecordBuffer(row));
      if (record.Header.IsLive) {
        yield return record.Document;
      }
    }
  }
}

public class DisplayRuleEntry {
  public Ulid Id { get; set; }
  public string CollectionName { get; set; } = string.Empty;
  public string Template { get; set; } = string.Empty;
}

public class SettingsEntry {
  public Ulid Id { get; set; }
  public string CollectionName { get; set; } = string.Empty;
  public Dictionary<string, string> Values { get; set; } = new(StringComparer.Ordinal);
}

public static class DisplayRuleDocument {
  public const string IdField = "id";
  public const string CollectionField = "collection";
  public const string TemplateField = "template";

  public static List<ColumnDescriptor> CreateColumns() {
    return [
      new ColumnDescriptor(IdField, ValueTypeEnum.Ulid, "Identifier of the rule", unique: true, readOnly: true),
      new ColumnDescriptor(CollectionField, ValueTypeEnum.String, "Collection the rule renders", unique: true),
      new ColumnDescriptor(TemplateField, ValueTypeEnum.String, "The template, uninterpreted by the engine")
    ];
  }

  public static ObjectDocument Write(DisplayRuleEntry entry) {
    var document = new ObjectDocument();
    document.SetIdentifierValue(new UlidDocumentValue(entry.Id));
    document.SetValue(new ObjectDocumentValue(new Dictionary<string, IDocumentValue> {
      [IdField] = new UlidDocumentValue(entry.Id),
      [CollectionField] = new StringDocumentValue(entry.CollectionName),
      [TemplateField] = new StringDocumentValue(entry.Template)
    }));
    return document;
  }

  public static DisplayRuleEntry Read(ObjectDocument document) {
    var value = (ObjectDocumentValue)document.Value;
    return new DisplayRuleEntry {
      Id = SystemDocumentFields.ReadUlid(value, IdField),
      CollectionName = SystemDocumentFields.ReadString(value, CollectionField),
      Template = SystemDocumentFields.ReadString(value, TemplateField)
    };
  }
}

public static class SettingsDocument {
  public const string IdField = "id";
  public const string CollectionField = "collection";
  public const string EntriesField = "entries";
  public const string KeyField = "key";
  public const string ValueField = "value";

  public static List<ColumnDescriptor> CreateColumns() {
    return [
      new ColumnDescriptor(IdField, ValueTypeEnum.Ulid, "Identifier of the settings document", unique: true,
        readOnly: true),
      new ColumnDescriptor(CollectionField, ValueTypeEnum.String, "Collection the settings belong to",
        unique: true),
      new ColumnDescriptor(EntriesField, ValueTypeEnum.Array, "Key and value pairs, uninterpreted by the engine")
    ];
  }

  public static ObjectDocument Write(SettingsEntry entry) {
    var document = new ObjectDocument();
    document.SetIdentifierValue(new UlidDocumentValue(entry.Id));
    document.SetValue(new ObjectDocumentValue(new Dictionary<string, IDocumentValue> {
      [IdField] = new UlidDocumentValue(entry.Id),
      [CollectionField] = new StringDocumentValue(entry.CollectionName),
      //Ordered, so that writing the same settings twice produces the same bytes and a
      //descriptor that did not change does not look as though it did.
      [EntriesField] = new ArrayDocumentValue(entry.Values
        .OrderBy(pair => pair.Key, StringComparer.Ordinal)
        .Select(IDocumentValue (pair) => new ObjectDocumentValue(new Dictionary<string, IDocumentValue> {
          [KeyField] = new StringDocumentValue(pair.Key),
          [ValueField] = new StringDocumentValue(pair.Value ?? string.Empty)
        })).ToArray())
    }));
    return document;
  }

  public static SettingsEntry Read(ObjectDocument document) {
    var value = (ObjectDocumentValue)document.Value;
    return new SettingsEntry {
      Id = SystemDocumentFields.ReadUlid(value, IdField),
      CollectionName = SystemDocumentFields.ReadString(value, CollectionField),
      Values = SystemDocumentFields.ReadArray(value, EntriesField)
        .OfType<ObjectDocumentValue>()
        .ToDictionary(pair => SystemDocumentFields.ReadString(pair, KeyField),
          pair => SystemDocumentFields.ReadString(pair, ValueField), StringComparer.Ordinal)
    };
  }
}

//DC-7: a field the writer did not know about reads as its default, so adding one is not a
//migration.
public static class SystemDocumentFields {
  public static string ReadString(ObjectDocumentValue value, string field) {
    return value.Values.GetValueOrDefault(field) is StringDocumentValue text ? text.Value : string.Empty;
  }

  public static Ulid ReadUlid(ObjectDocumentValue value, string field) {
    return value.Values.GetValueOrDefault(field) is UlidDocumentValue identifier ? identifier.Value : default;
  }

  public static IDocumentValue[] ReadArray(ObjectDocumentValue value, string field) {
    return value.Values.GetValueOrDefault(field) is ArrayDocumentValue array ? array.Values : [];
  }
}
