# Plan de construcción: agente de diagnóstico y optimización de performance para APIs .NET

**Versión:** 1.2  
**Fecha:** 8 de octubre de 2026  
**Estrategia:** dos fases — MVP de aprendizaje rápido y evolución al alcance completo.

## 1. Objetivo

Construir un **agente personalizado para GitHub Copilot en VS Code (Agent Mode)** que pueda descubrir una API .NET, levantarla en un entorno controlado, ejecutar pruebas de performance con k6, recopilar evidencia del runtime, investigar posibles cuellos de botella y entregar un diagnóstico reproducible. En la segunda fase, debe adaptarse a dependencias y ambientes distintos, comprobar identidades federadas y redes privadas cuando corresponda, y verificar si las correcciones producen una mejora real. La experiencia principal será la conversación con Copilot; las acciones que requieren precisión y control se ejecutarán mediante herramientas deterministas expuestas principalmente por MCP, no mediante comandos inventados libremente por el modelo.

La meta no es que el modelo lea código y proponga optimizaciones genéricas. La meta es que **formule hipótesis, recopile evidencia, diseñe experimentos para confirmarlas o descartarlas y compare resultados antes y después**.

## 2. Principios de diseño

1. **Evidencia antes que conclusiones.** Un síntoma no demuestra por sí solo la causa raíz. El informe debe separar observaciones, hipótesis, evidencia confirmatoria, evidencia contradictoria y preguntas abiertas.
2. **Fidelidad explícita del ambiente.** Todo resultado indicará qué partes fueron reales, simuladas, inaccesibles o diferentes del ambiente objetivo.
3. **Experimentación reproducible.** Registrar versión del código, runtime, configuración relevante, carga, datos de prueba, dependencias, resultados y artefactos.
4. **Progresión de menor a mayor coste.** Ejecutar primero preflight y smoke test; realizar pruebas intensivas y profiling solo cuando exista una razón y dentro de los límites autorizados.
5. **Copilot razona; las herramientas ejecutan.** GitHub Copilot aporta la conversación y el razonamiento del agente. Build, arranque, ejecución de pruebas, captura de métricas, permisos y límites de seguridad deben implementarse como herramientas deterministas con entradas validadas; el prompt del agente no es un control de seguridad.
6. **Seguridad por defecto.** No ejecutar pruebas de carga contra producción, no extraer ni mostrar secretos, no ejecutar comandos arbitrarios del modelo y no modificar código automáticamente en la fase MVP.
7. **Reutilizar antes de reimplementar.** La primera implementación integra herramientas comunitarias existentes: `grafana/mcp-k6` para capacidades MCP de k6 y `pedrosakuma/dotnet-diagnostics` para diagnóstico del runtime. Solo se implementará una alternativa propia cuando exista una brecha demostrada de seguridad, compatibilidad o funcionalidad.
8. **Arquitectura extensible, integrada con Copilot.** Definir el rol y el flujo en un custom agent de Copilot, documentar convenciones del repositorio con instrucciones y skills, y encapsular k6, diagnóstico .NET, proceso de la API y ambientes detrás de herramientas/adaptadores reemplazables.

### Plataforma objetivo: GitHub Copilot en VS Code (Agent Mode)

Este plan asume **GitHub Copilot dentro de VS Code**, usando un agente personalizado visible en el selector de agentes. No asume una aplicación de chat propia ni, por ahora, GitHub Copilot coding agent ejecutándose en GitHub.com. Esa variante cloud puede evaluarse después porque los requisitos para levantar procesos locales y acceder a redes privadas son diferentes.

El repositorio debe contener la configuración necesaria para que el equipo pueda compartir, revisar y versionar el agente:

```text
.github/
  copilot-instructions.md
  agents/
    performance-diagnostics.agent.md
  skills/
    dotnet-performance-diagnosis/
      SKILL.md
  prompts/
    diagnose-api-performance.prompt.md   # opcional para invocar el flujo con una plantilla
.mcp.json                                 # servidores MCP del workspace, sin secretos
```

Responsabilidades de cada artefacto:

- **`.github/agents/performance-diagnostics.agent.md`:** rol, límites, herramientas permitidas, flujo de diagnóstico, condiciones de parada y formato esperado del informe.
- **`.github/copilot-instructions.md`:** contexto global del proyecto, convenciones, comandos verificados y reglas que siempre debe seguir Copilot.
- **`SKILL.md`:** procedimiento reutilizable de diagnóstico paso a paso, criterios de selección de pruebas, interpretación de señales y plantilla de evidencias.
- **`.mcp.json`:** configuración compartible de los servidores MCP necesarios. No debe contener tokens, contraseñas ni valores de secretos.
- **MCP propio `PerfAgent.Mcp`:** herramientas de control seguro del laboratorio: descubrir proyecto, validar configuración, build, start, readiness, cleanup, perfiles y generación del informe.
- **MCP comunitarios obligatorios en el MVP:** `grafana/mcp-k6` y `pedrosakuma/dotnet-diagnostics`. Sus capacidades deben aparecer explícitamente en el plan de Copilot y pasar por una prueba de integración; no basta con citarlos como referencias.
- **Fallback de diagnóstico:** si `dotnet-diagnostics` no soporta el runtime/plataforma objetivo, evaluar `timheuer/dotnet-profiler-mcp` o invocar las herramientas oficiales (`dotnet-counters`, `dotnet-trace`, `dotnet-dump`) a través del adaptador propio. No activar ambos proveedores de diagnóstico a la vez hasta justificar la diferencia.

Ejemplo orientativo del archivo del agente (los nombres de herramientas deben coincidir exactamente con los configurados en el workspace):

```markdown
---
name: performance-diagnostics
description: Diagnostica performance de APIs ASP.NET Core mediante pruebas reproducibles y evidencia del runtime.
target: vscode
tools:
  - "perf-lab/*"
---

Eres el agente de diagnóstico de performance para APIs .NET.
Sigue la skill `dotnet-performance-diagnosis`.
Antes de generar carga, ejecuta el preflight y confirma el destino y los límites.
No afirmes causa raíz sin evidencia; separa observaciones, hipótesis y conclusiones.
No ejecutes carga contra producción ni reveles secretos.
``` 

El bloque es una plantilla inicial, no una configuración lista para usar: las herramientas se validan en VS Code y contra el catálogo MCP real (`tools/list`). No uses `tools: ['*']` como atajo. **Copilot solo ve `perf-lab/*`; no se exponen directamente los ejecutores genéricos de la comunidad.** `PerfAgent.Mcp` actúa como fachada y cliente MCP de `grafana/mcp-k6` y `pedrosakuma/dotnet-diagnostics`; valida target, PID, duración, concurrencia y nivel de riesgo antes de delegar. Así no se puede saltar el preflight llamando directamente a `run_script`, `validate_script` o herramientas de diagnóstico sobre otro PID.

