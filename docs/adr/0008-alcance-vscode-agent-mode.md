# ADR 0008 — Alcance: VS Code Agent Mode (no coding agent en GitHub.com)

- **Estado:** aceptada
- **Decisión:** el MVP supone ejecución local (procesos, puertos de loopback, herramientas en `.tools/`). GitHub
  Copilot coding agent en GitHub.com requiere otra evaluación (procesos, red, MCP y credenciales en la nube).
- **Consecuencias:** `.mcp.json` usa rutas relativas al workspace y `dotnet run`; el `timeout` del servidor se eleva a
  600 s porque una prueba de carga con capturas puede durar varios minutos.
