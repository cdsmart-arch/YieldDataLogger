using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;
using Brush = System.Windows.Media.Brush;
using MessageBox = System.Windows.MessageBox;

namespace YieldDataLogger.Manager;

public partial class MainWindow : Window
{
    private readonly ManagerConfig _config;
    private readonly AgentStatusReader _statusReader = new();
    private readonly DispatcherTimer _timer;
    private readonly ObservableCollection<SymbolRow> _rows = new();
    private ManagerState? _latestState;

    internal event EventHandler<ManagerState>? StateUpdated;

    public MainWindow()
    {
        InitializeComponent();
        _config = ManagerConfig.Resolve();

        SymbolsGrid.ItemsSource = _rows;

        _timer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = _config.RefreshInterval,
        };
        _timer.Tick += (_, _) => Refresh();

        Loaded           += (_, _) => { _timer.Start(); Refresh(); };
        Closing          += OnClosing;
        SourceInitialized += OnSourceInitialized;
    }

    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        // Match the frame to the content. Done here because the HWND only exists after SourceInitialized.
        var hwnd = new WindowInteropHelper(this).Handle;
        Win32Interop.EnableDarkTitleBar(hwnd);
        Win32Interop.SetCaptionColor(hwnd, 0x16, 0x1B, 0x22); // #161B22 - same as PanelBrush
    }

    private void OnClosing(object? sender, CancelEventArgs e)
    {
        // Clicking the X on a tray-app closes to tray rather than exiting - the user uses
        // the tray context menu -> Exit when they really want to stop the Manager.
        e.Cancel = true;
        Hide();
    }

    private void Refresh()
    {
        var state = _statusReader.Read(_config);
        _latestState = state;
        StateUpdated?.Invoke(this, state);

        UpdateHeader(state);
        UpdateBackfillBanner(state);
        UpdateFooter(state);
        UpdateGrid(state);

        // Symbols dialog needs the API base URL, which the Agent writes into status.json.
        // If the Agent hasn't ever run we can't know where to fetch the catalog from, so
        // grey the button out with a helpful tooltip.
        var canPickSymbols = !string.IsNullOrWhiteSpace(state.Status?.ApiBaseUrl);
        SymbolsButton.IsEnabled = canPickSymbols;
        SymbolsButton.ToolTip = canPickSymbols
            ? "Choose which instruments this Agent subscribes to"
            : "Start the Agent first so the Manager knows which API to query.";
    }

    private void OnSymbolsClick(object sender, RoutedEventArgs e)
    {
        var apiBase = _latestState?.Status?.ApiBaseUrl;
        if (string.IsNullOrWhiteSpace(apiBase)) return;

        var win = new SubscriptionsWindow(_config, apiBase) { Owner = this };
        win.ShowDialog();
    }

    private void OnClearHistoryClick(object sender, RoutedEventArgs e)
    {
        var sqliteDir = _latestState?.Status?.SqliteSinkPath;
        if (string.IsNullOrWhiteSpace(sqliteDir) || !Directory.Exists(sqliteDir))
        {
            MessageBox.Show("No local history folder found.", "Clear History",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var files = Directory.GetFiles(sqliteDir, "*.sqlite");
        if (files.Length == 0)
        {
            MessageBox.Show("No history files to delete.", "Clear History",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var result = MessageBox.Show(
            $"This will permanently delete {files.Length} history file(s) in:\n{sqliteDir}\n\nThis cannot be undone. Continue?",
            "Clear History",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning,
            MessageBoxResult.No);

        if (result != MessageBoxResult.Yes) return;

        var deleted = 0;
        var errors  = 0;
        foreach (var file in files)
        {
            try   { File.Delete(file); deleted++; }
            catch { errors++; }
        }

        var msg = errors == 0
            ? $"Deleted {deleted} file(s). History will be rebuilt from Azure on the next Agent start."
            : $"Deleted {deleted} file(s). {errors} file(s) could not be deleted (they may be in use).";

        MessageBox.Show(msg, "Clear History", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private void UpdateHeader(ManagerState state)
    {
        if (!state.AgentRunning)
        {
            StateDot.Fill       = (Brush)FindResource("DisconnectedBrush");
            StateText.Text      = "AGENT NOT RUNNING";
            StatusDetailText.Text = state.ErrorMessage ?? state.StaleReason ?? "No recent status update";
            UptimeText.Text     = "Uptime: -";
            TicksText.Text      = "Ticks: -";
            LastTickText.Text   = "Last tick: -";
            return;
        }

        var s = state.Status!;
        var connected    = string.Equals(s.ConnectionState, "Connected",    StringComparison.OrdinalIgnoreCase);
        var reconnecting = string.Equals(s.ConnectionState, "Reconnecting", StringComparison.OrdinalIgnoreCase)
                        || string.Equals(s.ConnectionState, "Connecting",   StringComparison.OrdinalIgnoreCase);

        StateDot.Fill = connected
            ? (Brush)FindResource("ConnectedBrush")
            : reconnecting
                ? (Brush)FindResource("ReconnectingBrush")
                : (Brush)FindResource("DisconnectedBrush");

        StateText.Text = connected ? "CONNECTED"
            : reconnecting ? "RECONNECTING" : "DISCONNECTED";

        // Friendly status line — no internal URLs or IDs.
        StatusDetailText.Text = connected
            ? $"Live data streaming  •  {s.SubscribedSymbols?.Count ?? 0} instrument(s)"
            : !string.IsNullOrWhiteSpace(s.LastError)
                ? $"Last error: {s.LastError}"
                : "Waiting for connection…";

        var uptime = DateTime.UtcNow - s.StartedAtUtc;
        UptimeText.Text = $"Uptime: {FormatDuration(uptime)}   host: {s.MachineName}";

        TicksText.Text = $"Received: {s.TicksReceived:N0}   Dispatched: {s.TicksDispatched:N0}";

        if (s.LastTickUtc is { } last)
        {
            var ago = DateTime.UtcNow - last;
            LastTickText.Text = $"Last tick: {s.LastTickSymbol ?? "?"} {FormatAgo(ago)} ago";
        }
        else
        {
            LastTickText.Text = "Last tick: (none yet)";
        }
    }

    private void UpdateBackfillBanner(ManagerState state)
    {
        var msg = state.Status?.BackfillStatus;
        if (string.IsNullOrWhiteSpace(msg))
        {
            BackfillBanner.Visibility = Visibility.Collapsed;
        }
        else
        {
            BackfillText.Text         = msg;
            BackfillBanner.Visibility = Visibility.Visible;
        }
    }

    private void UpdateFooter(ManagerState state)
    {
        FooterText.Text = state.Status is null
            ? "Waiting for agent to start…"
            : $"History: {(string.IsNullOrEmpty(state.Status.SqliteSinkPath) ? "(disabled)" : state.Status.SqliteSinkPath)}" +
              (string.IsNullOrEmpty(state.Status.ScidSinkPath) ? "" : $"   SCID: {state.Status.ScidSinkPath}");
    }

    private void UpdateGrid(ManagerState state)
    {
        var symbols = state.Status?.SubscribedSymbols ?? Array.Empty<string>();
        var sqliteDir = state.Status?.SqliteSinkPath ?? string.Empty;
        var sqliteData = SqliteRowReader.Read(sqliteDir, symbols);
        var bySymbol = sqliteData.ToDictionary(s => s.Symbol, StringComparer.OrdinalIgnoreCase);

        // Add / update rows.
        foreach (var sym in symbols)
        {
            bySymbol.TryGetValue(sym, out var data);
            var existing = _rows.FirstOrDefault(r => r.Symbol == sym);
            if (existing is null)
            {
                _rows.Add(SymbolRow.From(sym, data));
            }
            else
            {
                existing.Apply(data);
            }
        }

        // Remove rows whose symbol is no longer subscribed.
        for (var i = _rows.Count - 1; i >= 0; i--)
        {
            if (!symbols.Contains(_rows[i].Symbol, StringComparer.OrdinalIgnoreCase))
                _rows.RemoveAt(i);
        }
    }

    private static string FormatDuration(TimeSpan t)
    {
        if (t.TotalDays >= 1)    return $"{(int)t.TotalDays}d {t.Hours:D2}:{t.Minutes:D2}:{t.Seconds:D2}";
        if (t.TotalHours >= 1)   return $"{t.Hours:D2}:{t.Minutes:D2}:{t.Seconds:D2}";
        return $"{t.Minutes:D2}:{t.Seconds:D2}";
    }

    private static string FormatAgo(TimeSpan t)
    {
        if (t.TotalSeconds < 2) return "just now";
        if (t.TotalSeconds < 60) return $"{(int)t.TotalSeconds}s";
        if (t.TotalMinutes < 60) return $"{(int)t.TotalMinutes}m {(int)(t.Seconds)}s";
        return $"{(int)t.TotalHours}h {t.Minutes:D2}m";
    }
}
