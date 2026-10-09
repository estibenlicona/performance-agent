# Puerta MVP → Fase 2: estado

Criterios de la sección 10 del plan, con la evidencia que los respalda. Verificado el 2026-10-09 en la matriz de
[`tested-matrix.md`](tested-matrix.md).

| # | Criterio | Estado | Evidencia |
|---|---|---|---|
| 1 | El agente aparece y puede seleccionarse en GitHub Copilot Chat de VS Code | ⏳ **Verificación manual pendiente** | `.github/agents/performance-diagnostics.agent.md` (formato validado por `RepositoryConsistencyTests`). Pasos abajo. |
| 2 | Agente, instrucciones y skill versionados y cargados en un chat nuevo | ⏳ **Verificación manual pendiente** (versionado: ✅) | `.github/copilot-instructions.md`, `.github/skills/dotnet-performance-diagnosis/SKILL.md`, `.github/prompts/…` |
| 3 | Copilot invoca solo `perf-lab/*`; la fachada delega en mcp-k6 y dotnet-diagnostics | ✅ | `.mcp.json` declara solo `perf-lab`; el agente solo tiene `perf-lab/*` + `read`/`search`. `McpFacadeTests` (ninguna herramienta upstream expuesta); `LabEndToEndTests` verifica `run_script`/`validate_script` de grafana/mcp-k6 y `collect_batch` de dotnet-diagnostics con el PID del laboratorio en cada ejecución |
| 4 | Versiones fijadas y contratos MCP con pruebas de integración | ✅ | `eng/tools.json` (SHA-256), `.config/dotnet-tools.json`, `appsettings.json`; `ProviderContractTests`, `RepositoryConsistencyTests.PinnedVersionsAgreeAcrossFiles` |
| 5 | Catálogo del agente limitado al flujo; control en el servidor | ✅ | 17 herramientas tipadas sin parámetros de PID/URL/comando; allowlists de proveedores; `docs/safety-policy.md` |
| 6 | Build y arranque automatizados y controlados | ✅ | `start_lab` (Release, puerto libre, readiness, clasificación de fallos); `WrongHealthPathIsAConfigurationFailureAndLeavesNoProcesses` |
| 7 | Smoke y carga limitada con k6 | ✅ | `run_k6_test`; todos los escenarios E2E |
| 8 | Resultados de k6 correlacionados temporalmente con el runtime del proveedor | ✅ | `TimeCorrelation` ≥ 80% verificada en cada escenario E2E (captura iniciada antes que k6 tras calentar el proveedor) |
| 9 | La ejecución de k6 no puede saltarse destino, límites ni confirmaciones | ✅ | Scripts generados y fijados por SHA-256, `blacklistIPs`/`blockHostnames`, validación estática antes de delegar; `K6ScenarioTests`, `LoadFollowsTheCostProgressionAndRejectsTamperedScripts`, `ProfileConfirmationIsRequiredBeforeLoad` |
| 10 | La API de laboratorio produce síntomas conocidos y las pruebas los reproducen | ✅ | `LabEndToEndTests` (CPU, bloqueo, asignaciones, N+1, errores de dependencia, control sano); `docs/lab-scenarios.md` |
| 11 | El informe identifica dependencias reales o simuladas | ✅ | Matriz de fidelidad en `report.md`; aserción en los escenarios con dependencias |
| 12 | El agente diferencia error de preflight, síntoma e hipótesis | ✅ | Compuertas del triage (`environment-failure`, `functional-failure`); `TriageEngineTests`; `UnreachableRequiredDependencyBlocksTheDiagnosisWithoutLoad` |
| 13 | Ambiente limpio tras éxito, fallo y cancelación | ✅ | Job Object + registro de procesos; `CancellationDuringTheDiagnosisCleansUpEverything` (incluye k6 lanzado por mcp-k6), fallos de arranque y todos los E2E |
| 14 | Límites de carga, destinos autorizados y saneamiento de secretos probados | ✅ | `PolicyEngineTests`, `OutOfScopeRequestsAreRejectedBeforeAnyProcessStarts`, `SecretRedactorTests`, `JsonRedactionTests` |
| 15 | Una segunda ejecución comparable produce un informe contrastable | ✅ | `FixedVersionIsComparableAndMeasurablyBetter` (`lab-cpu` vs `lab-cpu-fixed`), `RunComparer` rechaza comparaciones no comparables |

