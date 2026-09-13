using System.Windows;
using FCon.App.Services;

namespace FCon.App.Views;

public partial class TextViewerWindow : Window
{
    public TextViewerWindow(string title, string content)
    {
        InitializeComponent();
        Title = title;
        ContentBox.Text = content;
        Loaded += (_, _) => ThemeManager.ApplyToWindow(this);
    }

    private void Copy_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            Clipboard.SetText(ContentBox.Text);
        }
        catch (System.Runtime.InteropServices.COMException)
        {
            // Another process holds the clipboard; the text is on screen either way.
        }
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
