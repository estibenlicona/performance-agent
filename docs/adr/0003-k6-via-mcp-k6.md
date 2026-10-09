# ADR 0003 — k6 vía grafana/mcp-k6 con fallback CLI controlado

- **Estado:** aceptada
- **Contexto:** mcp-k6 0.7.0 ofrece `validate_script` y `run_script`, pero sin salidas adicionales, sin variables de
  entorno, con un tope de 5 minutos y resultados en texto; `validate_script` ejecuta 1 iteración (genera tráfico).
- **Decisión:** proveedor principal = mcp-k6 (`run_script`). Solo se ejecutan scripts **generados** por perf-lab,
  fijados por SHA-256, con la URL de la instancia embebida y `blacklistIPs` + `blockHostnames` (verificado en k6
  2.3.0). El resumen se obtiene con `handleSummary` → stdout entre marcadores. `validate_script` solo tras la
  validación estática del destino. Fallback: k6 CLI fijado (`--no-usage-report --out json`) y `k6 inspect` para
  validar sin tráfico; se registra como `fallback` con el motivo.
- **Consecuencias:** sin línea temporal por segundo cuando se usa mcp-k6 (sí con el CLI); duración máxima 240 s.
  k6 usage report desactivado mediante `config.json` en el directorio de trabajo de mcp-k6.
- **Generación de escenarios:** la "generación" de mcp-k6 es un *prompt* (`generate_script`) para que un LLM escriba
  un script libre. No se usa para ejecutar: un script escrito por el modelo no puede garantizar destino, métodos ni
  límites. perf-lab genera escenarios deterministas desde el plan de endpoints validado (no son plantillas genéricas:
  incluyen los controles de red, checks funcionales, submétricas por endpoint y el resumen estructurado) y expone la
  documentación oficial de mcp-k6 al agente con `search_k6_docs` (`list_sections`/`get_documentation`).
