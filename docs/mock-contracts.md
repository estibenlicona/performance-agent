# Contratos de mocks (`mode: mock`)

Un mock declarado sustituye a una dependencia HTTP **que está fuera del objetivo de la medición**. El informe lo marca
como **simulado** y limita las conclusiones a la API con ese comportamiento. Lo sirve `src/PerfAgent.MockServer`,
lanzado por el laboratorio en un puerto libre de 127.0.0.1; su URL se inyecta en la API con `injectAs`.

## Formato (JSON, versionado junto al manifiesto)

```json
{
  "name": "pricing-api",
  "version": 1,
  "routes": [
    {
      "method": "GET",
      "path": "/prices/{id}",
      "status": 200,
      "body": { "id": 1, "price": 12.5 },
      "latencyMs": { "min": 30, "max": 50 },
      "errorRate": 0.0,
      "errorStatus": 503
    }
  ]
}
```

| Campo | Significado |
|---|---|
| `method`, `path` | Ruta (plantilla de ASP.NET Core). `/__mock/*` está reservada. |
| `status`, `body` | Respuesta normal (cuerpo JSON estático). |
| `latencyMs` | Número fijo o `{min, max}` uniforme (≤ 10 s). |
| `errorRate`, `errorStatus` | Probabilidad de responder con `errorStatus` (0–1). |

## Medición

`GET /__mock/stats` devuelve, por ruta, peticiones, fallos y latencia media servida. La fachada toma una lectura antes
y otra después de cada prueba de carga: así el informe sabe cuántas llamadas hizo la API por petición, con qué
latencia y cuántos errores inyectó el mock (`DependencyCallStats`, fuente `mock-stats`). Esa aritmética es la que
permite confirmar un patrón N+1 o errores propagados.

## Limitación

La latencia y los errores son los **declarados**: un mock demasiado rápido puede ocultar la causa real. Para medir
contra la dependencia real usa `local-hybrid` con `mode: remote` (autorizado en la política).
