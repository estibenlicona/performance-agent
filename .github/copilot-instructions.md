# Instrucciones del repositorio (performance-agent)

Este repositorio contiene **perf-lab**, un laboratorio para diagnosticar el rendimiento de APIs ASP.NET Core desde
GitHub Copilot en VS Code (agente `Performance Diagnostics`). El plan de producto está en
`plan_agente_diagnostico_performance_dotnet.md`; el alcance del MVP en `docs/mvp-scope.md`.

## Arquitectura

- `src/PerfAgent.Mcp`: servidor MCP `perf-lab` (stdio). Es la **única** superficie que ve el agente: aplica la
  política de seguridad y delega en los proveedores comunitarios, que no se exponen directamente.
- `src/PerfAgent.Core`: contratos (`ApiTarget`, `EnvironmentProfile`, `DependencyCheck`, `LoadTestRun`,
  `RuntimeSnapshot`, `Hypothesis`, `DiagnosticReport`), política, manifiestos, descubrimiento, triage por reglas y
  cliente MCP de proveedores.
- `src/PerfAgent.Adapters.K6Mcp`: generación/validación de escenarios k6 y ejecución vía `grafana/mcp-k6`
  (fallback: k6 CLI fijado).
- `src/PerfAgent.Adapters.DotNetDiagnosticsMcp`: evidencia del runtime vía `pedrosakuma/dotnet-diagnostics`.
- `src/PerfAgent.Adapters.Process`: build, arranque, readiness, mocks, contenedores y limpieza (Job Object en Windows).
- `src/PerfAgent.Reporting`: informes Markdown/JSON y comparación de ejecuciones.
- `src/PerfAgent.MockServer`: simulador HTTP declarativo para dependencias `mode: mock`.
- `lab/PerfLab.Api`: API de laboratorio con fallos conocidos (CPU, asignaciones, bloqueo, N+1).
- `scenarios/*.yaml`: manifiestos de ejecución (esquema `schemas/run-manifest.v1.schema.json`).
- `perf-lab.policy.json`: límites duros (VUs, duración, destinos, métodos, diagnóstico). Los manifiestos no pueden
  superarlos.

## Comandos verificados

- Instalar herramientas fijadas (k6 2.3.0, mcp-k6 0.7.0, dotnet-diagnostics-mcp 0.28.0): `powershell -ExecutionPolicy Bypass -File eng/bootstrap.ps1`
- Compilar: `dotnet build PerfAgent.slnx`
- Pruebas unitarias: `dotnet test tests/PerfAgent.UnitTests`
- Pruebas de integración (lentas, usan proveedores reales): `dotnet test tests/PerfAgent.IntegrationTests`
- Diagnóstico completo sin Copilot (utilidad de pruebas):
  `dotnet run --project src/PerfAgent.Mcp -- diagnose scenarios/lab-cpu.yaml`

## Reglas

- Las operaciones del laboratorio se hacen con las herramientas `perf-lab/*`, no con comandos de terminal.
- Nunca generes carga contra producción ni contra destinos que no sean la instancia local del laboratorio.
- No escribas secretos en manifiestos, configuración versionada, informes ni conversaciones.
- Separa siempre observaciones, hipótesis y causas confirmadas; cita el artefacto que respalda cada dato.
- Documentación y mensajes al usuario en español; identificadores de código en inglés.
- Si cambias contratos, reglas de triage o la política, actualiza las pruebas y `docs/`.
