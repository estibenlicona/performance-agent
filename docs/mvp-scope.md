# Alcance del MVP (Fase 1)

Entregable del **Paso 1** del plan. Define qué hace perf-lab hoy, con qué límites y qué queda fuera.

## Escenario soportado

- **Una** API ASP.NET Core (`Microsoft.NET.Sdk.Web`) por ejecución, iniciada desde un **`.csproj` explícito**
  (`api.project` en el manifiesto). Las soluciones (`.sln`/`.slnx`) solo se inspeccionan para listar candidatos.
- Compilación en **Release**; la API se ejecuta como `dotnet <ensamblado>.dll` (nunca `dotnet run`, para que el PID
  sea el de la aplicación) en un puerto libre de **127.0.0.1** asignado por el laboratorio.
- Interfaz principal: **GitHub Copilot en VS Code (Agent Mode)** con el custom agent `Performance Diagnostics`, que
  solo ve las herramientas `perf-lab/*`. El CLI (`PerfAgent.Mcp diagnose|report|status|schema`) es una utilidad de
  pruebas.
- Una ejecución activa a la vez.

## Frameworks y plataformas

| Elemento | Estado |
|---|---|
| `net10.0` (ASP.NET Core 10.0.12) | **Probado** (ver `docs/tested-matrix.md`) |
| `net8.0`, `net9.0` | No probado: la política lo **rechaza** (`allowUntestedFrameworks: false`). El proveedor de diagnóstico declara soporte, pero no se ha verificado en este laboratorio. |
| `net6.0`, `net7.0` y anteriores | No soportado |
| Windows 11 x64 | **Probado** |
| Linux / macOS | No probado (el bootstrap solo instala binarios windows-amd64; la limpieza usa el registro de procesos en lugar de Job Objects) |

## Límites por defecto (`perf-lab.policy.json`)

| Límite | Valor |
|---|---|
| VUs máximos (baseline) | 50 |
| Duración máxima (rampa + carga) | 240 s (mcp-k6 corta `run_script` a los 5 min) |
| Smoke | ≤ 2 VUs, ≤ 50 iteraciones |
| Endpoints por ejecución | ≤ 20 |
| Destinos de carga | solo `127.0.0.1` (la instancia iniciada por el laboratorio; k6 bloquea cualquier otra IP o hostname) |
| Métodos HTTP | `GET`, `HEAD` (escrituras denegadas) |
| Proyectos | bajo `lab/` (ampliable en `allowedProjectRoots`) |
| Manifiestos | bajo `scenarios/` |
| Dependencias remotas | ninguna autorizada por defecto (`allowedRemoteDependencyHosts: []`) |
| Capturas del runtime | counters, muestras de CPU, eventos (GC, ThreadPool, contención, excepciones), ≤ 60 s |
| Dumps, heap en vivo, captura de parámetros | **deshabilitados** |

## Perfiles de ambiente

- `local-isolated`: API local; dependencias en contenedor (`container`) o simuladas (`mock`).
- `local-hybrid`: además admite dependencias `remote` autorizadas y comprobadas (DNS, TCP, TLS, salud).

Una dependencia `required` no disponible **bloquea** el diagnóstico; nunca se sustituye en silencio por un mock.

## Datos

Solo datos **sintéticos**: valores de parámetros de ruta declarados o inferidos de forma explícita (entero → `1`) y
contratos de mock versionados. No se envían cuerpos ni escrituras salvo autorización doble (política + manifiesto).

## Fuera del MVP (deliberadamente)

- Carga intensiva o sin límites, y cualquier destino de producción.
- Soluciones con varios servicios, IaC, Kubernetes, Azure App Service/AKS.
- Runner remoto en red privada; identidad administrada/federada y OIDC (solo credencial de desarrollo de la propia API).
- Autenticación de k6 contra la API (los endpoints que la requieren se excluyen y se informa).
- Dumps de memoria, inspección de heap, captura de parámetros de métodos.
- Aplicación automática de cambios de código.
- Soak/stress/spike, modelos de llegada (arrival-rate), dashboards.
- Afirmar causa raíz sin evidencia discriminante.
