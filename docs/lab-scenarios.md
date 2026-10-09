# API de laboratorio y escenarios de fallos conocidos

`lab/PerfLab.Api` es una API mínima con datos sintéticos. Cada fallo se activa **individualmente** por configuración
(`Lab__Faults__<Fallo>=true` en `api.settings` del manifiesto) y está acotado (iteraciones, bytes y esperas máximas;
timeout de petición de 10 s) para que ninguna prueba cuelgue ni consuma recursos sin límite.

| Endpoint | Fallo (`Lab:Faults:*`) | Comportamiento con el fallo | Sin el fallo |
|---|---|---|---|
| `GET /api/baseline` | — | respuesta pequeña de referencia | — |
| `GET /api/products/{id}` | — | producto sintético (404 fuera de 1..1000) | — |
| `GET /api/cpu/report` | `Cpu` | criba de primos ingenua en cada petición (`CpuReportService.CountPrimesNaive`) | resultado calculado una vez |
| `GET /api/alloc/report` | `Allocations` | 16 buffers de 128 KB (LOH) + concatenación cuadrática de 600 fragmentos | `ArrayPool` + `StringBuilder` |
| `GET /api/blocking/report` | `Blocking` | `Task.Delay(100).Wait()` (sync-over-async) en un hilo del pool | `await Task.Delay(100)` |
| `GET /api/dependency/quote` | `DependencyNPlusOne` | 5 llamadas secuenciales a la dependencia de precios | 1 llamada por lotes |
| `POST /api/orders` | — | escritura sintética acotada (la política la excluye por defecto) | — |
| `GET /health`, `GET /openapi/v1.json` | — | readiness y OpenAPI | — |

La dependencia de precios es un **mock declarado** (`lab/contracts/pricing.json`, 30–50 ms; variante con 5% de 503
en `pricing-flaky.json`). Sin URL configurada, la API usa una simulación en proceso.

## Resultados esperados y verificados

Ejecutados con `dotnet run --project src/PerfAgent.Mcp -- diagnose <manifiesto>` (y en
`tests/PerfAgent.IntegrationTests/LabEndToEndTests.cs`) en Windows 11 x64, 4 núcleos, .NET 10.0.12, k6 2.3.0 vía
grafana/mcp-k6 0.7.0, dotnet-diagnostics 0.28.0. Los valores varían entre ejecuciones; lo que se verifica es el
veredicto y la evidencia.

| Manifiesto | Síntoma observado (ejemplo de una ejecución) | Hipótesis confirmada | Evidencia discriminante |
|---|---|---|---|
| `lab-cpu.yaml` (16 VUs) | p95 10 ms (1 VU) → 39 ms; CPU media 83% | CPU-001 | `CountPrimesNaive` ≈ 16–20% de las muestras de pila (tiempo propio) |
| `lab-blocking.yaml` (24 VUs) | p95 112 ms → 2,5 s; CPU 2%; hilos 5 → 33; 19 ajustes por starvation | TP-001 | camino caliente `BlockingReportService.BlockThread` → `Task.InternalWaitCore` (≈ 59–75%) |
| `lab-alloc.yaml` (8 VUs) | sin degradación de latencia; 5,2 GB/s (~5,7 MB/petición), ~2.000 GCs gen2 en 20 s, 11% en GC | GC-001 (observación de capacidad) | `BuildReportWithExcessiveAllocations` ≈ 99% de los bytes muestreados |
| `lab-dependency-nplus1.yaml` (mock) | p95 ≈ 265 ms con 1 VU; CPU 3% | DEP-001 | 5 llamadas/petición × ~47 ms ≈ latencia media (secuenciales) |
| `lab-dependency-errors.yaml` (mock 5% 503) | 4,4% de errores (160 × 502) | ERR-002 | 160 fallos del mock = 160 respuestas 5xx |
| `local-baseline.yaml` (control) | ~10.000 req/s, p95 1,5 ms; 177 MB/s (~17 KB/petición) | — | veredicto `no-degradation-detected` (sin falsos positivos) |
| `lab-cpu-fixed.yaml` + `compare_runs` | p95 y throughput mejoran frente a `lab-cpu.yaml` | — | comparación aceptada (mismo escenario, carga y máquina) |

En todos los casos el informe separa observaciones de hipótesis, declara la fidelidad (mocks como simulados, k6 en la
misma máquina) y la limpieza deja sin procesos huérfanos.
