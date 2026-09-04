#if IOS || MACCATALYST
using UIKit;
#endif

namespace TokkDb.LLM.Application.Controls;

/// <summary>
/// Read-only text that the user can select and copy.
///
/// MAUI's <see cref="Label"/> renders text that cannot be selected on any
/// platform, so chat content is displayed with this control instead. It is an
/// <see cref="Editor"/> locked into read-only mode and stripped of its native
/// chrome, which gives native partial selection and the platform's normal copy
/// action (context menu, Cmd/Ctrl+C, long-press) without writing a bespoke
/// text-rendering handler per platform.
///
/// Use it for message bodies. Short interactive affordances - a link, a status
/// word - stay as labels, because they are controls rather than prose.
/// </summary>
public sealed class SelectableLabel : Editor
{
    public SelectableLabel()
    {
        IsReadOnly = true;
        // Grow with the content so it lays out like a label.
        AutoSize = EditorAutoSizeOption.TextChanges;
        BackgroundColor = Colors.Transparent;
        MinimumHeightRequest = 0;
        Margin = new Thickness(0);
    }

    /// <summary>
    /// Removes the native editor chrome and enables selection without editing.
    /// Called once at startup; the mapper ignores ordinary editors such as the
    /// chat input box.
    /// </summary>
    public static void ConfigureHandler()
    {
        Microsoft.Maui.Handlers.EditorHandler.Mapper.AppendToMapping(
            nameof(SelectableLabel),
            (handler, view) =>
            {
                if (view is not SelectableLabel)
                {
                    return;
                }

#if IOS || MACCATALYST
                var textView = handler.PlatformView;
                textView.BackgroundColor = UIColor.Clear;
                // Selectable but not editable: selection and the copy menu work,
                // and no keyboard is raised.
                textView.Editable = false;
                textView.Selectable = true;
                textView.ScrollEnabled = false;
                textView.TextContainerInset = UIEdgeInsets.Zero;
                textView.TextContainer.LineFragmentPadding = 0;
#elif ANDROID
                var editText = handler.PlatformView;
                editText.Background = null;
                editText.SetPadding(0, 0, 0, 0);
                // Null key listener keeps the text selectable while preventing
                // editing and the soft keyboard.
                editText.KeyListener = null;
                editText.SetTextIsSelectable(true);
#elif WINDOWS
                var textBox = handler.PlatformView;
                textBox.IsReadOnly = true;
                textBox.BorderThickness = new Microsoft.UI.Xaml.Thickness(0);
                textBox.Background = null;
                textBox.Padding = new Microsoft.UI.Xaml.Thickness(0);
                textBox.MinHeight = 0;
#endif
            });
    }
}
