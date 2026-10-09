using System.Collections.Concurrent;

namespace PerfAgent.Adapters.Process;

/// <summary>
/// Resuelve ejecutables permitidos buscando SOLO en PATH (nunca en el directorio de trabajo del proyecto,
/// para evitar que un binario plantado en el repositorio suplante a dotnet, git o docker).
/// </summary>
public sealed class ExecutableResolver
{
    private readonly ConcurrentDictionary<string, string?> _cache = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _allowedNames;
    private readonly HashSet<string> _allowedFullPaths;

    public ExecutableResolver(IEnumerable<string> allowedNames, IEnumerable<string> allowedFullPaths)
    {
        _allowedNames = new HashSet<string>(allowedNames, StringComparer.OrdinalIgnoreCase);
        _allowedFullPaths = new HashSet<string>(allowedFullPaths.Select(Path.GetFullPath), OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
    }

    public static ExecutableResolver Default(params string[] extraFullPaths) => new(["dotnet", "git", "docker"], extraFullPaths);

    public void AllowFullPath(string fullPath) => _allowedFullPaths.Add(Path.GetFullPath(fullPath));

    /// <summary>Devuelve la ruta completa o lanza si el ejecutable no está permitido o no existe.</summary>
    public string Resolve(string fileName)
    {
        if (Path.IsPathRooted(fileName))
        {
            var full = Path.GetFullPath(fileName);
            if (!_allowedFullPaths.Contains(full))
            {
                throw new UnauthorizedAccessException($"Ejecutable no permitido: {fileName}");
            }

            return File.Exists(full) ? full : throw new FileNotFoundException($"No existe el ejecutable {fileName}.", full);
        }

        if (!_allowedNames.Contains(fileName))
        {
            throw new UnauthorizedAccessException($"Ejecutable no permitido: {fileName}");
        }

        return _cache.GetOrAdd(fileName, FindOnPath)
            ?? throw new FileNotFoundException($"'{fileName}' no está instalado o no está en PATH.", fileName);
    }

    public bool TryResolve(string fileName, out string? fullPath)
    {
        try
        {
            fullPath = Resolve(fileName);
            return true;
        }
        catch (Exception ex) when (ex is FileNotFoundException or UnauthorizedAccessException)
        {
            fullPath = null;
            return false;
        }
    }

    private static string? FindOnPath(string fileName)
    {
        var extensions = OperatingSystem.IsWindows()
            ? (Environment.GetEnvironmentVariable("PATHEXT") ?? ".EXE;.CMD;.BAT").Split(';', StringSplitOptions.RemoveEmptyEntries)
            : [string.Empty];
        if (OperatingSystem.IsWindows() && fileName.Equals("dotnet", StringComparison.OrdinalIgnoreCase))
        {
            // Preferimos el host dotnet en ejecución (mismo SDK/runtime que el laboratorio).
            var host = Environment.ProcessPath;
            if (host is not null && Path.GetFileNameWithoutExtension(host).Equals("dotnet", StringComparison.OrdinalIgnoreCase))
            {
                return host;
            }
        }

        foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? string.Empty).Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            if (!Path.IsPathRooted(dir))
            {
                continue;
            }

            foreach (var ext in extensions)
            {
                var candidate = Path.Combine(dir.Trim('"'), fileName + ext.ToLowerInvariant());
                if (File.Exists(candidate))
                {
                    return candidate;
                }
            }
        }

        return null;
    }
}
