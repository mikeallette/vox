using System.Windows;
using System.Windows.Controls;

namespace Vox;

/// <summary>Small dialog: one text box per speaker label found in a transcript ("Speaker 1" -> "Bob").</summary>
public sealed class RenameSpeakersWindow : Window
{
    readonly List<(string From, TextBox Box)> _rows = new();
    public Dictionary<string, string> Renames { get; } = new();

    public RenameSpeakersWindow(IEnumerable<string> speakers)
    {
        Title = "Rename speakers";
        SizeToContent = SizeToContent.WidthAndHeight;
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        FontFamily = new System.Windows.Media.FontFamily("Segoe UI");
        FontSize = 14;

        var root = new StackPanel { Margin = new Thickness(20), MinWidth = 380 };
        root.Children.Add(new TextBlock
        {
            Text = "Type a name for each voice. Leave a box as it is to keep that label.",
            TextWrapping = TextWrapping.Wrap, MaxWidth = 380, Margin = new Thickness(0, 0, 0, 12)
        });
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        int r = 0;
        foreach (var s in speakers)
        {
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            var label = new TextBlock { Text = s, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 4, 12, 4) };
            var box = new TextBox { Text = s, Padding = new Thickness(6, 4, 6, 4), Margin = new Thickness(0, 4, 0, 4), MinWidth = 220 };
            Grid.SetRow(label, r); Grid.SetRow(box, r); Grid.SetColumn(box, 1);
            grid.Children.Add(label); grid.Children.Add(box);
            _rows.Add((s, box)); r++;
        }
        root.Children.Add(grid);

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 16, 0, 0) };
        var ok = new Button { Content = "Rename", IsDefault = true, Padding = new Thickness(18, 6, 18, 6), Margin = new Thickness(0, 0, 8, 0) };
        var cancel = new Button { Content = "Cancel", IsCancel = true, Padding = new Thickness(18, 6, 18, 6) };
        ok.Click += (_, _) =>
        {
            foreach (var (from, box) in _rows)
            {
                var to = box.Text.Trim();
                if (to.Length > 0 && to != from) Renames[from] = to;
            }
            DialogResult = true;
        };
        buttons.Children.Add(ok); buttons.Children.Add(cancel);
        root.Children.Add(buttons);
        Content = root;
        Loaded += (_, _) => { if (_rows.Count > 0) { _rows[0].Box.Focus(); _rows[0].Box.SelectAll(); } };
    }
}
