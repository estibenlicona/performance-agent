using PerfAgent.Core.Policy;

namespace PerfAgent.Mcp.Lab;

/// <summary>Localiza la raíz del workspace: configuración explícita, variable de entorno o búsqueda del archivo de política.</summary>
public static class WorkspaceLocator
{
    public const string EnvironmentVariable = "PERFLAB_WORKSPACE";

    public static string Locate(string? configured)
    {
        if (!string.IsNullOrWhiteSpace(configured))
        {
            return Path.GetFullPath(configured);
        }

        var fromEnvironment = Environment.GetEnvironmentVariable(EnvironmentVariable);
        if (!string.IsNullOrWhiteSpace(fromEnvironment))
        {
            return Path.GetFullPath(fromEnvironment);
        }

        return FindUpwards(Environment.CurrentDirectory) ?? FindUpwards(AppContext.BaseDirectory) ?? Environment.CurrentDirectory;
    }

    private static string? FindUpwards(string start)
    {
        for (var dir = new DirectoryInfo(start); dir is not null; dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, SafetyPolicy.FileName)))
            {
                return dir.FullName;
            }
        }

        return null;
    }
}
