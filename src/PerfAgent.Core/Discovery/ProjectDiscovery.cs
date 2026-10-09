using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using PerfAgent.Core.Contracts;
using PerfAgent.Core.Execution;
using PerfAgent.Core.Policy;

namespace PerfAgent.Core.Discovery;

/// <summary>
/// Descubrimiento mínimo de una API ASP.NET Core: proyecto, frameworks, runtime disponible, OpenAPI
/// y pistas de dependencias. Solo lee archivos y evalúa propiedades MSBuild; nunca compila ni ejecuta la API.
/// </summary>
public sealed partial class ProjectDiscovery(ICommandRunner runner, Workspace workspace)
{
    private static readonly TimeSpan CommandTimeout = TimeSpan.FromSeconds(60);

    public async Task<ApiTarget> DiscoverAsync(string fullPath, string? framework, CancellationToken cancellationToken)
    {
        var relative = workspace.ToRelative(fullPath);
        if (fullPath.EndsWith(".sln", StringComparison.OrdinalIgnoreCase) || fullPath.EndsWith(".slnx", StringComparison.OrdinalIgnoreCase))
        {
            return DiscoverSolution(fullPath, relative);
        }

        var notes = new List<string>();
        var missing = new List<string>();
        var project = XDocument.Load(fullPath);
        var sdk = project.Root?.Attribute("Sdk")?.Value ?? string.Empty;
        var isWeb = sdk.Contains("Microsoft.NET.Sdk.Web", StringComparison.OrdinalIgnoreCase);
        var packages = project.Descendants()
            .Where(e => e.Name.LocalName == "PackageReference")
            .Select(e => e.Attribute("Include")?.Value)
            .OfType<string>()
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        var userSecretsId = project.Descendants().Any(e => e.Name.LocalName == "UserSecretsId");

        if (!isWeb)
        {
            missing.Add("El proyecto no usa Microsoft.NET.Sdk.Web: el MVP solo inicia APIs ASP.NET Core.");
        }

        var properties = await EvaluatePropertiesAsync(fullPath, framework, cancellationToken);
        var frameworks = ParseFrameworks(properties);
        if (frameworks.Count == 0)
        {
            missing.Add("No se pudo determinar el TargetFramework del proyecto.");
        }

        var selected = framework ?? (frameworks.Count == 1 ? frameworks[0] : null);
        if (selected is null && frameworks.Count > 1)
        {
            missing.Add($"El proyecto declara varios frameworks ({string.Join(", ", frameworks)}): indica api.framework en el manifiesto.");
        }
        else if (selected is not null && frameworks.Count > 0 && !frameworks.Contains(selected, StringComparer.OrdinalIgnoreCase))
        {
            missing.Add($"El framework '{selected}' no está declarado por el proyecto ({string.Join(", ", frameworks)}).");
        }

        RuntimeCompatibility? runtime = null;
        if (selected is not null)
        {
            runtime = await CheckRuntimeAsync(selected, cancellationToken);
            if (!runtime.RuntimeAvailable)
            {
                missing.Add($"No hay un runtime ASP.NET Core instalado compatible con {selected}.");
            }
        }

        var openApi = DetectOpenApi(packages);
        if (!openApi.Likely)
        {
            missing.Add("No se detectó OpenAPI: declara los endpoints en el manifiesto (endpoints) o indica api.openApiPath.");
        }

        if (userSecretsId)
        {
            notes.Add("El proyecto usa user-secrets: el laboratorio no los lee ni los muestra; la API los cargará solo si ASPNETCORE_ENVIRONMENT=Development.");
        }

        var projectDir = Path.GetDirectoryName(fullPath)!;
        var hints = DependencyHintScanner.Scan(projectDir, packages, userSecretsId);
        var launchProfiles = ReadLaunchProfileNames(projectDir);
        if (launchProfiles.Count > 0)
        {
            notes.Add($"launchSettings.json declara perfiles ({string.Join(", ", launchProfiles)}); el laboratorio no los usa: fija puerto y ambiente propios.");
        }

        return new ApiTarget
        {
            ProjectPath = relative,
            ProjectName = Path.GetFileNameWithoutExtension(fullPath),
            AssemblyName = properties.GetValueOrDefault("AssemblyName"),
            IsWebProject = isWeb,
            TargetFrameworks = frameworks,
            SelectedFramework = selected,
            Runtime = runtime,
            OpenApi = openApi,
            DependencyHints = hints,
            MissingInformation = missing,
            Notes = notes,
            Revision = await GetRevisionAsync(projectDir, cancellationToken),
        };
    }

