using System.Diagnostics;
using System.Text;
using PerfAgent.Core.Execution;
using PerfAgent.Core.Security;

namespace PerfAgent.Adapters.Process;

/// <summary>
/// Proceso de larga duración del laboratorio (API o mock). Su salida se sanea y se guarda en archivos de log
/// del run; se conservan las últimas líneas en memoria para clasificar fallos de arranque.
/// </summary>
public sealed class ManagedProcess : IAsyncDisposable
{
    private readonly System.Diagnostics.Process _process;
    private readonly StreamWriter _stdoutLog;
    private readonly StreamWriter _stderrLog;
    private readonly TailBuffer _stdoutTail = new(200);
    private readonly TailBuffer _stderrTail = new(200);
    private readonly Lock _writeLock = new();
    private readonly ProcessRegistry? _registry;
    private bool _stopped;

    private ManagedProcess(System.Diagnostics.Process process, string role, string runId, CommandSpec spec, string stdoutLogPath, string stderrLogPath, ProcessRegistry? registry)
    {
        _process = process;
        Role = role;
        RunId = runId;
        Command = spec.Display;
        StdoutLogPath = stdoutLogPath;
        StderrLogPath = stderrLogPath;
        _registry = registry;
        _stdoutLog = new StreamWriter(stdoutLogPath, append: false, Encoding.UTF8) { AutoFlush = true };
        _stderrLog = new StreamWriter(stderrLogPath, append: false, Encoding.UTF8) { AutoFlush = true };
    }

    public string Role { get; }

    public string RunId { get; }

    public string Command { get; }

    public int ProcessId { get; private set; }

    public DateTimeOffset StartedAt { get; private set; }

    public string StdoutLogPath { get; }

    public string StderrLogPath { get; }

    public bool HasExited
    {
        get
        {
            try
            {
                return _process.HasExited;
            }
            catch (InvalidOperationException)
            {
                return true;
            }
        }
    }

    public int? ExitCode => HasExited ? _process.ExitCode : null;

    public static ManagedProcess Start(
        CommandSpec spec,
        ExecutableResolver resolver,
        string role,
        string runId,
        string logDirectory,
        ProcessRegistry? registry,
        WindowsJobObjectHolder? job)
    {
        Directory.CreateDirectory(logDirectory);
        var safeRole = role.Replace(':', '-');
        var psi = new ProcessStartInfo(resolver.Resolve(spec.FileName))
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            CreateNoWindow = true,
            WorkingDirectory = spec.WorkingDirectory ?? Environment.CurrentDirectory,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        foreach (var argument in spec.Arguments)
        {
            psi.ArgumentList.Add(argument);
        }

        ProcessEnvironment.Apply(psi.Environment, spec.Environment, deterministicCli: false);
        var process = new System.Diagnostics.Process { StartInfo = psi, EnableRaisingEvents = true };
        var managed = new ManagedProcess(process, role, runId, spec,
            Path.Combine(logDirectory, $"{safeRole}.stdout.log"), Path.Combine(logDirectory, $"{safeRole}.stderr.log"), registry);
        process.OutputDataReceived += (_, e) => managed.OnLine(e.Data, isError: false);
        process.ErrorDataReceived += (_, e) => managed.OnLine(e.Data, isError: true);
        process.Start();
        job?.TryAssign(process);
        process.StandardInput.Close();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        managed.ProcessId = process.Id;
        managed.StartedAt = DateTimeOffset.UtcNow;
        registry?.Add(new RegisteredProcess(runId, role, process.Id, process.StartTime.ToUniversalTime(), spec.Display));
        return managed;
    }

    public IReadOnlyList<string> StdoutTail(int lines) => Tail(_stdoutTail, lines);

    public IReadOnlyList<string> StderrTail(int lines) => Tail(_stderrTail, lines);

    /// <summary>Detiene el proceso y todo su árbol. Devuelve true si quedó detenido.</summary>
    public async Task<bool> StopAsync(TimeSpan timeout)
    {
        if (_stopped)
        {
            return HasExited;
        }

        _stopped = true;
        ProcessCommandRunner.KillTree(_process);
        try
        {
            using var cts = new CancellationTokenSource(timeout);
            await _process.WaitForExitAsync(cts.Token);
        }
        catch (OperationCanceledException)
        {
            // Se informa como no detenido.
        }

        var exited = HasExited;
        if (exited)
        {
            _registry?.Remove(ProcessId);
        }

        lock (_writeLock)
        {
            _stdoutLog.Flush();
            _stderrLog.Flush();
        }

        return exited;
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync(TimeSpan.FromSeconds(10));
        lock (_writeLock)
        {
            _stdoutLog.Dispose();
            _stderrLog.Dispose();
        }

        _process.Dispose();
    }

    private void OnLine(string? line, bool isError)
    {
        if (line is null)
        {
            return;
        }

        var sanitized = SecretRedactor.Redact(line);
        (isError ? _stderrTail : _stdoutTail).Add(sanitized);
        lock (_writeLock)
        {
            try
            {
                (isError ? _stderrLog : _stdoutLog).WriteLine(sanitized);
            }
            catch (ObjectDisposedException)
            {
                // El proceso se está cerrando.
            }
        }
    }

    private static IReadOnlyList<string> Tail(TailBuffer buffer, int lines)
    {
        var all = buffer.Snapshot();
        return all.Count <= lines ? all : all.Skip(all.Count - lines).ToList();
    }
}

/// <summary>Contenedor opcional del Job Object (solo Windows).</summary>
public sealed class WindowsJobObjectHolder : IDisposable
{
    private readonly WindowsJobObject? _job;

    private WindowsJobObjectHolder(WindowsJobObject? job, bool selfAssigned, string? note)
    {
        _job = job;
        SelfAssigned = selfAssigned;
        Note = note;
    }

    /// <summary>true si el propio servidor está dentro del job: todo lo que lance hereda la limpieza garantizada.</summary>
    public bool SelfAssigned { get; }

    public string? Note { get; }

    public static WindowsJobObjectHolder Create(bool assignSelf)
    {
        if (!OperatingSystem.IsWindows())
        {
            return new WindowsJobObjectHolder(null, false, "Job Objects no disponibles fuera de Windows: limpieza por registro de procesos.");
        }

        var job = WindowsJobObject.TryCreateKillOnClose(out var error);
        if (job is null)
        {
            return new WindowsJobObjectHolder(null, false, error);
        }

        if (assignSelf && job.TryAssignCurrentProcess(out var assignError))
        {
            return new WindowsJobObjectHolder(job, true, "Servidor asignado a un Job Object con KILL_ON_JOB_CLOSE.");
        }

        return new WindowsJobObjectHolder(job, false, assignSelf ? "No se pudo asignar el servidor al Job Object; se asignan los hijos individualmente." : null);
    }

    public void TryAssign(System.Diagnostics.Process process)
    {
        if (_job is not null && !SelfAssigned && OperatingSystem.IsWindows())
        {
            _job.TryAssign(process, out _);
        }
    }

    public void Dispose()
    {
        // Si el propio proceso está en el job, cerrar el handle lo terminaría (KILL_ON_JOB_CLOSE): el sistema lo cierra
        // al salir y entonces mueren los hijos que queden. Si no, cerrarlo termina los hijos asignados individualmente.
        if (OperatingSystem.IsWindows() && !SelfAssigned)
        {
            _job?.Dispose();
        }
    }
}
