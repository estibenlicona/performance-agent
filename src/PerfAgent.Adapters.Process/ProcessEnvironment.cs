namespace PerfAgent.Adapters.Process;

/// <summary>Entorno heredado saneado para procesos hijos del laboratorio.</summary>
public static class ProcessEnvironment
{
    /// <summary>
    /// Variables que permiten inyectar código o perfiladores en un proceso .NET, o alterar su enlace de red.
    /// Se eliminan del entorno heredado: el laboratorio no debe ejecutar código ajeno a la API.
    /// </summary>
    private static readonly string[] StrippedVariables =
    [
        "DOTNET_STARTUP_HOOKS", "ASPNETCORE_HOSTINGSTARTUPASSEMBLIES", "ASPNETCORE_HOSTINGSTARTUPEXCLUDEASSEMBLIES",
        "ASPNETCORE_URLS", "ASPNETCORE_HTTP_PORTS", "ASPNETCORE_HTTPS_PORTS", "DOTNET_URLS", "URLS",
        "CORECLR_ENABLE_PROFILING", "CORECLR_PROFILER", "CORECLR_PROFILER_PATH", "CORECLR_PROFILER_PATH_32", "CORECLR_PROFILER_PATH_64",
        "COR_ENABLE_PROFILING", "COR_PROFILER", "COR_PROFILER_PATH", "DOTNET_DiagnosticPorts", "DOTNET_EnableDiagnostics",
        "DOTNET_EnableDiagnostics_IPC", "DOTNET_EnableDiagnostics_Profiler", "DOTNET_EnableDiagnostics_Debugger",
        "ASPNETCORE_ENVIRONMENT", "DOTNET_ENVIRONMENT",
    ];

    public static void Apply(IDictionary<string, string?> target, IReadOnlyDictionary<string, string> overrides, bool deterministicCli)
    {
        foreach (var name in StrippedVariables)
        {
            foreach (var key in target.Keys.Where(k => string.Equals(k, name, StringComparison.OrdinalIgnoreCase)).ToList())
            {
                target.Remove(key);
            }
        }

        // K6_* del entorno del usuario podría añadir salidas, destinos o tokens a k6: el laboratorio los fija él mismo.
        foreach (var key in target.Keys.Where(k => k.StartsWith("K6_", StringComparison.OrdinalIgnoreCase)).ToList())
        {
            target.Remove(key);
        }

        if (deterministicCli)
        {
            target["DOTNET_CLI_UI_LANGUAGE"] = "en";
            target["DOTNET_NOLOGO"] = "1";
            target["DOTNET_CLI_TELEMETRY_OPTOUT"] = "1";
            target["DOTNET_SKIP_FIRST_TIME_EXPERIENCE"] = "1";
            target["MSBUILDTERMINALLOGGER"] = "off";
            target["MSBUILDDISABLENODEREUSE"] = "1";
        }

        foreach (var (key, value) in overrides)
        {
            target[key] = value;
        }
    }
}
