---
name: dotnet-performance-diagnosis
description: Procedimiento para diagnosticar el rendimiento de APIs ASP.NET Core con el laboratorio performance-agent (k6 + dotnet-diagnostics) - qué prueba ejecutar, cómo interpretar CPU, GC, ThreadPool, dependencias y errores, qué evidencia confirma cada hipótesis y cómo redactar el informe. Úsala al investigar latencia, throughput, errores bajo carga o consumo de recursos de una API .NET, o al escribir manifiestos en scenarios/.
---

# Diagnóstico de rendimiento de APIs .NET con performance-agent

## Principios

1. **Evidencia antes que conclusiones.** Un síntoma (p95 alto, CPU alta) no es una causa. Una hipótesis solo es
   `confirmed` con evidencia discriminante: una muestra de pilas que señala código de usuario, el sitio que asigna la
   memoria, o una aritmética que cuadra (llamadas × latencia de la dependencia ≈ latencia observada).
2. **De menor a mayor coste.** preflight → smoke (función) → baseline (carga + runtime en la misma ventana) →
   como máximo dos capturas de seguimiento → informe.
3. **Fidelidad explícita.** Indica siempre qué fue real, simulado (mock), no disponible o no verificado, y que k6
   comparte máquina con la API en los perfiles locales.

## Elegir la prueba

| Situación | Acción |
|---|---|
| Proyecto nuevo | `discover_api`; propone un manifiesto con endpoints GET de datos sintéticos |
| Sin OpenAPI | Declara `endpoints` en el manifiesto (con `pathParameters` sintéticos) |
| Operaciones de escritura | Excluidas por defecto. Requieren `writeOperations: allow-declared` en la política Y en el manifiesto |
| Dependencia HTTP fuera del objetivo de la medición | `mode: mock` con un contrato en `contracts/` |
| Dependencia que debe ser real | Perfil `local-hybrid`, `mode: remote`, host en `allowedRemoteDependencyHosts` |
| Smoke con fallos sistemáticos | Para y corrige escenario/datos: es un problema funcional |

## Interpretar señales (baseline)

| Síntoma observado | Hipótesis | Evidencia que la apoya | Evidencia que la contradice | Seguimiento |
|---|---|---|---|---|
| p95 sube con la concurrencia y CPU media ≥ 70% | CPU-001 trabajo de CPU en la petición | CPU alta | CPU baja | `collect_runtime_evidence kind=cpu-sample` |
| p95 sube, CPU baja, cola del ThreadPool, ajustes por starvation / bloqueo cooperativo | TP-001 esperas síncronas (sync-over-async, `Task.Wait`, `.Result`, `Thread.Sleep`) | starvation > 0, hilos creciendo, CPU < 40% | cola vacía | `cpu-sample` (las pilas muestran la espera) |
| Asignación > 256 KB/petición, gen2 frecuentes (≥1 por 1.000 peticiones), tiempo en GC ≥ 10% | GC-001 presión de asignaciones | ≥2 de esas señales | asignación por petición baja | `gc-events` (sitios de asignación) |
| Lento incluso con 1 VU; mock muestra N llamadas por petición | DEP-001 latencia de dependencia / N+1 | llamadas × latencia ≈ latencia de la API | CPU alta, poca latencia explicada | revisar el patrón de llamadas en el código |
| Errores 5xx bajo carga que coinciden con fallos del mock | ERR-002 errores propagados | recuentos que coinciden | 5xx sin fallos de la dependencia | excepciones del runtime |
| Muchas contenciones de lock | LOCK-001 contención | esperas largas frente al p95 de la API | esperas breves (p95 de pocos ms) | `contention-events` |

Trampas conocidas:

- **Esperas ociosas**: en las muestras de pilas aparecen hilos del pool esperando trabajo
  (`LowLevelLifoSemaphore.WaitForSignal`, `WaitHandle.WaitOneNoCheck`). No son bloqueos de la petición. Un bloqueo
  solo se confirma si el **camino caliente** va de un método de usuario a la espera.
- **Asignación absoluta vs. por petición**: cientos de MB/s pueden ser normales a 10.000 req/s; juzga KB por petición.
- **Las muestras de EventPipe** miden frecuencia de pilas, no tiempo de CPU del sistema operativo.
- **Counters del proveedor**: son el último intervalo de 1 s y el máximo de la ventana; la CPU media sale del
  muestreo del SO; los recuentos de GC y ajustes del ThreadPool son exactos para la ventana.
- **Mocks**: la latencia y los errores son los del contrato; no representan necesariamente a la dependencia real.
- **Ventanas**: comprueba la correlación temporal (solapamiento ≥ 80%) antes de relacionar k6 con el runtime.

## Informe

- Usa el informe de `generate_report` como fuente y cita sus artefactos (`artifacts/runs/<runId>/...`).
- Plantilla de evidencia: `[fuente] señal = valor unidad: descripción (artefacto)`.
- Formula las recomendaciones como experimentos: qué cambiar, qué métrica debería moverse y cómo validarlo con
  `compare_runs` sobre el mismo escenario.
- Si el veredicto es `inconclusive`, enumera la evidencia que falta en lugar de adivinar.

## Manifiestos (scenarios/*.yaml)

Campos principales (esquema completo en `scenarios/run-manifest.v1.schema.json`, guía en `docs/manifest.md`):

```yaml
schemaVersion: 1
name: mi-escenario
profile: local-isolated          # o local-hybrid
environment: lab                 # nunca prod/production
api:
  project: C:/Repos/my-api/src/MyApi/MyApi.csproj   # su carpeta debe estar en allowedProjectRoots
  healthPath: /health
  settings: { Feature__X: "true" }   # sin secretos ni claves reservadas (DOTNET_*, ASPNETCORE_*...)
endpoints:                         # o endpointSource: openapi + include
  - { name: products, method: GET, path: /api/products/{id}, pathParameters: { id: "42" }, expectedStatus: 200 }
dependencies:
  pricing-api: { mode: mock, contract: contracts/pricing.json, injectAs: Dependencies__Pricing__BaseUrl }
load:
  smoke: { vus: 1, iterations: 10, maxDuration: 30s }
  baseline: { executor: constant-vus, vus: 10, duration: 30s }
  thresholds: { http_req_failed: ['rate<0.01'], http_req_duration: ['p(95)<500'] }
safety:
  requireConfirmation: [baseline]  # el agente pedirá confirmación antes de la carga
```
