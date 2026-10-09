# ADR 0006 — Triage con reglas transparentes y seguimiento acotado

- **Estado:** aceptada
- **Decisión:** reglas interpretables (`docs/triage-rules.md`) que producen observaciones e hipótesis con estado,
  evidencia a favor/en contra/faltante y una siguiente acción del catálogo `perf-lab`. "Confirmada" exige evidencia
  discriminante. El agente (o `run_diagnosis`) ejecuta como máximo dos capturas de seguimiento.
- **Consecuencias:** decisiones reproducibles y testeables; el LLM interpreta y redacta, pero no decide el veredicto.
  Las heurísticas se calibraron en el laboratorio y tienen regresiones para falsos positivos detectados.
