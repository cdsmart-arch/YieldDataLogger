// =============================================================================
//  YieldValueLine  –  NinjaTrader 8 Indicator
//  Namespace : NinjaTrader.NinjaScript.Indicators.CSI
//
//  REQUIREMENTS (place in Documents\NinjaTrader 8\bin\Custom\)
//  -----------------------------------------------------------
//  1. NCalc.dll          – grab latest from https://github.com/ncalc/ncalc/releases
//  2. System.Data.SQLite.dll + SQLite.Interop.dll
//       Copy from  C:\Program Files\NinjaTrader 8\bin64\  (already on disk)
//       or from    Documents\NinjaTrader 8\bin\Custom\  if you prefer a local copy.
//
//  After copying, open NinjaTrader → Tools → Edit NinjaScript → References
//  and add both DLLs so the compiler can find them.
//
//  INSTALL
//  -------
//  Copy this file to  Documents\NinjaTrader 8\bin\Custom\Indicators\CSI\
//  Then Tools → Edit NinjaScript → Compile  (or F5).
// =============================================================================

#region Using declarations
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using NinjaTrader.Gui.Chart;
using NinjaTrader.NinjaScript;
using SharpDX.Direct2D1;
#endregion

namespace NinjaTrader.NinjaScript.Indicators.CSI
{
    /// <summary>
    /// Reads up to 8 instrument prices from local YieldDataLogger SQLite files,
    /// evaluates an NCalc formula, and renders the result on a user-defined
    /// 0–1000 normalized scale in its own panel.
    ///
    /// A gradient-coloured toggle button is added to the chart toolbar so the
    /// line can be shown/hidden without removing the indicator.
    ///
    /// Formula examples
    ///   US10Y - US02Y
    ///   (US10Y - US02Y) * 500 + 500
    ///   US10Y / US30Y * 1000
    ///   (US10Y - DE10Y) * 200 + 500
    /// </summary>
    [Gui.CategoryOrder("Data",       1)]
    [Gui.CategoryOrder("Appearance", 2)]
    [Gui.CategoryOrder("Scale",      3)]
    public class YieldValueLine : NinjaTrader.NinjaScript.Indicators.Indicator
    {
        // ── private state ─────────────────────────────────────────────────────
        private readonly object _lock = new object();

        // symbol → sorted (unix-seconds → price) lookup built from SQLite
        private Dictionary<string, SortedList<long, double>> _data;

        // symbols actually referenced (parsed from the Instrument1..8 fields)
        private List<string> _symbols;

        // SharpDX brushes – created lazily, disposed on render-target change
        private SharpDX.Direct2D1.SolidColorBrush _lineBrush;
        private SharpDX.Direct2D1.SolidColorBrush _midBrush;

        // Chart toolbar button
        private System.Windows.Controls.Button     _toggleBtn;
        private System.Windows.Controls.ToolBar    _ownerToolBar;
        private bool _visible = true;

        // Background refresh for realtime
        private System.Threading.Timer _timer;

        // ── lifecycle ─────────────────────────────────────────────────────────
        protected override void OnStateChange()
        {
            if (State == State.SetDefaults)
            {
                Description              = "Plots a NCalc formula over YieldDataLogger SQLite price data on a 0–1000 normalised scale.";
                Name                     = "YieldValueLine";
                Calculate                = Calculate.OnBarClose;
                IsOverlay                = false;
                DisplayInDataBox         = true;
                DrawOnPricePanel         = false;
                ScaleJustification       = NinjaTrader.Gui.Chart.ScaleJustification.Right;
                IsSuspendedWhileInactive = false;

                // ── default parameter values ──────────────────────────────
                Formula        = "US10Y - US02Y";
                Instrument1    = "US10Y";
                Instrument2    = "US02Y";
                Instrument3    = string.Empty;
                Instrument4    = string.Empty;
                Instrument5    = string.Empty;
                Instrument6    = string.Empty;
                Instrument7    = string.Empty;
                Instrument8    = string.Empty;
                DataPath       = @"%ProgramData%\YieldDataLogger\Yields";
                RefreshSeconds = 5;
                LineColour     = System.Windows.Media.Colors.DodgerBlue;
                LineWidth      = 2;
                ButtonLabel    = string.Empty;   // empty = auto from Formula
                ScaleMin       = 0.0;
                ScaleMax       = 1000.0;

                // One plot entry so the value appears in the DataBox
                AddPlot(new Stroke(System.Windows.Media.Brushes.DodgerBlue, 2),
                        PlotStyle.Line, "Value");
            }
            else if (State == State.Configure)
            {
                _data    = new Dictionary<string, SortedList<long, double>>(StringComparer.OrdinalIgnoreCase);
                _symbols = CollectSymbols();
            }
            else if (State == State.DataLoaded)
            {
                LoadAllData();

                if (ChartControl != null)
                    ChartControl.Dispatcher.InvokeAsync(AddToolbarButton);
            }
            else if (State == State.Realtime)
            {
                var interval = TimeSpan.FromSeconds(Math.Max(1, RefreshSeconds));
                _timer = new System.Threading.Timer(_ =>
                {
                    LoadAllData();
                    ChartControl?.Dispatcher.InvokeAsync(() => ChartControl.InvalidateVisual());
                }, null, interval, interval);
            }
            else if (State == State.Terminated)
            {
                _timer?.Dispose();
                _timer = null;
                ReleaseSharpDx();

                if (ChartControl != null && _toggleBtn != null)
                    ChartControl.Dispatcher.InvokeAsync(RemoveToolbarButton);
            }
        }

