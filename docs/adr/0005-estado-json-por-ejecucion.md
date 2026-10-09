# ADR 0005 — Estado persistido como JSON por ejecución

- **Estado:** aceptada
- **Decisión:** cada ejecución es `artifacts/runs/<runId>/run.json` (contratos versionados `schemaVersion`), con
  copia del manifiesto, scripts, respuestas de proveedores y logs saneados. Sin base de datos en el MVP.
- **Consecuencias:** las conclusiones se pueden **recalcular** desde los artefactos (`PerfAgent.Mcp report <runId>`)
  y comparar entre ejecuciones; `artifacts/` está fuera de control de versiones.
