namespace TokkDb.LLM.Application.Databases;

public sealed class EditorFieldViewModel : BindableObject
{
    private string? _value;

    public EditorFieldViewModel(
        string name,
        string type,
        string? description,
        bool readOnly,
        string? value)
    {
        Name = name;
        Type = type;
        Description = description ?? name;
        IsEditable = !readOnly;
        _value = value;
    }

    public string Name { get; }

    public string Type { get; }

    public string Description { get; }

    public bool IsEditable { get; }

    public string? Value
    {
        get => _value;

        set
        {
            if (_value == value)
                return;

            _value = value;

            OnPropertyChanged();
        }
    }


    public string? ValidationMessage { get; set; }


    public bool HasValidationError =>
        !string.IsNullOrWhiteSpace(ValidationMessage);
}
