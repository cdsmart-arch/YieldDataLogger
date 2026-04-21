# YieldDataLogger

Clean-slate rewrite of `YieldLoggerUI`. Scrapes near-real-time instrument closes from
investing.com and CNBC, ultimately to feed value-line indicators in NinjaTrader and
Sierra Chart (plus the in-house options GEX calculator apps).

Replaces the old Selenium + ChromeDriver + local HTML scraping trick with (a) direct HTTP
for CNBC and (b) direct Socket.IO for investing.com. Designed to run 24/7 on Azure with a
thin local agent on each trader's PC.

## Solution layout

```
src/
  YieldDataLogger.Core         class library: models, sinks, symbol translation, error log
  YieldDataLogger.Collector    hosted service: CNBC HTTP poller + investing WS client + sink fan-out
  YieldDataLogger.Api          (phase 3) ASP.NET Core REST + SignalR, Azure-hosted
  YieldDataLogger.Agent        (phase 4) Windows Service running on each trading PC
  YieldDataLogger.Manager      (phase 5) WPF tray app for credentials + subscriptions
  YieldDataLogger.NT           (phase 6) NinjaTrader 8 indicator
  YieldDataLogger.Client       (phase 8) .NET library consumed by the GEX apps
  YieldDataLogger.Installer    WiX/Inno MSI
```

## Phase 1 status

Done:
- Solution + `Core` + `Collector` scaffolded for `net8.0`.
- `SqliteSink` writes per-instrument `{symbol}.sqlite` files (same schema as the old app).
- `ScidSink` appends Sierra Chart intraday records (binary-compatible with the old app).
- `CnbcPoller` hosted service hits `quote.cnbc.com/quote-html-webservice/quote.htm` on
  a configurable interval, dispatches ticks to every enabled sink. Verified end-to-end:
  running the collector populates `%ProgramData%\YieldDataLogger\Yields\*.sqlite` with
  live CNBC quotes in under a minute.
- `InvestingSocketClient` connects to `stream80.forexpros.com` directly via Socket.IO
  (no headless browser), emits `socketRetry` for the configured pid rooms, and parses
  tick-shaped payloads into `PriceTick` events. Ships behind `IInvestingStreamClient`
  so a Playwright-based fallback can slot in without touching the hosted service.

Expected next:
- Live-test the investing stream against the vendor's current protocol. Their bundle
  (`main-1.17.55.min.js`) uses a custom event envelope on top of Socket.IO, and if the
  direct client cannot handshake we plug in the Playwright fallback behind the same
  interface. The hosted service already watchdogs for a silent stream and logs when
  that fallback would be needed.

## Run locally

```powershell
cd src\YieldDataLogger.Collector
dotnet run
```

Defaults write SQLite files to `%ProgramData%\YieldDataLogger\Yields\`.
Edit `appsettings.json` to toggle sinks, change poll interval, switch on SCID writing, etc.

## Where it's going

See `.cursor/plans/azure_yieldlogger_migration_*.plan.md` at the repo root (planning folder)
for the full phased roadmap to Azure, Windows Service agent, Manager UX, and NT indicator.
