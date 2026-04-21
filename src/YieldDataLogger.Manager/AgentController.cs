using System.Diagnostics;
using System.IO;
using YieldDataLogger.Core.Models;

namespace YieldDataLogger.Manager;

/// <summary>
/// Lightweight lifecycle control for the Agent exe from the Manager UI. Only suited for the
/// current console-form Agent (phase 4a). Once the Agent is packaged as a Windows Service
/// (phase 4b) this class should switch to <c>ServiceController</c>, but the public surface
/// stays the same.
/// </summary>
internal sealed class AgentController
{
    /// <summary>
    /// Locates the YieldDataLogger.Agent.exe for the current machine, trying the following
    /// in order: YIELDDATALOGGER_AGENT_PATH env var, the Manager's own directory (installed
    /// layout), then a handful of dev-time relative paths so `dotnet run` from the repo works.
    /// </summary>
    public static string? FindAgentExe()
    {
        var env = Environment.GetEnvironmentVariable("YIELDDATALOGGER_AGENT_PATH");
        if (!string.IsNullOrWhiteSpace(env) && File.Exists(env)) return env;

        var baseDir = AppContext.BaseDirectory;

        // Installed layout: Agent exe lives alongside Manager.
        var sibling = Path.Combine(baseDir, "YieldDataLogger.Agent.exe");
        if (File.Exists(sibling)) return sibling;

        // Dev layout: Manager is at ...\src\YieldDataLogger.Manager\bin\<cfg>\net8.0-windows
        // Agent is at   ...\src\YieldDataLogger.Agent  \bin\<cfg>\net8.0
        // Walk up until we find a folder with YieldDataLogger.Agent next to us, then project
        // the expected bin path.
        var dir = new DirectoryInfo(baseDir);
        for (var i = 0; i < 8 && dir is not null; i++, dir = dir.Parent)
        {
            var agentProj = Path.Combine(dir.FullName, "YieldDataLogger.Agent");
            if (Directory.Exists(agentProj))
            {
                foreach (var cfg in new[] { "Debug", "Release" })
                {
                    var candidate = Path.Combine(agentProj, "bin", cfg, "net8.0", "YieldDataLogger.Agent.exe");
                    if (File.Exists(candidate)) return candidate;
                }
            }
        }
        return null;
    }

    /// <summary>Launches the Agent exe in its own console window so the user can see live logs.</summary>
    public static (bool Ok, string Message) Start()
    {
        var exe = FindAgentExe();
        if (exe is null)
            return (false, "Could not locate YieldDataLogger.Agent.exe (set YIELDDATALOGGER_AGENT_PATH or build the Agent project).");

        try
        {
            var psi = new ProcessStartInfo(exe)
            {
                UseShellExecute     = true,
                WorkingDirectory    = Path.GetDirectoryName(exe)!,
                CreateNoWindow      = false,
            };
            var proc = Process.Start(psi);
            return proc is null
                ? (false, "Process.Start returned null")
                : (true, $"Started {Path.GetFileName(exe)} (pid {proc.Id})");
        }
        catch (Exception ex)
        {
            return (false, "Failed to start Agent: " + ex.Message);
        }
    }

    /// <summary>
    /// Stops a running Agent by reading the pid from its status.json and killing it. Safer
    /// than ProcessName-based kill because multiple dev instances would otherwise all die.
    /// </summary>
    public static (bool Ok, string Message) Stop(AgentStatus? status)
    {
        if (status is null || status.Pid <= 0)
            return (false, "No running Agent found in status.json.");

        try
        {
            var proc = Process.GetProcessById(status.Pid);
            proc.Kill(entireProcessTree: false);
            return (true, $"Stopped Agent (pid {status.Pid}).");
        }
        catch (ArgumentException)
        {
            return (false, $"Agent pid {status.Pid} is no longer alive.");
        }
        catch (Exception ex)
        {
            return (false, $"Failed to stop Agent (pid {status.Pid}): {ex.Message}");
        }
    }
}
