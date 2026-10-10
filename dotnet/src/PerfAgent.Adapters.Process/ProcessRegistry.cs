using System.Text.Json;
using PerfAgent.Core;

namespace PerfAgent.Adapters.Process;

/// <param name="StartTimeUtc">Hora de inicio: evita matar un proceso ajeno que reutilice el mismo PID.</param>
public sealed record RegisteredProcess(string RunId, string Role, int ProcessId, DateTime StartTimeUtc, string Command);

/// <summary>
/// Registro persistente de los procesos lanzados por el laboratorio. Permite limpiar procesos que quedaron
/// vivos si el servidor terminó de forma abrupta (en sistemas sin Job Objects) y verificar la limpieza.
/// </summary>
public sealed class ProcessRegistry
{
    private readonly string _path;
    private readonly Lock _lock = new();

    public ProcessRegistry(string stateDirectory)
    {
        Directory.CreateDirectory(stateDirectory);
        _path = Path.Combine(stateDirectory, "processes.json");
    }

    public void Add(RegisteredProcess process)
    {
        lock (_lock)
        {
            var all = Read();
            all.RemoveAll(p => p.ProcessId == process.ProcessId);
            all.Add(process);
            Write(all);
        }
    }

    public void Remove(int processId)
    {
        lock (_lock)
        {
            var all = Read();
            if (all.RemoveAll(p => p.ProcessId == processId) > 0)
            {
                Write(all);
            }
        }
    }

    public IReadOnlyList<RegisteredProcess> List()
    {
        lock (_lock)
        {
            return Read();
        }
    }

    /// <summary>Procesos registrados que siguen vivos (mismo PID y misma hora de inicio).</summary>
    public IReadOnlyList<RegisteredProcess> FindAlive(string? runId = null) =>
        List().Where(p => (runId is null || p.RunId == runId) && IsAlive(p)).ToList();

    /// <summary>Termina los procesos registrados que siguen vivos. Devuelve (detenidos, no detenidos).</summary>
    public (IReadOnlyList<string> Stopped, IReadOnlyList<string> Failed) KillOrphans(string? runId = null)
    {
        var stopped = new List<string>();
        var failed = new List<string>();
        foreach (var p in List().Where(p => runId is null || p.RunId == runId))
        {
            if (!IsAlive(p))
            {
                Remove(p.ProcessId);
                continue;
            }

            try
            {
                using var process = System.Diagnostics.Process.GetProcessById(p.ProcessId);
                process.Kill(entireProcessTree: true);
                process.WaitForExit(TimeSpan.FromSeconds(10));
                stopped.Add($"{p.Role} (PID {p.ProcessId}, run {p.RunId})");
                Remove(p.ProcessId);
            }
            catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception)
            {
                if (IsAlive(p))
                {
                    failed.Add($"{p.Role} (PID {p.ProcessId}): {ex.Message}");
                }
                else
                {
                    Remove(p.ProcessId);
                }
            }
        }

        return (stopped, failed);
    }

    internal static bool IsAlive(RegisteredProcess registered)
    {
        try
        {
            using var process = System.Diagnostics.Process.GetProcessById(registered.ProcessId);
            if (process.HasExited)
            {
                return false;
            }

            // Tolerancia de 2 s por la resolución del reloj del sistema.
            return Math.Abs((process.StartTime.ToUniversalTime() - registered.StartTimeUtc).TotalSeconds) < 2;
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException)
        {
            return false;
        }
    }

    private List<RegisteredProcess> Read()
    {
        if (!File.Exists(_path))
        {
            return [];
        }

        try
        {
            return JsonSerializer.Deserialize<List<RegisteredProcess>>(File.ReadAllText(_path), PerfJson.Indented) ?? [];
        }
        catch (JsonException)
        {
            return [];
        }
    }

    private void Write(List<RegisteredProcess> all)
    {
        var temp = _path + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(all, PerfJson.Indented));
        File.Move(temp, _path, overwrite: true);
    }
}
