# perf-lab — agente de diagnóstico de rendimiento para APIs .NET

Laboratorio controlado para que **GitHub Copilot en VS Code** (agente `Performance Diagnostics`) diagnostique el
rendimiento de APIs ASP.NET Core: descubre la API, la compila y arranca en local, ejecuta pruebas **k6** limitadas,
recoge evidencia del **runtime .NET** en la misma ventana, formula hipótesis con evidencia a favor y en contra, ejecuta
la investigación de seguimiento más barata que las discrimine y entrega un informe reproducible.

Implementa la **Fase 1 (MVP)** de [`docs/plan.md`](docs/plan.md).
Estado de la puerta MVP → Fase 2: [`docs/mvp-gate.md`](docs/mvp-gate.md).

## Cómo funciona

- El agente solo ve las herramientas **`perf-lab/*`** de un servidor MCP propio (`dotnet/src/PerfAgent.Mcp`). Ese servidor
  aplica la política de seguridad (`perf-lab.policy.json`) y delega en proveedores comunitarios fijados por versión,
  que el agente no puede invocar directamente:
  - [`grafana/mcp-k6`](https://github.com/grafana/mcp-k6) 0.7.0 + k6 2.3.0 — validación y ejecución de escenarios.
  - [`pedrosakuma/dotnet-diagnostics`](https://github.com/pedrosakuma/dotnet-diagnostics) 0.28.0 — counters, GC,
    ThreadPool, muestras de CPU y asignaciones del proceso de la API.
- La carga solo puede ir a la instancia local que inicia el laboratorio (127.0.0.1): k6 bloquea cualquier otra IP u
  hostname. Sin escrituras, dumps ni secretos por defecto. Detalle: [`docs/safety-policy.md`](docs/safety-policy.md).
- Separación estricta entre **observaciones**, **hipótesis** (`new`/`supported`/`contradicted`/`confirmed`/
  `unresolved`) y **causas confirmadas** con evidencia discriminante. Reglas: [`docs/triage-rules.md`](docs/triage-rules.md).

## Requisitos

- Windows x64 (única plataforma probada; ver [`docs/tested-matrix.md`](docs/tested-matrix.md)).
- .NET SDK 10.0.1xx o superior.
- VS Code con GitHub Copilot (Agent Mode).

## Puesta en marcha

```powershell
# 1. Herramientas fijadas (k6, mcp-k6 con verificación SHA-256; dotnet-diagnostics-mcp como herramienta local)
powershell -ExecutionPolicy Bypass -File eng/bootstrap.ps1

# 2. Compilar y probar
dotnet build dotnet/PerfAgent.slnx
dotnet test dotnet/tests/PerfAgent.UnitTests
dotnet test dotnet/tests/PerfAgent.IntegrationTests      # proveedores reales y escenarios E2E (≈7 min)
```

3. Abre la carpeta en VS Code. El servidor `perf-lab` está declarado en [`.mcp.json`](.mcp.json); confía en el
   workspace e inicia el servidor cuando VS Code lo proponga.
4. En Copilot Chat elige el agente **Performance Diagnostics** y pide, por ejemplo:
   *"Diagnostica scenarios/lab-cpu.yaml"* o usa el prompt `/diagnose-api-performance`.

Sin Copilot (utilidad de pruebas):

```powershell
dotnet run --project dotnet/src/PerfAgent.Mcp -- status
dotnet run --project dotnet/src/PerfAgent.Mcp -- diagnose scenarios/lab-blocking.yaml
dotnet run --project dotnet/src/PerfAgent.Mcp -- report <runId>      # recalcula el informe desde los artefactos
```

Los resultados quedan en `artifacts/runs/<runId>/` (`report.md`, `report.json`, `run.json`, scripts k6, respuestas
saneadas de los proveedores y logs).

## Diagnosticar tu propia API

1. Añade la ruta del proyecto a `allowedProjectRoots` en `perf-lab.policy.json`.
2. Crea un manifiesto en `scenarios/` (guía: [`docs/manifest.md`](docs/manifest.md); esquema:
   `scenarios/run-manifest.v1.schema.json`). Declara endpoints con datos sintéticos si no hay OpenAPI y cada dependencia
   con su modo (`mock`, `container`, `remote`).
3. Pide al agente que lo valide y ejecute. Los secretos se configuran fuera del repositorio (user-secrets/entorno).

## Herramientas `perf-lab/*`

| Herramienta | Uso |
|---|---|
| `get_lab_status` | política vigente, proveedores y versiones |
| `discover_api` | proyecto, frameworks, runtime, OpenAPI, dependencias (solo nombres) |
| `validate_manifest`, `preflight_environment` | alcance y límites; fidelidad por dependencia (DNS/TCP/TLS/salud) |
| `start_lab`, `stop_lab` | compilar en Release, iniciar API y dependencias, readiness; limpieza garantizada |
| `generate_k6_scenario`, `validate_k6_scenario`, `run_k6_test` | smoke y baseline con k6 (vía mcp-k6) |
| `inspect_runtime`, `collect_runtime_evidence` | triage del proveedor; CPU, GC/asignaciones, ThreadPool, contención, excepciones |
| `analyze_run`, `generate_report`, `compare_runs`, `list_runs` | hipótesis, informe y comparación antes/después |
| `search_k6_docs` | documentación de k6 (solo lectura) |
| `run_diagnosis` | todo el flujo con una sola orden |

## Estructura

```text
.github/                  agente, instrucciones, skill y prompt de Copilot
.mcp.json                 servidor MCP del workspace (solo perf-lab)
perf-lab.policy.json      límites de seguridad
eng/                      bootstrap y versiones fijadas (tools.json)
dotnet/                   todo el código .NET: PerfAgent.slnx, Directory.Build.props, Directory.Packages.props
  src/PerfAgent.*         Core, Mcp (fachada), Adapters (Process, K6Mcp, DotNetDiagnosticsMcp), Reporting, MockServer
  lab/                    API de laboratorio con fallos conocidos, contratos de mocks, docker-compose (no probado)
  tests/                  pruebas unitarias e integración
scenarios/                manifiestos de ejemplo y su JSON Schema
docs/                     plan, alcance, política, arquitectura, proveedores, reglas, ADRs, puerta MVP
```

## Licencias de terceros

k6 y mcp-k6 son AGPL-3.0; dotnet-diagnostics es MIT. Se descargan de sus releases oficiales y se ejecutan sin
modificar como procesos independientes (ver [`docs/providers.md`](docs/providers.md)).
