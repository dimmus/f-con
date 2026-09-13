using System.Windows;
using FCon.App.Services;

namespace FCon.App.Views;

public partial class TextPromptWindow : Window
{
    public TextPromptWindow(string title, string description, string initial, bool multiline)
    {
        InitializeComponent();

        Title = title;
        DescriptionText.Text = description;
        InputBox.Text = initial;
        InputBox.AcceptsReturn = multiline;

        if (multiline)
        {
            InputBox.MinHeight = 160;
            InputBox.VerticalContentAlignment = VerticalAlignment.Top;
        }

        Loaded += (_, _) =>
        {
            ThemeManager.ApplyToWindow(this);
            InputBox.Focus();
            InputBox.SelectAll();
        };
    }

    public string? Result { get; private set; }

    private void Ok_Click(object sender, RoutedEventArgs e)
    {
        Result = InputBox.Text;
        DialogResult = true;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;
}
