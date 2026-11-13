using System.Collections.ObjectModel;
using System.IO;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Interop;

namespace YieldDataLogger.Manager;

/// <summary>
/// End-user subscription picker. Fetches the admin-maintained catalog from
/// <c>GET /api/instruments</c>, pre-selects whatever the Agent is currently subscribed to
/// (per the local <c>subscriptions.json</c>), and on Apply writes a new subscriptions file
/// that the Agent's file watcher consumes within a second or so.
/// </summary>
public partial class SubscriptionsWindow : Window
{
    private readonly ManagerConfig _config;
    private readonly string _apiBaseUrl;
    private readonly ObservableCollection<InstrumentRow> _rows = new();
    private readonly HashSet<string> _initialSubs = new(StringComparer.Ordinal);
    private readonly HashSet<string> _initialScid = new(StringComparer.Ordinal);
    private string _initialSierraFolder = string.Empty;
    private IReadOnlyList<string> _detectedSierra = Array.Empty<string>();
    private ICollectionView? _view;
    private int _initialHistoryDays  = 30;
    private int _initialBackfillDelay = 300;

    internal SubscriptionsWindow(ManagerConfig config, string apiBaseUrl)
    {
        InitializeComponent();
        _config = config;
        _apiBaseUrl = apiBaseUrl;
        Grid.ItemsSource = _rows;
        SourceInitialized += OnSourceInitialized;
        Loaded += async (_, _) => await LoadAsync();
    }

    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        Win32Interop.EnableDarkTitleBar(hwnd);
        Win32Interop.SetCaptionColor(hwnd, 0x16, 0x1B, 0x22);
    }

    private async Task LoadAsync()
    {
        StatusText.Text = $"Loading instruments from {_apiBaseUrl} ...";
        try
        {
            var client = new InstrumentsApiClient(_apiBaseUrl);
            var instruments = await client.GetAllAsync();

            // Sierra installs on this PC - disk scan, so keep it off the UI thread.
            _detectedSierra = await Task.Run(SierraLocator.FindDataFolders);
            SierraDetectBtn.IsEnabled = _detectedSierra.Count > 0;
            if (_detectedSierra.Count == 0)
                SierraDetectBtn.ToolTip = "No Sierra Chart install found automatically - use Browse.";

            // Current subscribed set - file is the source of truth, empty set is fine.
            var current = SubscriptionsFile.Load(_config.SubscriptionsFilePath);

            // Sierra selection: the file wins; if the Manager never saved one, show what the
            // Agent is actually doing (appsettings fallback, reported via status.json).
            var status = new AgentStatusReader().Read(_config).Status;
            _initialScid.Clear();
            foreach (var s in current?.ScidSymbols ?? status?.ScidSymbols ?? Array.Empty<string>())
                _initialScid.Add(s.Trim().ToUpperInvariant());
            _initialSierraFolder =
                !string.IsNullOrWhiteSpace(current?.ScidFolder) ? current!.ScidFolder!
                : !string.IsNullOrWhiteSpace(status?.ScidSinkPath) ? status!.ScidSinkPath
                : _detectedSierra.FirstOrDefault() ?? string.Empty;
            SierraFolderBox.Text = _initialSierraFolder;
            _initialSubs.Clear();
            if (current is not null)
            {
                foreach (var s in current.Symbols) _initialSubs.Add(s.Trim().ToUpperInvariant());
                // Pre-populate HistoryDays; fall back to 30 if never set.
                _initialHistoryDays   = current.HistoryDays    > 0 ? current.HistoryDays    : 30;
                _initialBackfillDelay = current.BackfillDelayMs > 0 ? current.BackfillDelayMs : 300;
            }
            HistoryDaysBox.Text   = _initialHistoryDays.ToString();
            BackfillDelayBox.Text = _initialBackfillDelay.ToString();

            _rows.Clear();
            foreach (var i in instruments)
            {
                var row = new InstrumentRow(i, _initialSubs.Contains(i.CanonicalSymbol),
                                            _initialScid.Contains(i.CanonicalSymbol));
                row.PropertyChanged += OnRowPropertyChanged;
                _rows.Add(row);
            }

            _view = CollectionViewSource.GetDefaultView(_rows);
            _view.Filter = FilterPredicate;
            _view.SortDescriptions.Add(new System.ComponentModel.SortDescription(
                nameof(InstrumentRow.Category), System.ComponentModel.ListSortDirection.Ascending));
            _view.SortDescriptions.Add(new System.ComponentModel.SortDescription(
                nameof(InstrumentRow.CanonicalSymbol), System.ComponentModel.ListSortDirection.Ascending));

            UpdateStatus();
        }
        catch (Exception ex)
        {
            StatusText.Text = "Failed to load instruments: " + ex.Message;
            ApplyBtn.IsEnabled = false;
        }
    }

    private void OnRowPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(InstrumentRow.Subscribed) or nameof(InstrumentRow.Scid))
            UpdateStatus();
    }

    private void UpdateStatus()
    {
        var selected = _rows.Count(r => r.Subscribed);
        var sierra   = _rows.Count(r => r.Scid);
        var total    = _rows.Count;
        var dirty    = IsDirty();
        StatusText.Text = $"{selected} of {total} selected, {sierra} to Sierra"
                          + (dirty ? " - unsaved changes" : string.Empty);
        ApplyBtn.IsEnabled = dirty;
        UpdateSierraHint();
    }

    private bool IsDirty()
    {
        var currentSet = _rows.Where(r => r.Subscribed).Select(r => r.CanonicalSymbol).ToHashSet(StringComparer.Ordinal);
        if (currentSet.Count != _initialSubs.Count) return true;
        foreach (var s in currentSet) if (!_initialSubs.Contains(s)) return true;
        var scidSet = _rows.Where(r => r.Scid).Select(r => r.CanonicalSymbol).ToHashSet(StringComparer.Ordinal);
        if (!scidSet.SetEquals(_initialScid)) return true;
        if (!string.Equals(SierraFolder(), _initialSierraFolder, StringComparison.OrdinalIgnoreCase)) return true;
        if (ParseHistoryDays()   != _initialHistoryDays)   return true;
        if (ParseBackfillDelay() != _initialBackfillDelay) return true;
        return false;
    }

    private int ParseHistoryDays()
    {
        return int.TryParse(HistoryDaysBox?.Text?.Trim(), out var d) && d > 0 ? d : _initialHistoryDays;
    }

    private int ParseBackfillDelay()
    {
        return int.TryParse(BackfillDelayBox?.Text?.Trim(), out var d) && d >= 0 ? d : _initialBackfillDelay;
    }

    private string SierraFolder() => SierraFolderBox?.Text?.Trim().TrimEnd('\\') ?? string.Empty;

    /// <summary>Live hint under the folder box: what Sierra symbol names to use, or what's wrong.</summary>
    private void UpdateSierraHint()
    {
        if (SierraHint is null) return;
        var folder = SierraFolder();
        var first  = _rows.FirstOrDefault(r => r.Scid)?.CanonicalSymbol ?? "US10Y";
        if (string.IsNullOrEmpty(folder))
            SierraHint.Text = "Pick the Data folder of the Sierra Chart install that should receive the .scid files.";
        else if (!Directory.Exists(folder))
            SierraHint.Text = "Folder does not exist.";
        else if (!SierraLocator.LooksLikeSierraData(folder))
            SierraHint.Text = "This doesn't look like a Sierra Chart Data folder (no .scid/.dly files) - double-check before applying.";
        else
            SierraHint.Text = $"Ticked symbols are written as _{first}.scid etc. In Sierra Chart, open a chart with symbol _{first}.";
    }

    private void OnSierraFolderChanged(object sender, TextChangedEventArgs e) => UpdateStatus();

    private void OnSierraDetected(object sender, RoutedEventArgs e)
    {
        var menu = new ContextMenu
        {
            PlacementTarget = SierraDetectBtn,
            Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom,
        };
        foreach (var folder in _detectedSierra)
        {
            var item = new MenuItem { Header = folder };
            item.Click += (_, _) => SierraFolderBox.Text = folder;
            menu.Items.Add(item);
        }
        menu.IsOpen = true;
    }

    private void OnSierraBrowse(object sender, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.OpenFolderDialog
        {
            Title = "Select the Sierra Chart Data folder",
        };
        var current = SierraFolder();
        if (Directory.Exists(current)) dlg.InitialDirectory = current;
        if (dlg.ShowDialog(this) == true) SierraFolderBox.Text = dlg.FolderName;
    }

    // -- Event handlers --------------------------------------------------------

    private void OnSearchChanged(object sender, TextChangedEventArgs e) => _view?.Refresh();

    private void OnHistoryDaysChanged(object sender, TextChangedEventArgs e) => UpdateStatus();

    private bool FilterPredicate(object o)
    {
        if (o is not InstrumentRow r) return false;
        var q = SearchBox.Text?.Trim();
        if (string.IsNullOrEmpty(q)) return true;
        return (r.CanonicalSymbol?.Contains(q, StringComparison.OrdinalIgnoreCase) == true)
            || (r.Category?.Contains(q, StringComparison.OrdinalIgnoreCase) == true)
            || (r.CnbcSymbol?.Contains(q, StringComparison.OrdinalIgnoreCase) == true);
    }

    private void OnSelectAll(object sender, RoutedEventArgs e)
    {
        // Respect any active filter - only toggle what's currently visible.
        foreach (var r in VisibleRows()) r.Subscribed = true;
    }

    private void OnSelectNone(object sender, RoutedEventArgs e)
    {
        foreach (var r in VisibleRows()) r.Subscribed = false;
    }

    private IEnumerable<InstrumentRow> VisibleRows()
    {
        if (_view is null) return _rows;
        return _view.Cast<InstrumentRow>();
    }

    private void OnApply(object sender, RoutedEventArgs e)
    {
        try
        {
            var symbols      = _rows.Where(r => r.Subscribed).Select(r => r.CanonicalSymbol).ToArray();
            var scidSymbols  = _rows.Where(r => r.Scid).Select(r => r.CanonicalSymbol).ToArray();
            var sierraFolder = SierraFolder();
            var historyDays  = ParseHistoryDays();
            var backfillDelay = ParseBackfillDelay();

            // Never point the Agent at a folder that isn't there - a typo would otherwise
            // silently create a stray directory and the user would see nothing in Sierra.
            if (scidSymbols.Length > 0 && (sierraFolder.Length == 0 || !Directory.Exists(sierraFolder)))
            {
                StatusText.Text = "Choose an existing Sierra Chart Data folder before sending symbols to Sierra.";
                return;
            }

            SubscriptionsFile.Save(_config.SubscriptionsFilePath, symbols, historyDays, backfillDelay,
                                   scidSymbols, sierraFolder);
            _initialSubs.Clear();
            foreach (var s in symbols) _initialSubs.Add(s);
            _initialScid.Clear();
            foreach (var s in scidSymbols) _initialScid.Add(s);
            _initialSierraFolder = sierraFolder;
            _initialHistoryDays   = historyDays;
            _initialBackfillDelay = backfillDelay;
            StatusText.Text = $"Saved. Agent will pick up changes within ~1 second.";
            ApplyBtn.IsEnabled = false;
        }
        catch (Exception ex)
        {
            StatusText.Text = "Save failed: " + ex.Message;
        }
    }

    private void OnCancel(object sender, RoutedEventArgs e) => Close();
}

