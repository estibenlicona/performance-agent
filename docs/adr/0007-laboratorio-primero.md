# ADR 0007 — Validar primero contra la API de laboratorio

- **Estado:** aceptada
- **Decisión:** `dotnet/lab/PerfLab.Api` con fallos conocidos y activables por configuración (CPU, asignaciones, bloqueo
  sync-over-async, N+1 contra un mock, errores de dependencia) y un control sano. La política solo autoriza proyectos
  bajo `dotnet/lab/` hasta que se amplíe explícitamente.
- **Consecuencias:** cada regla tiene un escenario que la ejercita y pruebas de extremo a extremo con el veredicto
  esperado; no se conecta el agente a servicios reales hasta validar los controles.
