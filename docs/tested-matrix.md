# Matriz probada

Entregable del **Paso 1**: solo se declara compatible lo que se ha probado. Fecha de la verificación: 2026-10-09.

## Plataforma

| Componente | Versión probada | Notas |
|---|---|---|
| Sistema operativo | Windows 11 Pro 10.0.22621 x64, 4 núcleos lógicos, 8 GB | Única plataforma probada |
| .NET SDK | 10.0.401 (`global.json`: 10.0.100 + `latestFeature`) | |
| Runtime de la API objetivo | ASP.NET Core 10.0.12 (`net10.0`) | `supportedTargetFrameworks: [net10.0]` |
| k6 | 2.3.0 (windows-amd64) | `blacklistIPs`/`blockHostnames` verificados |
| grafana/mcp-k6 | 0.7.0 (windows-amd64) | protocolo MCP 2026-07-28 |
| dotnet-diagnostics-mcp | 0.28.0 (herramienta .NET local) | protocolo MCP 2025-11-25 |
| SDK MCP C# | ModelContextProtocol 2.2.0 | servidor stdio y clientes |
| VS Code | 1.140 instalado en la máquina de desarrollo | La selección del agente en Copilot Chat debe verificarse manualmente (ver `docs/mvp-gate.md`) |

## Modos de ejecución

| Modo | Estado |
|---|---|
| Servidor MCP por stdio lanzado con `dotnet run` (como `.mcp.json`) | Probado (`McpFacadeTests` y verificación manual con un cliente MCP) |
| CLI `PerfAgent.Mcp diagnose|report|status|schema` | Probado |
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