En el MVP se mantendrá **un solo agente de Copilot**, con un conjunto pequeño de herramientas. No se crearán todavía subagentes ni un framework propio de agentes. `grafana/mcp-k6` y `pedrosakuma/dotnet-diagnostics` son proveedores reales usados por los adaptadores, no alternativas mencionadas a futuro: sus herramientas se invocan detrás de `perf-lab/*`, y la ejecución registra el proveedor y la herramienta upstream utilizados.

Referencias oficiales para la implementación:

- [Custom agents en VS Code](https://code.visualstudio.com/docs/agent-customization/custom-agents)
- [Instrucciones personalizadas de Copilot](https://docs.github.com/en/copilot/how-tos/configure-custom-instructions-in-your-ide/add-repository-instructions-in-your-ide)
- [Agregar y administrar servidores MCP en VS Code](https://code.visualstudio.com/docs/agent-customization/mcp-servers)
- [Conceptos de personalización de agentes](https://code.visualstudio.com/docs/agents/concepts/customization)

## 3. Alcance resumido por fase

| Capacidad | Fase 1 — MVP | Fase 2 — Alcance esperado |
|---|---|---|
| Plataforma IA | Custom agent de GitHub Copilot en VS Code, Agent Mode | Agente maduro con handoffs/subagentes opcionales y evaluación sistemática de comportamiento |
| Configuración Copilot | `.agent.md`, instrucciones, una skill y configuración MCP mínima | Skills por especialidad, instrucciones por ruta, hooks y evaluación automatizada de personalizaciones cuando aporte valor |
| Reutilización comunitaria | Integrar `grafana/mcp-k6` y `pedrosakuma/dotnet-diagnostics`; fallback de diagnóstico condicionado por compatibilidad | Ampliar funciones o añadir proveedor alternativo solo por brechas identificadas; actualizar versiones con pruebas de regresión |
| API objetivo | Una solución ASP.NET Core local por ejecución | Múltiples soluciones, proyectos, versiones y topologías compatibles |
| Descubrimiento | `.sln`/`.csproj`, configuración básica y OpenAPI disponible | OpenAPI, contenedores, IaC, configuración de despliegue y dependencias con mayor cobertura |
| Ambientes | Local aislado; híbrido básico con dependencias declaradas | Local aislado, local híbrido, runner remoto en red autorizada y ambiente de rendimiento representativo |
| Dependencias | Local/container, mock declarado o dependencia remota requerida y comprobada | Adaptadores avanzados, provisionamiento efímero y preparación de datos controlada |
| Identidad | Credencial de desarrollo explícita cuando proceda; no simular identidad administrada | Entra ID, identidad administrada/federada y OIDC según el contexto de ejecución y con permisos mínimos |
| Pruebas k6 | Smoke test y prueba de carga sencilla con límites | Load, stress, spike, soak, escenarios realistas, autenticación, datos y thresholds configurables |
| Diagnóstico .NET | Métricas básicas del runtime; traza bajo demanda cuando sea compatible | Counters, CPU/allocation traces, GC, ThreadPool, dumps con aprobación y correlación distribuida |
| Decisión del siguiente paso | Flujo acotado por reglas y selección de una investigación adicional | Diagnóstico adaptativo con hipótesis, evidencia contradictoria y experimentos sucesivos |
| Soluciones de código | Recomendaciones; cambios manuales | Cambios aislados en branch/worktree, pruebas funcionales y comparación antes/después con aprobación |
| Informes | Markdown y JSON por ejecución | Historial, tendencias, CI/CD, comparaciones y dashboard opcional |

## 4. Arquitectura objetivo

Separar las responsabilidades para que los componentes puedan crecer sin reescribir el agente.

- **Agente de GitHub Copilot:** interfaz conversacional y razonador que planifica el flujo, consulta la evidencia y selecciona la siguiente herramienta dentro del catálogo permitido. La definición vive en `.github/agents/performance-diagnostics.agent.md`.
- **`PerfAgent.Mcp` / API Workspace Manager / MCP Facade:** único servidor que consume el custom agent. Expone herramientas tipadas para descubrir/build/start/stop, ejecutar k6 y recopilar evidencia. Mantiene clientes MCP internos hacia los servidores comunitarios y aplica una política antes de invocarles. Copilot no accede directamente a las herramientas upstream genéricas ni ejecuta cadenas de shell arbitrarias.
- **Environment Readiness & Fidelity Engine:** comprueba dependencias, conectividad, identidad, configuración y grado de fidelidad del ambiente; decide si se puede continuar, usar un mock declarado o cambiar de perfil.
- **Load Test Adapter:** integra k6, genera/valida scripts, ejecuta escenarios y normaliza resultados.
- **.NET Diagnostics Adapter:** integra las herramientas de observabilidad del runtime y normaliza contadores, trazas y artefactos.
- **Evidence & Hypothesis Engine:** correlaciona la degradación de k6 con señales del runtime y dependencias; mantiene hipótesis sustentadas en evidencia.
- **Report Generator:** produce resultados reproducibles, limitaciones, recomendaciones y comparaciones.
- **Policy/Safety Layer:** impone límites en las propias herramientas, no solo en las instrucciones de Copilot: destinos, duración, concurrencia, operaciones de escritura, permisos, captura de dumps y cambios de código.

### Flujo de diagnóstico

1. Recibir la ruta o referencia del proyecto y el perfil solicitado.
2. Validar que el proyecto y el destino estén autorizados.
3. Descubrir la API y las dependencias conocidas.
4. Ejecutar el preflight del ambiente y clasificar cada dependencia.
5. Compilar en `Release`, iniciar la API y comprobar readiness.
6. Descubrir endpoints mediante OpenAPI o leer un escenario declarado.
7. Ejecutar smoke test y validar resultados funcionales.
8. Medir baseline y correr una prueba de carga limitada.
9. Capturar métricas del runtime durante la misma ventana de prueba.
10. Identificar señales y seleccionar una investigación de seguimiento.
11. Analizar las evidencias y formular hipótesis con sus límites.
12. Generar el informe y limpiar los recursos temporales.

## 5. Fase 1 — MVP: laboratorio local que mide, investiga e informa

### Objetivo del MVP

Conseguir una primera versión pequeña, rápida de probar y fácil de depurar **desde GitHub Copilot en VS Code**: el usuario selecciona `Performance Diagnostics` en el selector de agentes y le indica una solución .NET local. Copilot invoca herramientas MCP controladas para descubrirla, compilarla, levantarla, ejecutar una prueba k6 limitada, recoger métricas básicas y entregar un informe con evidencia y próximos pasos. No se empieza construyendo una aplicación web ni un chat separado.

El MVP no debe prometer diagnóstico perfecto ni corregir código en automático. Debe demostrar el ciclo completo de medición y diagnóstico sobre casos controlados.

### Paso 1. Fijar el contrato y las restricciones

**Trabajo**

- Definir el único escenario soportado inicialmente: API ASP.NET Core local, ejecutable desde un `.csproj` explícito.
- Definir los frameworks de destino soportados después de verificar las herramientas elegidas. No declarar compatibilidad con una versión de .NET que no se haya probado.
- Definir límite de duración, VUs máximos, directorios permitidos y destinos de red autorizados.
- Bloquear producción por defecto; rechazar targets remotos no declarados.
- Exigir datos sintéticos o de prueba y prohibir operaciones de escritura no aprobadas.

**Entregables**

- `docs/mvp-scope.md`
- `docs/safety-policy.md`
- Lista de runtimes, sistemas operativos y modos de ejecución probados.

**Criterio de aceptación**

El sistema rechaza una ejecución fuera del scope o que supere los límites definidos, y explica la razón antes de iniciar la carga.

### Paso 2. Crear la configuración de Copilot y los contratos internos

**Trabajo**

- Crear los archivos `.github/agents/performance-diagnostics.agent.md` y `.github/copilot-instructions.md`; documentar el método en `.github/skills/dotnet-performance-diagnosis/SKILL.md`.
- Crear `PerfAgent.Core` y `PerfAgent.Mcp`. El servidor MCP expondrá herramientas pequeñas, tipadas y con parámetros validados; el CLI podrá existir como utilidad de pruebas, pero no será la interfaz principal.
- Definir contratos estables para `ApiTarget`, `EnvironmentProfile`, `DependencyCheck`, `LoadTestRun`, `RuntimeSnapshot`, `Hypothesis` y `DiagnosticReport`.
- Proporcionar salida estructurada JSON además de mensajes legibles para que Copilot pueda razonar sobre resultados no ambiguos.
- Configurar `.mcp.json` para exponer solo el MCP propio `perf-lab` a Copilot. Definir los proveedores comunitarios como procesos secundarios de los adaptadores y no agregar sus herramientas genéricas al allowlist del agente. No incluir secretos en configuraciones versionadas.
- Registrar `perf-lab` como el único servidor MCP expuesto al custom agent de Copilot. Los adaptadores propios lanzan los servidores comunitarios como clientes/proveedores MCP secundarios y usan exclusivamente sus capacidades aprobadas.
- Implementar pruebas de contrato contra las herramientas comunitarias fijadas por versión: nombres, parámetros, salida JSON, errores, timeouts, compatibilidad y permisos. No codificar contra una herramienta supuesta sin consultar el catálogo MCP real.
- Mantener el MVP centrado en Copilot; no construir un proveedor LLM propio ni una UI conversacional alternativa.

**Estructura sugerida**

```text
src/
  PerfAgent.Core/
  PerfAgent.Mcp/
  PerfAgent.Adapters.Process/
  PerfAgent.Adapters.K6Mcp/
  PerfAgent.Adapters.DotNetDiagnosticsMcp/
  PerfAgent.Reporting/
.github/
  copilot-instructions.md
  agents/performance-diagnostics.agent.md
  skills/dotnet-performance-diagnosis/SKILL.md
  prompts/diagnose-api-performance.prompt.md  # opcional
.mcp.json
tests/
  PerfAgent.UnitTests/
  PerfAgent.IntegrationTests/
lab/
  PerfLab.Api/
  docker-compose.yml
scenarios/
  local-baseline.yaml
  local-hybrid.example.yaml
docs/
```

**Configuración de Copilot y proveedores comunitarios**

El archivo portable `.mcp.json` de Copilot registra solo la fachada propia. Los servidores de la comunidad se configuran como proveedores secundarios de los adaptadores: así Copilot no puede eludir las restricciones llamando directamente a un ejecutor de scripts o a un diagnóstico con otro PID.

`.mcp.json` (plantilla para Copilot):

```json
{
  "mcpServers": {
    "perf-lab": {
      "command": "dotnet",
      "args": ["run", "--project", "src/PerfAgent.Mcp/PerfAgent.Mcp.csproj"]
    }
  }
}
```

Configuración conceptual de los proveedores secundarios en `appsettings.Development.json` o una configuración de desarrollo equivalente (no incluir secretos):

```json
{
  "CommunityProviders": {
    "K6Mcp": {
      "Command": "docker",
      "Arguments": ["run", "--rm", "-i", "grafana/mcp-k6"]
    },
    "DotNetDiagnosticsMcp": {
      "Command": "dotnet-diagnostics-mcp",
      "Arguments": ["--stdio"]
    }
  }
}
```

Estos fragmentos son plantillas: fijar tag/digest de la imagen de `grafana/mcp-k6` y versión del paquete `dotnet-diagnostics-mcp` antes de usarlos en equipo o CI. Instalar los prerrequisitos según los README oficiales, comprobar sus licencias y fijar las versiones tras el spike de compatibilidad. `PerfAgent.Mcp` debe arrancar ambos procesos como clientes MCP secundarios, consultar su catálogo de herramientas al inicio y fallar con un error explicativo si falta una capacidad requerida. No configurar estos proveedores como herramientas directas del custom agent.

**Mapeo de capacidades upstream a herramientas expuestas a Copilot**

- `perf-lab/generate_k6_scenario` → puede consultar prompt/documentación de `grafana/mcp-k6` y generar un escenario guardado bajo `scenarios/`.
- `perf-lab/validate_k6_scenario` → valida el esquema y los destinos del escenario; solo después delega la validación al proveedor `mcp-k6`. Como `validate_script` puede ejecutar una petición mínima, la llamada está bloqueada si no se verificó que el destino sea parte del laboratorio autorizado.
- `perf-lab/run_k6_test` → valida perfil, host/destino permitido, VUs, duración, concurrencia y endpoints antes de invocar la ejecución upstream `run_script` de `mcp-k6` o el CLI fijado de k6 si el proveedor no pasa la validación técnica.
- `perf-lab/inspect_runtime` → delega a `dotnet-diagnostics` (`inspect_process` con vista `preflight`/`triage`) usando exclusivamente el PID asociado a la ejecución activa.
- `perf-lab/collect_runtime_evidence` → delega a `collect_events`, `collect_sample` o `collect_batch` tras validar PID, tipo de captura, duración y permisos.
- `perf-lab/compare_runs` → normaliza métricas y puede usar `compare_to_baseline` cuando ese proveedor lo soporte.

La lista es el contrato de la fachada, no una garantía de que todos los nombres estén presentes en cualquier release. Las pruebas de integración deben validar el catálogo real y confirmar que el proveedor externo recibió los parámetros esperados. No exponer al LLM herramientas upstream genéricas ni parámetros de riesgo que no necesite el flujo.

**Criterio de aceptación**

Desde VS Code, el usuario puede seleccionar el agente `Performance Diagnostics` en Copilot Chat. El agente usa herramientas MCP para iniciar un diagnóstico de laboratorio, recibe resultados JSON tipados y presenta el estado y las evidencias. La configuración del agente y los servidores MCP está versionada y documentada en el repositorio.

### Paso 3. Implementar el descubrimiento mínimo de la API

**Trabajo**

- Aceptar ruta explícita a `.csproj` o `.sln`.
- Detectar el/los `TargetFramework` declarados y comprobar los SDK/runtimes disponibles.
- Identificar configuración de ejecución básica y puerto libre para la instancia del agente.
- Detectar OpenAPI/Swagger cuando la API lo exponga.
- Si no existe OpenAPI, exigir un archivo de escenario explícito; no inferir endpoints peligrosos ni enviar solicitudes arbitrarias.
- Detectar referencias conocidas a dependencias sin imprimir valores de secretos.

**Fuera del MVP**

No se necesita descubrir correctamente toda solución con múltiples servicios, infraestructura como código o configuración dinámica compleja.

**Criterio de aceptación**

El agente explica qué proyecto va a iniciar, con qué runtime, qué endpoints conoce y qué información le falta para continuar.

### Paso 4. Implementar los perfiles de ambiente mínimos

**Trabajo**

Implementar un manifiesto declarativo por ejecución. En el MVP solo hacen falta dos perfiles:

- `local-isolated`: API local; dependencias reproducibles mediante contenedor o mock declarado.
- `local-hybrid`: API local; alguna dependencia remota permitida y comprobada, y el resto local o simulado.

Para cada dependencia admitir inicialmente estos modos:

- `container`: servicio levantado por el laboratorio.
- `mock`: simulador explícito con contrato y comportamiento configurados.
- `remote`: endpoint autorizado que debe superar los checks de conectividad y autenticación.
- `unavailable`: no accesible y sin reemplazo válido.

Si una dependencia marcada `required` no se puede alcanzar y no tiene mock permitido, el agente debe detener la ejecución o declarar el diagnóstico bloqueado/incompleto. No debe sustituirla silenciosamente.

**Entregables**

- Esquema versionado del manifiesto.
- Checks de DNS, TCP/TLS y endpoint de salud cuando sean aplicables.
- Matriz por dependencia: real, simulada, no disponible o no verificada.

**Criterio de aceptación**

El informe permite distinguir sin ambigüedad las pruebas hechas contra dependencias reales de las hechas contra mocks.

### Paso 5. Levantar, comprobar y limpiar la API

**Trabajo**

- Ejecutar build en configuración `Release`.
- Iniciar la API con argumentos generados a partir de configuración validada.
- Registrar PID, puerto, stdout/stderr saneados y timeout de inicio.
- Esperar readiness mediante un endpoint de salud o una petición segura configurada.
- Si falla, distinguir build error, proceso terminado, puerto, configuración, autenticación y dependencia no disponible.
- Cerrar el proceso y los contenedores temporales incluso si falla k6 o se cancela el diagnóstico.
- Limitar los comandos a operaciones allowlisted; el LLM no debe poder formar comandos shell arbitrarios.

**Criterio de aceptación**

Una ejecución de principio a fin inicia y finaliza la API de forma controlada, sin dejar procesos o contenedores huérfanos tras éxito, timeout o error.

### Paso 6. Integrar k6 para dos escenarios iniciales

**Trabajo**

- Integrar `grafana/mcp-k6` como proveedor secundario y reutilizar sus capacidades de documentación, generación de escenarios, validación y ejecución desde `PerfAgent.Adapters.K6Mcp`; no reimplementar plantillas genéricas que ya cubre.
- Copilot solo invoca `perf-lab/run_k6_test`. El adaptador valida que el destino corresponda al API iniciado o a un destino permitido, comprueba límites de VUs/duración y conserva los parámetros del escenario antes de delegar al `run_script` comunitario (o fallback controlado a k6 CLI).
- La validación upstream de scripts solo ocurre después de comprobar los destinos declarados y aplicar la política de ejecución, porque una validación mínima puede generar tráfico.

1. **Smoke test:** comprobar respuesta, status esperado y validación funcional mínima.
2. **Baseline/load reducido:** carga constante o escalonada simple, duración y concurrencia configurables con límites conservadores.

- Generar el script a partir de OpenAPI o de un escenario declarado.
- Permitir solo métodos/endpoints permitidos; las operaciones de escritura requieren datos y autorización explícitos.
- Definir thresholds mediante configuración, sin asumir un SLO empresarial universal.
- Guardar script, versión de k6, parámetros, stdout/stderr saneados, resumen de métricas y resultado.
- Separar error funcional, error de red y fallo del propio generador.

**Criterio de aceptación**

El agente valida el script, ejecuta ambos escenarios, detecta una respuesta funcional inválida y genera datos que se pueden comparar con una ejecución posterior.

### Paso 7. Recopilar métricas básicas de .NET

**Trabajo**

- Usar `pedrosakuma/dotnet-diagnostics` como proveedor principal de investigación del runtime a través de `PerfAgent.Adapters.DotNetDiagnosticsMcp`. La fachada comienza con `inspect_process(view="preflight")` y `inspect_process(view="triage")`; después, según los síntomas, delega en `collect_events`, `collect_sample` o `collect_batch`. Usar `start_investigation` y `compare_to_baseline` si aportan valor al caso.
- No recrear inicialmente sus analizadores de EventPipe/CLR ni duplicar su triage: normalizar sus salidas estructuradas dentro de `RuntimeSnapshot` y `DiagnosticReport`.
- Fijar versión y verificar compatibilidad con el runtime objetivo. El proyecto se enfoca en .NET 10 y declara compatibilidad validada para procesos .NET 8/9; no asumir soporte para .NET 6. Si el API objetivo no es compatible, ejecutar un spike con `timheuer/dotnet-profiler-mcp` o las herramientas oficiales .NET a través del adaptador, documentando la cobertura.
- Restringir la investigación MVP al proceso de laboratorio y señales de bajo riesgo. No habilitar por defecto análisis sensible de heap, dumps, captura de parámetros de métodos ni herramientas de attach cloud/Kubernetes.
- Recopilar métricas del proceso durante la prueba: CPU, memoria/heap, GC y señales disponibles de ThreadPool.
- Guardar timestamps para correlacionar con las ventanas de k6.
- Agregar captura de traza de CPU bajo demanda si la compatibilidad y los permisos del entorno están verificados.
- Verificar compatibilidad en una prueba técnica con las versiones de runtime elegidas antes de depender de un servidor MCP comunitario.
- No capturar dumps automáticamente en el MVP; pueden contener secretos, tokens, datos personales u otro contenido sensible.

**Criterio de aceptación**

El informe correlaciona las métricas de k6 con las del proceso en intervalos comparables, o indica con claridad qué señales no fue posible recopilar. La prueba debe demostrar que `PerfAgent.Adapters.DotNetDiagnosticsMcp` delegó en el proveedor comunitario fijado y que el PID corresponde al proceso de laboratorio.

### Paso 8. Crear una API de laboratorio con fallos conocidos

**Trabajo**

Construir un laboratorio pequeño, reproducible y de datos sintéticos con escenarios aislados, por ejemplo:

- Endpoint normal de referencia.
- Endpoint intensivo en CPU.
- Endpoint con asignaciones de memoria excesivas.
- Endpoint con espera/bloqueo síncrono controlado.
- Endpoint que simula una dependencia lenta o con errores.

Cada fallo debe poder activarse individualmente y tener un resultado esperado documentado. Evitar hacer que las pruebas cuelguen indefinidamente o consuman recursos sin límites.

**Criterio de aceptación**

Las pruebas de integración confirman que los síntomas esperados aparecen con la carga configurada y que el agente no afirma detectar una causa que no puede sustentar con los datos recogidos.

### Paso 9. Implementar triage por reglas y una investigación de seguimiento

**Trabajo**

- Definir reglas interpretables para señales como CPU alta, errores, latencia creciente, aumento de memoria y señales de cola/bloqueo.
- Cada regla debe incluir: observación, hipótesis posibles, evidencia requerida, señales contradictorias y siguiente acción recomendada.
- El agente puede seleccionar una investigación adicional (por ejemplo, capturar una traza) cuando los datos iniciales la justifiquen.
- Usar el LLM para interpretar el código y redactar el diagnóstico, pero obligarlo a referenciar observaciones y artefactos concretos.
- Si no hay información suficiente, el resultado debe ser `inconclusive` y debe indicar qué dato falta.

**Criterio de aceptación**

En el laboratorio, el agente distingue “síntoma observado” de “causa confirmada” y explica la evidencia que respalda cada conclusión.

### Paso 10. Generar informe y cerrar el ciclo

**Trabajo**

Generar Markdown y JSON con:

- Identificador, fecha y estado de ejecución.
- Proyecto, commit/revisión cuando esté disponible, runtime y versiones de herramientas.
- Perfil del ambiente y matriz de fidelidad por dependencia.
- Escenarios, carga y thresholds.
- Latencias p50/p95/p99 cuando estén disponibles, throughput y errores.
- Métricas del runtime y referencias a artefactos de profiling.
- Observaciones, hipótesis, evidencias, nivel de confianza cualitativo y limitaciones.
- Próximos experimentos y recomendaciones.
- Limpieza realizada y recursos que no pudieron cerrarse.

**Criterio de aceptación del MVP**

En la API de laboratorio, una sola orden genera build, arranque, smoke test, carga limitada, métricas de runtime e informe; se puede repetir la ejecución y comparar resultados; los errores de ambiente no se presentan como problemas de rendimiento de la aplicación.

### Alcance que se excluye deliberadamente del MVP

- Pruebas intensivas o sin límites, especialmente contra producción.
- Compatibilidad universal con todas las soluciones .NET y sistemas operativos.
- Provisionamiento autónomo de recursos cloud.
- Emulación automática de identidad administrada o federación OIDC.
- Descubrimiento y simulación automática de cualquier dependencia.
- Dashboard completo e integración con todos los sistemas de observabilidad.
- Aplicación automática de cambios de código.
- Afirmar causa raíz cuando solo existe correlación o evidencia incompleta.

---

## 6. Fase 2 — Diagnóstico adaptativo, ambientes híbridos/remotos y corrección verificada

### Objetivo de la fase 2

Extender el MVP hasta cubrir el alcance completo: diferentes soluciones .NET, tipos de carga, perfiles de ambiente, identidades federadas y servicios privados; investigación adaptativa apoyada en telemetría; correcciones aisladas y validación reproducible del resultado.

### Paso 1. Ampliar el descubrimiento y el modelo de aplicaciones

- Soportar soluciones con varios proyectos, múltiples `TargetFrameworks`, workers y servicios dependientes.
- Analizar OpenAPI con seguridad: rutas, parámetros, esquemas y autenticación declarada.
- Detectar Docker Compose, contenedores, configuración de despliegue y archivos de infraestructura cuando existan.
- Añadir adaptadores para escenarios de ejecución como Docker, Kubernetes y Azure App Service/AKS, según el orden de prioridad definido.
- Construir un grafo de dependencias y no asumir que un proyecto equivale a una única API.

**Salida:** plan de ejecución generado y revisable que declare los proyectos, dependencias, puertos, perfiles y permisos que requiere.

### Paso 2. Completar el motor de fidelidad de ambientes

Desarrollar estos perfiles:

- **Local aislado:** dependencias locales o simuladas.
- **Local híbrido:** API local con dependencias remotas reales, accesibles y explícitas.
- **Runner remoto:** las pruebas y/o la API se ejecutan desde un entorno con conectividad autorizada a servicios privados.
- **Rendimiento representativo:** recursos, topología, identidad, configuración y generador k6 se aproximan al ambiente objetivo según una matriz declarada.

Para cada dependencia, comparar las dimensiones de fidelidad:

- Identidad/autorización.
- DNS, rutas, TLS y conectividad privada.
- Tipo y versión del servicio.
- Datos y volumen.
- Recursos, límites, cuotas y configuración.
- Latencia, distancia de red y capacidad del generador de carga.

El agente debe sugerir el perfil menos costoso que responda a la pregunta investigada, sin presentar una aproximación como equivalente al ambiente real.

**Salida:** reporte de preflight y nivel de fidelidad por dimensión, con requisitos que faltan y una decisión explícita de continuar, degradar el alcance o bloquear.

### Paso 3. Añadir identidad federada con un modelo de seguridad explícito

Separar dos recorridos:

1. **Cliente de carga → API:** token usado por k6, audiencia, emisor, permisos, expiración y forma autorizada de obtenerlo.
2. **API → dependencias:** identidad con la que la aplicación accede a Key Vault, SQL, Storage, Service Bus u otros servicios.

Implementar adaptadores para los escenarios requeridos, priorizando según el ecosistema real:

- Credenciales de desarrollo autorizadas para la ejecución local.
- Identidad administrada cuando la aplicación se ejecuta en Azure.
- Federación de identidad de carga de trabajo/OIDC en plataformas compatibles.
- Mecanismos de credenciales aprobados para runners de CI.

El agente no debe fabricar, robar, copiar de procesos ajenos ni imprimir tokens o secretos. Debe pedir que el usuario configure los permisos y el mecanismo de identidad admitido por la organización. Las pruebas de integración que dependan de una identidad administrada o de federación real deben ejecutarse en un contexto donde esa identidad exista.

**Salida:** prueba de identidad por recorrido, clasificación de errores de autenticación/autorización y evidencia saneada. Un fallo de identidad no se clasifica automáticamente como cuello de botella de rendimiento.

### Paso 4. Extender escenarios de k6 y modelar tráfico realista

- Añadir load, stress, spike y soak tests con límites y autorización configurables.
- Admitir modelos por usuarios virtuales y por tasa de llegadas; seleccionar según el objetivo y registrar el modelo elegido.
- Admitir secuencias de llamadas, datos sintéticos, autenticación, correlación de IDs, headers y precondiciones funcionales.
- Añadir thresholds por endpoint y escenario, y baselines versionados por ambiente.
- Detectar cuándo se satura el generador de carga y cuándo el resultado deja de representar la capacidad de la API.
- Comparar repeticiones y rechazar comparaciones que difieran materialmente en carga, configuración, ambiente o versión.

**Salida:** escenarios versionados y un resumen de calidad/validez de la medición antes de interpretar los percentiles.

### Paso 5. Profundizar el diagnóstico y la observabilidad

Ampliar los adaptadores para recopilar, bajo demanda:

- Counters de runtime, GC y asignaciones.
- Trazas de CPU y allocation profiling.
- ThreadPool, colas, contención y stacks relevantes.
- Dumps de memoria con aprobación explícita, almacenamiento restringido y política de retención.
- Trazas distribuidas y tiempos de dependencias mediante OpenTelemetry u otros sistemas aprobados.
- Métricas de base de datos, pool de conexiones y consultas lentas donde se disponga de acceso autorizado.
- Telemetría de plataformas como Azure Monitor/Application Insights, Prometheus/Grafana o Instana mediante adaptadores opcionales.

No exigir instrumentación de código para toda investigación: comenzar con datos del runtime cuando sea suficiente y utilizar instrumentación adicional cuando la pregunta lo requiera.

**Salida:** timeline común para correlacionar la ventana de k6 con el proceso .NET y las dependencias.

### Paso 6. Implementar el ciclo adaptativo de hipótesis

El agente debe operar como un ciclo acotado:

1. Formular el síntoma que se intenta explicar.
2. Enumerar las hipótesis plausibles y la evidencia que distingue unas de otras.
3. Elegir la captura o prueba menos costosa que aporte información.
4. Ejecutar la herramienta autorizada y validar la calidad del resultado.
5. Actualizar las hipótesis con evidencia a favor y en contra.
6. Repetir hasta confirmar una causa, agotar el presupuesto autorizado o concluir que se requiere intervención humana.

Cada hipótesis tendrá estado (nueva, apoyada, contradicha, confirmada o sin resolver), evidencias y contrapruebas asociadas. Definir límites de tiempo, consumo, número de iteraciones y herramientas por ejecución para impedir ciclos interminables.

**Salida:** explicación reproducible que permita a otra persona comprobar el razonamiento desde los artefactos, sin depender exclusivamente de la conversación con el LLM.

### Paso 7. Añadir correcciones aisladas y validación antes/después

- Crear una rama, worktree o copia de trabajo aislada.
- Ejecutar tests funcionales y de seguridad antes del cambio.
- Proponer una modificación pequeña y ligada a la hipótesis confirmada.
- Requerir aprobación antes de editar archivos o añadir dependencias.
- Aplicar un cambio por experimento cuando sea posible.
- Recompilar, ejecutar pruebas funcionales y repetir el escenario de performance en condiciones comparables.
- Comparar latencia, throughput, errores y señales de consumo; detectar regresiones.
- Revertir o dejar el cambio como propuesta si no mejora los objetivos o si rompe funcionalidad.

**Salida:** diff del cambio, pruebas ejecutadas, métricas antes/después y decisión explicada. Nunca declarar que una optimización funcionó solo porque el código compila.

### Paso 8. Construir persistencia, historial e informes comparativos

- Almacenar metadatos de ejecución, métricas normalizadas, hipótesis y referencias a artefactos.
- Gestionar secretos por referencia, no almacenarlos en informes ni contexto del modelo.
- Retener artefactos grandes —trazas y dumps— con políticas de acceso y caducidad.
- Comparar dos revisiones, dos perfiles o una ejecución anterior con la actual.
- Añadir tendencia histórica y exportación Markdown/JSON; construir dashboard solo cuando los flujos y métricas estén estabilizados.

**Salida:** cualquier informe puede relacionarse con su ejecución, revisión de código y entorno original.

### Paso 9. Integrar con CI/CD de forma gradual

- Crear un modo no interactivo para ejecutar pruebas aprobadas en pipelines.
- Usar un runner alojado en la red que corresponda cuando haya dependencias privadas.
- Introducir gates de performance solo para escenarios estables y repetibles.
- Diferenciar regresiones de producto, fallos del ambiente y fallos del generador de carga.
- Guardar artefactos y configurar límites de recursos y concurrencia.
- Mantener la carga de estrés fuera de pipelines frecuentes; ejecutar soak/stress en un proceso separado y autorizado.

**Salida:** una integración de CI que falla por criterios medibles y no por interpretaciones libres del LLM.

### Paso 10. Evaluar calidad, seguridad y robustez

Crear un conjunto de evaluación con fallos conocidos y casos difíciles:

- CPU-bound, presión de GC y crecimiento de memoria.
- Bloqueos o starvation de ThreadPool.
- Dependencia lenta, error de conexión y timeout.
- Problemas de pool/concurrencia en acceso a datos.
- Error de autenticación/autorización.
- Dependencia inaccesible desde local.
- Mock demasiado rápido que podría ocultar la causa real.
- Generador k6 saturado.
- Datos insuficientes o señales contradictorias.

Medir: porcentaje de diagnósticos sustentados, falsas afirmaciones de causa raíz, calidad de la evidencia, tasa de experimentos útiles, mejoras confirmadas, regresiones y coste por ejecución. Incluir pruebas que deben acabar en `inconclusive` para evitar premiar una respuesta segura en apariencia pero sin evidencia.

**Salida:** criterios objetivos de calidad antes de activar diagnóstico autónomo o cambios de código a mayor escala.

---

## 7. Estrategia de ambientes e identidades: reglas transversales

| Caso encontrado por preflight | Comportamiento esperado |
|---|---|
| Dependencia localizable y reproducible | Levantarla con configuración y datos de prueba controlados. |
| Dependencia HTTP simulable y fuera del objetivo de medición | Usar un mock declarado, registrar su comportamiento y limitar las conclusiones a la API aislada. |
| Dependencia requerida y accesible de forma remota | Verificar conectividad, permisos y endpoint seguro; usarla con carga conservadora. |
| Dependencia privada inaccesible localmente | Cambiar a un runner autorizado, simularla si la pregunta lo permite o detener esa parte del diagnóstico. |
| Identidad de producción no disponible localmente | No intentar reproducirla extrayendo secretos; usar credencial de desarrollo o ejecutar la integración en un contexto autorizado. |
| Error de autenticación o DNS antes de generar carga válida | Reportar fallo de preflight, no producir una conclusión de performance. |
| Ambiente incompleto o con fidelidad parcial | Ejecutar solo las pruebas válidas para ese perfil y documentar limitaciones por dependencia. |
| Destino o acción no autorizados | Bloquear antes de iniciar tráfico. |

### Ejemplo de manifiesto conceptual

```yaml
profile: local-hybrid
api:
  project: src/Orders.Api/Orders.Api.csproj
  configuration: Release
  mode: local

load:
  engine: k6
  scenario: baseline
  maxVUs: 20
  duration: 2m

dependencies:
  sql:
    mode: container
    data: synthetic
  pricing-api:
    mode: mock
    contract: contracts/pricing.json
  inventory-api:
    mode: remote
    required: true
    authentication: developer-credential

diagnostics:
  runtime-counters: true
  cpu-trace: on-demand
  memory-dump: approval-required

safety:
  production-targets: deny
  write-operations: deny
  secrets-in-model-context: deny
```

Este manifiesto ilustra el diseño; antes de ejecutarlo deben validarse el esquema, la política de credenciales y los comandos permitidos. El modelo nunca debe recibir valores de secretos como parte de su contexto.

## 8. Integración explícita de herramientas comunitarias

Estas herramientas forman parte del diseño de la solución. No se consideran solo referencias ni trabajo futuro: el MVP debe instalarlas, conectar `PerfAgent.Mcp` con ellas como proveedores secundarios, exponer sus capacidades aprobadas mediante herramientas tipadas de la fachada y demostrar el uso real en pruebas de integración. El custom agent de Copilot no recibe acceso directo a sus herramientas genéricas; recibe solo `perf-lab/*`.

| Proyecto | Rol en la solución | Integración en el MVP | Evolución en fase 2 |
|---|---|---|---|
| **[Grafana `mcp-k6`](https://github.com/grafana/mcp-k6)** | Proveedor MCP para k6: documentación, validación, ejecución de pruebas y apoyo a la generación de escenarios. | Arrancarlo como proveedor secundario de `PerfAgent.Adapters.K6Mcp`. La fachada utiliza `list_sections`, `get_documentation`, `validate_script` y `run_script` tras aplicar allowlists y controles de ejecución. Copilot ve únicamente `perf-lab/run_k6_test` y las herramientas de escenario. | Mejorar aislamiento/red, cancelación, telemetría y comparación de escenarios. Mantener k6 CLI como fallback si el MCP experimental no supera compatibilidad/seguridad. |
| **[`dotnet-diagnostics`](https://github.com/pedrosakuma/dotnet-diagnostics)** | Proveedor principal de diagnóstico .NET con MCP, CLI, capturas de eventos y muestreo. | Arrancarlo como proveedor secundario de `PerfAgent.Adapters.DotNetDiagnosticsMcp`; delegar `inspect_process`, `collect_events`, `collect_sample`, `collect_batch`, `start_investigation` y `compare_to_baseline` según la versión. Validar PID/capacidad antes de cada llamada y normalizar las salidas. | Explorar capturas persistentes, trazas más profundas, comparaciones históricas y capacidades cloud/sidecar solo si se requieren y tienen permisos expresos. |
| **[`dotnet-profiler-mcp`](https://github.com/timheuer/dotnet-profiler-mcp)** | Alternativa/fallback que envuelve `dotnet-trace`, `dotnet-counters` y `dotnet-dump`. | No instalar por defecto junto al proveedor anterior. Evaluarlo si la prueba de compatibilidad detecta que `dotnet-diagnostics` no cubre runtime/SO. | Adoptarlo solo para una brecha demostrada, tras comparación y revisión de riesgos de dumps. |
| **[Performance Lab for .NET API Performance Testing](https://thecodeman.net/posts/building-mcp-server-in-dotnet)** | Referencia de diseño para un laboratorio y una colección de herramientas MCP; usa un generador de carga propio en C#. | Consultar como referencia, sin añadirlo como dependencia ni reemplazar k6. | Tomar ideas de UX, comparación y reportes si se demuestran útiles; evitar mantener dos motores de carga sin necesidad. |

### Contrato de integración y pruebas requeridas

Para cada proveedor comunitario:

1. Registrar repositorio, versión/tag o commit, licencia, runtime/SO soportados y método de instalación. Fijar versión de paquete o digest de imagen; evitar `latest` en ejecuciones de equipo/CI.
2. Consultar el catálogo de herramientas MCP real (`tools/list`) al arrancar o durante la prueba de integración; no asumir que los nombres permanecen idénticos entre releases.
3. Crear pruebas de contrato para parámetros, salidas estructuradas, errores, timeout, cancelación y comportamiento cuando el proceso objetivo no está disponible.
4. Comprobar permisos. No exponer herramientas que puedan inspeccionar procesos arbitrarios, capturar memoria sensible o ejecutar carga contra URLs arbitrarias sin protección técnica fuera del prompt.
5. Normalizar las salidas comunitarias al modelo interno (`LoadTestRun`, `RuntimeSnapshot`, `Hypothesis`, `DiagnosticReport`) para no acoplar el agente a una API de proveedor.
6. Mantener un fallback: si un servidor comunitario no está disponible, declarar el diagnóstico parcial o usar el adaptador oficial aprobado; no inventar resultados ni fallar silenciosamente.

**Criterio de aceptación:** una prueba automatizada demuestra que Copilot invoca herramientas `perf-lab/*` que delegan realmente en proveedores comunitarios: consulta/genera/valida capacidades k6, inicia la prueba mediante el ejecutor limitado, inspecciona el proceso .NET correcto, recopila evidencia y genera un informe. Los artefactos identifican proveedor upstream, versión y herramienta invocada. Las herramientas upstream genéricas no están disponibles directamente en el allowlist de Copilot.

Documentación complementaria:

- [Custom agents en VS Code](https://code.visualstudio.com/docs/agent-customization/custom-agents)
- [Servidores MCP en VS Code](https://code.visualstudio.com/docs/agent-customization/mcp-servers)
- [Referencia de configuración MCP en VS Code](https://code.visualstudio.com/docs/agents/reference/mcp-configuration)
- [Instrucciones personalizadas de GitHub Copilot](https://docs.github.com/en/copilot/how-tos/configure-custom-instructions-in-your-ide/add-repository-instructions-in-your-ide)
- [Documentación de k6](https://grafana.com/docs/k6/latest/)
- [Guías de pruebas de k6](https://grafana.com/docs/k6/latest/testing-guides/)
- [Diagnóstico de aplicaciones .NET](https://learn.microsoft.com/dotnet/core/diagnostics/)
- [Identidades administradas para recursos de Azure](https://learn.microsoft.com/entra/identity/managed-identities-azure-resources/overview)
- [Federación de identidades de carga de trabajo en Microsoft Entra](https://learn.microsoft.com/entra/workload-id/workload-identity-federation)

## 9. Decisiones técnicas que se deben cerrar al comenzar

Estas decisiones se deben resolver en el primer paso técnico y registrar como ADRs cortos; no hace falta diseñar toda la plataforma por adelantado.

1. **Plataforma principal:** GitHub Copilot en VS Code, Agent Mode, mediante un custom agent de workspace en `.github/agents/performance-diagnostics.agent.md`. No construir un frontend propio en el MVP.
2. **Contratos y controles:** un servidor `PerfAgent.Mcp` propio expone operaciones del laboratorio con entradas validadas, límites y salidas JSON. El modelo no debe tener acceso irrestricto a shell.
3. **Integración de k6:** incorporar `grafana/mcp-k6` desde el MVP para documentación/validación y reutilizar su ejecución a través de una frontera controlada solo si se pueden aplicar allowlists; mantener el CLI como opción interna para escenarios validados.
4. **Diagnóstico .NET:** incorporar `pedrosakuma/dotnet-diagnostics` como proveedor principal en el MVP; hacer un spike de compatibilidad por runtime/SO y activar `dotnet-profiler-mcp` o herramientas oficiales únicamente si existe una brecha demostrada.
5. **Modelo de estado:** persistir cada ejecución como un artefacto JSON versionado antes de implementar una base de datos.
6. **Motor de reglas:** empezar con reglas transparentes y permitir al agente de Copilot seleccionar una herramienta de seguimiento dentro de un catálogo permitido.
7. **Estrategia de prueba:** usar primero la API de laboratorio y no conectar el agente a servicios de negocio reales hasta validar los controles de seguridad y las políticas de red.
8. **Alcance de Copilot:** confirmar desde el inicio que el entorno objetivo es VS Code Agent Mode. Si más adelante se desea GitHub Copilot coding agent en GitHub.com, hacer una evaluación separada de disponibilidad de procesos, redes, MCP y credenciales.

## 10. Plan de validación y puertas de salida

### Puerta MVP → Fase 2

No avanzar hasta cumplir lo siguiente:

- [ ] El agente aparece y puede seleccionarse en GitHub Copilot Chat de VS Code.
- [ ] `.github/agents/performance-diagnostics.agent.md`, las instrucciones y la skill están versionadas y se cargan en un chat nuevo.
- [ ] Copilot invoca solo `perf-lab/*`; las pruebas de contrato demuestran que la fachada delega en `grafana/mcp-k6` y `pedrosakuma/dotnet-diagnostics` (o un fallback documentado con causa técnica).
- [ ] Las versiones de proveedores comunitarios están fijadas y sus contratos MCP tienen pruebas de integración.
- [ ] El catálogo de herramientas del agente está limitado al flujo del diagnóstico; el control real se valida en el servidor, no solo en el prompt.
- [ ] Build y arranque de la API automatizados y controlados.
- [ ] Smoke test y carga limitada ejecutados con k6.
- [ ] Resultados de k6 correlacionados temporalmente con métricas del runtime recogidas mediante el proveedor comunitario.
- [ ] La ejecución genérica de k6 no puede saltarse la validación de destino, límites de carga y confirmaciones definidas en el perfil.
- [ ] La API de laboratorio produce síntomas conocidos y las pruebas automatizadas los reproducen.
- [ ] El informe identifica qué dependencias son reales o simuladas.
- [ ] El agente diferencia error de preflight, síntoma e hipótesis de causa raíz.
- [ ] La ejecución deja el ambiente limpio tras éxito, fallo y cancelación.
- [ ] Límites de carga, destinos autorizados y saneamiento de secretos probados.
- [ ] Una segunda ejecución comparable produce un informe que puede contrastarse con el primero.

### Puerta de la fase 2 a uso operativo más amplio

- [ ] Perfiles locales e integrados/remotos validados en entornos autorizados.
- [ ] Identidad del cliente y de la API comprobadas por separado.
- [ ] El agente evalúa la fidelidad y las limitaciones antes de aceptar un resultado.
- [ ] Causas importantes sustentadas por artefactos reproducibles y señales contradictorias consideradas.
- [ ] Cambios de código aislados y aprobados, con pruebas funcionales y comparación antes/después.
- [ ] Retención, acceso y saneamiento de dumps, trazas y datos de prueba definidos.
- [ ] Los escenarios de evaluación cubren falsos positivos, datos insuficientes y fallos de ambiente.
- [ ] Integración CI/CD con gates deterministas, límites y separación de pruebas de carga intensiva.

## 11. Orden recomendado de implementación

Para aprender rápido y evitar construir demasiado antes de tener evidencia útil:

1. Scope, políticas de seguridad y ADRs mínimos.
2. Crear el custom agent de Copilot, instrucciones, skill mínima y configuración MCP del workspace.
3. Crear los contratos y un servidor MCP de laboratorio con herramientas stub y salida JSON.
4. Descubrimiento de una API, build, arranque, readiness y cleanup.
5. Perfil `local-isolated` y una API de laboratorio reproducible.
6. Smoke test y baseline/load sencillo con k6.
7. Integrar un servidor MCP de k6 y validar resultados estructurados.
8. Integrar diagnóstico básico .NET durante la prueba.
9. Informe JSON/Markdown y comparación entre ejecuciones.
10. Añadir reglas de triage y una herramienta de seguimiento bajo demanda.
11. Perfil `local-hybrid`, clasificación de dependencias y simulación declarada.
12. Evaluar falsos positivos y cumplir criterios de salida del MVP.
13. En fase 2, runner remoto y dependencias privadas.
14. Después, identidad federada, diagnóstico avanzado, correcciones verificadas, persistencia e integración CI/CD.

**Resultado esperado:** al terminar la fase 1 habrá un laboratorio pequeño pero real que puede demostrar una degradación, observar parte de su causa y emitir un informe confiable. La fase 2 convertirá esa base en un agente adaptativo capaz de investigar sistemas más complejos y ambientes corporativos con controles de seguridad y fidelidad explícitos.