    public async Task<SourceRevision> GetRevisionAsync(string directory, CancellationToken cancellationToken)
    {
        var head = await runner.RunAsync(AllowedCommands.GitHead(directory), CommandTimeout, cancellationToken);
        if (!head.Succeeded)
        {
            return new SourceRevision(null, null, null, "No es un repositorio git o no hay commits: revisión no disponible.");
        }

        var branch = await runner.RunAsync(AllowedCommands.GitBranch(directory), CommandTimeout, cancellationToken);
        var status = await runner.RunAsync(AllowedCommands.GitStatus(directory), CommandTimeout, cancellationToken);
        return new SourceRevision(
            head.StandardOutput.Trim(),
            branch.Succeeded ? branch.StandardOutput.Trim() : null,
            status.Succeeded ? status.StandardOutput.Trim().Length > 0 : null,
            null);
    }

    private ApiTarget DiscoverSolution(string fullPath, string relative)
    {
        var solutionDir = Path.GetDirectoryName(fullPath)!;
        var text = File.ReadAllText(fullPath);
        IEnumerable<string> projectPaths = fullPath.EndsWith(".slnx", StringComparison.OrdinalIgnoreCase)
            ? XDocument.Parse(text).Descendants().Where(e => e.Name.LocalName == "Project")
                .Select(e => e.Attribute("Path")?.Value).OfType<string>()
            : SlnProjectRegex().Matches(text).Select(m => m.Groups["path"].Value);

        var candidates = new List<string>();
        foreach (var path in projectPaths.Where(p => p.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase)))
        {
            var projectFull = Path.GetFullPath(Path.Combine(solutionDir, path.Replace('\\', Path.DirectorySeparatorChar)));
            if (!workspace.IsInsideWorkspace(projectFull) || !File.Exists(projectFull))
            {
                continue;
            }

            var sdk = XDocument.Load(projectFull).Root?.Attribute("Sdk")?.Value ?? string.Empty;
            if (sdk.Contains("Microsoft.NET.Sdk.Web", StringComparison.OrdinalIgnoreCase))
            {
                candidates.Add(workspace.ToRelative(projectFull));
            }
        }

