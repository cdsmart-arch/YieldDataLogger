# YieldValueLine – NinjaTrader 8 Indicator

## Dependencies

| DLL | Purpose | Source |
|---|---|---|
| **NCalc.dll** | Formula evaluation | [github.com/ncalc/ncalc/releases](https://github.com/ncalc/ncalc/releases) – download latest zip, grab `NCalc.dll` |
| **System.Data.SQLite.dll** | Reads `.sqlite` price files | Already on disk: `C:\Program Files\NinjaTrader 8\bin64\` |
| **SQLite.Interop.dll** | Native SQLite library (required by above) | Same folder as above |

**SharpDX** is bundled with NinjaTrader – no action needed.

---

## One-time setup (new machine)

### 1. Copy DLLs
Copy all three DLLs to:
```
Documents\NinjaTrader 8\bin\Custom\
```

### 2. Copy the indicator file
Copy `YieldValueLine.cs` to:
```
Documents\NinjaTrader 8\bin\Custom\Indicators\CSI\
```
Create the `CSI` folder if it does not exist.

### 3. Add DLL references in NinjaTrader
1. NinjaTrader → **Tools → Edit NinjaScript → References**
2. Click **Add** → browse to `Documents\NinjaTrader 8\bin\Custom\NCalc.dll`
3. Click **Add** → browse to `Documents\NinjaTrader 8\bin\Custom\System.Data.SQLite.dll`
4. Click **OK**

### 4. Compile
NinjaTrader → **Tools → Edit NinjaScript → Compile** (or **F5**)

---

## Adding the indicator to a chart

1. Right-click the chart → **Indicators**
2. Find **YieldValueLine** under the **CSI** category
3. Configure properties:

| Property | Description | Example |
|---|---|---|
| **Formula** | NCalc expression — symbol names are parsed automatically from the formula | `US10Y - US02Y` |
| **SQLite folder** | Where the Agent stores price files | `%ProgramData%\YieldDataLogger\Yields` |
| **Line colour** | Colour of the plotted line and toolbar button | DodgerBlue |
| **Line width** | Stroke width in pixels | `2` |
| **Button label** | Chart toolbar button text (blank = auto-generated from Formula) | `2s/10s Spread` |

---

## How symbols work

Symbol names are **parsed automatically from the Formula** — no need to enter them separately.
Every word in the formula that is not an NCalc built-in function becomes a symbol.

```
Formula: US10Y - US02Y     → loads US10Y.sqlite and US02Y.sqlite
Formula: (RTY + ES1) / 2   → loads RTY.sqlite and ES1.sqlite
```

Symbol names must match the canonical symbol names in your Agent subscriptions (and therefore
the `.sqlite` filenames written to the `SQLite folder`).

---

## How the scale works

The panel auto-scales to fit the **closed bar** values on screen — no manual scale min/max needed.
The right-edge tick shows the live real-time value on the current forming bar.

---

## Chart toolbar button

A gradient button matching the line colour is added to the chart toolbar automatically.
Click it to **show / hide** the line (fades to 35% opacity when hidden).
Multiple instances each add their own button and hide when you switch chart tabs.

---

## Formula examples

```
US10Y - US02Y                       2s/10s yield spread (raw %)
(US10Y - US02Y) * 200 + 500         spread scaled to a visual 0-1000 range
US10Y / US30Y * 1000                ratio × 1000
(US10Y + US30Y) / 2                 average of two yields
US10Y - DE10Y                       US vs German 10Y differential
RTY - ES1                           Russell minus S&P spread
VIX * 10                            VIX scaled up
```

NCalc supports standard math: `+`, `-`, `*`, `/`, `(`, `)`, `Abs()`, `Sqrt()`, `Pow()`, `Round()`, etc.

---

## Troubleshooting

| Symptom | Likely cause |
|---|---|
| Flat line | No SQLite data found – check `SQLite folder` path and that the Agent service is running |
| Values stop updating in realtime | FileSystemWatcher restarted automatically; check NT8 Output window for `[YieldValueLine]` messages |
| `NCalc.Expression` compile error | `NCalc.dll` not in `bin\Custom\` or not added to References |
| `SQLiteConnection` compile error | `System.Data.SQLite.dll` not added to References |
| Button missing from toolbar | Check NT8 Output window for `[YieldValueLine] Toolbar button error:` |
| Symbol not found warning | Symbol name in formula doesn't match the `.sqlite` filename – check Output window |
