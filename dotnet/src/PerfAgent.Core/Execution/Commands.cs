namespace PerfAgent.Core.Execution;

/// <summary>
/// Comando a ejecutar sin shell. Solo se construye desde <see cref="AllowedCommands"/>: el modelo nunca
/// forma líneas de comando; únicamente aporta valores que antes valida la política.
/// </summary>
public sealed record CommandSpec
{
    internal CommandSpec(string fileName, IReadOnlyList<string> arguments, string? workingDirectory, string purpose)
    {
        FileName = fileName;
        Arguments = arguments;
        WorkingDirectory = workingDirectory;
        Purpose = purpose;
    }

    public string FileName { get; }

    public IReadOnlyList<string> Arguments { get; }

    public string? WorkingDirectory { get; }

    /// <summary>Descripción del propósito (auditoría).</summary>
    public string Purpose { get; }

    public IReadOnlyDictionary<string, string> Environment { get; init; } = new Dictionary<string, string>();

    /// <summary>Línea de comando legible (ya saneada) para logs e informes.</summary>
    public string Display => Security.SecretRedactor.Redact(
        FileName + " " + string.Join(' ', Arguments.Select(a => a.Contains(' ', StringComparison.Ordinal) ? $"\"{a}\"" : a)));
}

/// <param name="StandardOutput">Salida estándar ya saneada (y truncada si excede el límite).</param>
/// <param name="StandardError">Error estándar ya saneado (y truncado si excede el límite).</param>
public sealed record CommandResult(
    int ExitCode,
    string StandardOutput,
    string StandardError,
    bool TimedOut,
    bool Cancelled,
    TimeSpan Duration,
    bool Truncated)
{
    public bool Succeeded => ExitCode == 0 && !TimedOut && !Cancelled;
}

public interface ICommandRunner
{
    Task<CommandResult> RunAsync(CommandSpec spec, TimeSpan timeout, CancellationToken cancellationToken);
}

/// <summary>Catálogo cerrado de comandos que el laboratorio puede ejecutar.</summary>
public static class AllowedCommands
{
    public static CommandSpec DotnetListRuntimes() =>
        new("dotnet", ["--list-runtimes"], null, "Listar runtimes .NET instalados");

    public static CommandSpec DotnetListSdks() =>
        new("dotnet", ["--list-sdks"], null, "Listar SDKs .NET instalados");

    public static CommandSpec DotnetVersion(string workingDirectory) =>
        new("dotnet", ["--version"], workingDirectory, "Versión del SDK .NET efectivo (global.json)");

    public static CommandSpec DotnetGetProperties(string projectFullPath, string? framework, params string[] properties)
    {
        var args = new List<string> { "msbuild", projectFullPath, "-nologo", "-nodeReuse:false", "-p:Configuration=Release" };
        if (framework is not null)
        {
            args.Add($"-p:TargetFramework={framework}");
        }

        args.AddRange(properties.Select(p => $"-getProperty:{p}"));
        return new CommandSpec("dotnet", args, Path.GetDirectoryName(projectFullPath), "Evaluar propiedades MSBuild del proyecto");
    }

    public static CommandSpec DotnetBuildRelease(string projectFullPath, string? framework)
    {
        // Sin reutilización de nodos MSBuild ni servidor de compilación: no deben quedar procesos tras la ejecución.
        var args = new List<string>
        {
            "build", projectFullPath, "-c", "Release", "-nologo", "-v:minimal", "-clp:NoSummary",
            "-nodeReuse:false", "-p:UseSharedCompilation=false",
        };
        if (framework is not null)
        {
            args.Add("-f");
            args.Add(framework);
        }

        return new CommandSpec("dotnet", args, Path.GetDirectoryName(projectFullPath), "Compilar la API en Release");
    }

    /// <summary>Inicia un ensamblado ya compilado (API o mock); la configuración llega por variables de entorno validadas.</summary>
    public static CommandSpec DotnetRunAssembly(string assemblyFullPath, string purpose, params string[] extraArguments) =>
        new("dotnet", [assemblyFullPath, .. extraArguments], Path.GetDirectoryName(assemblyFullPath), purpose);

    public static CommandSpec GitHead(string directory) =>
        new("git", ["rev-parse", "HEAD"], directory, "Revisión del código");

    public static CommandSpec GitBranch(string directory) =>
        new("git", ["rev-parse", "--abbrev-ref", "HEAD"], directory, "Rama del código");

    public static CommandSpec GitStatus(string directory) =>
        new("git", ["status", "--porcelain", "--untracked-files=no"], directory, "Cambios sin confirmar");

    public static CommandSpec DockerVersion() =>
        new("docker", ["version", "--format", "{{.Server.Version}}"], null, "Comprobar Docker");

    public static CommandSpec DockerComposeUp(string composeFullPath, string projectName, string service) =>
        new("docker", ["compose", "-f", composeFullPath, "-p", projectName, "up", "-d", "--wait", service],
            Path.GetDirectoryName(composeFullPath), "Levantar dependencia en contenedor");

    public static CommandSpec DockerComposeDown(string composeFullPath, string projectName) =>
        new("docker", ["compose", "-f", composeFullPath, "-p", projectName, "down", "-v", "--remove-orphans"],
            Path.GetDirectoryName(composeFullPath), "Eliminar contenedores del laboratorio");

    public static CommandSpec K6Version(string k6Executable) =>
        new(k6Executable, ["version"], null, "Versión de k6");

    /// <summary>Fallback controlado: k6 CLI con un script generado y validado por la fachada (resumen por handleSummary).</summary>
    public static CommandSpec K6Run(string k6Executable, string scriptFullPath, string jsonOutputPath) =>
        new(k6Executable,
            ["run", "--quiet", "--no-color", "--no-usage-report", "--out", $"json={jsonOutputPath}", scriptFullPath],
            Path.GetDirectoryName(scriptFullPath),
            "Ejecutar escenario k6 validado (fallback CLI)");

    /// <summary>Validación sin tráfico: k6 analiza el script y sus opciones sin ejecutarlo.</summary>
    public static CommandSpec K6Inspect(string k6Executable, string scriptFullPath) =>
        new(k6Executable, ["inspect", scriptFullPath], Path.GetDirectoryName(scriptFullPath), "Validar escenario k6 sin generar tráfico");
}
