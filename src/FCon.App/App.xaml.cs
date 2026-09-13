using System.Windows;
using FCon.App.Services;
using FCon.App.Views;
using FCon.Core;
using FCon.Core.Engine;
using FCon.Core.Net;

namespace FCon.App;

public partial class App : Application
{
    private SingleInstance? _instance;
    private AppServices? _services;
    private TrayIcon? _tray;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        _instance = SingleInstance.Acquire();
        if (!_instance.IsFirstInstance)
        {
            // A second launch should surface the running window, not start a rival engine.
            SingleInstance.SignalExisting();
            _instance.Dispose();
            _instance = null;
            Shutdown();
            return;
        }

        DispatcherUnhandledException += OnDispatcherUnhandledException;

        _services = new AppServices();
        ThemeManager.Apply(_services.Settings.Theme);

        // A previous run may have died with the system proxy still pointed at a dead port.
        if (SystemProxy.HasStaleSnapshot())
        {
            SystemProxy.Disable();
            _services.Log.Add(new EngineLogLine(
                DateTimeOffset.Now,
                "Restored the system proxy left behind by a previous session.",
                false));
        }

        // Logging off or shutting down does give us a chance to run, unlike a force-kill.
        // Worth taking: it stops a reboot from leaving the machine pointed at a dead port.
        Microsoft.Win32.SystemEvents.SessionEnding += OnSessionEnding;

        var window = new MainWindow(_services);
        MainWindow = window;

        _instance.ActivationRequested += () => Dispatcher.Invoke(window.RestoreFromTray);
        _tray = new TrayIcon(_services, window);

        if (!_services.Settings.StartMinimized) window.Show();
    }

    /// <summary>
    /// Guards the crash handler against itself. A modal dialog runs a nested message
    /// pump, so a second fault raised while it is open re-enters this handler and opens
    /// another dialog. That recursion overflowed the stack and killed the process
    /// outright - the reported error took the app down harder than the original fault.
    /// A try/catch cannot prevent it: the nested exception is dispatched, not thrown
    /// through the call that opened the dialog.
    /// </summary>
    private int _reportingCrash;

    private void OnDispatcherUnhandledException(
        object sender,
        System.Windows.Threading.DispatcherUnhandledExceptionEventArgs args)
    {
        // Keep running: a render or layout fault should not be fatal.
        args.Handled = true;

        // Record every fault, including ones that arrive while a dialog is already up.
        WriteCrashLog(args.Exception);

        if (Interlocked.Exchange(ref _reportingCrash, 1) == 1) return;

        try
        {
            MessageWindow.Show(
                MainWindow,
                "FCon hit an unexpected error",
                args.Exception.ToString(),
                MessageKind.Error);
        }
        catch (Exception)
        {
            // The themed dialog itself is broken; there is nowhere left to report to
            // except the log already written above.
        }
        finally
        {
            Volatile.Write(ref _reportingCrash, 0);
        }
    }

    /// <summary>Append a fault to a log file, so a crash leaves evidence behind.</summary>
    private static void WriteCrashLog(Exception exception)
    {
        try
        {
            Directory.CreateDirectory(AppPaths.LogDirectory);
            var path = Path.Combine(AppPaths.LogDirectory, "errors.log");
            File.AppendAllText(
                path,
                $"[{DateTimeOffset.Now:u}] {exception}{Environment.NewLine}{Environment.NewLine}");
        }
        catch (Exception)
        {
            // Logging must never be the reason a fault handler fails.
        }
    }

    private static void OnSessionEnding(object sender, Microsoft.Win32.SessionEndingEventArgs e)
    {
        try
        {
            SystemProxy.Disable();
        }
        catch (Exception)
        {
            // Nothing useful to do while Windows is tearing the session down.
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        Microsoft.Win32.SystemEvents.SessionEnding -= OnSessionEnding;
        _tray?.Dispose();
        if (_services is not null) _services.DisposeAsync().AsTask().GetAwaiter().GetResult();
        _instance?.Dispose();
        base.OnExit(e);
    }
}