## Verificación manual pendiente (criterios 1 y 2)

1. Ejecuta `eng/bootstrap.ps1` y `dotnet build PerfAgent.slnx -c Release`.
2. Abre la carpeta en VS Code (1.140+), confía en el workspace y arranca el servidor `perf-lab` (vista MCP o al
   primer uso). Debe listar 17 herramientas.
3. En Copilot Chat abre un chat nuevo, despliega el selector de agentes y elige **Performance Diagnostics**.
4. Pide: *"Comprueba el estado del laboratorio y diagnostica scenarios/lab-cpu.yaml"*. Verifica que solo invoca
   herramientas `perf-lab/*`, que te pide confirmación antes de la carga y que el resumen final separa observaciones,
   hipótesis y limitaciones.
5. Escribe `/dotnet-performance-diagnosis` o pregunta por la skill para comprobar que se carga.

## Hallazgos de la validación con proveedores reales

Problemas que solo aparecieron al ejecutar contra los proveedores y la API de laboratorio, y cómo quedaron resueltos:

| Hallazgo | Corrección | Cobertura |
|---|---|---|
| La captura del runtime empezaba ~19 s tarde (5% de solapamiento): el proveedor de diagnóstico se conectaba en su primer uso, ya bajo carga | Calentamiento (`inspect_process view=info`) antes de la carga; la captura arranca 1 s antes que k6 | E2E: solapamiento ≥ 80% |
| Las esperas ociosas del ThreadPool (`LowLevelLifoSemaphore`) se tomaban por bloqueo y anulaban la confirmación de CPU | El bloqueo solo cuenta si el camino caliente va de código de usuario a la espera | `TriageEngineTests` (regresiones) |
| Falso positivo de GC en el control sano (177 MB/s a ~10.000 req/s) | Umbrales relativos a la carga: KB por petición y GCs gen2 por cada 1.000 peticiones | `NormalAllocationAtHighThroughputIsNotGcPressure`, E2E `local-baseline` |
| El fallo de asignaciones no degradaba la latencia con 8 VUs y el triage concluía "sin degradación" | La presión de recursos (GC, CPU, starvation) es un síntoma de capacidad aunque la latencia aún no se degrade | E2E `lab-alloc` |
| El saneamiento de secretos corrompía artefactos JSON (`"metadataToken": ***`) y fallaba con árboles de llamadas de > 64 niveles | Saneamiento JSON en streaming que solo toca valores de texto | `JsonRedactionTests`, `DeepJsonRedactionTests` |
| Tras cancelar un diagnóstico, k6 (lanzado por mcp-k6) seguía en ejecución | Al cancelar o vencer una llamada, la fachada recicla el cliente del proveedor y termina su árbol de procesos | `CancellationDuringTheDiagnosisCleansUpEverything` |
| Opciones JSON sin `TypeInfoResolver` impedían arrancar el servidor MCP; parámetros opcionales sin valor por defecto se publicaban como obligatorios | Corregidos | `McpFacadeTests` |
| Un fallo intermitente de la dependencia (5%) en un smoke de 3 iteraciones bloqueaba la carga | El smoke solo falla por errores sistemáticos (≥ 50% por endpoint); los intermitentes se cuantifican en el baseline | E2E `lab-dependency-errors` |

## Riesgos y deuda conocida antes de la Fase 2

- Solo Windows x64 y `net10.0` probados; contenedores sin probar (sin Docker en la máquina de referencia).
- k6 comparte máquina con la API: las cifras absolutas no representan capacidad productiva.
- Sin línea temporal por segundo de k6 en la vía mcp-k6 (limitación de `run_script`); hay series del runtime.
- Las heurísticas del triage están calibradas con el laboratorio; requieren evaluación sistemática (fase 2, paso 10)
  antes de usarse sobre APIs reales con más autonomía.
