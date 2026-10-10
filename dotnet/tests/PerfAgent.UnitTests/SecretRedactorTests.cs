using PerfAgent.Core.Discovery;
using PerfAgent.Core.Security;

namespace PerfAgent.UnitTests;

public sealed class SecretRedactorTests
{
    // Credenciales FALSAS partidas en trozos: así los escáneres de secretos (p. ej. push protection de GitHub)
    // no las confunden con credenciales reales; el valor final que se prueba es el mismo.
    private const string FakeJwtHeader = "eyJhbGciOiJIUzI1NiJ9";
    private const string FakeJwt = FakeJwtHeader + "." + "eyJzdWIiOiIxMjM0NTY3ODkwIn0" + "." + "c2lnbmF0dXJlLXZhbHVl";
    private const string FakeAwsKey = "AK" + "IA" + "ABCDEFGHIJKLMNOP";
    private const string FakeGitHubToken = "gh" + "p_" + "abcdefghijklmnopqrstuvwxyz0123456789AB";
    private const string FakePrivateKey = "-----BEGIN RSA " + "PRIVATE" + " KEY-----" + "\n" + "MIIEow" + "\n" + "-----END RSA " + "PRIVATE" + " KEY-----";

    [Theory]
    [InlineData("Server=tcp:db.local,1433;User Id=sa;Password=P@ssw0rd!;", "P@ssw0rd!")]
    [InlineData("DefaultEndpointsProtocol=https;AccountName=acct;AccountKey=bWFnaWMta2V5LXZhbHVl==;EndpointSuffix=core.windows.net", "bWFnaWMta2V5LXZhbHVl==")]
    [InlineData("Authorization: Bearer abcdefghijklmnopqrstuvwxyz0123456789", "abcdefghijklmnopqrstuvwxyz0123456789")]
    [InlineData("token " + FakeJwt + " ok", FakeJwtHeader)]
    [InlineData("calling https://admin:hunter2@internal.example.com/api", "hunter2")]
    [InlineData("GET https://acct.blob.core.windows.net/c/b?sv=2024&sig=AbCdEf123%2B&se=2026", "AbCdEf123%2B")]
    [InlineData("{\"ClientSecret\": \"s3cr3t-value\"}", "s3cr3t-value")]
    [InlineData("x-api-key: 0123456789abcdef", "0123456789abcdef")]
    [InlineData("aws " + FakeAwsKey + " used", FakeAwsKey)]
    [InlineData("gh token " + FakeGitHubToken, FakeGitHubToken)]
    [InlineData(FakePrivateKey, "MIIEow")]
    public void SecretsAreMaskedButKeysRemainReadable(string input, string secret)
    {
        var output = SecretRedactor.Redact(input);

        Assert.DoesNotContain(secret, output, StringComparison.Ordinal);
        Assert.True(SecretRedactor.LooksLikeSecret(input));
    }

    [Theory]
    [InlineData("info: PerfLab.Api[0] PerfLab.Api ready. Faults: Cpu=True")]
    [InlineData("Now listening on: http://127.0.0.1:5000")]
    [InlineData("GET /api/products/42 200 3.1 ms")]
    public void OrdinaryLogLinesAreUnchanged(string line) =>
        Assert.Equal(line, SecretRedactor.Redact(line));

    [Theory]
    [InlineData("ConnectionStrings__Default", true)]
    [InlineData("Payments:ApiKey", true)]
    [InlineData("AzureAd__ClientSecret", true)]
    [InlineData("Lab__Faults__Cpu", false)]
    [InlineData("Dependencies__Pricing__BaseUrl", false)]
    public void SensitiveKeysAreDetected(string key, bool sensitive) =>
        Assert.Equal(sensitive, SecretRedactor.IsSensitiveKey(key));

