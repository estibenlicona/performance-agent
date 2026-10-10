namespace PerfAgent.Core.Policy;

/// <summary>Workspace autorizado y resolución segura de rutas (sin escapes con <c>..</c> ni enlaces).</summary>
public sealed class Workspace
{
    private static readonly StringComparison PathComparison =
        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    public Workspace(string root, SafetyPolicy policy)
    {
        Root = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        Policy = policy;
    }

    public string Root { get; }

    public SafetyPolicy Policy { get; }

    public string ArtifactsRoot => Path.Combine(Root, Policy.ArtifactsRoot);

    public string ToRelative(string fullPath) =>
        Path.GetRelativePath(Root, fullPath).Replace('\\', '/');

    /// <summary>
    /// Resuelve <paramref name="path"/> (relativa al workspace o absoluta) y comprueba que esté dentro de
    /// alguna de las raíces permitidas. Devuelve null y una violación si no lo está.
    /// </summary>
    public string? TryResolve(string? path, IEnumerable<string> allowedRoots, string field, out PolicyViolation? violation)
    {
        violation = null;
        if (string.IsNullOrWhiteSpace(path))
        {
            violation = new PolicyViolation("path.missing", field, "La ruta es obligatoria.");
            return null;
        }

        if (path.IndexOfAny(Path.GetInvalidPathChars()) >= 0)
        {
            violation = new PolicyViolation("path.invalid", field, "La ruta contiene caracteres no válidos.");
            return null;
        }

        string full;
        try
        {
            full = Path.GetFullPath(Path.IsPathRooted(path) ? path : Path.Combine(Root, path));
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            violation = new PolicyViolation("path.invalid", field, $"Ruta no válida: {ex.Message}");
            return null;
        }

        full = ResolveLinks(full);
        foreach (var root in allowedRoots)
        {
            var allowed = Path.GetFullPath(Path.Combine(Root, root))
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            if (IsSameOrInside(full, allowed))
            {
                return full;
            }
        }

        violation = new PolicyViolation(
            "path.outside-allowed-roots",
            field,
            $"La ruta '{path}' está fuera de las raíces autorizadas ({string.Join(", ", allowedRoots)}) del workspace.");
        return null;
    }

    public bool IsInsideWorkspace(string fullPath) => IsSameOrInside(Path.GetFullPath(fullPath), Root);

    private static bool IsSameOrInside(string candidate, string root) =>
        string.Equals(candidate, root, PathComparison)
        || candidate.StartsWith(root + Path.DirectorySeparatorChar, PathComparison);

    private static string ResolveLinks(string fullPath)
    {
        try
        {
            FileSystemInfo info = Directory.Exists(fullPath) ? new DirectoryInfo(fullPath) : new FileInfo(fullPath);
            var target = info.LinkTarget is null ? null : info.ResolveLinkTarget(returnFinalTarget: true);
            return target is null ? fullPath : Path.GetFullPath(target.FullName);
        }
        catch (IOException)
        {
            return fullPath;
        }
        catch (UnauthorizedAccessException)
        {
            return fullPath;
        }
    }
}
