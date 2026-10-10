# Matriz probada

Entregable del **Paso 1**: solo se declara compatible lo que se ha probado. Fecha de la verificación: 2026-10-09.

## Plataforma

| Componente | Versión probada | Notas |
|---|---|---|
| Sistema operativo | Windows 11 Pro 10.0.22621 x64, 4 núcleos lógicos, 8 GB | Única plataforma probada |
| Servidor performance-agent | 0.1.0, ejecutable autocontenido win-x64 | No requiere .NET instalado (verificado con `dotnet` fuera del PATH) |
| .NET SDK de la API objetivo | 10.0.401 | Lo usa la herramienta para compilar la API en Release |
| Runtime de la API objetivo | ASP.NET Core 10.0.12 (`net10.0`) | `supportedTargetFrameworks: [net10.0]` |
| k6 | 2.3.0 (windows-amd64) | `blacklistIPs`/`blockHostnames` verificados |
| grafana/mcp-k6 | 0.7.0 (windows-amd64) | protocolo MCP 2026-07-28 |
| dotnet-diagnostics-mcp | 0.28.0 (ejecutable autocontenido win-x64) | protocolo MCP 2025-11-25 |
| SDK MCP C# | ModelContextProtocol 2.2.0 | servidor stdio y clientes |
| VS Code | 1.140 instalado en la máquina de desarrollo | La selección del agente en Copilot Chat debe verificarse manualmente (ver `performance-agent-server/docs/mvp-gate.md`) |

## Modos de ejecución

| Modo | Estado |
|---|---|
| Servidor MCP por stdio: `.tools/performance-agent/0.1.0/performance-agent.exe` (como `.mcp.json`) | Probado (`initialize` + `tools/list`, sin `dotnet` en el PATH); en desarrollo, `McpFacadeTests` |
| CLI `performance-agent diagnose|report|status|schema` | Probado |
| Mocks con `performance-agent mock` desde el ejecutable autocontenido | Probado (sin `dotnet` en el PATH) |
| Perfil `local-isolated` con mocks declarados | Probado |
| Perfil `local-hybrid` con dependencia remota | Probado el **bloqueo** por DNS (dependencia inalcanzable); no probado contra un servicio remoto real |
| Dependencias `container` (docker compose) | Implementado, **no probado** (Docker no instalado en la máquina de prueba) |
| k6 vía mcp-k6 (`run_script`) | Probado (vía principal) |
| k6 CLI (fallback) | Implementado; ejercitado solo si mcp-k6 no está disponible |
| Job Object (Windows) para evitar huérfanos | Probado; Linux/macOS usan el registro de procesos (no probado) |

## No probado / no soportado

- `net8.0`, `net9.0`: no probados en este laboratorio (bloqueados por política hasta probarlos).
- Linux y macOS: el bootstrap solo instala binarios windows-amd64.
- Docker/contenedores, identidad administrada/federada, runner remoto.