    [Fact]
    public void DependencyHintsNeverExposeConnectionStringValues()
    {
        var dir = Path.Combine(Path.GetTempPath(), "perflab-hints-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        try
        {
            File.WriteAllText(Path.Combine(dir, "appsettings.json"), """
                {
                  "ConnectionStrings": {
                    "Orders": "Server=tcp:orders-sql.database.windows.net,1433;User Id=app;Password=Sup3rS3cret;",
                    "Cache": "orders-cache.redis.cache.windows.net:6380,password=abc123,ssl=True",
                    "Bus": "Endpoint=sb://orders-bus.servicebus.windows.net/;SharedAccessKeyName=root;SharedAccessKey=K3y=",
                    "Empty": ""
                  },
                  "Inventory": { "BaseUrl": "https://svc:pw@inventory.internal.example.com/api?code=XYZ" },
                  "AzureAd": { "ClientId": "x" }
                }
                """);
            var hints = DependencyHintScanner.Scan(dir, ["Microsoft.EntityFrameworkCore.SqlServer", "Azure.Identity", "Serilog"], usesUserSecrets: true);
            var serialized = System.Text.Json.JsonSerializer.Serialize(hints);

            foreach (var secret in new[] { "Sup3rS3cret", "abc123", "K3y=", "pw@", "XYZ", "User Id=app" })
            {
                Assert.DoesNotContain(secret, serialized, StringComparison.Ordinal);
            }

            Assert.Contains(hints, h => h.Name == "ConnectionStrings:Orders" && h.Kind == "sql" && h.Host == "orders-sql.database.windows.net");
            Assert.Contains(hints, h => h.Name == "ConnectionStrings:Cache" && h.Kind == "redis");
            Assert.Contains(hints, h => h.Name == "ConnectionStrings:Bus" && h.Kind == "messaging" && h.Host == "orders-bus.servicebus.windows.net");
            Assert.Contains(hints, h => h.Name == "Inventory:BaseUrl" && h.Host == "inventory.internal.example.com");
            Assert.Contains(hints, h => h.Kind == "identity");
            Assert.Contains(hints, h => h.Source == "user-secrets");
            Assert.DoesNotContain(hints, h => h.Name == "Serilog");
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }
}

public sealed class JsonRedactionTests
{
    [Fact]
    public void JsonArtifactsStayValidAndKeepNumbers()
    {
        const string json = """{"metadataToken":100681065,"name":"x","clientSecret":"abc","nested":[{"conn":"Server=a;Password=p4ss;"}],"ok":true}""";

        var redacted = SecretRedactor.RedactJson(json);

        using var doc = System.Text.Json.JsonDocument.Parse(redacted);
        Assert.Equal(100681065, doc.RootElement.GetProperty("metadataToken").GetInt64());
        Assert.Equal("***", doc.RootElement.GetProperty("clientSecret").GetString());
        Assert.DoesNotContain("p4ss", redacted, StringComparison.Ordinal);
        Assert.True(doc.RootElement.GetProperty("ok").GetBoolean());
    }

    [Fact]
    public void NonJsonFallsBackToTextRedaction() =>
        Assert.DoesNotContain("hunter2", SecretRedactor.RedactJson("password=hunter2"), StringComparison.Ordinal);
}

public sealed class DeepJsonRedactionTests
{
    [Fact]
    public void DeeplyNestedJsonFromCallTreesStaysValid()
    {
        var depth = 300;
        var json = string.Concat(Enumerable.Repeat("{\"metadataToken\":123,\"children\":[", depth)) + "{\"password\":\"x\"}" + string.Concat(Enumerable.Repeat("]}", depth));

        var redacted = SecretRedactor.RedactJson(json);

        using var doc = System.Text.Json.JsonDocument.Parse(redacted, new System.Text.Json.JsonDocumentOptions { MaxDepth = 1024 });
        Assert.Equal(123, doc.RootElement.GetProperty("metadataToken").GetInt32());
        Assert.DoesNotContain("\"x\"", redacted, StringComparison.Ordinal);
    }

    [Fact]
    public void SensitiveKeyAppliesToArrayElementsButNotToSiblingsAfterNestedObjects()
    {
        const string json = """{"secrets":[{"name":"a"},"plain-but-secret"],"label":"visible"}""";

        var redacted = SecretRedactor.RedactJson(json);

        Assert.Contains("\"name\":\"a\"", redacted, StringComparison.Ordinal);
        Assert.Contains("\"secrets\":[{\"name\":\"a\"},\"***\"]", redacted, StringComparison.Ordinal);
        Assert.Contains("\"label\":\"visible\"", redacted, StringComparison.Ordinal);
    }
}
