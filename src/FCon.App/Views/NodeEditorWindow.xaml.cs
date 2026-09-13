using System.Windows;
using FCon.Abstractions.Model;
using FCon.App.Services;
using FCon.App.ViewModels;
using FCon.Core.Import;
using FCon.Core.Plugins;

namespace FCon.App.Views;

public partial class NodeEditorWindow : Window
{
    private readonly NodeEditorViewModel _vm;

    public NodeEditorWindow(PluginRegistry registry, LinkImporter importer, ProxyNode node, bool isNew)
    {
        InitializeComponent();

        _vm = new NodeEditorViewModel(registry, importer, node, isNew);
        DataContext = _vm;
        Title = _vm.Title;

        Loaded += (_, _) => ThemeManager.ApplyToWindow(this);
    }

    public ProxyNode? Result => _vm.Result;

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        if (!_vm.TrySave())
        {
            MessageWindow.Show(this, "Fix these first", _vm.ValidationText, MessageKind.Warning);
            return;
        }
        DialogResult = true;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;

    private void PasteLink_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            if (Clipboard.ContainsText()) _vm.PasteLinkCommand.Execute(Clipboard.GetText());
        }
        catch (System.Runtime.InteropServices.COMException)
        {
            // Clipboard was busy; the user can simply try again.
        }
    }

    private void CopyLink_Click(object sender, RoutedEventArgs e)
    {
        var link = _vm.BuildShareLink();
        if (link is null) return;

        try
        {
            Clipboard.SetText(link);
        }
        catch (System.Runtime.InteropServices.COMException)
        {
            // Nothing useful to report; the link is regenerated on demand.
        }
    }
}
