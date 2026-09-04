namespace TokkDb.LLM.Application.Databases;

public partial class DatabasePage : ContentPage
{
    public DatabasePage(DatabaseViewModel viewModel)
    {
        InitializeComponent();
        BindingContext = viewModel;
    }
}