        // ── bar update (DataBox + historical values) ──────────────────────────
        protected override void OnBarUpdate()
        {
            double v = ComputeAtTime(BarToUnix(Time[0]));
            Values[0][0] = double.IsNaN(v) || double.IsInfinity(v) ? double.NaN : v;
        }

        // ── custom rendering on 0–1000 scale ──────────────────────────────────
        protected override void OnRender(ChartControl chartControl, ChartScale chartScale)
        {
            if (!_visible) return;

            base.OnRender(chartControl, chartScale);
            EnsureSharpDx();

            float panelTop = (float)ChartPanel.Y;
            float panelH   = (float)ChartPanel.H;
            float range    = (float)(ScaleMax - ScaleMin);
            if (range <= 0f) return;

            // Map a raw formula value to pixel y inside this panel
            float ToY(double val)
            {
                float norm = (float)Math.Max(0, Math.Min(1, (val - ScaleMin) / range));
                return panelTop + (1f - norm) * panelH;
            }

            int panelW = ChartPanel.W;

            // ── mid-line reference (50%) ─────────────────────────────────
            float midY = ToY(ScaleMin + range / 2.0);
            RenderTarget.DrawLine(
                new SharpDX.Vector2(0,      midY),
                new SharpDX.Vector2(panelW, midY),
                _midBrush, 1f);

            // ── value line ────────────────────────────────────────────────
            float stroke = (float)Math.Max(1, LineWidth);
            SharpDX.Vector2? prev = null;

            for (int i = ChartBars.FromIndex; i <= ChartBars.ToIndex; i++)
            {
                double v = Values[0].GetValueAt(i);
                if (double.IsNaN(v) || double.IsInfinity(v)) { prev = null; continue; }

                float x  = (float)chartControl.GetXByBarIndex(ChartBars, i);
                float y  = ToY(v);
                var   pt = new SharpDX.Vector2(x, y);

                if (prev.HasValue)
                    RenderTarget.DrawLine(prev.Value, pt, _lineBrush, stroke);

                prev = pt;
            }

            // ── current-value callout at right edge (realtime) ────────────
            double latest = ComputeLatest();
            if (!double.IsNaN(latest))
            {
                float cy  = ToY(latest);
                float tick = 6f;
                // Small horizontal tick on the y-axis
                RenderTarget.DrawLine(
                    new SharpDX.Vector2(panelW - tick, cy),
                    new SharpDX.Vector2(panelW,        cy),
                    _lineBrush, stroke + 1f);
            }
        }

        // ── SharpDX resource management ───────────────────────────────────────
        private void EnsureSharpDx()
        {
            if (_lineBrush == null || _lineBrush.IsDisposed)
                _lineBrush = new SharpDX.Direct2D1.SolidColorBrush(RenderTarget,
                    ToSharpDxColor(LineColour));

            if (_midBrush == null || _midBrush.IsDisposed)
                _midBrush  = new SharpDX.Direct2D1.SolidColorBrush(RenderTarget,
                    new SharpDX.Color4(0.35f, 0.35f, 0.35f, 0.45f));
        }

        private void ReleaseSharpDx()
        {
            _lineBrush?.Dispose(); _lineBrush = null;
            _midBrush?.Dispose();  _midBrush  = null;
        }

        public override void OnRenderTargetChanged() => ReleaseSharpDx();

        // ── data loading ──────────────────────────────────────────────────────
        private List<string> CollectSymbols()
        {
            var list = new List<string>();
            foreach (var s in new[] { Instrument1, Instrument2, Instrument3, Instrument4,
                                      Instrument5, Instrument6, Instrument7, Instrument8 })
            {
                if (!string.IsNullOrWhiteSpace(s))
                {
                    var u = s.Trim().ToUpperInvariant();
                    if (!list.Contains(u)) list.Add(u);
                }
            }
            return list;
        }

