using TokkDb.Assistant.Application.Chat;

namespace TokkDb.Assistant.Application.Shell;

/// <summary>The conversations, most recently active first, and the way to start one (UI-1, SC-10).</summary>
public sealed class ConversationList : Grid
{
    private readonly ChatViewModel _model;
    private readonly VerticalStackLayout _list;

    public ConversationList(ChatViewModel model)
    {
        _model = model;
        BackgroundColor = Theme.Panel;
        RowDefinitions = [new RowDefinition(GridLength.Auto), new RowDefinition(GridLength.Star)];

        var newChat = Theme.Action("+ New chat");
        newChat.BackgroundColor = Theme.Panel;
        newChat.BorderColor = Theme.Border;
        newChat.BorderWidth = 1;
        newChat.Margin = new Thickness(12);
        newChat.HorizontalOptions = LayoutOptions.Fill;
        newChat.Clicked += (_, _) => _model.NewChat();
        SemanticProperties.SetDescription(newChat, "Start a new conversation");
        this.Add(newChat, 0, 0);

        _list = new VerticalStackLayout { Spacing = 2, Padding = new Thickness(8, 0, 8, 8) };
        SemanticProperties.SetDescription(_list, "Your conversations, most recent first");
        this.Add(new ScrollView { Content = _list }, 0, 1);

        _model.Conversations.CollectionChanged += (_, _) => MainThread.BeginInvokeOnMainThread(Render);
        _model.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(ChatViewModel.Selected)) MainThread.BeginInvokeOnMainThread(Render); };
        Render();
    }

    private void Render()
    {
        _list.Clear();
        foreach (var conversation in _model.Conversations)
        {
            var selected = _model.Selected?.Id == conversation.Id;
            var block = new VerticalStackLayout { Spacing = 2, Padding = new Thickness(10, 8), BackgroundColor = selected ? Theme.Hover : Theme.Panel };
            block.Add(new Label { Text = conversation.Title, FontFamily = selected ? Theme.Semibold : Theme.Regular, FontSize = Theme.Small, TextColor = selected ? Theme.Bright : Theme.Text, LineBreakMode = LineBreakMode.TailTruncation });
            block.Add(Theme.Text11(conversation.When, Theme.Dimmer));
            var tap = new TapGestureRecognizer();
            tap.Tapped += (_, _) => _model.Open(conversation);
            block.GestureRecognizers.Add(tap);
            SemanticProperties.SetDescription(block, $"{conversation.Title}, {conversation.When}");
            _list.Add(block);
        }
    }
}
