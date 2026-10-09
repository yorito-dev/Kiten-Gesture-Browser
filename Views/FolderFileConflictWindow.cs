using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using TabBrowser.Services.Workspaces;

namespace TabBrowser.Views;

internal enum FolderFileConflictChoice
{
    Cancel,
    Skip,
    KeepBoth,
    SkipAll,
    KeepBothAll
}

internal sealed class FolderFileConflictWindow : Window
{
    public FolderFileConflictWindow(
        ShellFileTransferConflict conflict,
        int currentNumber,
        int totalCount)
    {
        Title = "同名項目の確認";
        Width = 620;
        Height = 390;
        MinWidth = 520;
        MinHeight = 360;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = new SolidColorBrush(Color.FromRgb(16, 17, 20));

        var root = new Grid { Margin = new Thickness(16) };
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        Content = root;

        var title = new TextBlock
        {
            Text = $"同じ名前の項目があります（{currentNumber:N0} / {totalCount:N0}）",
            Foreground = new SolidColorBrush(Color.FromRgb(243, 244, 246)),
            FontSize = 15,
            FontWeight = FontWeights.SemiBold,
            Margin = new Thickness(0, 0, 0, 12)
        };
        root.Children.Add(title);

        var source = CreatePathBlock("コピー／移動元", conflict.SourcePath);
        Grid.SetRow(source, 1);
        root.Children.Add(source);

        var destination = CreatePathBlock("保存先", conflict.DestinationPath);
        destination.Margin = new Thickness(0, 10, 0, 14);
        Grid.SetRow(destination, 2);
        root.Children.Add(destination);

        var buttons = new Grid
        {
            VerticalAlignment = VerticalAlignment.Bottom
        };
        buttons.ColumnDefinitions.Add(new ColumnDefinition());
        buttons.ColumnDefinitions.Add(new ColumnDefinition());
        buttons.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        buttons.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        buttons.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        Grid.SetRow(buttons, 3);
        root.Children.Add(buttons);
        AddChoiceButton(buttons, "スキップ", FolderFileConflictChoice.Skip, row: 0, column: 0);
        AddChoiceButton(buttons, "別名で保持", FolderFileConflictChoice.KeepBoth, row: 0, column: 1);
        AddChoiceButton(buttons, "以降すべてスキップ", FolderFileConflictChoice.SkipAll, row: 1, column: 0);
        AddChoiceButton(buttons, "以降すべて別名で保持", FolderFileConflictChoice.KeepBothAll, row: 1, column: 1);
        var cancelButton = AddChoiceButton(
            buttons,
            "操作全体をキャンセル",
            FolderFileConflictChoice.Cancel,
            row: 2,
            column: 0,
            columnSpan: 2);
        cancelButton.MinWidth = 160;
        cancelButton.HorizontalAlignment = HorizontalAlignment.Left;
    }

    public FolderFileConflictChoice Choice { get; private set; } = FolderFileConflictChoice.Cancel;

    private Button AddChoiceButton(
        Grid buttons,
        string text,
        FolderFileConflictChoice choice,
        int row,
        int column,
        int columnSpan = 1)
    {
        var button = CreateChoiceButton(text, choice);
        button.Margin = column == 0
            ? new Thickness(0, 0, 4, 8)
            : new Thickness(4, 0, 0, 8);
        Grid.SetRow(button, row);
        Grid.SetColumn(button, column);
        Grid.SetColumnSpan(button, columnSpan);
        buttons.Children.Add(button);
        return button;
    }

    private static Border CreatePathBlock(string label, string path)
    {
        var panel = new StackPanel();
        panel.Children.Add(new TextBlock
        {
            Text = label,
            Foreground = new SolidColorBrush(Color.FromRgb(148, 163, 184)),
            FontSize = 11
        });
        panel.Children.Add(new TextBlock
        {
            Text = Path.GetFileName(path),
            Foreground = new SolidColorBrush(Color.FromRgb(229, 231, 235)),
            FontSize = 13,
            FontWeight = FontWeights.SemiBold,
            Margin = new Thickness(0, 3, 0, 0)
        });
        panel.Children.Add(new TextBlock
        {
            Text = path,
            Foreground = new SolidColorBrush(Color.FromRgb(148, 163, 184)),
            FontSize = 11,
            TextTrimming = TextTrimming.CharacterEllipsis,
            Margin = new Thickness(0, 2, 0, 0),
            ToolTip = path
        });
        return new Border
        {
            Padding = new Thickness(10, 8, 10, 8),
            Background = new SolidColorBrush(Color.FromRgb(24, 27, 34)),
            BorderBrush = new SolidColorBrush(Color.FromRgb(52, 56, 66)),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(4),
            Child = panel
        };
    }

    private Button CreateChoiceButton(string text, FolderFileConflictChoice choice)
    {
        var button = new Button
        {
            Content = text,
            Height = 30,
            Padding = new Thickness(12, 0, 12, 0),
            Foreground = new SolidColorBrush(Color.FromRgb(243, 244, 246)),
            Background = new SolidColorBrush(Color.FromRgb(35, 38, 45)),
            BorderBrush = new SolidColorBrush(Color.FromRgb(52, 56, 66)),
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
}
