namespace PerfAgent.Mcp.Configuration;

public sealed class PerfLabOptions
{
    public const string SectionName = "PerfLab";

    /// <summary>Raíz del workspace. Si es null se usa PERFLAB_WORKSPACE o se busca perf-lab.policy.json hacia arriba.</summary>
    public string? WorkspaceRoot { get; set; }

    public string MockServerProject { get; set; } = "dotnet/src/PerfAgent.MockServer/PerfAgent.MockServer.csproj";

    /// <summary>Asignar el propio servidor a un Job Object (Windows) para que sus procesos hijos nunca queden huérfanos.</summary>
    public bool AssignServerToJobObject { get; set; } = true;
}

/// <summary>Proveedores comunitarios secundarios (no se exponen al agente; solo los usa la fachada).</summary>
public sealed class CommunityProvidersOptions
{
    public const string SectionName = "CommunityProviders";

    public ProviderProcessOptions K6Mcp { get; set; } = new();

    public ProviderProcessOptions DotNetDiagnosticsMcp { get; set; } = new();

    public K6CliOptions K6 { get; set; } = new();
}

public sealed class ProviderProcessOptions
{
    public bool Enabled { get; set; } = true;

    public string Command { get; set; } = string.Empty;

    public List<string> Arguments { get; set; } = [];

    public string? ExpectedVersion { get; set; }

    /// <summary>Directorio de trabajo del proveedor (por defecto, la raíz del workspace; dotnet-diagnostics-mcp necesita el manifiesto de herramientas locales).</summary>
    public string? WorkingDirectory { get; set; }
}

public sealed class K6CliOptions
{
    public string Executable { get; set; } = string.Empty;

    public string ExpectedVersion { get; set; } = string.Empty;
}
