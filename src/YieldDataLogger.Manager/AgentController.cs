using System.Diagnostics;
using System.IO;
using System.ServiceProcess;
using YieldDataLogger.Core.Models;

namespace YieldDataLogger.Manager;

/// <summary>
/// Lifecycle control for the YieldDataLogger Agent.
///
/// When installed via the Setup wizard the Agent runs as a Windows Service and is managed
/// via ServiceController.  During development (dotnet run) it is a plain console process
/// found relative to the Manager's bin directory; that path is used as a fallback.
/// </summary>
internal sealed class AgentController
{
    private const string ServiceName = "YieldDataLogger.Agent";

    // -------------------------------------------------------------------------
    // Start
    // -------------------------------------------------------------------------
    public static (bool Ok, string Message) Start()
    {
        // Primary: Windows Service (installed via Setup.exe)
        var svc = FindService();
        if (svc is not null)
        {
            try
            {
                if (svc.Status == ServiceControllerStatus.Running)
                    return (true, "Agent service is already running.");

                svc.Start();
                svc.WaitForStatus(ServiceControllerStatus.Running, TimeSpan.FromSeconds(10));
                return (true, $"Agent service started.");
            }
            catch (Exception ex)
            {
                return (false, $"Failed to start service: {ex.Message}");
            }
        }

        // Fallback: dev environment – launch the exe directly
        var exe = FindAgentExe();
        if (exe is null)
            return (false, "Agent service not found and Agent.exe could not be located.\n" +
                           "Make sure the Agent is installed or the project is built.");

        try
        {
            var psi = new ProcessStartInfo(exe)
            {
                UseShellExecute  = true,
                WorkingDirectory = Path.GetDirectoryName(exe)!,
                CreateNoWindow   = false,
            };
            var proc = Process.Start(psi);
            return proc is null
                ? (false, "Process.Start returned null")
                : (true, $"Started Agent (pid {proc.Id})");
        }
        catch (Exception ex)
        {
            return (false, "Failed to start Agent: " + ex.Message);
        }
    }

    // -------------------------------------------------------------------------
    // Stop
    // -------------------------------------------------------------------------
    public static (bool Ok, string Message) Stop(AgentStatus? status)
    {
        // Primary: Windows Service
        var svc = FindService();
        if (svc is not null)
        {
            try
            {
                if (svc.Status == ServiceControllerStatus.Stopped)
                    return (true, "Agent service is already stopped.");

                svc.Stop();
                svc.WaitForStatus(ServiceControllerStatus.Stopped, TimeSpan.FromSeconds(10));
                return (true, "Agent service stopped.");
            }
            catch (Exception ex)
            {
                return (false, $"Failed to stop service: {ex.Message}");
            }
        }

        // Fallback: kill by PID from status.json (dev console process)
        if (status is null || status.Pid <= 0)
            return (false, "Agent service not found and no PID in status.json.");

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

    // -------------------------------------------------------------------------
    // Helpers
    // -------------------------------------------------------------------------

    /// <summary>Returns the ServiceController if the Windows Service is installed, else null.</summary>
    private static ServiceController? FindService()
    {
        try
        {
            var svc = new ServiceController(ServiceName);
            // Accessing Status forces a query – throws if the service doesn't exist.
            _ = svc.Status;
            return svc;
        }
        catch { return null; }
    }

    /// <summary>
    /// Locates the Agent exe for dev scenarios.
    /// Checks, in order:
    ///   1. YIELDDATALOGGER_AGENT_PATH env var
    ///   2. Installed layout: ..\Agent\YieldDataLogger.Agent.exe (sibling of Manager folder)
    ///   3. Dev layout: walks up the tree looking for the Agent bin output
    /// </summary>
    public static string? FindAgentExe()
    {
        var env = Environment.GetEnvironmentVariable("YIELDDATALOGGER_AGENT_PATH");
        if (!string.IsNullOrWhiteSpace(env) && File.Exists(env)) return env;

        var baseDir = AppContext.BaseDirectory;

        // Installed layout: Manager is at {install}\Manager\, Agent at {install}\Agent\
        var installedAgent = Path.Combine(baseDir, "..", "Agent", "YieldDataLogger.Agent.exe");
        var normalized = Path.GetFullPath(installedAgent);
        if (File.Exists(normalized)) return normalized;

        // Dev layout: walk up from bin directory to find the Agent project output
        var dir = new DirectoryInfo(baseDir);
        for (var i = 0; i < 8 && dir is not null; i++, dir = dir.Parent)
        {
            var agentProj = Path.Combine(dir.FullName, "YieldDataLogger.Agent");
            if (!Directory.Exists(agentProj)) continue;

            foreach (var cfg in new[] { "Debug", "Release" })
            foreach (var fx  in new[] { "net8.0", "net8.0-windows" })
            {
                var candidate = Path.Combine(agentProj, "bin", cfg, fx, "YieldDataLogger.Agent.exe");
                if (File.Exists(candidate)) return candidate;
            }
        }
        return null;
    }
}
