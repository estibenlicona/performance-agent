# Manifiestos de ejecución (`scenarios/*.yaml`)

Esquema versionado: `scenarios/run-manifest.v1.schema.json` (generado desde `dotnet/src/PerfAgent.Core/Manifest/RunManifest.cs`
con `dotnet run --project dotnet/src/PerfAgent.Mcp -- schema`; una prueba verifica que está al día). En VS Code, la línea
`# yaml-language-server: $schema=./run-manifest.v1.schema.json` activa autocompletado y validación.

La carga es **estricta**: un campo desconocido o mal escrito es un error, no se ignora.

## Secciones

| Sección | Campos | Notas |
|---|---|---|
| raíz | `schemaVersion` (1), `name`, `description`, `profile` (`local-isolated`/`local-hybrid`), `environment` | `environment` no puede ser producción |
| `api` | `project` (.csproj), `configuration` (Release), `framework`, `aspNetCoreEnvironment` (Development), `healthPath` (/health), `readinessTimeoutSeconds`, `openApiPath`, `settings` | `settings` son variables de entorno **no secretas**; claves reservadas prohibidas (`DOTNET_*`, `ASPNETCORE_*`, `Kestrel__*`, proxies, `PATH`…) |
| `endpoints[]` | `name`, `method`, `path`, `expectedStatus`, `bodyContains`, `weight`, `pathParameters`, `body` | rutas relativas; parámetros `{x}` con valores sintéticos; `body` solo en escrituras autorizadas |
| `load` | `engine` (k6), `endpointSource` (`auto`/`openapi`/`declared`), `include`, `exclude`, `smoke` (`vus`, `iterations`, `maxDuration`), `baseline` (`executor`, `vus`, `duration`, `rampUp`), `thresholds` | duraciones con sintaxis k6 (`30s`, `2m`); thresholds con gramática estricta |
| `dependencies.<nombre>` | `mode` (`container`/`mock`/`remote`/`unavailable`), `required`, `injectAs`, `contract`, `url`, `healthPath`, `authentication` (`none`/`developer-credential`), `composeFile`, `service`, `port`, `data` (synthetic) | `injectAs` es la clave de configuración de la API que recibe la URL |
| `diagnostics` | `runtimeCounters`, `counterIntervalSeconds`, `cpuTrace` (`on-demand`/`disabled`), `memoryDump` (`disabled`) | |
| `safety` | `productionTargets` (deny), `writeOperations` (`deny`/`allow-declared`), `secretsInModelContext` (deny), `requireConfirmation` (`smoke`, `baseline`, `runtime-capture`) | |

## Endpoints: OpenAPI o declarados

- `endpointSource: auto` (por defecto): si el manifiesto declara endpoints, se usan; si no, OpenAPI.
- Desde OpenAPI se excluyen, con su motivo: escrituras no autorizadas, operaciones con seguridad declarada (k6 no
  obtiene tokens en el MVP), parámetros de query obligatorios, cuerpos obligatorios y parámetros de ruta sin valor
  sintético (los enteros reciben `1`, marcado como inferido).
- `include`/`exclude` aceptan nombre (`operationId`) o ruta.

## Ejemplos

- `scenarios/local-baseline.yaml` — control sano (OpenAPI).
- `scenarios/lab-*.yaml` — un fallo conocido por escenario (ver `docs/lab-scenarios.md`).
- `scenarios/local-hybrid.example.yaml` — dependencia remota real + mock; requiere autorizar el host en la política.
