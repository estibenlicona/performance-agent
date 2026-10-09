# ADR 0004 — Diagnóstico con pedrosakuma/dotnet-diagnostics + muestreo del SO

- **Estado:** aceptada
- **Contexto:** dotnet-diagnostics 0.28.0 funciona en Windows con .NET 10 (spike de compatibilidad). Sus counters son
  agregados (último intervalo y máximo, sin marcas de tiempo) y la vista `resources` en Windows no incluye CPU.
  `timheuer/dotnet-profiler-mcp` no es viable (sin licencia ni paquete publicado).
- **Decisión:** proveedor principal = dotnet-diagnostics (allowlist: `inspect_process`, `collect_events`,
  `collect_sample`, `collect_batch`, `query_snapshot`; vistas/tipos de bajo riesgo; nunca se concede `safetyApproval`).
  Para la serie temporal y la CPU media, la fachada muestrea a nivel de SO (CPU, working set) **solo el proceso que
  ella inició**. No es un segundo proveedor de diagnóstico: no usa EventPipe ni duplica analizadores del runtime.
- **Consecuencias:** correlación por ventana (solapamiento) y series de CPU/memoria, hilos del ThreadPool y heap tras
  GC. Si el proveedor no está disponible, el diagnóstico es parcial y lo declara.
