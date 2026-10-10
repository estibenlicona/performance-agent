# Reglas de triage

Implementadas en `performance-agent-server` (`dotnet/src/PerformanceAgent.Core/Triage/TriageEngine.cs`; umbrales en `TriageThresholds.cs`). Son heurísticas
**interpretables**, no SLOs: deciden qué síntoma se observa, qué hipótesis abrir, qué evidencia la apoya o contradice
y qué captura de seguimiento la discrimina. Cada hipótesis tiene estado:

- `new`: hay un indicio, sin evidencia suficiente.
- `supported`: señales coherentes y ninguna en contra, sin evidencia discriminante.
- `confirmed`: evidencia discriminante (muestra de pilas, sitio de asignación, aritmética que cuadra).
- `contradicted`: las señales la descartan.
- `unresolved`: faltan los datos para evaluarla.

## Compuertas (antes de cualquier hipótesis de rendimiento)

| Situación | Veredicto |
|---|---|
| Preflight bloqueado o fallo de compilación/arranque/autenticación/dependencia | `environment-failure` (sin hipótesis de rendimiento) |
| Smoke con fallos funcionales sistemáticos (≥ 50% de comprobaciones de un endpoint) | `functional-failure` (FUNC-001) |
| Smoke sin respuesta HTTP | `environment-failure` |
| Fallo del generador (k6) o cancelación | `environment-failure` / `inconclusive` |

## Síntomas

| Síntoma | Condición (por defecto) |
|---|---|
| Latencia degradada | p95 baseline / p95 smoke ≥ 2,5 **y** aumento ≥ 20 ms |
| Lento sin concurrencia | p95 de un endpoint en el smoke ≥ 150 ms |
| Threshold de latencia incumplido | thresholds del manifiesto |
| Errores | tasa de error baseline ≥ 1% (desglose 2xx/4xx/5xx/0) |
| Presión de GC | ≥ 2 de: asignación ≥ 256 KB/petición o ≥ 1.000 MB/s; tiempo en GC ≥ 10%; ≥ 5 GCs gen2 y ≥ 1 por cada 1.000 peticiones |
| CPU saturada | CPU media del proceso ≥ 70% de la máquina |
| Starvation | ajustes del ThreadPool con motivo *Starvation* > 0 |

Si solo hay presión de recursos (sin síntoma de latencia), se registra una observación de **capacidad**: la API aún
no se degrada con la carga probada, pero consume recursos de forma anómala.

## Hipótesis

| Regla | Hipótesis | Apoya | Contradice | Confirma | Seguimiento |
|---|---|---|---|---|---|
| CPU-001 | Trabajo de CPU en la ruta de la petición | CPU ≥ 70% | CPU < 40% | método de **usuario** con ≥ 15% de las muestras de pila (tiempo propio) o como hoja del camino caliente, sin bloqueo dominante | `cpu-sample` |
| TP-001 | Esperas síncronas / starvation del ThreadPool | cola (último ≥ 2 o máx. ≥ 10), ajustes por starvation, bloqueo cooperativo, hilos creciendo, CPU < 40% | cola vacía, CPU alta sin starvation | camino caliente **de un método de usuario a una espera** (`Task.Wait`, `Thread.Sleep`, `Monitor.Wait`…) con ≥ 15% | `cpu-sample` |
| GC-001 | Presión de asignaciones / GC | ≥ 2 señales de GC | ninguna señal | sitio de **usuario** con ≥ 15% de los bytes asignados muestreados | `gc-events` |
| LOCK-001 | Contención de locks | contenciones ≥ 50/s (solo abre la hipótesis) | p95 de espera por lock < máx(5 ms, 5% del p95 de la API) | sitio de usuario con esperas ≥ 10% del p95 de la API | `contention-events` |
| DEP-001 | Latencia de dependencia / N+1 | llamadas × latencia del mock explican ≥ 60% de la latencia media | < 30% explicado, CPU alta | ≥ 2 llamadas por petición cuya suma explica 80–125% de la latencia (secuenciales) | — |
| ERR-001 | Errores del servidor bajo carga | 5xx + excepciones/s | — | — | `exception-events` |
| ERR-002 | Errores propagados de una dependencia | 5xx ≈ fallos del mock (≥ 50%) | — | recuentos que coinciden (≥ 95%) | — |
| ERR-003 | Timeouts / conexiones rechazadas | peticiones sin respuesta HTTP | — | — | — |
| MEM-001 | Crecimiento de memoria | heap final/inicial ≥ 1,5 y +50 MB | — | requiere soak (fase 2) | — |

## Veredicto

1. Alguna hipótesis `confirmed` → `cause-confirmed`.
2. Alguna `supported` → `symptoms-observed`.
3. Síntomas sin hipótesis sustentada → `inconclusive` (con la evidencia que falta).
4. Sin síntomas → `no-degradation-detected` (con la limitación de que no implica ausencia de problemas).

## Limitaciones que siempre se informan

- k6 y la API comparten máquina en los perfiles locales.
- Dependencias simuladas, no disponibles o no verificadas.
- Framework fuera de la matriz probada.
- Endpoints excluidos del escenario.
- Señales del runtime no disponibles o ventanas con solapamiento < 80%.

## Regresiones cubiertas por pruebas

- Esperas ociosas del ThreadPool (`LowLevelLifoSemaphore.WaitForSignal`) no confirman bloqueos ni anulan la
  confirmación de CPU.
- 177 MB/s a ~10.000 req/s (~17 KB/petición) no es presión de GC.
- Ruido sub-milisegundo (p95 0,5 → 1,6 ms) no es degradación.
