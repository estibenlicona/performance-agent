# performance-agent — agente de diagnóstico de rendimiento para APIs .NET

Workspace para que **GitHub Copilot en VS Code** (agente `Performance Diagnostics`) diagnostique y ayude a mejorar el
rendimiento de **cualquier API ASP.NET Core**: descubre la API, la compila y arranca en un laboratorio local aislado,
ejecuta pruebas **k6** limitadas, recoge evidencia del **runtime .NET** en la misma ventana, formula hipótesis con
evidencia a favor y en contra, ejecuta la investigación de seguimiento más barata que las discrimine y entrega un
informe reproducible.

No necesitas un entorno de desarrollo para la herramienta: el servidor y los proveedores son ejecutables autocontenidos
que `setup/bootstrap.ps1` instala en `.tools/`. Solo hace falta el SDK con el que se compila **tu** API.
El código fuente del servidor está en el repositorio `performance-agent-server`.

## Cómo funciona

- El agente solo ve las herramientas **`performance-agent/*`** de un servidor MCP propio
  (`.tools/performance-agent/<versión>/performance-agent.exe`). Ese servidor aplica la política de seguridad
  (`safety-policy.json`) y delega en proveedores comunitarios fijados por versión, que el agente no puede invocar
  directamente:
  - [`grafana/mcp-k6`](https://github.com/grafana/mcp-k6) 0.7.0 + k6 2.3.0 — validación y ejecución de escenarios.
  - [`pedrosakuma/dotnet-diagnostics`](https://github.com/pedrosakuma/dotnet-diagnostics) 0.28.0 — counters, GC,
    ThreadPool, muestras de CPU y asignaciones del proceso de la API.
- La carga solo puede ir a la instancia local que inicia el laboratorio (127.0.0.1): k6 bloquea cualquier otra IP u
  hostname. Sin escrituras, dumps ni secretos por defecto. Detalle: [`docs/safety-policy.md`](docs/safety-policy.md).
- Separación estricta entre **observaciones**, **hipótesis** (`new`/`supported`/`contradicted`/`confirmed`/
  `unresolved`) y **causas confirmadas** con evidencia discriminante. Reglas: [`docs/triage-rules.md`](docs/triage-rules.md).

## Requisitos

- Windows x64 (única plataforma probada; ver [`docs/tested-matrix.md`](docs/tested-matrix.md)).
- VS Code con GitHub Copilot (Agent Mode).
- El SDK de .NET que necesite tu API para compilarse. Hoy la política solo admite APIs `net10.0`, la única versión
  probada; otras se habilitarán en `supportedTargetFrameworks` cuando se validen.

## Puesta en marcha

```powershell
# Instala el servidor y los proveedores fijados (verificación SHA-256) y comprueba la configuración del workspace
powershell -ExecutionPolicy Bypass -File setup/bootstrap.ps1
```

Mientras no haya releases publicados, el paquete del servidor se toma de
`../performance-agent-server/artifacts/release/` (se genera allí con `setup/publish.ps1`).

## Diagnosticar tu API

1. Añade la carpeta de tu API a `allowedProjectRoots` en `safety-policy.json` (ruta absoluta o relativa).
2. Copia `scenarios/my-api.example.yaml` (p. ej. a `scenarios/my-api.yaml`) y cambia `api.project`. Guía:
   [`docs/manifest.md`](docs/manifest.md). Declara endpoints con datos sintéticos si no hay OpenAPI, y cada dependencia
   con su modo (`mock` con un contrato en `contracts/`, `container` o `remote`).
3. Abre esta carpeta en VS Code; el servidor `performance-agent` está declarado en [`.mcp.json`](.mcp.json). Confía en
   el workspace e inicia el servidor cuando VS Code lo proponga.
4. En Copilot Chat elige el agente **Performance Diagnostics** y pide *"Diagnostica scenarios/my-api.yaml"*, o usa el
   prompt `/diagnose-api-performance`. Los secretos se configuran fuera del repositorio (user-secrets/entorno).

Sin Copilot (utilidad de pruebas):

```powershell
.tools/performance-agent/0.1.0/performance-agent.exe status
.tools/performance-agent/0.1.0/performance-agent.exe diagnose scenarios/my-api.yaml
.tools/performance-agent/0.1.0/performance-agent.exe report <runId>      # recalcula el informe desde los artefactos
```

Los resultados quedan en `artifacts/runs/<runId>/` (`report.md`, `report.json`, `run.json`, scripts k6, respuestas
saneadas de los proveedores y logs).

## Herramientas `performance-agent/*`

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
safety-policy.json        límites de seguridad y carpetas permitidas (aquí se añade tu API)
.mcp.json                 servidor MCP del workspace (solo performance-agent)
.github/                  agente, instrucciones, skill y prompt de Copilot
scenarios/                tus manifiestos, la plantilla my-api.example.yaml y el JSON Schema
setup/                    bootstrap.ps1 (instalación), verify.ps1 (comprobación), tools.json (versiones fijadas)
docs/                     manifiestos, política, contratos de mocks, reglas de triage, proveedores, alcance, matriz probada
.tools/, artifacts/       instalados y generados (no se versionan)
```

## Licencias de terceros

k6 y mcp-k6 son AGPL-3.0; dotnet-diagnostics es MIT. Se descargan de sus releases oficiales y se ejecutan sin
modificar como procesos independientes (ver [`docs/providers.md`](docs/providers.md)).
