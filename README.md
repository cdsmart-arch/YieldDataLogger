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
  YieldDataLogger.Manager      WPF tray app: dashboard, subscriptions, Agent control
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
| 3b    | done   | SignalR hub + real-time push + Live admin page                     |
| 3b-bis| done   | Centralised price-change dedup in `TickDispatcher`                 |
| 4a    | done   | `YieldDataLogger.Agent` console host - SignalR client → local sinks |
| 4b    | next   | Package Agent as Windows Service + installer                       |
| 5a    | done   | Manager tray dashboard + Agent `status.json` observability         |
| 5b    | done   | Manager symbol picker → `subscriptions.json` → live hub Subscribe/Unsubscribe |
| 5c    | next   | Historical backfill: pull `GET /api/ticks` into local SQLite after connect |
| 3c    | later  | Identity + JWT auth (deferred until end-to-end pipeline is solid)  |
| 3d    | later  | Container Apps + hardened Azure deployment                         |

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

## Historical data: durability vs. app updates

Tick history is **not** stored inside the App Service process. It lives in **Azure Table
Storage** (or SQL), keyed by storage account / database. Deploying a new build of the API
replaces only application binaries and configuration; **it does not delete table rows**
unless you explicitly change code or migrations to do so.

**Operational rules of thumb**

- Keep the **same storage account and connection string** across API redeploys so the
  `PriceTicks` table continues to accumulate. Rotating keys is fine; deleting the account is not.
- **EF migrations** for the SQL path must never `DROP` the ticks table or bulk-delete rows as
  part of routine deploys. Prefer additive-only migrations.
- Treat scraped ticks as **append-only**: there is no vendor API to re-download history if
  you wipe storage. Back up the storage account (or enable point-in-time restore where
  available) according to your risk tolerance.

**Future: chart history on the client**

Live SignalR gives you the latest price changes; **historic lines** need rows already on
disk. Planned next step (phase **5c**): when the Agent connects (or when you add a symbol),
call `GET /api/ticks/{symbol}` with a time range and merge into local SQLite so Ninja /
Sierra / indicators can render a full line. Until that ships, history grows from the moment
you start scraping—so getting the API onto Azure and logging continuously matters as soon
as possible.

## Deploying the API to Azure (checklist)

High level—details depend on whether you use App Service, Container Apps, or VMs:

1. **Create or reuse an Azure Storage account** with Tables enabled. Note the connection string.
2. **Configure the hosted API** with application settings (not committed to git):
   - `Storage:Backend` = `table`
   - `Storage:Tables:ConnectionString` = your production connection string
   - Any collector / Playwright settings you use in production
3. **HTTPS URL**: set the public site URL (e.g. `https://your-app.azurewebsites.net`). The
   Agent’s `HubUrl` should be `https://your-app.azurewebsites.net/hubs/ticks` and
   `ApiBaseUrl` (or derivation from hub URL) should match `https://your-app.azurewebsites.net`
   so the Manager can load `GET /api/instruments`.
4. **CORS** is not required for the Agent (SignalR client is not browser-based); browser admin
   UI may need CORS if served from a different origin.
5. After deploy, verify `GET https://.../healthz` and open `/admin/` for instrument management.

The **local Agent + Manager** on each PC then point at the production URL; subscription
and status files stay on that machine under `%ProgramData%` (or `%TEMP%` in Development).

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

### Option 3: Run the local Agent against the API

The Agent connects to the API's SignalR hub, subscribes to a configured symbol list, and
writes every received tick into local per-symbol SQLite files (and optional Sierra `.scid`
files). This is the same binary that will be wrapped as a Windows Service in phase 4b.

```powershell
# Terminal 1 - API (also runs the collector + SignalR hub)
cd src\YieldDataLogger.Api
dotnet run

# Terminal 2 - Agent on the same PC
cd src\YieldDataLogger.Agent
dotnet run
```

Configuration lives in `src\YieldDataLogger.Agent\appsettings.json`:

```jsonc
{
  "Agent": {
    "HubUrl": "http://localhost:5055/hubs/ticks",
    "Symbols": [ "US10Y", "DE10Y", "VIX" ],
    "AuthToken": null,                 // reserved for phase 3c
    "Sinks": {
      "Sqlite": { "Enabled": true,  "Path": "%ProgramData%\\YieldDataLogger\\Yields" },
      "Scid":   { "Enabled": false, "Path": "C:\\SierraChart\\Data", "AllowedSymbols": [] }
    }
  }
}
```

In `Development` the SQLite path is redirected to `%TEMP%\YieldDataLogger\Yields` so you
don't need admin rights to see data flow.

To inspect what the Agent has written without installing `sqlite3.exe`:

```powershell
dotnet run --project tools\SqliteProbe
```

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
