using System.Diagnostics;
using System.Text;
using Microsoft.Extensions.Logging;
using PerfAgent.Core.Execution;
using PerfAgent.Core.Security;

namespace PerfAgent.Adapters.Process;

/// <summary>
/// Ejecuta comandos cortos del catálogo permitido, sin shell, con timeout, salida acotada y saneada,
/// y terminación del árbol de procesos si se cancela o vence el tiempo.
/// </summary>
public sealed class ProcessCommandRunner(ExecutableResolver resolver, ILogger<ProcessCommandRunner> logger) : ICommandRunner
{
    private const int MaxCapturedChars = 512 * 1024;

    public async Task<CommandResult> RunAsync(CommandSpec spec, TimeSpan timeout, CancellationToken cancellationToken)
    {
        var started = Stopwatch.StartNew();
        string executable;
        try
        {
            executable = resolver.Resolve(spec.FileName);
        }
        catch (Exception ex) when (ex is FileNotFoundException or UnauthorizedAccessException)
        {
            return new CommandResult(-1, string.Empty, SecretRedactor.Redact(ex.Message), false, false, started.Elapsed, false);
        }

        var psi = new ProcessStartInfo(executable)
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

        ProcessEnvironment.Apply(psi.Environment, spec.Environment, deterministicCli: true);

        var stdout = new BoundedText(MaxCapturedChars);
        var stderr = new BoundedText(MaxCapturedChars);
        using var process = new System.Diagnostics.Process { StartInfo = psi, EnableRaisingEvents = true };
        process.OutputDataReceived += (_, e) =>
        {
            if (e.Data is not null)
            {
                stdout.AppendLine(e.Data);
            }
        };
        process.ErrorDataReceived += (_, e) =>
        {
            if (e.Data is not null)
            {
                stderr.AppendLine(e.Data);
            }
        };

        logger.LogDebug("Running {Purpose}: {Command}", spec.Purpose, spec.Display);
        try
        {
            process.Start();
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            return new CommandResult(-1, string.Empty, SecretRedactor.Redact(ex.Message), false, false, started.Elapsed, false);
        }

        process.StandardInput.Close();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        using var timeoutCts = new CancellationTokenSource(timeout);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCts.Token);
        var timedOut = false;
        var cancelled = false;
        try
        {
            await process.WaitForExitAsync(linked.Token);
        }
        catch (OperationCanceledException)
        {
            cancelled = cancellationToken.IsCancellationRequested;
            timedOut = !cancelled;
            KillTree(process);
        }

        // Garantiza que los lectores asíncronos vaciaron stdout/stderr.
        process.WaitForExit(TimeSpan.FromSeconds(10));
        var exitCode = process.HasExited ? process.ExitCode : -1;
        return new CommandResult(
            exitCode,
            SecretRedactor.Redact(stdout.ToString()),
            SecretRedactor.Redact(stderr.ToString()),
            timedOut,
            cancelled,
            started.Elapsed,
            stdout.Truncated || stderr.Truncated);
    }

    internal static void KillTree(System.Diagnostics.Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch (InvalidOperationException)
        {
            // Ya terminó.
        }
        catch (System.ComponentModel.Win32Exception)
        {
            // Sin permisos o ya terminó; se informará como no detenido si sigue vivo.
        }
    }
}
