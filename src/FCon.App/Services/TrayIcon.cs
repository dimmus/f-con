using System.Drawing;
using System.Windows.Forms;
using FCon.App.Views;
using FCon.Core.Engine;

namespace FCon.App.Services;

/// <summary>
/// System tray presence. The icon is drawn in code so the app ships without binary assets,
/// and its colour tracks the connection state at a glance.
/// </summary>
public sealed partial class TrayIcon : IDisposable
{
    private readonly AppServices _services;
    private readonly MainWindow _window;
    private readonly NotifyIcon _icon;
    private readonly System.Windows.Controls.ContextMenu _menu;
    private readonly System.Windows.Controls.MenuItem _connectItem;
    private readonly System.Windows.Controls.MenuItem _statusItem;
    private Icon? _currentIcon;

    public TrayIcon(AppServices services, MainWindow window)
    {
        _services = services;
        _window = window;

        // A WPF menu rather than a WinForms ContextMenuStrip: the strip renders with its
        // own light chrome and ignores the app theme entirely, which looked wrong hanging
        // off a dark window. This one picks up the same styles as every other menu.
        _statusItem = new System.Windows.Controls.MenuItem { Header = "Disconnected", IsEnabled = false };
        _connectItem = new System.Windows.Controls.MenuItem { Header = "Connect" };
        _connectItem.Click += (_, _) => Toggle();

        var showItem = new System.Windows.Controls.MenuItem { Header = "Show window" };
        showItem.Click += (_, _) => window.RestoreFromTray();

        var exitItem = new System.Windows.Controls.MenuItem { Header = "Exit" };
        exitItem.Click += (_, _) => window.CloseForReal();

        _menu = new System.Windows.Controls.ContextMenu();
        _menu.Items.Add(_statusItem);
        _menu.Items.Add(new System.Windows.Controls.Separator());
        _menu.Items.Add(_connectItem);
        _menu.Items.Add(showItem);
        _menu.Items.Add(new System.Windows.Controls.Separator());
        _menu.Items.Add(exitItem);

        _icon = new NotifyIcon
        {
            Text = "FCon",
            Visible = true,
        };
        _icon.MouseUp += OnIconMouseUp;
        _icon.DoubleClick += (_, _) => window.RestoreFromTray();

        UpdateIcon(ConnectionState.Disconnected);
        services.Engine.StatusChanged += OnStatusChanged;
    }

    private void OnIconMouseUp(object? sender, MouseEventArgs e)
    {
        if (e.Button != MouseButtons.Right) return;

        _window.Dispatcher.Invoke(() =>
        {
            _menu.Placement = System.Windows.Controls.Primitives.PlacementMode.MousePoint;
            _menu.IsOpen = true;

            // A menu opened from the tray belongs to no active window, so Windows never
            // sends it a deactivate message and it would stay on screen after a click
            // elsewhere. Making the popup itself the foreground window fixes that.
            if (System.Windows.PresentationSource.FromVisual(_menu)
                is System.Windows.Interop.HwndSource source)
            {
                SetForegroundWindow(source.Handle);
            }
        });
    }

    [System.Runtime.InteropServices.LibraryImport("user32.dll")]
    [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
    private static partial bool SetForegroundWindow(nint handle);

    private void OnStatusChanged(ConnectionStatus status)
    {
        _window.Dispatcher.Invoke(() =>
        {
            UpdateIcon(status.State);

            var name = status.Node?.DisplayName;
            _statusItem.Header = status.State switch
            {
                ConnectionState.Connected => $"Connected — {name}",
                ConnectionState.Connecting => "Connecting...",
                ConnectionState.Disconnecting => "Stopping...",
                ConnectionState.Faulted => "Failed",
                _ => "Disconnected",
            };

            _connectItem.Header = status.State == ConnectionState.Connected ? "Disconnect" : "Connect";

            // Tray tooltips are capped at 63 characters by the shell.
            var tooltip = $"FCon — {_statusItem.Header}";
            _icon.Text = tooltip.Length > 62 ? tooltip[..62] : tooltip;
        });
    }

    private void Toggle() =>
        _window.Dispatcher.InvokeAsync(() => _window.ViewModel.ToggleConnectionCommand.ExecuteAsync(null));

    private void UpdateIcon(ConnectionState state)
    {
        var colour = state switch
        {
            ConnectionState.Connected => Color.FromArgb(61, 214, 140),
            ConnectionState.Connecting or ConnectionState.Disconnecting => Color.FromArgb(245, 181, 71),
            ConnectionState.Faulted => Color.FromArgb(242, 85, 90),
            _ => Color.FromArgb(122, 133, 153),
        };

        var previous = _currentIcon;
        _currentIcon = CreateIcon(colour);
        _icon.Icon = _currentIcon;

        // Safe to release only after the new icon is installed: the shell reads the
        // current one on demand, not just once when it is assigned.
        previous?.Dispose();
    }

    private static Icon CreateIcon(Color colour)
    {
        using var bitmap = new Bitmap(32, 32);
        using (var g = Graphics.FromImage(bitmap))
        {
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            g.Clear(Color.Transparent);

            using var shield = new SolidBrush(colour);
            g.FillEllipse(shield, 3, 3, 26, 26);

            using var inner = new SolidBrush(Color.FromArgb(230, 15, 17, 21));
            g.FillEllipse(inner, 9, 9, 14, 14);
        }

        // Build a real .ico in memory rather than going through GetHicon/FromHandle.
        // An Icon made by FromHandle does not own its handle, and Icon.Clone() on one
        // shares that same handle instead of copying it - so destroying the handle left
        // the tray pointing at freed memory and the icon eventually vanished.
        using var png = new MemoryStream();
        bitmap.Save(png, System.Drawing.Imaging.ImageFormat.Png);
        var payload = png.ToArray();

        using var ico = new MemoryStream();
        using (var w = new BinaryWriter(ico, System.Text.Encoding.UTF8, leaveOpen: true))
        {
            const int headerSize = 6 + 16;

            w.Write((short)0);                  // reserved
            w.Write((short)1);                  // type: icon
            w.Write((short)1);                  // image count

            w.Write((byte)bitmap.Width);        // 0 would mean 256
            w.Write((byte)bitmap.Height);
            w.Write((byte)0);                   // palette size
            w.Write((byte)0);                   // reserved
            w.Write((short)1);                  // colour planes
            w.Write((short)32);                 // bits per pixel
            w.Write(payload.Length);
            w.Write(headerSize);                // offset to the image data
            w.Write(payload);
        }

        ico.Position = 0;
        return new Icon(ico);
    }

    public void Dispose()
    {
        _services.Engine.StatusChanged -= OnStatusChanged;
        _icon.Visible = false;
        _icon.Dispose();
        _currentIcon?.Dispose();
    }
}
