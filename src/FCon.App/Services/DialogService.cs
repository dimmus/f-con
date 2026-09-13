using System.Windows;
using FCon.Abstractions.Model;
using FCon.App.Views;

namespace FCon.App.Services;

public sealed class DialogService(AppServices services, Window owner) : IDialogService
{
    public ProxyNode? EditNode(ProxyNode node, bool isNew)
    {
        var window = new NodeEditorWindow(services.Registry, services.Importer, node, isNew)
        {
            Owner = owner,
        };
        return window.ShowDialog() == true ? window.Result : null;
    }

    public string? PromptText(string title, string description, string initial = "", bool multiline = false)
    {
        var window = new TextPromptWindow(title, description, initial, multiline) { Owner = owner };
        return window.ShowDialog() == true ? window.Result : null;
    }

    public bool Confirm(string title, string message) =>
        MessageWindow.Confirm(owner, title, message);

    public void ShowError(string title, string message) =>
        MessageWindow.Show(owner, title, message, MessageKind.Error);

    public void ShowInfo(string title, string message) =>
        MessageWindow.Show(owner, title, message, MessageKind.Info);

    public void ShowConfig(string title, string json) =>
        new TextViewerWindow(title, json) { Owner = owner }.ShowDialog();
}
