using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace PerfAgent.Core.Security;

/// <summary>
/// Saneamiento de secretos en cualquier texto que pueda llegar al contexto del modelo, a logs o a informes
/// (stdout/stderr de procesos, configuración, errores de proveedores). Conserva la clave y oculta el valor.
/// </summary>
public static partial class SecretRedactor
{
    public const string Mask = "***";

    private static readonly string[] SensitiveKeyFragments =
    [
        "password", "passwd", "pwd", "secret", "token", "apikey", "api_key", "api-key", "accountkey",
        "sharedaccesskey", "accesskey", "privatekey", "private_key", "clientsecret", "client_secret",
        "connectionstring", "credential", "sas", "signature",
    ];

    public static string Redact(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return text ?? string.Empty;
        }

        var result = PrivateKeyRegex().Replace(text, "***private-key***");
        result = JwtRegex().Replace(result, "***jwt***");
        result = BearerRegex().Replace(result, "${scheme} " + Mask);
        result = UrlUserInfoRegex().Replace(result, "${scheme}://" + Mask + "@");
        result = QuerySecretRegex().Replace(result, "${prefix}" + Mask);
        result = KeyValueSecretRegex().Replace(result, m => m.Groups["key"].Value + m.Groups["sep"].Value + Mask);
        result = AwsKeyRegex().Replace(result, "***aws-access-key***");
        result = GitHubTokenRegex().Replace(result, "***github-token***");
        return result;
    }

    /// <summary>
    /// Saneamiento de documentos JSON preservando su validez: solo se modifican valores de texto (y se ocultan por
    /// completo los valores de texto de claves sensibles). Números, booleanos y estructura quedan intactos.
    /// Si el texto no es JSON, se aplica <see cref="Redact"/>.
    /// </summary>
    public static string RedactJson(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return json ?? string.Empty;
        }

        // Copia en streaming token a token: sin límite práctico de profundidad (los árboles de llamadas de una
        // muestra de CPU superan los 64 niveles) y tolerante a claves duplicadas.
        try
        {
            var reader = new Utf8JsonReader(Encoding.UTF8.GetBytes(json), new JsonReaderOptions { MaxDepth = 1024 });
            using var stream = new MemoryStream();
            using (var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { MaxDepth = 1024, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping }))
            {
                var keys = new Stack<string?>();
                string? key = null;
                while (reader.Read())
                {
                    switch (reader.TokenType)
                    {
                        case JsonTokenType.StartObject:
                            writer.WriteStartObject();
                            keys.Push(key);
                            key = null;
                            break;
                        case JsonTokenType.EndObject:
                            writer.WriteEndObject();
                            key = keys.Pop();
                            break;
                        case JsonTokenType.StartArray:
                            writer.WriteStartArray();
                            keys.Push(key);
                            break;
                        case JsonTokenType.EndArray:
                            writer.WriteEndArray();
                            key = keys.Pop();
                            break;
                        case JsonTokenType.PropertyName:
                            key = reader.GetString();
                            writer.WritePropertyName(key!);
                            break;
                        case JsonTokenType.String:
                            var text = reader.GetString() ?? string.Empty;
                            writer.WriteStringValue(key is not null && IsSensitiveKey(key) && text.Length > 0 ? Mask : Redact(text));
                            break;
                        case JsonTokenType.Number:
                            writer.WriteRawValue(reader.ValueSpan, skipInputValidation: true);
                            break;
                        case JsonTokenType.True:
                        case JsonTokenType.False:
                            writer.WriteBooleanValue(reader.TokenType == JsonTokenType.True);
                            break;
                        case JsonTokenType.Null:
                            writer.WriteNullValue();
                            break;
                    }
                }
            }

            return Encoding.UTF8.GetString(stream.ToArray());
        }
        catch (JsonException)
        {
            // No es JSON: saneamiento de texto.
            return Redact(json);
        }
    }

    /// <summary>true si el nombre de una clave sugiere que su valor es un secreto.</summary>
    public static bool IsSensitiveKey(string key)
    {
        var normalized = key.Replace("__", ":", StringComparison.Ordinal).ToLowerInvariant();
        return SensitiveKeyFragments.Any(fragment => normalized.Contains(fragment, StringComparison.Ordinal));
    }

    /// <summary>true si el valor contiene algo con forma de secreto (token, credencial en URL, etc.).</summary>
    public static bool LooksLikeSecret(string? value) =>
        !string.IsNullOrEmpty(value) && !string.Equals(Redact(value), value, StringComparison.Ordinal);

    [GeneratedRegex(@"-----BEGIN [A-Z ]*PRIVATE KEY-----[\s\S]*?-----END [A-Z ]*PRIVATE KEY-----")]
    private static partial Regex PrivateKeyRegex();

    [GeneratedRegex(@"\beyJ[A-Za-z0-9_-]{8,}\.[A-Za-z0-9_-]{8,}\.[A-Za-z0-9_-]{8,}")]
    private static partial Regex JwtRegex();

    [GeneratedRegex(@"(?<scheme>\b(?:Bearer|Basic))\s+[A-Za-z0-9\-._~+/]{8,}=*", RegexOptions.IgnoreCase)]
    private static partial Regex BearerRegex();

    [GeneratedRegex(@"(?<scheme>\b[a-z][a-z0-9+.\-]{1,20})://[^\s/:@""']+:[^\s/@""']+@", RegexOptions.IgnoreCase)]
    private static partial Regex UrlUserInfoRegex();

    [GeneratedRegex(@"(?<prefix>[?&](?:sig|signature|se|skoid|sktid|skt|ske|sks|skv|code|token|access_token|api[_-]?key|key)=)[^&\s""']+", RegexOptions.IgnoreCase)]
    private static partial Regex QuerySecretRegex();

    [GeneratedRegex(
        @"(?<key>\b[\w.\-:]*(?:password|passwd|pwd|secret|token|api[_-]?key|accountkey|sharedaccesskey|accesskey|client[_-]?secret|private[_-]?key|sharedaccesssignature)[\w.\-:]*)(?<sep>""?\s*[=:]\s*""?)(?:""[^""]*""|'[^']*'|[^;\s,&""'}]+)",
        RegexOptions.IgnoreCase)]
    private static partial Regex KeyValueSecretRegex();

    [GeneratedRegex(@"\b(?:AKIA|ASIA)[0-9A-Z]{16}\b")]
    private static partial Regex AwsKeyRegex();

    [GeneratedRegex(@"\bgh[pousr]_[A-Za-z0-9]{30,}\b")]
    private static partial Regex GitHubTokenRegex();
}
