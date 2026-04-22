# YieldValueLine – NinjaTrader 8 Indicator

## One-time setup

### 1. NCalc library
Download `NCalc.dll` from https://github.com/ncalc/ncalc/releases  
Copy it to:
```
Documents\NinjaTrader 8\bin\Custom\
```

### 2. SQLite library
NinjaTrader 8 ships with SQLite. Just copy two files from NT8's own install:
```
C:\Program Files\NinjaTrader 8\bin64\System.Data.SQLite.dll
C:\Program Files\NinjaTrader 8\bin64\SQLite.Interop.dll
```
Paste both into:
```
Documents\NinjaTrader 8\bin\Custom\
```

### 3. Add DLL references in NinjaTrader
1. Open NinjaTrader → **Tools → Edit NinjaScript → References**
2. Click **Add** and browse to `Documents\NinjaTrader 8\bin\Custom\NCalc.dll`
3. Click **Add** again for `System.Data.SQLite.dll`
4. Click **OK**

### 4. Copy the indicator file
Copy `YieldValueLine.cs` to:
```
Documents\NinjaTrader 8\bin\Custom\Indicators\CSI\
```
Create the `CSI` folder if it does not exist.

### 5. Compile
NinjaTrader → **Tools → Edit NinjaScript → Compile** (or press **F5**)

---

## Adding the indicator to a chart

1. Right-click the chart → **Indicators**
2. Find **YieldValueLine** under the **CSI** category
3. Configure properties:

| Property | Description | Example |
|---|---|---|
| **Formula** | NCalc expression using symbol names | `US10Y - US02Y` |
| **Instrument 1–8** | Canonical symbol names matching your subscriptions | `US10Y` |
| **SQLite folder** | Where the Agent stores price files | `%ProgramData%\YieldDataLogger\Yields` |
| **Refresh (seconds)** | How often to re-read SQLite in realtime | `5` |
| **Line colour** | Colour of the plotted line + toolbar button | DodgerBlue |
| **Button label** | Chart toolbar button text (blank = auto from Formula) | `2s/10s Spread` |
| **Scale minimum** | Raw formula value shown at panel bottom | `-2.0` |
| **Scale maximum** | Raw formula value shown at panel top | `3.0` |

---

## How the scale works

The panel y-axis always runs **0 → 1000** visually.  
You control what raw value maps to each end:

```
Scale Min = -2.0  →  panel bottom (y=1000)
Scale Max =  3.0  →  panel top    (y=0)
```

A formula result of `0.5` on a `–2 / 3` scale renders at:
```
norm = (0.5 – (–2)) / (3 – (–2)) = 2.5 / 5.0 = 50%  →  mid-panel
```

If your formula already outputs values in a natural 0–1000 range, set
`Scale Min = 0` and `Scale Max = 1000`.

---

## Chart toolbar button

A gradient button matching the line colour is automatically added to the chart
toolbar when the indicator loads.  Click it to **show/hide** the line.  
The button fades to 35% opacity when hidden.

Multiple instances of the indicator (different formulas) each add their own button.

---

## Formula examples

```
US10Y - US02Y                          2s/10s spread (raw %)
(US10Y - US02Y) * 200 + 500            spread scaled to 0-1000 range
US10Y / US30Y * 1000                   ratio × 1000
(US10Y + US30Y) / 2                    average of two yields
US10Y - DE10Y                          US vs German 10Y differential
VIX * 10                               VIX scaled up
```

> **Tip** – use `Math.Round(x, 2)` inside formulas if you want to reduce
> repainting jitter on small moves.

---

## Troubleshooting

| Symptom | Likely cause |
|---|---|
| Flat line at panel centre | No SQLite data found – check `SQLite folder` path and that the Agent is running |
| `NCalc.Expression` compile error | NCalc.dll not in `bin\Custom\` or not referenced |
| `SQLiteConnection` compile error | System.Data.SQLite.dll not referenced |
| Button missing from toolbar | Chart structure varies by NT8 version; check Output window for error |
| Values stop updating in realtime | Increase `Refresh (seconds)` or check Agent service is running |
