using System.Security.Cryptography;
using System.Text;
using YamlDotNet.Core;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace PerfAgent.Core.Manifest;

public sealed record LoadedManifest(RunManifest Manifest, string RelativePath, string FullPath, string Sha256);

/// <summary>Carga estricta de manifiestos YAML: campos desconocidos o tipos inválidos son errores.</summary>
public static class ManifestLoader
{
    private static readonly IDeserializer Deserializer = new DeserializerBuilder()
        .WithNamingConvention(CamelCaseNamingConvention.Instance)
        .Build();

    public static RunManifest Parse(string yaml)
    {
        try
        {
            return Deserializer.Deserialize<RunManifest>(yaml) ?? new RunManifest();
        }
        catch (YamlException ex)
        {
            var inner = ex.InnerException?.Message;
            var detail = inner is null ? ex.Message : $"{ex.Message} {inner}";
            throw new ManifestFormatException(
                $"Manifiesto inválido (línea {ex.Start.Line}, columna {ex.Start.Column}): {detail}", ex);
        }
    }

    public static LoadedManifest Load(string fullPath, string relativePath)
    {
        var bytes = File.ReadAllBytes(fullPath);
        var yaml = Encoding.UTF8.GetString(bytes);
        var manifest = Parse(yaml);
        return new LoadedManifest(manifest, relativePath, fullPath, Convert.ToHexStringLower(SHA256.HashData(bytes)));
    }
}

public sealed class ManifestFormatException(string message, Exception inner) : Exception(message, inner);
