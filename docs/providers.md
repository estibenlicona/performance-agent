# Proveedores comunitarios: contrato de integración

Los proveedores se ejecutan como **procesos MCP secundarios** de `performance-agent`. El custom agent de Copilot **no**
los ve: solo recibe `performance-agent/*`. Cada delegación queda registrada (proveedor, versión, herramienta, argumentos
saneados, duración, éxito, fallback) en `run.json` y en el informe ("Proveedores upstream invocados").

## Versiones fijadas

| Proveedor | Versión | Licencia | Instalación | Verificación |
|---|---|---|---|---|
| [grafana/k6](https://github.com/grafana/k6) | 2.3.0 | AGPL-3.0 | `setup/bootstrap.ps1` → `.tools/k6/2.3.0/` | SHA-256 fijado en `setup/tools.json` **y** contrastado con `k6-v2.3.0-checksums.txt` |
| [grafana/mcp-k6](https://github.com/grafana/mcp-k6) | 0.7.0 | AGPL-3.0 | `setup/bootstrap.ps1` → `.tools/mcp-k6/0.7.0/` | SHA-256 fijado + `mcp-k6_0.7.0_checksums.txt` |
| [pedrosakuma/dotnet-diagnostics](https://github.com/pedrosakuma/dotnet-diagnostics) (`dotnet-diagnostics-mcp`) | 0.28.0 | MIT | `setup/bootstrap.ps1` → `.tools/dotnet-diagnostics-mcp/0.28.0/` (ejecutable autocontenido, no requiere .NET) | SHA-256 fijado + digest del asset en el release de GitHub; `serverInfo.version` = 0.28.0 al conectar |

> **Licencia AGPL-3.0 (k6, mcp-k6):** se usan sin modificar, como procesos independientes descargados de sus
> releases oficiales; no se redistribuyen en este repositorio. Revísalo con tu área legal antes de distribuir
> binarios o imágenes que los incluyan.

Al arrancar, la fachada consulta el catálogo real (`tools/list`), comprueba que existen las herramientas requeridas
y que la versión coincide con la fijada; si no, el proveedor queda **no disponible** con un error explicativo
(`get_lab_status`). Las versiones esperadas viajan en el `appsettings.json` del paquete de performance-agent; actualizar una versión =
cambiarla en `setup/tools.json` de este workspace y en `performance-agent-server` (una prueba verifica que su
`appsettings.json` y su `setup/tools.json` coinciden), repetir allí las pruebas de integración y publicar el paquete.

## grafana/mcp-k6 0.7.0

Arranque: `.tools/mcp-k6/0.7.0/mcp-k6.exe` (stdio), con `PATH` que antepone el k6 fijado (mcp-k6 exige `k6` en
`PATH`) y directorio de trabajo `artifacts/providers/mcp-k6/` (contiene `.config/k6/config.json` con
`noUsageReport: true`, que k6 lee porque mcp-k6 le pasa un entorno reducido sin `APPDATA`).

Catálogo real (verificado con `tools/list`): `info`, `validate_script`, `run_script`, `list_sections`,
`get_documentation`, `search_terraform`; recurso `docs://k6/best_practices`; prompts `generate_script`,
`convert_playwright_script`. Resultados como JSON en texto (sin `structuredContent`).

| Herramienta performance-agent | Delegación upstream | Controles previos |
|---|---|---|
| `validate_k6_scenario` | `validate_script(script)` — ejecuta 1 VU / 1 iteración | validación estática completa; destino = instancia de laboratorio |
| `run_k6_test` | `run_script(script, vus, duration \| iterations)` (ramping-vus usa las opciones del script) | smoke previo, SHA-256, destino, límites, confirmación |
| `search_k6_docs` | `list_sections`, `get_documentation` | solo lectura |
| `get_lab_status` | `info` (versión de k6 y del servidor) | — |

`search_terraform` no está en la allowlist de la fachada.

Limitaciones verificadas y cómo se tratan:

- `run_script` no admite salidas (`--out`, `--summary-export`) ni variables de entorno → el script generado
  incluye `handleSummary` que imprime el resumen JSON entre marcadores en stdout; la fachada lo extrae. Sin línea
  temporal por segundo en esta vía (sí en el fallback CLI con `--out json`).
- Tope duro de 5 minutos por ejecución → la política limita la duración a 240 s; si se superase, se usa el CLI.
- `validate_script` marca como inválido un script que cruza thresholds en 1 iteración → la fachada considera válida
  la ejecución si terminó con código 0 o 99 y produjo el resumen.

**Fallback controlado:** si mcp-k6 no está disponible (o la prueba supera su tope), `k6 run --no-usage-report --out json=…`
con el mismo script validado; si falla la validación upstream, `k6 inspect` (sin tráfico). El informe marca la
delegación como `fallback` con el motivo.

## pedrosakuma/dotnet-diagnostics 0.28.0

Arranque: `.tools/dotnet-diagnostics-mcp/0.28.0/dotnet-diagnostics-mcp.exe --stdio` en la raíz del workspace, con
`MCP_ARTIFACT_ROOT=artifacts/providers/dotnet-diagnostics`. Siempre se pasa el `processId` de la API iniciada por la
fachada (el proveedor también ve otros procesos .NET, incluida la propia fachada).

Catálogo real: 13 herramientas (`inspect_process`, `collect_events`, `collect_sample`, `collect_batch`,
`query_snapshot`, `inspect_heap`, `collect_process_dump`, `collect_thread_snapshot`, `capture_method_bytes`,
`get_bytes`, `start_investigation`, `export_investigation_summary`, `compare_to_baseline`). Resultados con
`structuredContent` (`DiagnosticResult<T>`: `summary`, `data`, `handle`, `safety`, `error`…).

**Allowlist de la fachada:** `inspect_process` (vistas `info`, `capabilities`, `preflight`, `triage`),
`collect_events` (`counters`, `gc`, `threadpool`, `contention`, `exceptions`), `collect_sample` (`cpu`,
`allocation`), `collect_batch` y `query_snapshot` (vistas `top-methods`, `hot-path`, `timeline`, `summary`…).
Si una respuesta trae `safetyApproval` (riesgo alto/crítico), la fachada **la rechaza**.

| Herramienta performance-agent | Delegación upstream |
|---|---|
| `run_k6_test` (baseline) | `inspect_process(view=info)` de calentamiento + `collect_batch[counters, gc, threadpool]` durante la carga + `query_snapshot(threadpool, timeline)` |
| `inspect_runtime` | `inspect_process(view=preflight\|triage\|info\|capabilities)` |
| `collect_runtime_evidence cpu-sample` | `collect_sample(kind=cpu, depth=detail)` + `query_snapshot(top-methods, hot-path)` |
| `collect_runtime_evidence gc-events` | `collect_batch[collect_events gc, collect_sample allocation]` |
| `collect_runtime_evidence threadpool-events \| contention-events \| exception-events` | `collect_events(kind=…)` |
| `compare_runs` | comparación propia sobre datos normalizados (`compare_to_baseline` del proveedor requiere capturas persistentes; no se usa en el MVP) |

Hallazgos verificados que condicionan el diseño:

- Los counters son **agregados** (último intervalo de 1 s y máximo de la ventana), sin marcas de tiempo, y la vista
  `resources` en Windows no incluye CPU. Por eso la fachada añade un muestreo del SO (CPU y working set cada 1 s)
  **del proceso que ella misma inició** para la serie temporal y la CPU media. No usa EventPipe ni duplica
  analizadores del proveedor (ver ADR 0004).
- Los recuentos de GC por generación y los ajustes del ThreadPool (`starvation`, `cooperative blocking`) son exactos
  para la ventana; la línea temporal de hilos tiene marcas de tiempo.
- Las muestras de CPU de EventPipe miden frecuencia de pilas (incluye hilos en espera). La fachada distingue esperas
  ociosas del pool de bloqueos en la ruta de la petición usando el camino caliente.

## Fallback de diagnóstico

`timheuer/dotnet-profiler-mcp` **no** es viable hoy: sin licencia, no publicado en nuget.org y de calidad prototipo.
Si `dotnet-diagnostics` no estuviera disponible, el diagnóstico continúa como **parcial**: el baseline conserva la CPU
y memoria del muestreo del SO y el informe lista las señales no disponibles. No se activan dos proveedores de
diagnóstico a la vez.

## Pruebas de contrato

En `performance-agent-server`, `dotnet/tests/PerformanceAgent.IntegrationTests/ProviderContractTests.cs` arranca los proveedores fijados y verifica: catálogo y
versión, parámetros y forma de las respuestas, errores, rechazo de herramientas fuera de la allowlist y que la
fachada pasa el PID del proceso de laboratorio.
