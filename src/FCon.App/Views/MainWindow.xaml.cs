using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using FCon.App.Services;
using FCon.App.ViewModels;

namespace FCon.App.Views;

public partial class MainWindow : Window
{
    private readonly AppServices _services;
    private readonly MainViewModel _vm;
    private readonly DispatcherTimer _subscriptionTimer;
    private bool _reallyClosing;
    private bool _logScrollQueued;

    public MainWindow(AppServices services)
    {
        _services = services;
        InitializeComponent();

        _vm = new MainViewModel(services, new DialogService(services, this));
        DataContext = _vm;

        _vm.Logs.Lines.CollectionChanged += (_, _) => QueueLogScroll();

        _subscriptionTimer = new DispatcherTimer { Interval = TimeSpan.FromMinutes(15) };
        _subscriptionTimer.Tick += async (_, _) => await AutoUpdateSubscriptionsAsync();
        _subscriptionTimer.Start();

        Loaded += OnLoaded;
    }

    public MainViewModel ViewModel => _vm;

    /// <summary>
    /// Scrolls the log to the newest line, but never from inside the CollectionChanged
    /// notification that produced it.
    /// </summary>
    /// <remarks>
    /// ScrollIntoView forces a synchronous layout pass. Called while the ItemsControl is
    /// still processing the Add, its generator finds its own accounting inconsistent
    /// ("accumulated count differs from actual count") and throws - once per log line.
    /// Each of those faults used to raise a modal error dialog, which pumps messages,
    /// which delivers the next log line, which throws again: nested dispatcher loops
    /// until the stack ran out. Deferring to Background priority lets the control finish
    /// first, and the flag collapses a burst of lines into a single scroll.
    /// </remarks>
    private void QueueLogScroll()
    {
        if (_logScrollQueued || !_vm.Logs.AutoScroll) return;
        _logScrollQueued = true;

        Dispatcher.BeginInvoke(DispatcherPriority.Background, () =>
        {
            _logScrollQueued = false;
            if (!_vm.Logs.AutoScroll || LogList.Items.Count == 0) return;

            try
            {
                LogList.ScrollIntoView(LogList.Items[^1]);
            }
            catch (InvalidOperationException)
            {
                // The generator was still mid-update; the next line scrolls instead.
                // Never let a cosmetic scroll become an application fault.
            }
        });
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        ThemeManager.ApplyToWindow(this);
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        Loaded -= OnLoaded;

        if (_services.Settings.AutoStartLastServer
            && _services.Settings.ActiveNodeId is { } id
            && _services.Profiles.FindNode(id) is { } node)
        {
            await _services.Engine.ConnectAsync(node);
        }

        await AutoUpdateSubscriptionsAsync();
    }

    /// <summary>Refresh subscriptions whose configured interval has elapsed.</summary>
    private async Task AutoUpdateSubscriptionsAsync()
    {
        if (!_services.Settings.AutoUpdateSubscriptions) return;

        var interval = TimeSpan.FromHours(Math.Max(1, _services.Settings.SubscriptionUpdateHours));
        var due = _services.Profiles.Subscriptions
            .Where(s => s.Enabled)
            .Where(s => s.LastUpdated is null || DateTimeOffset.Now - s.LastUpdated > interval)
            .ToList();

        foreach (var subscription in due)
            await _services.Subscriptions.UpdateAsync(subscription);

        if (due.Count > 0) _vm.Refresh();
    }

    private async void ServerGrid_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        // Ignore double-clicks on the header or empty space below the rows.
        if (e.OriginalSource is not DependencyObject source) return;
        if (FindParent<DataGridRow>(source) is null) return;

        await _vm.ConnectToCommand.ExecuteAsync(_vm.SelectedServer);
    }

    private static T? FindParent<T>(DependencyObject? child) where T : DependencyObject
    {
        while (child is not null)
        {
            if (child is T match) return match;
            child = System.Windows.Media.VisualTreeHelper.GetParent(child);
        }
        return null;
    }

    public void RestoreFromTray()
    {
        Show();
        WindowState = WindowState.Normal;
        Activate();
        Topmost = true;
        Topmost = false;
        Focus();
    }

    /// <summary>Called by the tray icon when the user chooses Exit.</summary>
    public void CloseForReal()
    {
        _reallyClosing = true;
        Close();
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        if (!_reallyClosing && _services.Settings.CloseToTray)
        {
            e.Cancel = true;
            Hide();
            return;
        }

        _subscriptionTimer.Stop();
        base.OnClosing(e);
        Application.Current.Shutdown();
    }
}
