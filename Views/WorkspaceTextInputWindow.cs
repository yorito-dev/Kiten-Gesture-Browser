using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace TabBrowser.Views;

public sealed class WorkspaceTextInputWindow : Window
{
    private readonly TextBox _valueBox;
    private readonly TextBlock _statusText;

    public WorkspaceTextInputWindow(string title, string label, string initialValue)
    {
        Title = title;
        Width = 420;
        Height = 190;
        MinWidth = 360;
        MinHeight = 170;
        Background = new SolidColorBrush(Color.FromRgb(16, 17, 20));
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        var root = new Grid
        {
            Margin = new Thickness(14)
        };
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        Content = root;

        var labelText = new TextBlock
        {
            Text = label,
            Foreground = new SolidColorBrush(Color.FromRgb(243, 244, 246)),
            FontSize = 14,
            FontWeight = FontWeights.SemiBold,
            Margin = new Thickness(0, 0, 0, 8)
        };
        Grid.SetRow(labelText, 0);
        root.Children.Add(labelText);

        _valueBox = new TextBox
        {
            Text = initialValue,
            Height = 30,
            Padding = new Thickness(10, 5, 10, 5),
            Foreground = new SolidColorBrush(Color.FromRgb(243, 244, 246)),
            Background = new SolidColorBrush(Color.FromRgb(24, 27, 34)),
            BorderBrush = new SolidColorBrush(Color.FromRgb(52, 56, 66)),
            BorderThickness = new Thickness(1),
            CaretBrush = new SolidColorBrush(Color.FromRgb(243, 244, 246)),
            VerticalContentAlignment = VerticalAlignment.Center
        };
        _valueBox.KeyDown += ValueBox_KeyDown;
        Grid.SetRow(_valueBox, 1);
        root.Children.Add(_valueBox);

        _statusText = new TextBlock
        {
            Foreground = new SolidColorBrush(Color.FromRgb(248, 113, 113)),
            FontSize = 12,
            Margin = new Thickness(0, 8, 0, 8)
        };
        Grid.SetRow(_statusText, 2);
        root.Children.Add(_statusText);

        var buttons = new DockPanel
        {
            LastChildFill = false
        };
        Grid.SetRow(buttons, 3);
        root.Children.Add(buttons);

        var okButton = CreateButton("OK");
        okButton.Click += OkButton_Click;
        buttons.Children.Add(okButton);

        var cancelButton = CreateButton("キャンセル");
        cancelButton.Click += (_, _) => Close();
        DockPanel.SetDock(cancelButton, Dock.Right);
        buttons.Children.Add(cancelButton);

        Loaded += (_, _) =>
        {
            _valueBox.SelectAll();
            _valueBox.Focus();
        };
    }

    public string Value { get; private set; } = string.Empty;

    public bool IsAccepted { get; private set; }

    private static Button CreateButton(string text)
    {
        return new Button
        {
            Content = text,
            MinWidth = 84,
            Height = 30,
            Margin = new Thickness(0, 0, 8, 0),
            Padding = new Thickness(12, 0, 12, 0),
            Foreground = new SolidColorBrush(Color.FromRgb(243, 244, 246)),
            Background = new SolidColorBrush(Color.FromRgb(35, 38, 45)),
            BorderBrush = new SolidColorBrush(Color.FromRgb(52, 56, 66)),
            BorderThickness = new Thickness(1),
            Cursor = Cursors.Hand
        };
    }

    private void ValueBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            e.Handled = true;
            TryAccept();
        }
    }

    private void OkButton_Click(object sender, RoutedEventArgs e)
    {
        TryAccept();
    }

    private void TryAccept()
    {
        var value = _valueBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(value))
        {
            _statusText.Text = "名前を入力してください";
            return;
        }

        Value = value;
        IsAccepted = true;
        Close();
    }
}
