using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using YieldDataLogger.Core.Models;

namespace YieldDataLogger.Collector.Instruments;

/// <summary>
/// Lightweight CLI for managing instruments.json without opening the file by hand.
/// Invoked when the collector is started with a recognised verb as its first argument.
/// The final UX will be in the Manager app; this is a developer-shaped stand-in.
/// </summary>
public static class AdminCli
{
    private static readonly HashSet<string> Verbs =
        new(StringComparer.OrdinalIgnoreCase) { "list", "show", "add", "remove", "help", "--help", "-h" };

    public static bool IsAdminCommand(string[] args) =>
        args.Length > 0 && Verbs.Contains(args[0]);

    /// <summary>
    /// Runs the command and returns a process exit code. Does not build the host.
    /// </summary>
    public static int Run(string[] args, string instrumentsPath)
    {
        try
        {
            var verb = args[0].ToLowerInvariant();
            return verb switch
            {
                "help" or "--help" or "-h" => PrintUsage(),
                "list" => CmdList(args.Skip(1).ToArray(), instrumentsPath),
                "show" => CmdShow(args.Skip(1).ToArray(), instrumentsPath),
                "add" => CmdAdd(args.Skip(1).ToArray(), instrumentsPath),
                "remove" => CmdRemove(args.Skip(1).ToArray(), instrumentsPath),
                _ => PrintUsage(),
            };
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"error: {ex.Message}");
            return 1;
        }
    }

    private static InstrumentCatalog LoadCatalog(string path)
    {
        var catalog = new InstrumentCatalog(NullLogger<InstrumentCatalog>.Instance);
        catalog.Load(path);
        return catalog;
    }

    private static int CmdList(string[] args, string instrumentsPath)
    {
        var catalog = LoadCatalog(instrumentsPath);
        bool onlyCnbc = args.Contains("--cnbc", StringComparer.OrdinalIgnoreCase);
        bool onlyInvesting = args.Contains("--investing", StringComparer.OrdinalIgnoreCase);

        IEnumerable<Instrument> items = catalog.All;
        if (onlyCnbc) items = items.Where(i => !string.IsNullOrEmpty(i.CnbcSymbol));
        if (onlyInvesting) items = items.Where(i => i.InvestingPid is not null);

        foreach (var group in items
            .GroupBy(i => i.Category ?? "(uncategorised)", StringComparer.OrdinalIgnoreCase)
            .OrderBy(g => g.Key, StringComparer.OrdinalIgnoreCase))
        {
            Console.WriteLine();
            Console.WriteLine($"# {group.Key}");
            foreach (var i in group.OrderBy(i => i.CanonicalSymbol, StringComparer.OrdinalIgnoreCase))
            {
                var pid = i.InvestingPid is int p ? p.ToString() : "-";
                var cnbc = i.CnbcSymbol ?? "-";
                Console.WriteLine($"  {i.CanonicalSymbol,-10}  pid={pid,-8}  cnbc={cnbc}");
            }
        }
        Console.WriteLine();
        Console.WriteLine($"({catalog.All.Count} instruments, {catalog.InvestingPids.Count} investing, {catalog.CnbcSymbols.Count} cnbc)");
        return 0;
    }

    private static int CmdShow(string[] args, string instrumentsPath)
    {
        if (args.Length == 0)
        {
            Console.Error.WriteLine("usage: show <canonicalSymbol>");
            return 2;
        }

        var catalog = LoadCatalog(instrumentsPath);
        if (!catalog.BySymbol.TryGetValue(args[0], out var i))
        {
            Console.Error.WriteLine($"not found: {args[0]}");
            return 1;
        }
        Console.WriteLine($"canonical: {i.CanonicalSymbol}");
        Console.WriteLine($"category:  {i.Category ?? "-"}");
        Console.WriteLine($"pid:       {(i.InvestingPid?.ToString() ?? "-")}");
        Console.WriteLine($"cnbc:      {i.CnbcSymbol ?? "-"}");
        return 0;
    }

    private static int CmdAdd(string[] args, string instrumentsPath)
    {
        var flags = ParseFlags(args);
        if (!flags.TryGetValue("symbol", out var symbol) || string.IsNullOrWhiteSpace(symbol))
        {
            Console.Error.WriteLine("usage: add --symbol <CANONICAL> [--pid <n>] [--cnbc <cnbcSymbol>] [--category <name>]");
            Console.Error.WriteLine("       at least one of --pid or --cnbc is required");
            return 2;
        }

        int? pid = null;
        if (flags.TryGetValue("pid", out var pidStr) && !string.IsNullOrEmpty(pidStr))
        {
            if (!int.TryParse(pidStr, out var p) || p <= 0)
            {
                Console.Error.WriteLine($"invalid pid: {pidStr}");
                return 2;
            }
            pid = p;
        }

        flags.TryGetValue("cnbc", out var cnbc);
        flags.TryGetValue("category", out var category);
        if (pid is null && string.IsNullOrWhiteSpace(cnbc))
        {
            Console.Error.WriteLine("add requires at least one of --pid or --cnbc.");
            return 2;
        }

        var catalog = LoadCatalog(instrumentsPath);
        catalog.Upsert(new Instrument
        {
            CanonicalSymbol = symbol,
            InvestingPid = pid,
            CnbcSymbol = string.IsNullOrWhiteSpace(cnbc) ? null : cnbc,
            Category = string.IsNullOrWhiteSpace(category) ? null : category,
        });
        catalog.Save();
        Console.WriteLine($"added/updated {symbol} (pid={pid?.ToString() ?? "-"}, cnbc={cnbc ?? "-"}, category={category ?? "-"})");
        return 0;
    }

    private static int CmdRemove(string[] args, string instrumentsPath)
    {
        if (args.Length == 0)
        {
            Console.Error.WriteLine("usage: remove <canonicalSymbol>");
            return 2;
        }

        var catalog = LoadCatalog(instrumentsPath);
        if (!catalog.Remove(args[0]))
        {
            Console.Error.WriteLine($"not found: {args[0]}");
            return 1;
        }
        catalog.Save();
        Console.WriteLine($"removed {args[0]}");
        return 0;
    }

    private static Dictionary<string, string?> ParseFlags(string[] args)
    {
        var flags = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < args.Length; i++)
        {
            var a = args[i];
            if (!a.StartsWith("--")) continue;
            var key = a[2..];
            string? value = null;
            if (i + 1 < args.Length && !args[i + 1].StartsWith("--"))
            {
                value = args[++i];
            }
            flags[key] = value;
        }
        return flags;
    }

    private static int PrintUsage()
    {
        Console.WriteLine("""
YieldDataLogger.Collector admin commands:

  dotnet run -- list [--cnbc|--investing]
      list all instruments (optionally filtered to a source)

  dotnet run -- show <CANONICAL>
      show one instrument

  dotnet run -- add --symbol <CANONICAL> [--pid <n>] [--cnbc <cnbcSymbol>] [--category <name>]
      add or update an instrument; at least one of --pid or --cnbc required

  dotnet run -- remove <CANONICAL>
      remove an instrument

With no verb, the collector runs as a worker service.
""");
        return 0;
    }
}
