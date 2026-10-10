---
name: diagnose-api-performance
description: Diagnosticar el rendimiento de una API .NET con el laboratorio performance-agent (preflight, smoke, carga, runtime, informe)
agent: Performance Diagnostics
argument-hint: scenarios/<manifiesto>.yaml
---

Diagnostica el rendimiento con el manifiesto `${input:manifest:scenarios/my-api.yaml}` siguiendo la skill
`dotnet-performance-diagnosis`:

1. Comprueba el estado del laboratorio y valida el manifiesto; si algo bloquea, explícalo y detente.
2. Muéstrame el plan (destino, endpoints, VUs, duración, dependencias reales o simuladas) y espera mi confirmación
   antes de generar carga.
3. Ejecuta smoke y baseline, analiza y, si una hipótesis lo pide, haz como máximo dos capturas de seguimiento.
4. Genera el informe, detén el laboratorio y resume: veredicto, observaciones, hipótesis con su evidencia a favor y
   en contra, fidelidad/limitaciones y próximos experimentos.
