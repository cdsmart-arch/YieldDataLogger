using System.Collections.ObjectModel;
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

            // Current subscribed set - file is the source of truth, empty set is fine.
            var current = SubscriptionsFile.Load(_config.SubscriptionsFilePath);
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
                var row = new InstrumentRow(i, _initialSubs.Contains(i.CanonicalSymbol));
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
        if (e.PropertyName == nameof(InstrumentRow.Subscribed))
            UpdateStatus();
    }

    private void UpdateStatus()
    {
        var selected = _rows.Count(r => r.Subscribed);
        var total    = _rows.Count;
        var dirty    = IsDirty();
        StatusText.Text = $"{selected} of {total} selected" + (dirty ? " - unsaved changes" : string.Empty);
        ApplyBtn.IsEnabled = dirty;
    }

    private bool IsDirty()
    {
        var currentSet = _rows.Where(r => r.Subscribed).Select(r => r.CanonicalSymbol).ToHashSet(StringComparer.Ordinal);
        if (currentSet.Count != _initialSubs.Count) return true;
        foreach (var s in currentSet) if (!_initialSubs.Contains(s)) return true;
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
            var symbols      = _rows.Where(r => r.Subscribed).Select(r => r.CanonicalSymbol);
            var historyDays  = ParseHistoryDays();
            var backfillDelay = ParseBackfillDelay();
            SubscriptionsFile.Save(_config.SubscriptionsFilePath, symbols, historyDays, backfillDelay);
            _initialSubs.Clear();
            foreach (var s in _rows.Where(r => r.Subscribed)) _initialSubs.Add(s.CanonicalSymbol);
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

/// <summary>Row bound to the instruments grid. Subscribed is the only editable property.</summary>
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
        }
    }

    public InstrumentRow(InstrumentsApiClient.InstrumentRecord r, bool subscribed)
    {
        CanonicalSymbol = r.CanonicalSymbol;
        InvestingPid    = r.InvestingPid;
        CnbcSymbol      = r.CnbcSymbol;
        Category        = r.Category;
        Source          = r.Source;
        _subscribed     = subscribed;
    }

    public event PropertyChangedEventHandler? PropertyChanged;
}
