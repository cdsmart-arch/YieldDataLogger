# YieldDataLogger

Clean-slate rewrite of `YieldLoggerUI`. Scrapes near-real-time instrument closes from
investing.com and CNBC, ultimately to feed value-line indicators in NinjaTrader and
Sierra Chart (plus the in-house options GEX calculator apps).

Replaces the old Selenium + ChromeDriver setup with headless Playwright for investing.com
and direct HTTP for CNBC. Designed to run 24/7 on Azure with a thin local agent on each
trader's PC.

## Solution layout

```
src/
  YieldDataLogger.Core         class library: models, sinks, symbol translation, error log
  YieldDataLogger.Collector    hosted service: CNBC HTTP poller + Playwright investing client + sink fan-out
  YieldDataLogger.Api          ASP.NET Core REST (+ SignalR in phase 3b), Azure-hosted
  YieldDataLogger.Agent        (phase 4) Windows Service running on each trading PC
  YieldDataLogger.Manager      (phase 5) WPF tray app for credentials + subscriptions
  YieldDataLogger.NT           (phase 6) NinjaTrader 8 indicator
  YieldDataLogger.Client       (phase 8) .NET library consumed by the GEX apps
  YieldDataLogger.Installer    WiX/Inno MSI
```

## Phase status

| Phase | Status | What                                                               |
|-------|--------|--------------------------------------------------------------------|
| 1     | done   | Solution + Core + Collector; SqliteSink / ScidSink; CNBC poller    |
| 2a    | done   | Unified instrument catalog + admin CLI                             |
| 2b    | done   | Playwright transport for investing.com stream                      |
| 3a    | done   | `YieldDataLogger.Api` + Azure SQL backend (SqlPriceSink, SQL fallback) |
| 3a-bis| done   | Azure Table Storage as primary backend; SQL retained as opt-in fallback |
| 3b    | next   | SignalR hub + real-time push                                       |
| 3c    | next   | Identity + JWT auth                                                |
| 3d    | next   | Container Apps + Azure Table Storage deployment                    |

## Storage backends

The API supports two backends, chosen by `Storage:Backend` in `appsettings.json`:

- **Azure Table Storage (default)** — cheap at scale (~$0.045/GB/mo + tiny transaction cost).
  PartitionKey=Symbol, RowKey=inverted-microsecond timestamp so newest ticks sort first.
  Connection string `UseDevelopmentStorage=true` talks to Azurite locally.
- **Azure SQL / SQL Server (fallback)** — EF Core, tall `PriceTicks` table clustered on
  `(Symbol, TsUnix)`. Enable by setting `Storage:Backend` = `"sql"` and
  `Storage:Sql:Enabled` = `true`. EF migrations live in `src/YieldDataLogger.Api/Data/Migrations`.

Both backends implement the same `IPriceSink` (write) and `IPriceHistoryReader` (read)
contracts, so switching is a single config flip. Both can also be enabled simultaneously
for dual-write during migrations.

## Run locally

### Option 1: Collector only (writes local SQLite / SCID files)

```powershell
cd src\YieldDataLogger.Collector
dotnet run
```

Defaults write SQLite files to `%ProgramData%\YieldDataLogger\Yields\`.
Edit `appsettings.json` to toggle sinks, change poll interval, switch on SCID writing, etc.

### Option 2: Full API (collector + REST + Table Storage)

The API runs the collector in-process and writes ticks to Azure Table Storage. For local
development, install **Azurite**, Microsoft's official local emulator:

```powershell
# Requires Node.js
npm install -g azurite

# In a dedicated shell, pick any workspace folder for Azurite's data:
azurite --location C:\Azurite --skipApiVersionCheck
```

Azurite keeps listening on its default ports (10002 for Tables). With it running, start
the API:

```powershell
cd src\YieldDataLogger.Api
dotnet run
```

Swagger UI opens at `http://localhost:5000/swagger` (or whatever port Kestrel picks).
Ticks start landing in the emulated `PriceTicks` table; check them with `GET /api/ticks/{symbol}`.

For the SQL fallback, install [SQL Server LocalDB](https://learn.microsoft.com/en-us/sql/database-engine/configure-windows/sql-server-express-localdb)
and flip `Storage:Backend` to `"sql"` in `appsettings.json`.

## Admin CLI (Collector project)

Manage instruments without editing `instruments.json` directly:

```powershell
cd src\YieldDataLogger.Collector
dotnet run -- list
dotnet run -- add --symbol MYNEW --pid 12345 --cnbc MYN --category FX
dotnet run -- remove MYNEW
```

The `--file` flag targets a specific `instruments.json` (handy in dev where you want edits
to persist in the source project rather than the bin copy).

## Where it's going

See `.cursor/plans/azure_yieldlogger_migration_*.plan.md` at the repo root (planning folder)
for the full phased roadmap to Azure, Windows Service agent, Manager UX, and NT indicator.
