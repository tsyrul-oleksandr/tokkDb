using Microsoft.Extensions.Logging;
using System.Collections.ObjectModel;
using System.Windows.Input;
using TokkDb.LLM.Core;
using TokkDb.LLM.Storage;

namespace TokkDb.LLM.Application.Databases;

public sealed class DatabaseViewModel : BindableObject
{
    private readonly IStorageRuntime _storageRuntime;
    private readonly ILogger<DatabaseViewModel> _logger;

    private string? _selectedBackend;
    private CollectionDefinition? _selectedCollection;
    private RecordViewModel? _selectedRecord;
    private string? _searchText;

    public DatabaseViewModel(
        IStorageRuntime storageRuntime,
        IRecordNavigationService recordNavigation,
        ILogger<DatabaseViewModel> logger)
    {
        _storageRuntime = storageRuntime;
        _logger = logger;

        // The chat raises navigation requests through the abstraction; this page
        // reacts to them. There is no direct reference in either direction.
        recordNavigation.RecordNavigationRequested += (_, request) => OpenRecord(request);

        RefreshCommand = new Command(Refresh);
        NewRecordCommand = new Command(NewRecord);
        SaveRecordCommand = new Command(SaveRecord);
        DeleteRecordCommand = new Command(DeleteRecord);

        Load();
    }


    public ObservableCollection<string> Backends { get; } = new();

    public ObservableCollection<CollectionDefinition> Collections { get; } = new();

    public ObservableCollection<ColumnViewModel> Columns { get; } = new();

    public ObservableCollection<string> Relations { get; } = new();

    public ObservableCollection<RecordViewModel> Records { get; } = new();

    public ObservableCollection<RecordViewModel> FilteredRecords { get; } = new();

    public ObservableCollection<EditorFieldViewModel> EditorFields { get; } = new();


    public ICommand RefreshCommand { get; }

    public ICommand NewRecordCommand { get; }

    public ICommand SaveRecordCommand { get; }

    public ICommand DeleteRecordCommand { get; }


    public string? SelectedBackend
    {
        get => _selectedBackend;
        set
        {
            if (_selectedBackend == value)
                return;

            _selectedBackend = value;

            OnPropertyChanged();

            ChangeBackend();
        }
    }


    public CollectionDefinition? SelectedCollection
    {
        get => _selectedCollection;
        set
        {
            if (_selectedCollection == value)
                return;

            _selectedCollection = value;

            OnPropertyChanged();

            LoadCollection();
        }
    }


    public RecordViewModel? SelectedRecord
    {
        get => _selectedRecord;
        set
        {
            if (_selectedRecord == value)
                return;

            _selectedRecord = value;

            OnPropertyChanged();

            LoadRecordIntoEditor();
            UpdateEditorState();
        }
    }


    public string? SearchText
    {
        get => _searchText;
        set
        {
            if (_searchText == value)
                return;

            _searchText = value;

            OnPropertyChanged();

            ApplyFilter();
        }
    }


    public string RecordsCountText =>
        $"{FilteredRecords.Count} records";


    public string EditorTitle =>
        SelectedRecord is null
            ? "New Record"
            : "Edit Record";


    public string EditorModeText =>
        SelectedRecord is null
            ? "NEW"
            : "EDIT";


    public bool CanDeleteRecord =>
        SelectedRecord is not null;


    private string? _validationErrors;

    public string? ValidationErrors
    {
        get => _validationErrors;
        set
        {
            _validationErrors = value;

            OnPropertyChanged();

            OnPropertyChanged(nameof(HasValidationErrors));
        }
    }


    public bool HasValidationErrors =>
        !string.IsNullOrWhiteSpace(ValidationErrors);


    private void Load()
    {
        LoadBackends();
        LoadCollections();
    }


    private void LoadBackends()
    {
        Backends.Clear();

        foreach (var backend in _storageRuntime.Backends)
        {
            Backends.Add(backend.ToString());
        }

        SelectedBackend = Settings.Settings.Instance.StorageType.ToString();
    }


    private void LoadCollections()
    {
        Collections.Clear();

        var definitions =
            _storageRuntime.Storage
                .GetCollectionDefinitions()
                .OrderBy(x => x.Name);

        foreach (var definition in definitions)
        {
            Collections.Add(definition);
        }

        SelectedCollection = Collections.FirstOrDefault();
    }


    private void ChangeBackend()
    {
        if (string.IsNullOrWhiteSpace(SelectedBackend))
            return;

        if (!Enum.TryParse<StorageBackend>(
                SelectedBackend,
                true,
                out var backend))
        {
            return;
        }

        _storageRuntime.SwitchBackend(backend);

        LoadCollections();
    }


    private void LoadCollection()
    {
        Columns.Clear();
        Relations.Clear();
        Records.Clear();
        FilteredRecords.Clear();

        if (SelectedCollection is null)
            return;


        foreach (var column in SelectedCollection.Columns)
        {
            Columns.Add(
                new ColumnViewModel(
                    column.Name,
                    column.Type.ToString(),
                    column.Description,
                    column.Unique,
                    column.ReadOnly));
        }


        foreach (var relation in _storageRuntime.Storage
                     .GetRelations()
                     .Where(x =>
                         string.Equals(
                             x.SourceCollection,
                             SelectedCollection.Name,
                             StringComparison.OrdinalIgnoreCase)
                         ||
                         string.Equals(
                             x.TargetCollection,
                             SelectedCollection.Name,
                             StringComparison.OrdinalIgnoreCase)))
        {
            Relations.Add(
                $"{relation.SourceCollection}.{relation.SourceColumn} → " +
                $"{relation.TargetCollection}.{relation.TargetColumn}");
        }


        LoadRecords();

        NewRecord();
    }


