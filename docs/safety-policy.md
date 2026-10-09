# Política de seguridad del laboratorio

Entregable del **Paso 1**. Los controles se aplican en el **servidor** `perf-lab` (código en
`src/PerfAgent.Core/Policy`, `src/PerfAgent.Adapters.*` y `src/PerfAgent.Mcp/Lab`). Las instrucciones del agente
los describen, pero **no son un control de seguridad**: aunque el modelo intentara saltárselos, las herramientas
rechazan la operación y explican por qué (`status: blocked`, `violations[]` con código estable).

## Fuente de los límites

`perf-lab.policy.json` (versionado, en la raíz). El manifiesto solo puede **restringir**, nunca ampliar. Si el
archivo falta se aplican valores por defecto equivalentes y restrictivos. Campos desconocidos → error.

## Controles

| Riesgo | Control técnico | Dónde |
|---|---|---|
| Carga contra producción o destinos arbitrarios | La URL base la fija el laboratorio (instancia propia en `127.0.0.1:<puerto libre>`); el script generado incluye `blacklistIPs` (todo IPv4 salvo 127.0.0.0/8 y todo IPv6) y `blockHostnames: ['*']`, verificado en k6 2.3.0; la validación estática rechaza cualquier URL distinta | `K6ScriptGenerator`, `K6ScriptValidator`, `PolicyEngine.EvaluateLoadTarget` |
| Ambientes de producción | Etiquetas `prod/production/prd/live` (y variantes) rechazadas en `environment`, `aspNetCoreEnvironment` y en hosts de dependencias remotas | `PolicyEngine` |
| Scripts arbitrarios del modelo | Solo se ejecutan scripts **generados** por perf-lab, fijados por SHA-256; cualquier modificación se detecta (`k6.tampered`). Imports limitados a `k6`, `k6/http`, `k6/metrics`; se rechazan `open(`, `require(`, `eval(`, extensiones `k6/x/`, etc. | `K6ScriptValidator` |
| Inyección en el script | Los datos del manifiesto se embeben como literales JSON escapados; thresholds validados con una gramática estricta | `K6ScriptGenerator.ToJs`, `PolicyEngine.IsValidThreshold` |
| Exceso de carga | VUs, duración, iteraciones y nº de endpoints validados al validar el manifiesto **y** otra vez justo antes de ejecutar | `PolicyEngine.EvaluateLoad` |
| Saltarse pasos | El baseline exige un smoke funcionalmente correcto; la carga exige laboratorio activo, manifiesto sin cambios (SHA-256) y escenario validado | `LabService` |
| Validación upstream que genera tráfico | `validate_script` de mcp-k6 ejecuta 1 VU/1 iteración: solo se invoca tras verificar que el destino es la instancia de laboratorio | `LabService.ValidateCoreAsync` |
| Escrituras | `GET`/`HEAD` por defecto; `POST/PUT/PATCH/DELETE` requieren `writeOperations: allow-declared` en la política **y** en el manifiesto, con cuerpo sintético | `PolicyEngine`, `EndpointPlanner` |
| Comandos arbitrarios | Catálogo cerrado de comandos (`AllowedCommands`); sin shell; ejecutables permitidos resueltos solo en `PATH` (nunca en el directorio del proyecto) | `Execution/Commands.cs`, `ExecutableResolver` |
| Inyección en el proceso de la API | Se eliminan del entorno heredado `DOTNET_STARTUP_HOOKS`, `ASPNETCORE_HOSTINGSTARTUPASSEMBLIES`, perfiladores (`CORECLR_*`, `COR_*`), `ASPNETCORE_URLS`, `K6_*`…; el manifiesto no puede fijar claves reservadas (`DOTNET_*`, `ASPNETCORE_*`, `Kestrel__*`, proxies, `PATH`) | `ProcessEnvironment`, `PolicyEngine.ValidateSetting` |
| Diagnóstico de otro proceso | El modelo nunca aporta un PID: la fachada usa el PID de la API que ella misma inició | `LabService` |
| Capturas sensibles | Allowlist de herramientas del proveedor (`inspect_process`, `collect_events`, `collect_sample`, `collect_batch`, `query_snapshot`) y de vistas/tipos; sin dumps, heap, bytes, snapshots con suspensión ni parámetros; si el proveedor pide aprobación de riesgo alto, la fachada **no** la concede | `DiagnosticsCatalog`, `DotNetDiagnosticsAdapter` |
| Secretos | Nunca en el manifiesto (claves sensibles y valores con forma de secreto rechazados); salida de procesos, logs y artefactos saneados (`SecretRedactor`, también JSON-aware); el descubrimiento solo devuelve **nombres** de claves y hosts | `SecretRedactor`, `DependencyHintScanner` |
| Textos del proceso objetivo | Se tratan como datos (saneados y acotados) y el agente tiene instrucción de no seguirlos | `RuntimeEvidenceNormalizer`, agente |
| Rutas | Proyectos, manifiestos y contratos solo dentro de raíces permitidas; se rechazan `..` y enlaces que escapen | `Workspace.TryResolve` |
| Procesos huérfanos | En Windows el servidor se asigna a un Job Object `KILL_ON_JOB_CLOSE`: todo lo que lanza muere con él. Además, registro persistente de PIDs (con hora de inicio) y limpieza al arrancar, en `stop_lab` y al apagar | `WindowsJobObject`, `ProcessRegistry` |
| Telemetría de terceros | k6 vía mcp-k6 se ejecuta con un `config.json` propio (`noUsageReport: true`); el CLI usa `--no-usage-report` | `LabServiceFactory`, `AllowedCommands.K6Run` |

## Confirmaciones

`safety.requireConfirmation: [smoke | baseline | runtime-capture]` en el manifiesto hace que la herramienta devuelva
`confirmation-required` con el plan exacto (destino, VUs, duración, endpoints) hasta recibir `confirm: true`. Además,
VS Code pide aprobación para las herramientas MCP que no son de solo lectura.

## Ampliar la política

1. Añadir la ruta del proyecto a `allowedProjectRoots` (relativa o absoluta).
2. Para dependencias reales: perfil `local-hybrid` y el host en `allowedRemoteDependencyHosts`.
3. Para otro framework: añadirlo a `supportedTargetFrameworks` **después** de probarlo y registrarlo en
   `docs/tested-matrix.md`.

Cualquier cambio de la política debe revisarse como código (PR) y no puede hacerse desde las herramientas del agente.
