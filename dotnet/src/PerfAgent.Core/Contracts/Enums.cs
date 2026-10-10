namespace PerfAgent.Core.Contracts;

public enum EnvironmentProfileKind
{
    LocalIsolated,
    LocalHybrid,
}

public enum DependencyMode
{
    Container,
    Mock,
    Remote,
    Unavailable,
}

/// <summary>Matriz de fidelidad por dependencia: real, simulada, no disponible o no verificada.</summary>
public enum FidelityStatus
{
    Real,
    Simulated,
    Unavailable,
    NotVerified,
}

public enum PreflightDecision
{
    Continue,
    ContinueDegraded,
    Blocked,
}

public enum CheckOutcome
{
    Passed,
    Failed,
    Skipped,
}

public enum LoadTestKind
{
    Smoke,
    Baseline,
}

public enum LoadTestStatus
{
    Passed,
    ThresholdsFailed,
    FunctionalFailure,
    NetworkError,
    GeneratorError,
    Aborted,
    Blocked,
}

public enum HypothesisStatus
{
    New,
    Supported,
    Contradicted,
    Confirmed,
    Unresolved,
}

/// <summary>Nivel de confianza cualitativo; nunca una probabilidad inventada.</summary>
public enum Confidence
{
    Low,
    Medium,
    High,
}

public enum RunPhase
{
    Created,
    PreflightBlocked,
    PreflightPassed,
    Built,
    Ready,
    SmokeFailed,
    SmokePassed,
    BaselineCompleted,
    Analyzed,
    Stopped,
    Failed,
}

public enum DiagnosisVerdict
{
    NotEvaluated,
    CauseConfirmed,
    SymptomsObserved,
    Inconclusive,
    NoDegradationDetected,
    EnvironmentFailure,
    FunctionalFailure,
}

/// <summary>Clasificación de fallos de ambiente: ninguno se presenta como problema de rendimiento.</summary>
public enum FailureKind
{
    None,
    PolicyViolation,
    BuildFailed,
    ProcessExited,
    PortInUse,
    ConfigurationError,
    AuthenticationError,
    DependencyUnavailable,
    ReadinessTimeout,
    ToolUnavailable,
    Cancelled,
    Unknown,
}
