using FCon.Abstractions.Model;

namespace FCon.App.Services;

/// <summary>
/// Window-opening pulled behind an interface so the view models stay testable and free
/// of direct WPF references.
/// </summary>
public interface IDialogService
{
    /// <summary>Open the node editor. Returns the edited node, or null if cancelled.</summary>
    ProxyNode? EditNode(ProxyNode node, bool isNew);

    /// <summary>Prompt for free text — used for pasting links and subscription URLs.</summary>
    string? PromptText(string title, string description, string initial = "", bool multiline = false);

    bool Confirm(string title, string message);

    void ShowError(string title, string message);

    void ShowInfo(string title, string message);

    /// <summary>Show the generated engine configuration for a node.</summary>
    void ShowConfig(string title, string json);
}