/// <summary>
/// Row bound to the instruments grid. Subscribed and Scid are editable; Scid implies
/// Subscribed (the Agent only receives ticks for subscribed symbols), so ticking Sierra
/// subscribes the symbol and unsubscribing clears Sierra.
/// </summary>
internal sealed class InstrumentRow : INotifyPropertyChanged
{
    public string   CanonicalSymbol { get; }
    public int?     InvestingPid    { get; }
    public string?  CnbcSymbol      { get; }
    public string?  Category        { get; }
    public string   Source          { get; }

    private bool _subscribed;
    public bool Subscribed
    {
        get => _subscribed;
        set
        {
            if (_subscribed == value) return;
            _subscribed = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Subscribed)));
            if (!value) Scid = false;
        }
    }

    private bool _scid;
    public bool Scid
    {
        get => _scid;
        set
        {
            if (_scid == value) return;
            _scid = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Scid)));
            if (value) Subscribed = true;
        }
    }

    public InstrumentRow(InstrumentsApiClient.InstrumentRecord r, bool subscribed, bool scid = false)
    {
        CanonicalSymbol = r.CanonicalSymbol;
        InvestingPid    = r.InvestingPid;
        CnbcSymbol      = r.CnbcSymbol;
        Category        = r.Category;
        Source          = r.Source;
        _subscribed     = subscribed || scid;
        _scid           = scid;
    }

    public event PropertyChangedEventHandler? PropertyChanged;
}