        private void LoadAllData()
        {
            var dir = Environment.ExpandEnvironmentVariables(DataPath ?? string.Empty);
            lock (_lock)
            {
                foreach (var sym in _symbols)
                {
                    var file = Path.Combine(dir, sym + ".sqlite");
                    if (!File.Exists(file)) continue;
                    try   { _data[sym] = ReadSqlite(file); }
                    catch (Exception ex) { Print($"YieldValueLine: load failed for {sym}: {ex.Message}"); }
                }
            }
        }

        private static SortedList<long, double> ReadSqlite(string file)
        {
            var result = new SortedList<long, double>();
            using var cn = new System.Data.SQLite.SQLiteConnection(
                $"Data Source={file};Version=3;Read Only=True;");
            cn.Open();
            using var cmd = new System.Data.SQLite.SQLiteCommand(
                "SELECT TIMESTAMP, CLOSE FROM PriceData ORDER BY TIMESTAMP ASC", cn);
            using var rdr = cmd.ExecuteReader();
            while (rdr.Read())
            {
                long   ts  = (long)rdr.GetDouble(0);
                double val = rdr.GetDouble(1);
                result[ts] = val;           // last write wins on duplicate key
            }
            return result;
        }

        // ── formula evaluation ────────────────────────────────────────────────
        private double ComputeAtTime(long unixSec)
        {
            var prices = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
            lock (_lock)
            {
                foreach (var sym in _symbols)
                {
                    if (!_data.TryGetValue(sym, out var series) || series.Count == 0)
                        return double.NaN;
                    double p = FloorLookup(series, unixSec);
                    if (double.IsNaN(p)) return double.NaN;
                    prices[sym] = p;
                }
            }
            return EvalFormula(prices);
        }

        /// <summary>Computes the formula using the most recent price for each symbol.</summary>
        private double ComputeLatest()
        {
            var prices = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
            lock (_lock)
            {
                foreach (var sym in _symbols)
                {
                    if (!_data.TryGetValue(sym, out var series) || series.Count == 0)
                        return double.NaN;
                    prices[sym] = series.Values[series.Count - 1];
                }
            }
            return EvalFormula(prices);
        }

        private double EvalFormula(Dictionary<string, double> prices)
        {
            if (string.IsNullOrWhiteSpace(Formula)) return double.NaN;
            try
            {
                var expr = new NCalc.Expression(Formula);
                foreach (var kv in prices) expr.Parameters[kv.Key] = kv.Value;
                return Convert.ToDouble(expr.Evaluate());
            }
            catch (Exception ex)
            {
                if (State == State.Historical)
                    Print($"YieldValueLine formula error: {ex.Message}");
                return double.NaN;
            }
        }

        /// <summary>Binary search for the largest key ≤ target, returning its value.</summary>
        private static double FloorLookup(SortedList<long, double> s, long target)
        {
            if (s.Count == 0)                  return double.NaN;
            if (target < s.Keys[0])            return double.NaN;
            if (target >= s.Keys[s.Count - 1]) return s.Values[s.Count - 1];

            int lo = 0, hi = s.Count - 1;
            while (lo <= hi)
            {
                int mid = (lo + hi) / 2;
                if      (s.Keys[mid] == target) return s.Values[mid];
                else if (s.Keys[mid] <  target) lo = mid + 1;
                else                            hi = mid - 1;
            }
            return hi >= 0 ? s.Values[hi] : double.NaN;
        }

        // ── chart toolbar button ──────────────────────────────────────────────
        private void AddToolbarButton()
        {
            try
            {
                // Locate the first ToolBar in the chart's visual tree
                var tb = FindChild<System.Windows.Controls.ToolBar>(ChartControl);
                if (tb == null) return;
                _ownerToolBar = tb;

                // Gradient brush matching the line colour
                var grad = new LinearGradientBrush
                {
                    StartPoint = new Point(0, 0),
                    EndPoint   = new Point(0, 1),
                };
                grad.GradientStops.Add(new GradientStop(Lighten(LineColour, 40), 0));
                grad.GradientStops.Add(new GradientStop(Darken(LineColour,  30), 1));

                var label = string.IsNullOrWhiteSpace(ButtonLabel)
                    ? (Formula.Length > 22 ? Formula.Substring(0, 22) + "…" : Formula)
                    : ButtonLabel;

                _toggleBtn = new System.Windows.Controls.Button
                {
                    Content         = label,
                    ToolTip         = $"Show / hide  {Formula}",
                    Background      = grad,
                    Foreground      = System.Windows.Media.Brushes.White,
                    BorderBrush     = new SolidColorBrush(LineColour),
                    BorderThickness = new Thickness(1),
                    Padding         = new Thickness(8, 2, 8, 2),
                    Margin          = new Thickness(3, 1, 3, 1),
                    FontSize        = 11,
                    FontWeight      = FontWeights.SemiBold,
                    Cursor          = System.Windows.Input.Cursors.Hand,
                };
                _toggleBtn.Click += OnToggle;
                _ownerToolBar.Items.Add(_toggleBtn);
            }
            catch (Exception ex)
            {
                Print($"YieldValueLine: toolbar button error: {ex.Message}");
            }
        }

