using System.IO;

namespace YieldDataLogger.Manager;

/// <summary>
/// Best-effort discovery of Sierra Chart installations so the user can pick which install's
/// Data folder receives the .scid files (traders often run several side by side, e.g.
/// C:\SierraChart and C:\SierraChart2). Sierra has no registry footprint worth relying on, so
/// we look for folders named Sierra* containing a SierraChart executable at each fixed drive
/// root and under Program Files. A custom "Data Files Folder" (set in Sierra's General
/// Settings) can't be detected - that's what the Browse button is for.
/// </summary>
internal static class SierraLocator
{
    private static readonly string[] ExeNames = { "SierraChart_64.exe", "SierraChart.exe" };

    public static IReadOnlyList<string> FindDataFolders()
    {
        var results = new List<string>();
        foreach (var root in CandidateRoots())
        {
            IEnumerable<string> dirs;
            try { dirs = Directory.EnumerateDirectories(root, "Sierra*"); }
            catch { continue; }

            foreach (var dir in dirs)
            {
                try
                {
                    if (!ExeNames.Any(e => File.Exists(Path.Combine(dir, e)))) continue;
                    var data = Path.Combine(dir, "Data");
                    if (Directory.Exists(data) &&
                        !results.Contains(data, StringComparer.OrdinalIgnoreCase))
                        results.Add(data);
                }
                catch { /* unreadable folder - skip */ }
            }
        }
        return results.OrderBy(p => p, StringComparer.OrdinalIgnoreCase).ToArray();
    }

    /// <summary>True if the folder looks like a Sierra Data folder (has .scid/.dly files or a Sierra exe one level up).</summary>
    public static bool LooksLikeSierraData(string folder)
    {
        try
        {
            if (!Directory.Exists(folder)) return false;
            if (Directory.EnumerateFiles(folder, "*.scid").Any()) return true;
            if (Directory.EnumerateFiles(folder, "*.dly").Any()) return true;
            var parent = Directory.GetParent(folder)?.FullName;
            return parent is not null && ExeNames.Any(e => File.Exists(Path.Combine(parent, e)));
        }
        catch { return false; }
    }

    private static IEnumerable<string> CandidateRoots()
    {
        foreach (var d in DriveInfo.GetDrives())
        {
            bool ok;
            try { ok = d.DriveType == DriveType.Fixed && d.IsReady; } catch { ok = false; }
            if (ok) yield return d.RootDirectory.FullName;
        }
        foreach (var pf in new[]
                 {
                     Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                     Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
                 })
        {
            if (!string.IsNullOrEmpty(pf)) yield return pf;
        }
    }
}