    /// <summary>
    /// Applies a record navigation request: selects the collection, filters to
    /// the record and selects it so the user can see which one was opened.
    /// Invalid requests are logged and ignored rather than throwing into the UI.
    /// </summary>
    public void OpenRecord(OpenRecordRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        MainThread.BeginInvokeOnMainThread(() =>
        {
            try
            {
                var collection = Collections.FirstOrDefault(candidate =>
                    string.Equals(candidate.Name, request.CollectionName, StringComparison.OrdinalIgnoreCase));

                if (collection is null)
                {
                    _logger.LogWarning(
                        "Record navigation ignored, unknown collection. CollectionName: {CollectionName}",
                        request.CollectionName);
                    return;
                }

                if (!ReferenceEquals(SelectedCollection, collection))
                {
                    SelectedCollection = collection;
                }

                // Filter down to the requested record so it is unambiguous.
                SearchText = request.RecordId;

                var target = FilteredRecords.FirstOrDefault(candidate =>
                    string.Equals(
                        candidate.Record.Id.ToString(),
                        request.RecordId,
                        StringComparison.OrdinalIgnoreCase))
                    ?? Records.FirstOrDefault(candidate =>
                        string.Equals(
                            candidate.Record.Id.ToString(),
                            request.RecordId,
                            StringComparison.OrdinalIgnoreCase));

                if (target is null)
                {
                    _logger.LogWarning(
                        "Record navigation could not locate the record. CollectionName: {CollectionName}, RecordId: {RecordId}",
                        request.CollectionName,
                        request.RecordId);
                    return;
                }

                SelectedRecord = target;

                _logger.LogInformation(
                    "Record opened on the database page. CollectionName: {CollectionName}, RecordId: {RecordId}",
                    request.CollectionName,
                    request.RecordId);
            }
            catch (Exception ex)
            {
                _logger.LogError(
                    ex,
                    "Record navigation failed. CollectionName: {CollectionName}, RecordId: {RecordId}",
                    request.CollectionName,
                    request.RecordId);
            }
        });
    }

    private void LoadRecords()
    {
        Records.Clear();

        if (SelectedCollection is null)
            return;


        foreach (var record in
                 _storageRuntime.Storage.GetAll(SelectedCollection.Name))
        {
            Records.Add(
                new RecordViewModel(
                    record,
                    BuildRecordSummary(record)));
        }

        ApplyFilter();
    }


    private void ApplyFilter()
    {
        FilteredRecords.Clear();

        var search =
            SearchText?.Trim();

        var records =
            string.IsNullOrWhiteSpace(search)
                ? Records
                : Records.Where(x =>
                    x.Summary.Contains(
                        search,
                        StringComparison.OrdinalIgnoreCase)
                    // Allows filtering straight to one record by id, which is
                    // how chat navigation locates the record it opened.
                    || x.Record.Id.ToString().Contains(
                        search,
                        StringComparison.OrdinalIgnoreCase));


        foreach (var record in records)
        {
            FilteredRecords.Add(record);
        }

        OnPropertyChanged(nameof(RecordsCountText));
    }


    private void NewRecord()
    {
        SelectedRecord = null;

        ValidationErrors = null;

        EditorFields.Clear();

        if (SelectedCollection is null)
            return;


        foreach (var column in SelectedCollection.Columns)
        {
            EditorFields.Add(
                new EditorFieldViewModel(
                    column.Name,
                    column.Type.ToString(),
                    column.Description,
                    column.ReadOnly,
                    column.DefaultValue?.ToString()));
        }

        UpdateEditorState();
    }


    private void LoadRecordIntoEditor()
    {
        if (SelectedRecord is null)
        {
            NewRecord();

            return;
        }


        EditorFields.Clear();

        foreach (var column in SelectedCollection!.Columns)
        {
            SelectedRecord.Record.Fields.TryGetValue(
                column.Name,
                out var value);

            EditorFields.Add(
                new EditorFieldViewModel(
                    column.Name,
                    column.Type.ToString(),
                    column.Description,
                    column.ReadOnly,
                    value?.ToString()));
        }
    }


    private void SaveRecord()
    {
        // Тут переносимо існуючу логіку:
        //
        // BuildFieldsFromEditor(...)
        // Storage.Create(...)
        // Storage.Update(...)
        //
        // після цього:
        //
        // LoadRecords();
        // NewRecord();
    }


    private void DeleteRecord()
    {
        if (SelectedCollection is null ||
            SelectedRecord is null)
        {
            return;
        }


        _storageRuntime.Storage.Delete(
            SelectedCollection.Name,
            SelectedRecord.Record.Id);


        LoadRecords();

        NewRecord();
    }


    private void Refresh()
    {
        LoadCollections();
    }


    private void UpdateEditorState()
    {
        OnPropertyChanged(nameof(EditorTitle));
        OnPropertyChanged(nameof(EditorModeText));
        OnPropertyChanged(nameof(CanDeleteRecord));
    }


    private string BuildRecordSummary(StorageRecord record)
    {
        if (SelectedCollection is null)
            return string.Empty;


        return string.Join(
            "  |  ",
            SelectedCollection.Columns.Select(column =>
            {
                record.Fields.TryGetValue(
                    column.Name,
                    out var value);

                return $"{column.Name}: {value}";
            }));
    }
}
