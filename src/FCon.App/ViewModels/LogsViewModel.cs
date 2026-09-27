using System.Collections.ObjectModel;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using FCon.App.Services;
using FCon.Core;
using FCon.Core.Engine;

namespace FCon.App.ViewModels;

public sealed partial class LogsViewModel : ObservableObject
{
    private const int MaxDisplayed = 2000;

    private readonly AppServices _services;
    private bool _appending;

    [ObservableProperty] private ObservableCollection<LogLineViewModel> _lines = [];
    [ObservableProperty] private bool _autoScroll = true;
    [ObservableProperty] private bool _errorsOnly;
    [ObservableProperty] private string _filter = "";

    public LogsViewModel(AppServices services)
    {
        _services = services;

        foreach (var line in services.Log.Snapshot()) Append(line);
        services.Log.LineAdded += line =>
            Application.Current.Dispatcher.BeginInvoke(() => Append(line));
    }

    /// <summary>
    /// Adds one line to the view. Re-entrancy is refused rather than queued: a fault
    /// raised while the list is being updated gets logged, and logging it here would
    /// re-enter this method from inside its own collection notification - the shape of
    /// bug that previously turned one layout fault into an unbounded cascade.
    /// </summary>
    private void Append(EngineLogLine line)
    {
        if (ErrorsOnly && !line.IsError) return;
        if (Filter.Length > 0 && !line.Text.Contains(Filter, StringComparison.OrdinalIgnoreCase)) return;
        if (_appending) return;

        _appending = true;
        try
        {
            Lines.Add(new LogLineViewModel(line));
            while (Lines.Count > MaxDisplayed) Lines.RemoveAt(0);
        }
        finally
        {
            _appending = false;
        }
    }

    partial void OnErrorsOnlyChanged(bool value) => Rebuild();

    partial void OnFilterChanged(string value) => Rebuild();

    private void Rebuild()
    {
        Lines.Clear();
        foreach (var line in _services.Log.Snapshot()) Append(line);
    }

    [RelayCommand]
    private void Clear()
    {
        _services.Log.Clear();
        Lines.Clear();
    }

    [RelayCommand]
    private void CopyAll()
    {
        var text = string.Join(Environment.NewLine, Lines.Select(l => l.Text));
        try
        {
            Clipboard.SetText(text);
        }
        catch (System.Runtime.InteropServices.COMException)
        {
            // Another process holds the clipboard; retrying here would just stall the UI.
        }
    }

    [RelayCommand]
    private async Task SaveAsync()
    {
        var path = Path.Combine(AppPaths.LogDirectory, $"kvn-{DateTime.Now:yyyyMMdd-HHmmss}.log");
        Directory.CreateDirectory(AppPaths.LogDirectory);
        await File.WriteAllLinesAsync(path, Lines.Select(l => l.Text));

        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("explorer.exe",
            $"/select,\"{path}\"") { UseShellExecute = true });
    }
}

public sealed class LogLineViewModel(EngineLogLine line)
{
    public string Time { get; } = line.Timestamp.ToString("HH:mm:ss");
    public string Message { get; } = line.Text;
    public bool IsError { get; } = line.IsError;
    public string Text => $"{Time}  {Message}";
}
