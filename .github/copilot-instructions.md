# Instrucciones del repositorio (performance-agent)

Este workspace usa **performance-agent**, una herramienta para diagnosticar el rendimiento de cualquier API ASP.NET Core
desde GitHub Copilot en VS Code (agente `Performance Diagnostics`). Aquí no hay código fuente: el servidor es un
ejecutable autocontenido instalado en `.tools/` por `setup/bootstrap.ps1` (su código está en `performance-agent-server`).
El alcance está en `docs/mvp-scope.md`.

## Contenido

- `.mcp.json`: declara el servidor MCP `performance-agent` (stdio). Es la **única** superficie que ve el agente: aplica la
  política de seguridad y delega en los proveedores comunitarios (grafana/mcp-k6, dotnet-diagnostics), que no se
  exponen directamente.
- `safety-policy.json`: límites duros (carpetas permitidas, VUs, duración, destinos, métodos, diagnóstico). Los
  manifiestos no pueden superarlos. La API a diagnosticar debe estar en `allowedProjectRoots`.
- `scenarios/*.yaml`: manifiestos de ejecución (esquema `scenarios/run-manifest.v1.schema.json`, guía `docs/manifest.md`);
  `scenarios/my-api.example.yaml` es la plantilla.
- `contracts/`: contratos de dependencias simuladas (`mode: mock`, ver `docs/mock-contracts.md`).
- `setup/`: `bootstrap.ps1` instala las versiones fijadas en `tools.json`; `verify.ps1` comprueba la configuración.
- `artifacts/runs/<runId>/`: informes, scripts k6, evidencia y logs de cada ejecución.

## Reglas

- Las operaciones del laboratorio se hacen con las herramientas `performance-agent/*`, no con comandos de terminal.
- Nunca generes carga contra producción ni contra destinos que no sean la instancia local del laboratorio.
- No escribas secretos en manifiestos, configuración versionada, informes ni conversaciones.
- Separa siempre observaciones, hipótesis y causas confirmadas; cita el artefacto que respalda cada dato.
- Documentación y mensajes al usuario en español.
- Si cambias `.mcp.json`, la política o la configuración de Copilot, ejecuta `setup/verify.ps1`.
