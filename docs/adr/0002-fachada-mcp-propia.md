# ADR 0002 — Fachada MCP propia (`perf-lab`) como única superficie del agente

- **Estado:** aceptada
- **Contexto:** los proveedores comunitarios exponen herramientas genéricas (`run_script` con cualquier script,
  diagnóstico de cualquier PID, dumps). El prompt no es un control de seguridad.
- **Decisión:** `PerfAgent.Mcp` es el único servidor registrado en `.mcp.json`. Expone 17 herramientas tipadas con
  `structuredContent` que validan política y estado y delegan en los proveedores como **clientes MCP internos** con
  allowlist de herramientas, vistas y tipos. Los comandos locales salen de un catálogo cerrado sin shell.
- **Consecuencias:** el agente no puede saltarse el preflight ni elegir destinos o PIDs. Cada delegación queda
  registrada. Coste: mantener el mapeo cuando cambien los catálogos upstream (cubierto por pruebas de contrato).
