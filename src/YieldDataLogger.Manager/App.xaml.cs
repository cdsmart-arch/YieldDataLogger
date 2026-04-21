using System.Drawing;
using System.IO;
using System.Windows;
using System.Windows.Resources;
using Forms = System.Windows.Forms;

namespace YieldDataLogger.Manager;

/// <summary>
/// Entry point. Starts with the main window visible (so the user sees the dashboard the
/// first time they launch) and installs a tray icon whose colour tracks connection state.
/// Closing the window only hides it; Exit from the tray menu terminates the process.
/// </summary>
public partial class App : System.Windows.Application
{
    private MainWindow? _window;
    private Forms.NotifyIcon? _tray;
    private Forms.ToolStripMenuItem? _startAgentItem;
    private Forms.ToolStripMenuItem? _stopAgentItem;
    private Icon? _iconConnected;
    private Icon? _iconReconnecting;
    private Icon? _iconDisconnected;
    private ManagerState? _latestState;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // State-coloured tray icons. We overlay a small coloured dot on the corner of the
        // app icon so users can tell state at a glance without losing product identity.
        var baseIcon = LoadAppIcon();
        _iconConnected    = ComposeStateIcon(baseIcon, Color.FromArgb(63, 185, 80));
        _iconReconnecting = ComposeStateIcon(baseIcon, Color.FromArgb(210, 153, 34));
        _iconDisconnected = ComposeStateIcon(baseIcon, Color.FromArgb(248, 81, 73));

        _tray = new Forms.NotifyIcon
        {
            Icon    = _iconDisconnected,
            Text    = "YieldDataLogger Manager - starting",
            Visible = true,
            ContextMenuStrip = BuildTrayMenu(),
        };
        _tray.DoubleClick += (_, _) => ShowWindow();

        _window = new MainWindow();
        _window.StateUpdated += OnStateUpdated;
        _window.Show();
    }

    private Forms.ContextMenuStrip BuildTrayMenu()
    {
        var menu = new Forms.ContextMenuStrip();

        var show = new Forms.ToolStripMenuItem("Show dashboard");
        show.Click += (_, _) => ShowWindow();
        show.Font = new Font(show.Font, System.Drawing.FontStyle.Bold);

        _startAgentItem = new Forms.ToolStripMenuItem("Start Agent");
        _startAgentItem.Click += (_, _) =>
        {
            var (ok, msg) = AgentController.Start();
            _tray!.ShowBalloonTip(ok ? 1500 : 3000,
                ok ? "Agent starting" : "Start failed",
                msg,
                ok ? Forms.ToolTipIcon.Info : Forms.ToolTipIcon.Error);
        };

        _stopAgentItem = new Forms.ToolStripMenuItem("Stop Agent");
        _stopAgentItem.Click += (_, _) =>
        {
            var (ok, msg) = AgentController.Stop(_latestState?.Status);
            _tray!.ShowBalloonTip(ok ? 1500 : 3000,
                ok ? "Agent stopped" : "Stop failed",
                msg,
                ok ? Forms.ToolTipIcon.Info : Forms.ToolTipIcon.Error);
        };

        menu.Items.Add(show);
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add(_startAgentItem);
        menu.Items.Add(_stopAgentItem);
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add("Exit", null, (_, _) => Shutdown());
        return menu;
    }

    private void ShowWindow()
    {
        if (_window is null) return;
        _window.Show();
        _window.WindowState = WindowState.Normal;
        _window.Activate();
    }

    private void OnStateUpdated(object? sender, ManagerState state)
    {
        _latestState = state;
        if (_tray is null) return;

        string label;
        Icon icon;
        bool agentRunning = state.AgentRunning;

        if (!agentRunning)
        {
            label = "YieldDataLogger - Agent not running";
            icon  = _iconDisconnected!;
        }
        else
        {
            var s  = state.Status!;
            var cn = s.ConnectionState;
            if (string.Equals(cn, "Connected", StringComparison.OrdinalIgnoreCase))
            {
                label = $"YieldDataLogger - Connected\n{s.SubscribedSymbols.Count} symbols | {s.TicksDispatched:N0} ticks";
                icon  = _iconConnected!;
            }
            else if (string.Equals(cn, "Reconnecting", StringComparison.OrdinalIgnoreCase)
                  || string.Equals(cn, "Connecting",   StringComparison.OrdinalIgnoreCase))
            {
                label = $"YieldDataLogger - {cn}";
                icon  = _iconReconnecting!;
            }
            else
            {
                label = $"YieldDataLogger - {cn}";
                icon  = _iconDisconnected!;
            }
        }

        _tray.Text = label.Length > 63 ? label.Substring(0, 63) : label;
        _tray.Icon = icon;

        // Enable/disable menu items based on whether the Agent is running.
        if (_startAgentItem is not null) _startAgentItem.Enabled = !agentRunning;
        if (_stopAgentItem  is not null) _stopAgentItem.Enabled  =  agentRunning;
    }

    protected override void OnExit(ExitEventArgs e)
    {
        if (_tray is not null) { _tray.Visible = false; _tray.Dispose(); }
        _iconConnected?.Dispose();
        _iconReconnecting?.Dispose();
        _iconDisconnected?.Dispose();
        base.OnExit(e);
    }

    /// <summary>Loads the embedded appicon.ico as a System.Drawing.Icon.</summary>
    private static Icon LoadAppIcon()
    {
        try
        {
            var uri = new Uri("pack://application:,,,/appicon.ico", UriKind.Absolute);
            StreamResourceInfo sri = GetResourceStream(uri);
            using var s = sri.Stream;
            return new Icon(s);
        }
        catch
        {
            return SystemIcons.Application;
        }
    }

    /// <summary>
    /// Builds a tray icon by overlaying a status-coloured dot onto the product app icon.
    /// 32x32 is Windows's preferred tray size on modern DPIs; Windows downscales to 16 for
    /// the notification area automatically.
    /// </summary>
    private static Icon ComposeStateIcon(Icon baseIcon, Color stateColor)
    {
        const int size = 32;
        using var bmp = new Bitmap(size, size, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode     = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
            g.Clear(Color.Transparent);
            g.DrawIcon(new Icon(baseIcon, size, size), new Rectangle(0, 0, size, size));

            // Dot at lower-right; stroke with dark ring so it's visible on any wallpaper.
            var dotR = 10;
            var dotRect = new Rectangle(size - dotR - 1, size - dotR - 1, dotR, dotR);
            using var brush = new SolidBrush(stateColor);
            g.FillEllipse(brush, dotRect);
            using var pen = new Pen(Color.FromArgb(200, 0, 0, 0), 1.5f);
            g.DrawEllipse(pen, dotRect);
        }
        return Icon.FromHandle(bmp.GetHicon());
    }
}
