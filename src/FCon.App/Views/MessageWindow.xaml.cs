using System.Windows;
using System.Windows.Media;
using FCon.App.Services;

namespace FCon.App.Views;

public enum MessageKind
{
    Info,
    Warning,
    Error,
    Question,
}

/// <summary>
/// The app's own message dialog. Replaces <see cref="MessageBox"/> so alerts match the
/// theme, and so error text can be selected and copied rather than retyped from a
/// screenshot.
/// </summary>
public partial class MessageWindow : Window
{
    private MessageWindow(string title, string headline, string body, MessageKind kind, bool isConfirm)
    {
        InitializeComponent();

        Title = title;
        HeadlineText.Text = headline;
        BodyText.Text = body;

        // A dialog with no detail beyond its headline should not show an empty body.
        if (string.IsNullOrWhiteSpace(body)) BodyText.Visibility = Visibility.Collapsed;

        var (brushKey, glyph) = kind switch
        {
            MessageKind.Error => ("Status.Bad", "!"),
            MessageKind.Warning => ("Status.Warn", "!"),
            MessageKind.Question => ("Accent", "?"),
            _ => ("Accent", "i"),
        };

        Badge.Background = (Brush?)TryFindResource(brushKey) ?? Brushes.Gray;
        BadgeGlyph.Text = glyph;

        if (isConfirm)
        {
            PrimaryButton.Content = "Yes";
            SecondaryButton.Content = "No";
        }
        else
        {
            SecondaryButton.Visibility = Visibility.Collapsed;
        }

        // Copying only earns its place when there is something worth pasting elsewhere.
        CopyButton.Visibility = kind is MessageKind.Error or MessageKind.Warning
                                && !string.IsNullOrWhiteSpace(body)
            ? Visibility.Visible
            : Visibility.Collapsed;

        Loaded += (_, _) =>
        {
            ThemeManager.ApplyToWindow(this);
            PrimaryButton.Focus();
        };
    }

    public static void Show(Window? owner, string title, string body, MessageKind kind)
    {
        var window = new MessageWindow(title, title, body, kind, isConfirm: false);
        if (owner is not null && owner.IsVisible) window.Owner = owner;
        else window.WindowStartupLocation = WindowStartupLocation.CenterScreen;
        window.ShowDialog();
    }

    public static bool Confirm(Window? owner, string title, string body)
    {
        var window = new MessageWindow(title, title, body, MessageKind.Question, isConfirm: true);
        if (owner is not null && owner.IsVisible) window.Owner = owner;
        else window.WindowStartupLocation = WindowStartupLocation.CenterScreen;
        return window.ShowDialog() == true;
    }

    private void Primary_Click(object sender, RoutedEventArgs e) => DialogResult = true;

    private void Secondary_Click(object sender, RoutedEventArgs e) => DialogResult = false;

    private void Copy_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            Clipboard.SetText($"{HeadlineText.Text}{Environment.NewLine}{BodyText.Text}");
        }
        catch (System.Runtime.InteropServices.COMException)
        {
            // Another process holds the clipboard; the text is selectable on screen anyway.
        }
    }
}
