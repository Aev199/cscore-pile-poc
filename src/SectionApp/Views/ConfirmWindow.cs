using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;

namespace SectionApp.Views;

/// <summary>A question with the facts to check and two answers (the forces of PLAXIS against Output).</summary>
public sealed class ConfirmWindow : Window
{
    public ConfirmWindow(string title, string text, string yes, string no)
    {
        Title = title;
        Width = 560; SizeToContent = SizeToContent.Height; CanResize = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        var ok = new Button { Content = yes, Classes = { "primary" }, IsDefault = true };
        var cancel = new Button { Content = no, IsCancel = true };
        ok.Click += (_, _) => Close(true);
        cancel.Click += (_, _) => Close(false);
        Content = new StackPanel
        {
            Margin = new Thickness(20), Spacing = 16,
            Children =
            {
                new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap, FontSize = 12.5, LineHeight = 19 },
                new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right, Children = { cancel, ok } },
            },
        };
    }
}
