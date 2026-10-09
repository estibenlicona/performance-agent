# Arquitectura

```text
 VS Code + GitHub Copilot (Agent Mode)
 └─ custom agent "Performance Diagnostics"  (.github/agents/performance-diagnostics.agent.md)
     tools: perf-lab/*, read, search          skill: dotnet-performance-diagnosis
          │  MCP (stdio, .mcp.json)
          ▼
 ┌──────────────────────── PerfAgent.Mcp  ("perf-lab", fachada) ────────────────────────┐
 │ PerfLabTools (17 herramientas tipadas, structuredContent)                             │
 │ LabService ── política ─▶ PolicyEngine / Workspace (perf-lab.policy.json)             │
 │    │                      SecretRedactor, AllowedCommands (catálogo cerrado)          │
 │    ├─ ProjectDiscovery, EndpointPlanner (OpenAPI / declarados)                        │
 │    ├─ Adapters.Process: build Release, ApiLauncher, readiness, DependencyPreflight,   │
 │    │                    DependencyLauncher (mocks/contenedores), Job Object, registro │
 │    ├─ Adapters.K6Mcp: K6ScriptGenerator, K6ScriptValidator, K6Executor ──────────┐    │
 │    ├─ Adapters.DotNetDiagnosticsMcp: DotNetDiagnosticsAdapter, Normalizer ──────┐│    │
 │    ├─ TriageEngine (reglas) ─▶ Reporting (Markdown/JSON, RunComparer)           ││    │
 │    └─ RunStore (artifacts/runs/<runId>/run.json)                                ││    │
 └──────────────────────────────────────────────────────────────────────────────── ││ ───┘
            MCP stdio (clientes internos, allowlist)                                ││
              ┌─────────────────────────────────────────────────────────────────────┘│
              ▼                                                                      ▼
   pedrosakuma/dotnet-diagnostics 0.28.0                          grafana/mcp-k6 0.7.0 ─▶ k6 2.3.0
   (EventPipe sobre el PID de la API)                             (scripts generados; solo 127.0.0.1)
              │                                                                      │
              └──────────────▶  API de laboratorio (dotnet App.dll, 127.0.0.1:<puerto>) ◀┘
                                 └─▶ mocks declarados (PerfAgent.MockServer) / contenedores
```

## Responsabilidades

| Componente | Responsabilidad |
|---|---|
| Custom agent (Copilot) | Conversación, planificación del flujo, interpretación del código, redacción. No ejecuta nada fuera de `perf-lab/*`. |
| `PerfAgent.Mcp` / `LabService` | Fachada y frontera de seguridad: valida, orquesta, registra y limpia. Máquina de estados por ejecución. |
| `PolicyEngine` | Política: alcance, límites, destinos, métodos, secretos, diagnósticos permitidos. |
| `Adapters.Process` | Procesos controlados (sin shell), readiness, clasificación de fallos de arranque, mocks, contenedores, huérfanos. |
| `Adapters.K6Mcp` | Generación y validación de escenarios; ejecución vía mcp-k6 con fallback CLI; normalización a `LoadTestRun`. |
| `Adapters.DotNetDiagnosticsMcp` | Evidencia del runtime vía dotnet-diagnostics; normalización a `RuntimeSnapshot`. |
| `TriageEngine` | Observaciones, hipótesis (con evidencia a favor/en contra/faltante), siguiente acción y veredicto. |
| `Reporting` | Informe reproducible y comparación de ejecuciones. |
| `MockServer` | Simulador HTTP declarativo y medible (`/__mock/stats`). |

## Flujo y máquina de estados

`created → preflight-passed | preflight-blocked → ready (start_lab) → smoke-passed | smoke-failed →
baseline-completed → analyzed → stopped` (o `failed` ante fallos de ambiente). Las herramientas comprueban la fase:
no hay carga sin laboratorio activo, ni baseline sin smoke correcto, ni ejecución si el manifiesto cambió desde el
preflight (SHA-256).

## Ventana de medición del baseline

1. Calentamiento del proveedor de diagnóstico (`inspect_process view=info`) para que su conexión no retrase la captura.
2. Arranca `collect_batch[counters, gc, threadpool]` con la duración de la carga; 1 s después, k6 (`run_script`).
3. En paralelo, muestreo del SO del PID de la API (CPU, working set) cada segundo.
4. Estadísticas de los mocks antes y después → llamadas por petición y latencia servida.
5. Correlación temporal (`TimeCorrelation`): solapamiento de las ventanas; < 80% se informa como limitación.

## Artefactos por ejecución (`artifacts/runs/<runId>/`)

| Ruta | Contenido |
|---|---|
| `run.json` | estado completo (contratos, eventos, invocaciones de proveedores) |
| `manifest.yaml` | copia del manifiesto usado |
| `report.md`, `report.json` | informe |
| `k6/*.js` | scripts generados (SHA-256 en `run.json`) |
| `k6/*.summary.json`, `k6/*.output.txt` | resumen de k6 y salida saneada |
| `runtime/*.json` | respuestas saneadas de dotnet-diagnostics y snapshots normalizados |
| `logs/` | build, stdout/stderr saneados de la API y los mocks |
| `openapi.json` | documento OpenAPI leído de la instancia |
| `comparisons/` | comparaciones con otras ejecuciones |

## Extensibilidad (fase 2)

Los contratos (`ApiTarget`, `EnvironmentProfile`, `DependencyCheck`, `LoadTestRun`, `RuntimeSnapshot`, `Hypothesis`,
`DiagnosticReport`) son independientes de los proveedores: otro generador de carga u otro proveedor de diagnóstico
se integra como un adaptador más detrás de las mismas herramientas `perf-lab/*`.