        private void RemoveToolbarButton()
        {
            try
            {
                if (_ownerToolBar != null && _toggleBtn != null)
                {
                    _toggleBtn.Click -= OnToggle;
                    _ownerToolBar.Items.Remove(_toggleBtn);
                }
            }
            catch { /* ignore on shutdown */ }
        }

        private void OnToggle(object sender, RoutedEventArgs e)
        {
            _visible           = !_visible;
            _toggleBtn.Opacity = _visible ? 1.0 : 0.35;
            ChartControl?.InvalidateVisual();
        }

        // ── visual tree helper ────────────────────────────────────────────────
        private static T FindChild<T>(DependencyObject parent) where T : DependencyObject
        {
            if (parent == null) return null;
            int count = System.Windows.Media.VisualTreeHelper.GetChildrenCount(parent);
            for (int i = 0; i < count; i++)
            {
                var child = System.Windows.Media.VisualTreeHelper.GetChild(parent, i);
                if (child is T hit)    return hit;
                var desc = FindChild<T>(child);
                if (desc != null)      return desc;
            }
            return null;
        }

        // ── colour helpers ────────────────────────────────────────────────────
        private static SharpDX.Color4 ToSharpDxColor(System.Windows.Media.Color c) =>
            new SharpDX.Color4(c.R / 255f, c.G / 255f, c.B / 255f, c.A / 255f);

        private static System.Windows.Media.Color Lighten(System.Windows.Media.Color c, int d) =>
            System.Windows.Media.Color.FromArgb(c.A,
                (byte)Math.Min(255, c.R + d),
                (byte)Math.Min(255, c.G + d),
                (byte)Math.Min(255, c.B + d));

        private static System.Windows.Media.Color Darken(System.Windows.Media.Color c, int d) =>
            System.Windows.Media.Color.FromArgb(c.A,
                (byte)Math.Max(0, c.R - d),
                (byte)Math.Max(0, c.G - d),
                (byte)Math.Max(0, c.B - d));

        // ── unix time helper ──────────────────────────────────────────────────
        private static readonly DateTime _epoch = new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        private static long BarToUnix(DateTime dt) =>
            (long)(dt.ToUniversalTime() - _epoch).TotalSeconds;

        // ── published properties ──────────────────────────────────────────────
        #region Properties

        [NinjaScriptProperty]
        [Display(Name = "Formula  (NCalc)", Order = 0, GroupName = "Data",
                 Description = "NCalc expression. Use the exact symbol names from Instrument 1-8 as variables.\nExamples:  US10Y - US02Y     (US10Y - DE10Y) * 200 + 500")]
        public string Formula { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Instrument 1", Order = 1, GroupName = "Data")]
        public string Instrument1 { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Instrument 2", Order = 2, GroupName = "Data")]
        public string Instrument2 { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Instrument 3", Order = 3, GroupName = "Data")]
        public string Instrument3 { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Instrument 4", Order = 4, GroupName = "Data")]
        public string Instrument4 { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Instrument 5", Order = 5, GroupName = "Data")]
        public string Instrument5 { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Instrument 6", Order = 6, GroupName = "Data")]
        public string Instrument6 { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Instrument 7", Order = 7, GroupName = "Data")]
        public string Instrument7 { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Instrument 8", Order = 8, GroupName = "Data")]
        public string Instrument8 { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "SQLite folder", Order = 9, GroupName = "Data",
                 Description = @"Folder containing {SYMBOL}.sqlite files written by the Agent. Environment variables are expanded.")]
        public string DataPath { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Refresh (seconds)", Order = 10, GroupName = "Data",
                 Description = "How often to re-read the SQLite files in realtime mode.")]
        public int RefreshSeconds { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Line colour", Order = 0, GroupName = "Appearance")]
        public System.Windows.Media.Color LineColour { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Line width", Order = 1, GroupName = "Appearance")]
        public int LineWidth { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Button label", Order = 2, GroupName = "Appearance",
                 Description = "Text shown on the chart toolbar toggle button. Leave blank to auto-generate from Formula.")]
        public string ButtonLabel { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Scale minimum  (raw → 0)", Order = 0, GroupName = "Scale",
                 Description = "Raw formula value that maps to the bottom of the panel (0). E.g. –2.0 for a –2% spread.")]
        public double ScaleMin { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Scale maximum  (raw → 1000)", Order = 1, GroupName = "Scale",
                 Description = "Raw formula value that maps to the top of the panel (1000). E.g. 3.0 for a 3% spread.")]
        public double ScaleMax { get; set; }

        #endregion
    }
}
