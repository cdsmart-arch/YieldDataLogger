using System;
using System.Data.SQLite;
using System.IO;

// One-shot diagnostic to count rows in each per-symbol SQLite file written by the Agent.
// Usage: dotnet run --project tools/SqliteProbe -- <dir>
var dir = args.Length > 0
    ? args[0]
    : Environment.ExpandEnvironmentVariables("%TEMP%\\YieldDataLogger\\Yields");

if (!Directory.Exists(dir))
{
    Console.WriteLine($"MISSING: {dir}");
    return 1;
}

Console.WriteLine($"Probing {dir}\n");
foreach (var f in Directory.GetFiles(dir, "*.sqlite"))
{
    using var c = new SQLiteConnection("Data Source=" + f + ";Version=3;Read Only=True;");
    c.Open();
    using var t = new SQLiteCommand("SELECT name FROM sqlite_master WHERE type='table' LIMIT 1", c);
    var tbl = (string?)t.ExecuteScalar();
    if (tbl == null) { Console.WriteLine($"{Path.GetFileName(f),-16} no tables"); continue; }

    using var cnt = new SQLiteCommand($"SELECT COUNT(*), MAX(TIMESTAMP), (SELECT CLOSE FROM {tbl} ORDER BY rowid DESC LIMIT 1) FROM {tbl}", c);
    using var r = cnt.ExecuteReader();
    r.Read();
    var rows   = r.IsDBNull(0) ? 0L : r.GetInt64(0);
    var lastTs = r.IsDBNull(1) ? 0d : r.GetDouble(1);
    var lastPx = r.IsDBNull(2) ? 0d : r.GetDouble(2);
    var lastUtc = lastTs > 0 ? DateTimeOffset.FromUnixTimeSeconds((long)lastTs).UtcDateTime.ToString("u") : "-";
    Console.WriteLine($"{Path.GetFileName(f),-16} table={tbl,-10} rows={rows,-5} last={lastUtc} px={lastPx}");
}
return 0;
