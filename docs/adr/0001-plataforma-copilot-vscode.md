# ADR 0001 — Plataforma: custom agent de GitHub Copilot en VS Code (Agent Mode)

- **Estado:** aceptada (2026-10-09)
- **Contexto:** el plan descarta un frontend o chat propio en el MVP y fija VS Code Agent Mode como entorno.
- **Decisión:** el agente vive en `.github/agents/performance-diagnostics.agent.md` (`target: vscode`), con
  instrucciones del repositorio (`.github/copilot-instructions.md`), una skill (`.github/skills/dotnet-performance-diagnosis`)
  y un prompt opcional. Solo tiene `perf-lab/*` más `read` y `search` (lectura de código); sin `edit` ni `execute`.
- **Consecuencias:** compartible y versionado con el repositorio. La selección del agente en el selector y la carga
  en un chat nuevo se verifican manualmente en VS Code (no automatizable). Los prompt files no los carga el harness
  "Copilot" (Agent Host); el flujo completo funciona igualmente con el agente y la skill.
