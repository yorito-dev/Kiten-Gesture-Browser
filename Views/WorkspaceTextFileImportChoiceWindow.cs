using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace TabBrowser.Views;

internal enum WorkspaceTextFileImportChoice
{
    Cancel,
    ImportAsNote,
    PlaceAsFile
}

internal sealed class WorkspaceTextFileImportChoiceWindow : Window
{
    public WorkspaceTextFileImportChoiceWindow(int textFileCount)
    {
        Title = "テキストファイルの配置";
        Width = 440;
        Height = 190;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = CreateBrush(16, 17, 20);
        PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape)
            {
                Choice = WorkspaceTextFileImportChoice.Cancel;
                Close();
                e.Handled = true;
            }
        };

        var root = new Grid { Margin = new Thickness(16) };
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        Content = root;

        var title = new TextBlock
        {
            Text = textFileCount == 1
                ? "テキストファイルをどの形式で配置しますか？"
                : $"{textFileCount:N0}件のテキストファイルをどの形式で配置しますか？",
            Foreground = CreateBrush(243, 244, 246),
            FontSize = 15,
            FontWeight = FontWeights.SemiBold
        };
        root.Children.Add(title);

        var description = new TextBlock
        {
            Text = "メモとして取り込んでも、元のファイルは変更・削除されません。",
            Margin = new Thickness(0, 10, 0, 14),
            Foreground = CreateBrush(156, 163, 175),
            FontSize = 12,
            TextWrapping = TextWrapping.Wrap
        };
        Grid.SetRow(description, 1);
        root.Children.Add(description);

        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right
        };
        Grid.SetRow(buttons, 2);
        root.Children.Add(buttons);
        buttons.Children.Add(CreateButton("メモとして取り込む", WorkspaceTextFileImportChoice.ImportAsNote));
        buttons.Children.Add(CreateButton("ファイルとして配置", WorkspaceTextFileImportChoice.PlaceAsFile));
        buttons.Children.Add(CreateButton("キャンセル", WorkspaceTextFileImportChoice.Cancel));
    }

    public WorkspaceTextFileImportChoice Choice { get; private set; } = WorkspaceTextFileImportChoice.Cancel;

    private Button CreateButton(string text, WorkspaceTextFileImportChoice choice)
    {
        var button = new Button
        {
            Content = text,
            Height = 30,
            MinWidth = choice == WorkspaceTextFileImportChoice.Cancel ? 78 : 126,
            Margin = new Thickness(6, 0, 0, 0),
            Padding = new Thickness(10, 0, 10, 0),
            Foreground = CreateBrush(243, 244, 246),
            Background = CreateBrush(35, 38, 45),
            BorderBrush = CreateBrush(52, 56, 66),
            BorderThickness = new Thickness(1),
            Cursor = Cursors.Hand
        };
        button.Click += (_, _) =>
        {
            Choice = choice;
            Close();
        };
        return button;
    }

    private static SolidColorBrush CreateBrush(byte r, byte g, byte b)
    {
        var brush = new SolidColorBrush(Color.FromRgb(r, g, b));
        brush.Freeze();
        return brush;
    }
}
