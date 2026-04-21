using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;
using Brush = System.Windows.Media.Brush;
using Color = System.Windows.Media.Color;

namespace YieldDataLogger.Manager;

/// <summary>
/// One row in the dashboard grid. INotifyPropertyChanged lets each periodic refresh update
/// individual cells in place instead of rebuilding the whole ObservableCollection (which
/// would flash and lose selection).
/// </summary>
internal sealed class SymbolRow : INotifyPropertyChanged
{
    // Shared foreground brushes; frozen for perf and to avoid per-row allocations during ticks.
    private static readonly Brush UpBrush      = Freeze(new SolidColorBrush(Color.FromRgb(0x3F, 0xB9, 0x50))); // green
    private static readonly Brush DownBrush    = Freeze(new SolidColorBrush(Color.FromRgb(0xF8, 0x51, 0x49))); // red
    private static readonly Brush NeutralBrush = Freeze(new SolidColorBrush(Color.FromRgb(0xE6, 0xE6, 0xE6))); // default text

    // Flash overlay colours - semi-transparent so the price text stays legible during the flash.
    private static readonly Color FlashUpColor   = Color.FromArgb(0x90, 0x3F, 0xB9, 0x50);
    private static readonly Color FlashDownColor = Color.FromArgb(0x90, 0xF8, 0x51, 0x49);
    private static readonly Duration FlashDuration = new(TimeSpan.FromMilliseconds(550));

    private static Brush Freeze(SolidColorBrush b) { b.Freeze(); return b; }

    public string Symbol { get; }

    private double _price;
    private DateTime _lastUpdateUtc;
    private long _rows;
    private string? _error;
    private Brush _priceBrush = NeutralBrush;

    // Per-row, mutable-because-animated brush backing the price cell's Background. Must NOT
    // be frozen and must be unique per row so flashes on different symbols don't overlap.
    private readonly SolidColorBrush _priceFlashBrush = new(Colors.Transparent);

    public SymbolRow(string symbol) { Symbol = symbol; }

    public static SymbolRow From(string symbol, SqliteRowReader.SymbolData? data)
    {
        var r = new SymbolRow(symbol);
        r.Apply(data);
        return r;
    }

    public void Apply(SqliteRowReader.SymbolData? data)
    {
        if (data is null)
        {
            Set(ref _price, 0, nameof(PriceDisplay));
            Set(ref _rows, 0, nameof(RowsDisplay));
            Set(ref _lastUpdateUtc, DateTime.MinValue, nameof(LastUpdateDisplay), nameof(AgeDisplay));
            Set(ref _error, "no data", nameof(Error));
            // Row went empty - drop back to neutral.
            Set(ref _priceBrush, NeutralBrush, nameof(PriceBrush));
            return;
        }

        // Tick direction is evaluated against the previously observed non-zero price, so
        // the very first observation (or a transient zero) doesn't flip the colour.
        var newPrice = data.LastPrice;
        var oldPrice = _price;
        if (newPrice > 0 && oldPrice > 0 && newPrice != oldPrice)
        {
            var up = newPrice > oldPrice;
            Set(ref _priceBrush, up ? UpBrush : DownBrush, nameof(PriceBrush));
            FlashBackground(up ? FlashUpColor : FlashDownColor);
        }
        // else: keep whatever brush was showing (latest direction persists until the next real change)

        Set(ref _price, newPrice, nameof(PriceDisplay));
        Set(ref _rows, data.Rows, nameof(RowsDisplay));
        Set(ref _lastUpdateUtc, data.LastUpdateUtc, nameof(LastUpdateDisplay), nameof(AgeDisplay));
        Set(ref _error, data.Error, nameof(Error));
    }

    public string PriceDisplay    => _price == 0 ? "-" : _price.ToString("0.######");
    public Brush  PriceBrush      => _priceBrush;
    public Brush  PriceFlashBrush => _priceFlashBrush;

    /// <summary>
    /// Bloomberg-style momentary tint on the price cell. Starts at the given colour and
    /// animates to transparent. Starting a new flash implicitly replaces any in-flight one
    /// (BeginAnimation semantics) which is the desired behaviour for rapid consecutive ticks.
    /// </summary>
    private void FlashBackground(Color start)
    {
        var anim = new ColorAnimation
        {
            From         = start,
            To           = Colors.Transparent,
            Duration     = FlashDuration,
            FillBehavior = FillBehavior.HoldEnd,
        };
        _priceFlashBrush.BeginAnimation(SolidColorBrush.ColorProperty, anim);
    }
    public string RowsDisplay  => _rows.ToString("N0");
    public string LastUpdateDisplay =>
        _lastUpdateUtc == DateTime.MinValue ? "-" : _lastUpdateUtc.ToString("yyyy-MM-dd HH:mm:ss");
    public string AgeDisplay
    {
        get
        {
            if (_lastUpdateUtc == DateTime.MinValue) return "-";
            var t = DateTime.UtcNow - _lastUpdateUtc;
            if (t.TotalSeconds < 2)  return "just now";
            if (t.TotalSeconds < 60) return $"{(int)t.TotalSeconds}s";
            if (t.TotalMinutes < 60) return $"{(int)t.TotalMinutes}m";
            if (t.TotalHours < 24)   return $"{(int)t.TotalHours}h";
            return $"{(int)t.TotalDays}d";
        }
    }
    public string? Error => _error;

    // -- INPC plumbing -------------------------------------------------------

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnChanged([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    private void Set<T>(ref T field, T value, params string[] propertiesToRaise)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
        {
            // Still raise AgeDisplay every tick so it refreshes even when the underlying ts is unchanged.
            foreach (var p in propertiesToRaise)
                if (p == nameof(AgeDisplay)) OnChanged(p);
            return;
        }
        field = value;
        foreach (var p in propertiesToRaise) OnChanged(p);
    }
}
