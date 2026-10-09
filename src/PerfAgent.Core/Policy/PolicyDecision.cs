namespace PerfAgent.Core.Policy;

/// <param name="Code">Código estable (p. ej. <c>load.vus-exceeded</c>) para pruebas y trazabilidad.</param>
/// <param name="Field">Campo del manifiesto o parámetro afectado.</param>
/// <param name="Message">Explicación legible de por qué se rechaza, antes de iniciar la carga.</param>
public sealed record PolicyViolation(string Code, string Field, string Message);

public sealed record PolicyDecision(IReadOnlyList<PolicyViolation> Violations, IReadOnlyList<string> Warnings)
{
    public bool Allowed => Violations.Count == 0;

    public static PolicyDecision Allow(params string[] warnings) => new([], warnings);

    public static PolicyDecision Deny(string code, string field, string message) =>
        new([new PolicyViolation(code, field, message)], []);

    public string Explain() => Allowed
        ? "Permitido por la política."
        : "Rechazado por la política: " + string.Join(" | ", Violations.Select(v => $"[{v.Code}] {v.Field}: {v.Message}"));
}

/// <summary>Acumulador de violaciones y avisos.</summary>
public sealed class PolicyDecisionBuilder
{
    private readonly List<PolicyViolation> _violations = [];
    private readonly List<string> _warnings = [];

    public bool HasViolations => _violations.Count > 0;

    public PolicyDecisionBuilder Deny(string code, string field, string message)
    {
        _violations.Add(new PolicyViolation(code, field, message));
        return this;
    }

    public PolicyDecisionBuilder Warn(string message)
    {
        _warnings.Add(message);
        return this;
    }

    public PolicyDecisionBuilder Merge(PolicyDecision decision)
    {
        _violations.AddRange(decision.Violations);
        _warnings.AddRange(decision.Warnings);
        return this;
    }

    public PolicyDecision Build() => new(_violations.ToList(), _warnings.ToList());
}
