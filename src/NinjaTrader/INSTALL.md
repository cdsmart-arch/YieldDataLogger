# YieldValueLine – NinjaTrader 8 Indicator

## Dependencies

| DLL | Type | Purpose |
|---|---|---|
| **NCalc.dll** | managed | Formula evaluation — [github.com/ncalc/ncalc/releases](https://github.com/ncalc/ncalc/releases) |
| **Antlr3.Runtime.dll** | managed | NCalc's own dependency — ships in the same NCalc zip. NCalc fails to load without it. |
| **System.Data.SQLite.dll** | managed | Reads the `.sqlite` price files |
| **SQLite.Interop.dll** | **native** | The actual SQLite engine that `System.Data.SQLite` P/Invokes |

**SharpDX** is bundled with NinjaTrader — no action needed.

### Where to get System.Data.SQLite.dll + SQLite.Interop.dll

NinjaTrader usually ships both next to `NinjaTrader.exe`. Find that folder with:

```
where /r "C:\Program Files\NinjaTrader 8" SQLite.Interop.dll
```

On most installs `NinjaTrader.exe` is in `C:\Program Files\NinjaTrader 8\bin\` (older docs said
`bin64\` — that folder does not exist on current installs). You can also take the pair from the
`System.Data.SQLite.Core` NuGet package, which lays them out as `x64\` and `x86\` subfolders.

**Use the `x64` build** — NinjaTrader 8 is a 64-bit process.

---

## The SQLite.Interop.dll trap (read this)

`System.Data.SQLite.dll` is only a *managed wrapper*. The real engine is the native
`SQLite.Interop.dll`, and Windows resolves native DLLs from the **process folder**
(`C:\Program Files\NinjaTrader 8\bin\`) — **not** from `bin\Custom`. So a machine where
NinjaTrader happens to ship a copy beside the exe "just works", and one where it doesn't fails with:

```
Unable to load DLL 'SQLite.Interop.dll': The specified module could not be found.
(Exception from HRESULT: 0x8007007E)
```

`YieldValueLine.cs` now handles this itself: before its first query it explicitly loads the DLL
by full path (`EnsureSqliteInterop`), checking, in order:

```
<Documents>\NinjaTrader 8\bin\Custom\x64\SQLite.Interop.dll
<Documents>\NinjaTrader 8\bin\Custom\SQLite.Interop.dll
<NinjaTrader exe folder>\x64\SQLite.Interop.dll
<NinjaTrader exe folder>\SQLite.Interop.dll
```

So you only need the native DLL to **exist** in `bin\Custom` (flat or in an `x64\` subfolder).
It does **not** go in NinjaTrader's References list — it is not a .NET assembly.

---

## One-time setup (new machine)

### 1. Copy DLLs
Copy the three **managed** DLLs to `Documents\NinjaTrader 8\bin\Custom\`:

```
NCalc.dll
Antlr3.Runtime.dll
System.Data.SQLite.dll
```

Then copy the **native** one into the same folder (or an `x64\` subfolder of it):

```
SQLite.Interop.dll      (x64 build)
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
| `Unable to load DLL 'SQLite.Interop.dll'` (0x8007007E) | The **native** DLL is missing from `bin\Custom` (flat or `x64\`). It is NOT a Reference - just copy the x64 file there and restart NT. |
| Button missing from toolbar | Check NT8 Output window for `[YieldValueLine] Toolbar button error:` |
| Symbol not found warning | Symbol name in formula doesn't match the `.sqlite` filename – check Output window |