        return new ApiTarget
        {
            ProjectPath = relative,
            ProjectName = Path.GetFileNameWithoutExtension(fullPath),
            CandidateProjects = candidates,
            MissingInformation = candidates.Count == 0
                ? ["La solución no contiene proyectos ASP.NET Core (Microsoft.NET.Sdk.Web)."]
                : ["El MVP inicia un .csproj explícito: elige uno de los proyectos candidatos y vuelve a ejecutar discover_api."],
        };
    }

    private async Task<Dictionary<string, string>> EvaluatePropertiesAsync(string projectFullPath, string? framework, CancellationToken cancellationToken)
    {
        var result = await runner.RunAsync(
            AllowedCommands.DotnetGetProperties(projectFullPath, framework, "TargetFramework", "TargetFrameworks", "AssemblyName"),
            CommandTimeout,
            cancellationToken);
        var properties = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (!result.Succeeded)
        {
            return properties;
        }

        try
        {
            using var json = JsonDocument.Parse(result.StandardOutput);
            if (json.RootElement.TryGetProperty("Properties", out var props))
            {
                foreach (var p in props.EnumerateObject())
                {
                    properties[p.Name] = p.Value.GetString() ?? string.Empty;
                }
            }
        }
        catch (JsonException)
        {
            // Salida no JSON: se tratará como propiedades no disponibles.
        }

        return properties;
    }

    private static List<string> ParseFrameworks(Dictionary<string, string> properties)
    {
        var multi = properties.GetValueOrDefault("TargetFrameworks");
        var single = properties.GetValueOrDefault("TargetFramework");
        var raw = !string.IsNullOrWhiteSpace(multi) ? multi : single;
        return (raw ?? string.Empty)
            .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private async Task<RuntimeCompatibility> CheckRuntimeAsync(string framework, CancellationToken cancellationToken)
    {
        var policy = workspace.Policy;
        var match = FrameworkVersionRegex().Match(framework);
        var major = match.Success ? match.Groups["major"].Value : null;
        var result = await runner.RunAsync(AllowedCommands.DotnetListRuntimes(), CommandTimeout, cancellationToken);
        var installed = result.Succeeded
            ? result.StandardOutput.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Where(l => l.StartsWith("Microsoft.AspNetCore.App ", StringComparison.Ordinal))
                .Select(l => l.Split(' ')[1])
                .ToList()
            : [];
        var available = major is not null && installed.Any(v => v.StartsWith(major + ".", StringComparison.Ordinal));
        var tested = policy.SupportedTargetFrameworks.Contains(framework, StringComparer.OrdinalIgnoreCase);
        var compatibility = !match.Success ? "unsupported" : tested ? "tested" : "untested";
        var detail = compatibility switch
        {
            "tested" => "Framework incluido en la matriz probada (docs/tested-matrix.md).",
            "untested" when policy.AllowUntestedFrameworks => "Framework no probado: la política lo permite, pero los resultados se marcarán como no verificados.",
            "untested" => "Framework no probado: la política (allowUntestedFrameworks=false) bloquea la ejecución.",
            _ => "Solo se admiten frameworks .NET (netX.Y).",
        };
        return new RuntimeCompatibility(framework, major, installed, available, compatibility, detail);
    }

    private static OpenApiDetection DetectOpenApi(IReadOnlyCollection<string> packages)
    {
        if (packages.Contains("Microsoft.AspNetCore.OpenApi", StringComparer.OrdinalIgnoreCase))
        {
            return new OpenApiDetection(true, "Microsoft.AspNetCore.OpenApi", "/openapi/v1.json",
                "MapOpenApi() suele registrarse solo en Development: comprueba ASPNETCORE_ENVIRONMENT.");
        }

        if (packages.Any(p => p.StartsWith("Swashbuckle.AspNetCore", StringComparison.OrdinalIgnoreCase)))
        {
            return new OpenApiDetection(true, "Swashbuckle.AspNetCore", "/swagger/v1/swagger.json",
                "UseSwagger() suele registrarse solo en Development.");
        }

        if (packages.Contains("NSwag.AspNetCore", StringComparer.OrdinalIgnoreCase))
        {
            return new OpenApiDetection(true, "NSwag.AspNetCore", "/swagger/v1/swagger.json", null);
        }

        return new OpenApiDetection(false, null, null, null);
    }

    private static List<string> ReadLaunchProfileNames(string projectDir)
    {
        var path = Path.Combine(projectDir, "Properties", "launchSettings.json");
        if (!File.Exists(path))
        {
            return [];
        }

        try
        {
            using var json = JsonDocument.Parse(File.ReadAllText(path), new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
            return json.RootElement.TryGetProperty("profiles", out var profiles)
                ? profiles.EnumerateObject().Select(p => p.Name).ToList()
                : [];
        }
        catch (JsonException)
        {
            return [];
        }
    }

    [GeneratedRegex(@"^Project\(""\{[^}]+\}""\)\s*=\s*""[^""]*"",\s*""(?<path>[^""]+)""", RegexOptions.Multiline)]
    private static partial Regex SlnProjectRegex();

    [GeneratedRegex(@"^net(?<major>\d{1,2})\.\d$")]
    private static partial Regex FrameworkVersionRegex();
}
