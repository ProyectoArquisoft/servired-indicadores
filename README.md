# servired-indicadores

Microservicio de **indicadores de continuidad** de **SERVI·RED**, la plataforma colaborativa de reporte y
monitoreo de servicios públicos domiciliarios en Colombia (proyecto de *Arquitectura de Software*,
Pontificia Universidad Javeriana).

Consume los eventos que publican **Reportes** y **Moderación**, mantiene estadísticas agregadas por servicio,
zona y mes, y las expone como **datos abiertos** para ciudadanos, operadores y entes reguladores (SSPD / CRA).

| | |
|---|---|
| Tecnología | ASP.NET Core · .NET 10 · EF Core + Npgsql · RabbitMQ.Client 7 |
| Puerto | `8083` (solo red interna; se llega por el gateway en `:8080`) |
| Base de datos | PostgreSQL (Supabase), esquema propio `indicadores` |
| Contratos | [`servired-infra/contracts`](https://github.com/ProyectoArquisoft/servired-infra) **v0.1.0** |
| Estado | Funcional: API, consumidor de eventos, migraciones, Docker, CI y 33 pruebas en verde |

---

## Contenido

1. [Lugar en la arquitectura](#1-lugar-en-la-arquitectura)
2. [Estructura del repositorio](#2-estructura-del-repositorio)
3. [API HTTP](#3-api-http)
4. [Eventos consumidos](#4-eventos-consumidos)
5. [Cómo se calculan los indicadores](#5-cómo-se-calculan-los-indicadores)
6. [Modelo de datos](#6-modelo-de-datos)
7. [Seguridad y privacidad](#7-seguridad-y-privacidad)
8. [Configuración](#8-configuración)
9. [Desarrollo local](#9-desarrollo-local)
10. [Pruebas](#10-pruebas)
11. [Docker y CI/CD](#11-docker-y-cicd)
12. [Solución de problemas](#12-solución-de-problemas)
13. [Pendientes y decisiones por confirmar](#13-pendientes-y-decisiones-por-confirmar)

---

## 1. Lugar en la arquitectura

SERVI·RED usa **microservicios con una base de datos por servicio** y comunicación **híbrida**: asíncrona por
bus de eventos por defecto, y solo dos llamadas síncronas, ambas originadas en Moderación. Una de ellas llega
a este servicio (`/internal/indicadores/patrones`).

```mermaid
flowchart LR
    App[App móvil<br/>Flutter] -->|HTTPS| GW[API Gateway :8080]
    GW --> REP[Reportes<br/>Spring Boot]
    GW --> MOD[Moderación<br/>Spring Boot]
    GW -->|/api/indicadores*| IND[<b>Indicadores</b><br/>.NET :8083]

    REP -- reporte.* / validacion.* --> BUS[(RabbitMQ<br/>servired.events)]
    MOD -- fraude.* --> BUS
    BUS -->|indicadores.events| IND
    MOD -.->|síncrono, 2 s<br/>/internal/indicadores/patrones| IND

    IND --> DB[(PostgreSQL<br/>esquema indicadores)]
```

| Repositorio | Rol |
|---|---|
| `servired-infra` | Contratos OpenAPI y de eventos, docker-compose, scripts de Supabase |
| `servired-gateway` | Único punto de entrada; enruta y valida el JWT |
| `servired-reportes` | Cortes, restablecimientos y validaciones comunitarias. Publica `reporte.*` y `validacion.*` |
| `servired-moderacion` | Fraudes anónimos. Publica `fraude.*` y consulta `/patrones` |
| **`servired-indicadores`** | **Este repo** |
| `servired-app` | App del ciudadano (Flutter) |

---

## 2. Estructura del repositorio

```
servired-indicadores/
├── src/Servired.Indicadores/
│   ├── Program.cs                     # arranque: DI, migraciones, auth, health, rutas
│   ├── Api/
│   │   ├── IndicadoresEndpoints.cs    # las 3 rutas HTTP y su validación
│   │   ├── ExportadorCsv.cs           # CSV de datos abiertos (RFC 4180)
│   │   └── Problemas.cs               # errores RFC 7807 con títulos en español
│   ├── Configuracion/
│   │   └── ConexionBaseDatos.cs       # DB_URL (JDBC / URI / Npgsql) → cadena de conexión
│   ├── Datos/
│   │   ├── Entidades.cs               # IndicadorAgregado, EventoConsumido, Novedad
│   │   ├── IndicadoresDbContext.cs    # mapeo EF Core en snake_case
│   │   ├── Zona.cs                    # normalización de zona y periodo (hora de Colombia)
│   │   ├── FabricaDisenoDbContext.cs  # solo para `dotnet ef`
│   │   └── Migraciones/               # migraciones de EF Core
│   ├── Eventos/
│   │   ├── Contratos.cs               # DTOs de los eventos v1
│   │   ├── ProcesadorEventos.cs       # aplica un evento en una transacción (idempotente)
│   │   └── ConsumidorEventos.cs       # BackgroundService de RabbitMQ + health check
│   ├── Indicadores/
│   │   ├── RecalculadorIndicadores.cs # recalcula una fila de indicador_agregado
│   │   ├── ServicioPatrones.cs        # patrón histórico por radio para Moderación
│   │   └── Geo.cs                     # haversine y caja de coordenadas
│   ├── Seguridad/
│   │   └── AutenticacionSupabase.cs   # JWT de Supabase (JWKS o HS256) y rol
│   ├── appsettings.json
│   └── appsettings.Development.json   # valores para Postgres/RabbitMQ locales en Docker
├── tests/Servired.Indicadores.Tests/
│   ├── Unitarias/                     # conexión, periodo, geo, CSV, roles
│   ├── Integracion/                   # procesador y API contra Postgres real (Testcontainers)
│   └── Contratos/eventos/             # copia de los ejemplos de eventos de infra v0.1.0
├── .github/workflows/ci.yml           # build, pruebas y publicación de la imagen en GHCR
├── Dockerfile
├── .env.example
├── dotnet-tools.json                  # dotnet-ef
└── Servired.Indicadores.slnx
```

---

## 3. API HTTP

Contrato: `servired-infra/contracts/openapi/indicadores.yaml` (las rutas `/api/*` son las de
`servired-public.yaml`). Convenciones:

- JSON en camelCase.
- Paginación con `page` (desde 0) y `size` (1–100, por defecto 20); la respuesta es `{ items, page, size, total }`.
- Errores en formato **RFC 7807** (`application/problem+json`).

| Método | Ruta | RF | Acceso | Descripción |
|---|---|---|---|---|
| GET | `/api/indicadores` | RF9 | Pública | Indicadores paginados y filtrados |
| GET | `/api/indicadores/export` | RF8 | `OPERADOR`, `REGULADOR` | Exportación regulatoria en CSV o JSON |
| GET | `/internal/indicadores/patrones` | – | Red interna (Moderación) | Patrón histórico de una zona |
| GET | `/health` | – | – | Estado de Postgres y RabbitMQ |
| GET | `/swagger` | – | Solo en Development o con `Swagger__Habilitado=true` | Swagger UI |

### 3.1 `GET /api/indicadores`

| Parámetro | Tipo | Notas |
|---|---|---|
| `idServicio` | int64 | Opcional |
| `departamento` | string | Opcional; no distingue mayúsculas |
| `municipio` | string | Opcional; no distingue mayúsculas |
| `periodo` | `YYYY-MM` | Opcional; mes en hora de Colombia |
| `page`, `size` | int | Paginación |

```http
GET /api/indicadores?municipio=Bogotá D.C.&periodo=2026-10
```

```json
{
  "items": [
    {
      "idIndicador": 1,
      "idServicio": 1,
      "tipoServicio": "ENERGIA",
      "tipoNovedad": "CORTE",
      "departamento": "Cundinamarca",
      "municipio": "Bogotá D.C.",
      "periodo": "2026-10",
      "horasInterrupcionProm": 5.33,
      "frecuenciaCortes": 1
    }
  ],
  "page": 0,
  "size": 20,
  "total": 1
}
```

Orden: periodo descendente, luego departamento, municipio y servicio.

### 3.2 `GET /api/indicadores/export`

Requiere `Authorization: Bearer <JWT de Supabase>` con `app_metadata.rol` igual a `OPERADOR` o `REGULADOR`.

| Parámetro | Notas |
|---|---|
| `formato` | **Obligatorio**: `csv` o `json` |
| `idServicio`, `departamento`, `municipio`, `periodo` | Mismos filtros que la consulta pública |

La respuesta trae `Content-Disposition: attachment; filename="indicadores-<periodo|todos>.<formato>"`.

```csv
idIndicador,idServicio,tipoServicio,tipoNovedad,departamento,municipio,periodo,horasInterrupcionProm,frecuenciaCortes
1,1,ENERGIA,CORTE,Cundinamarca,Bogotá D.C.,2026-10,5.33,1
```

En `json` devuelve un arreglo de `IndicadorAgregado`, sin paginación.

### 3.3 `GET /internal/indicadores/patrones`

No la expone el gateway; solo la llama Moderación (timeout de 2 s y 1 reintento) para decidir sobre un reporte
de fraude.

| Parámetro | Notas |
|---|---|
| `idServicio` | Obligatorio |
| `lat` | Obligatorio, entre -90 y 90 |
| `lng` | Obligatorio, entre -180 y 180 |

```json
{
  "idServicio": 1,
  "radioMetros": 1000,
  "ventanaDias": 90,
  "fraudesValidados": 4,
  "fraudesRechazados": 1,
  "cortesReportados": 17
}
```

Cuenta las novedades del mismo servicio a menos de `radioMetros` del punto, en los últimos `ventanaDias`
días. Ambos valores se configuran (ver [§8](#8-configuración)).

### 3.4 `GET /health`

```json
{
  "estado": "Healthy",
  "componentes": {
    "postgres": { "estado": "Healthy", "description": null },
    "rabbitmq": { "estado": "Healthy", "description": null }
  }
}
```

| Situación | Estado | HTTP |
|---|---|---|
| Todo bien | `Healthy` | 200 |
| RabbitMQ caído o consumidor apagado (la API sigue sirviendo) | `Degraded` | 200 |
| Postgres caído | `Unhealthy` | 503 |

### 3.5 Errores

| Código | `title` | Cuándo |
|---|---|---|
| 400 | `Solicitud inválida` | Parámetros mal formados. Trae `errores: [{ campo, mensaje }]` |
| 401 | `No autenticado` | `/export` sin token o con un token inválido |
| 403 | `Sin permiso` | `/export` con un rol distinto de `OPERADOR` o `REGULADOR` |
| 500 | `Error interno` | Error inesperado |

```json
{
  "type": "about:blank",
  "title": "Solicitud inválida",
  "status": 400,
  "detail": "periodo: Debe tener el formato YYYY-MM. size: Debe estar entre 1 y 100.",
  "errores": [
    { "campo": "periodo", "mensaje": "Debe tener el formato YYYY-MM." },
    { "campo": "size", "mensaje": "Debe estar entre 1 y 100." }
  ]
}
```

---

## 4. Eventos consumidos

Contrato: `servired-infra/contracts/events/*.v1.schema.json`. Todos llegan en el sobre común:

```json
{
  "eventId": "uuid",
  "type": "reporte.creado",
  "version": 1,
  "occurredAt": "2026-10-26T13:05:00Z",
  "source": "reportes",
  "correlationId": "uuid",
  "data": { }
}
```

| Evento | Publica | `data` | Efecto en Indicadores |
|---|---|---|---|
| `reporte.creado` | Reportes | `idReporte, codigoTrazabilidad, idServicio, tipoServicio, severidad, departamento, municipio, lat, lng, fechaCreacion` | Suma un corte al indicador `CORTE` del servicio, zona y mes |
| `reporte.restablecido` | Reportes | `idReporte, idServicio, departamento, municipio, fechaCreacion, fechaResolucion, duracionInterrupcionHoras` | Registra la duración, de la que sale `horasInterrupcionProm` |
| `validacion.registrada` | Reportes | `idReporte, totalValidaciones` | Guarda el conteo; no cambia los indicadores |
| `fraude.validado` | Moderación | `idFraude, idServicio, lat, lng, fecha` | Alimenta `/patrones` (`fraudesValidados`) |
| `fraude.rechazado` | Moderación | `idFraude, idServicio, fecha` | Alimenta `/patrones` (`fraudesRechazados`, ver [§13](#13-pendientes-y-decisiones-por-confirmar)) |

### 4.1 Topología de RabbitMQ

Con `RabbitMq__DeclararTopologia=true` (valor por defecto), el servicio declara al arrancar (es idempotente):

| Recurso | Tipo | Detalle |
|---|---|---|
| `servired.events` | exchange topic, durable | Las routing keys son el nombre del evento |
| `servired.dlx` | exchange topic, durable | Exchange de errores (dead-letter) |
| `indicadores.events` | cola durable | Bindings `reporte.*`, `validacion.*`, `fraude.*`; `x-dead-letter-exchange=servired.dlx`, `x-dead-letter-routing-key=indicadores.events` |
| `indicadores.events.dlq` | cola durable | Ligada a `servired.dlx` con la routing key `indicadores.events` |

> ⚠ El `compose/rabbitmq/definitions.json` de `servired-infra` (hito H2) debe usar **exactamente** estos
> argumentos. Si difieren, RabbitMQ rechaza la declaración con `PRECONDITION_FAILED` y el consumidor no arranca.

### 4.2 Qué pasa con cada mensaje

```mermaid
flowchart TD
    M[Mensaje recibido] --> P{¿Cumple el contrato?}
    P -- No --> DLQ[nack → indicadores.events.dlq]
    P -- Sí --> D{¿eventId ya consumido?}
    D -- Sí --> ACK1[ack: Duplicado]
    D -- No --> TX[Transacción: novedad + recálculo + evento_consumido]
    TX -- OK --> ACK2[ack: Aplicado / SinEfecto]
    TX -- Error --> R{¿3 intentos agotados?}
    R -- No --> TX
    R -- Sí, BD caída --> REQ[nack con requeue: vuelve a la cola]
    R -- Sí, otro error --> DLQ
```

- **Idempotencia por `eventId`:** el registro en `evento_consumido` se guarda en la misma transacción que el
  cambio, así que una reentrega nunca cuenta dos veces.
- **Contrato estricto:** si falta un campo obligatorio o llega `null` donde no se permite, el evento es inválido
  (no se convierte en un `0` silencioso).
- **Reconexión automática:** si el broker no está disponible al arrancar, el servicio reintenta con espera
  creciente (2 s, 4 s… hasta 30 s) mientras la API sigue respondiendo.

---

## 5. Cómo se calculan los indicadores

Cada fila de `indicador_agregado` corresponde a una combinación de **tipo de novedad + servicio + departamento +
municipio + periodo**.

| Campo | Cálculo |
|---|---|
| `frecuenciaCortes` | Número de cortes (`reporte.creado`) de esa combinación |
| `horasInterrupcionProm` | Promedio de `duracionInterrupcionHoras` de los cortes ya restablecidos, redondeado a 2 decimales; `null` si ninguno se ha restablecido |
| `periodo` | Mes de `fechaCreacion` del corte en **hora de Colombia (UTC-5)**: un corte del 31 de octubre a las 22:00 cuenta en octubre |
| `departamento` / `municipio` | Tal como llegan en el evento (sin espacios sobrantes). Si llegan `null`, se agrupan como `Sin información` |

**Se recalcula en vez de sumar.** Cada evento vuelve a calcular su fila a partir de la tabla de detalle
`novedad`. Por eso el resultado es el mismo sin importar el orden de llegada. Por ejemplo, si llega
`reporte.restablecido` antes que `reporte.creado`, la duración se guarda y la fila se publica cuando llega
`reporte.creado` con el `tipoServicio`.

---

## 6. Modelo de datos

Esquema `indicadores`. Las migraciones de EF Core están en
[`Datos/Migraciones`](src/Servired.Indicadores/Datos/Migraciones) y se aplican al arrancar (se desactiva con
`Database__MigrarAlIniciar=false`). El historial de migraciones queda en `indicadores.__ef_migraciones`.

```mermaid
erDiagram
    indicador_agregado {
        bigint id_indicador PK
        bigint id_servicio
        varchar tipo_servicio
        varchar tipo_novedad
        varchar departamento
        varchar municipio
        char periodo
        float horas_interrupcion_prom
        int frecuencia_cortes
    }
    evento_consumido {
        uuid id_evento PK
        varchar tipo_evento
        bigint id_origen
        timestamptz fecha_consumo
    }
    novedad {
        bigint id_novedad PK
        varchar tipo_novedad
        bigint id_origen
        bigint id_servicio
        varchar tipo_servicio
        varchar departamento
        varchar municipio
        float lat
        float lng
        timestamptz fecha
        char periodo
        timestamptz fecha_resolucion
        float duracion_interrupcion_horas
        int total_validaciones
        varchar resultado_fraude
    }
```

| Tabla | Para qué | Índices |
|---|---|---|
| `indicador_agregado` | Lo que expone la API | Único `(tipo_novedad, id_servicio, departamento, municipio, periodo)`; `periodo` (tabla normal, sin TimescaleDB) |
| `evento_consumido` | Idempotencia por `eventId` | PK `id_evento` |
| `novedad` ⚠ | Detalle de cada corte o fraude. **No está en el modelo del SAD**: hace falta para `/patrones` (que cuenta por radio, no por municipio) y para recalcular promedios | Único `(tipo_novedad, id_origen)`; `(id_servicio, fecha)` |

`id_servicio` es una **referencia lógica** al catálogo de Reportes, no una llave foránea (una base de datos por
servicio).

---

## 7. Seguridad y privacidad

- **JWT de Supabase validado también aquí** (defensa en profundidad: el gateway ya lo validó). Se revisan la
  firma, la expiración, la audiencia `authenticated` y el emisor `<SUPABASE_URL>/auth/v1`. El rol se lee de
  `app_metadata.rol`.
- La firma se valida con `SUPABASE_JWKS_URL` (claves asimétricas) o con `SUPABASE_JWT_SECRET` (HS256). Si no hay
  ninguna de las dos, **toda ruta protegida responde 401**.
- **Anonimato verificable:** los eventos de fraude con campos extra (por ejemplo `idUsuario`) se rechazan y van a
  la cola de errores. La tabla `novedad` no guarda ningún dato de usuario.
- `/internal/*` no lleva autenticación: depende de que el gateway no la enrute y de que el servicio solo esté
  en la red interna del compose.
- Ningún secreto va en git: `.env` está en `.gitignore`. `appsettings.Development.json` solo trae credenciales de
  contenedores locales.
- El contenedor corre con un usuario sin privilegios (`$APP_UID` de la imagen oficial).

---

## 8. Configuración

Son las mismas variables que pone el compose de `servired-infra`. Ver [`.env.example`](.env.example).

| Variable | Obligatoria | Ejemplo / valor por defecto |
|---|---|---|
| `DB_URL` | Sí | `jdbc:postgresql://db.<proyecto>.supabase.co:5432/postgres?sslmode=require` |
| `DB_USER` | Sí | `svc_indicadores` |
| `DB_PASSWORD` | Sí | — |
| `DB_SCHEMA` | No | `indicadores` (se usa como `search_path`) |
| `RABBITMQ_URL` | No | `amqp://guest:guest@rabbitmq:5672`. Vacía: consumidor apagado y `/health` en `Degraded` |
| `SUPABASE_URL` | Para `/export` | `https://<proyecto>.supabase.co` |
| `SUPABASE_JWKS_URL` **o** `SUPABASE_JWT_SECRET` | Para `/export` | Según el tipo de clave del proyecto |

`DB_URL` acepta tres formatos: **JDBC** (el mismo que usan los servicios Spring), **URI** (`postgresql://…`) o
una **cadena de Npgsql**. `DB_USER` y `DB_PASSWORD` tienen prioridad sobre las credenciales que traiga la URL.

Opciones adicionales (en `appsettings.json` o como variables de entorno con `__`):

| Opción | Por defecto | Descripción |
|---|---|---|
| `Database__MigrarAlIniciar` | `true` | Aplica las migraciones al arrancar |
| `RabbitMq__DeclararTopologia` | `true` | Declara exchanges y colas (ver [§4.1](#41-topología-de-rabbitmq)) |
| `RabbitMq__Prefetch` | `10` | Mensajes sin confirmar por consumidor |
| `RabbitMq__Reintentos` | `3` | Intentos antes de devolver el mensaje a la cola o mandarlo a la DLQ |
| `Patrones__RadioMetros` | `1000` | Radio de `/patrones` |
| `Patrones__VentanaDias` | `90` | Días hacia atrás que considera `/patrones` |
| `Swagger__Habilitado` | `false` | Swagger UI fuera de Development |

**Supabase:**
- El rol `svc_indicadores` necesita permiso `CREATE` sobre su esquema para aplicar las migraciones.
- Con el *transaction pooler* (puerto 6543) no hay que cambiar nada: Npgsql ya trae `Max Auto Prepare=0` por defecto.
- Los proyectos gratuitos se pausan por inactividad: hay que reactivarlo antes de una demo.

---

## 9. Desarrollo local

**Requisitos:** .NET SDK 10 y Docker.

```bash
# 1. Postgres y RabbitMQ locales (appsettings.Development.json ya apunta a ellos)
docker run -d --name pg  -p 5432:5432 -e POSTGRES_PASSWORD=postgres postgres:16-alpine
docker run -d --name rmq -p 5672:5672 -p 15672:15672 rabbitmq:3.13-management-alpine

# 2. Ejecutar el servicio
dotnet run --project src/Servired.Indicadores
#    API:      http://localhost:8083
#    Swagger:  http://localhost:8083/swagger
#    RabbitMQ: http://localhost:15672 (guest / guest)
```

**Publicar un evento de prueba** desde la consola de RabbitMQ: en *Exchanges → servired.events → Publish
message*, usa la routing key `reporte.creado` y pega como payload el contenido de
`tests/Servired.Indicadores.Tests/Contratos/eventos/reporte.creado.example.json`. Luego:

```bash
curl "http://localhost:8083/api/indicadores"
curl "http://localhost:8083/internal/indicadores/patrones?idServicio=1&lat=4.6097&lng=-74.0817"
```

**Cambiar el modelo de datos:**

```bash
dotnet tool restore
dotnet ef migrations add <NombreDeLaMigracion> -p src/Servired.Indicadores -o Datos/Migraciones
```

---

## 10. Pruebas

```bash
dotnet test
```

| Tipo | Carpeta | Qué cubre |
|---|---|---|
| Unitarias (15) | `Unitarias/` | Formatos de `DB_URL`, periodo en hora de Colombia, haversine y caja, CSV con escape, lectura del rol |
| Integración (18) | `Integracion/` | Procesador de eventos (idempotencia, desorden, promedios, contrato estricto, anonimato) y API completa (paginación, 400/401/403, CSV/JSON, `/patrones`, `/health`) |

- Las de integración levantan un **Postgres real con Testcontainers**. Si Docker no está disponible **se saltan**
  en vez de fallar.
- El JWT de las pruebas se firma con HS256 y un secreto de prueba.
- El reloj está fijo en 2026-10-30, para que la ventana de 90 días no dependa del día en que se corran.
- Los ejemplos de [`Contratos/eventos`](tests/Servired.Indicadores.Tests/Contratos/eventos) son una **copia de
  `servired-infra` v0.1.0**: si cambia el contrato, hay que actualizarlos.

---

## 11. Docker y CI/CD

```bash
docker build -t servired-indicadores .
docker run -p 8083:8083 --env-file .env servired-indicadores
```

**Imagen:**
- Construcción multi-etapa (SDK de .NET 10 para compilar, ASP.NET 10 para ejecutar).
- Usuario sin privilegios.
- `HEALTHCHECK` contra `/health` cada 15 s. Sirve para `depends_on: condition: service_healthy` en el compose.

**CI** ([`.github/workflows/ci.yml`](.github/workflows/ci.yml)):

| Job | Cuándo | Qué hace |
|---|---|---|
| `test` | Cada push y PR | `dotnet restore`, `build` y `test` (el runner trae Docker, así que también corren las de integración) |
| `imagen` | Push a `main`, si `test` pasó | Construye y publica `ghcr.io/<org>/servired-indicadores` con los tags `<sha>` y `latest` |

El Dockerfile y el workflow sirven de base para `servired-infra/templates/dotnet` (hito H4).

---

## 12. Solución de problemas

| Síntoma | Causa probable | Qué hacer |
|---|---|---|
| `/health` en `Degraded` con "Sin conexión a RabbitMQ" | El broker no está arriba o `RABBITMQ_URL` es incorrecta | El servicio reintenta solo; revisa la URL y que el contenedor esté corriendo |
| El consumidor no arranca: `PRECONDITION_FAILED` | `definitions.json` declaró las colas con otros argumentos | Alinea los argumentos con [§4.1](#41-topología-de-rabbitmq) o usa `RabbitMq__DeclararTopologia=false` |
| Mensajes en `indicadores.events.dlq` | El evento no cumple el contrato v1 | Revisa el log `Evento inválido` y el payload en la consola de RabbitMQ |
| `/export` siempre responde 401 | Faltan `SUPABASE_JWKS_URL` o `SUPABASE_JWT_SECRET`, o el `iss` no coincide con `SUPABASE_URL` | Configura las variables de [§8](#8-configuración) |
| Al arrancar, el log muestra `Failed executing DbCommand … __ef_migraciones` | Es normal la primera vez: EF busca el historial antes de crearlo | Nada; luego aparece `Applying migration` |
| Error de permisos al migrar en Supabase | `svc_indicadores` no tiene `CREATE` en su esquema | Revisa `servired-infra/supabase/00_roles_and_schemas.sql` |
| Las pruebas de integración aparecen como *Skipped* | Docker no está corriendo | Abre Docker Desktop y vuelve a ejecutar `dotnet test` |

---

## 13. Pendientes y decisiones por confirmar

| # | Tema | Situación | Propuesta |
|---|---|---|---|
| 1 | ⚠ §11.4 del brief: zona de los fraudes | `fraude.validado` solo trae `lat/lng`, así que no hay filas `tipoNovedad=FRAUDE` en `/api/indicadores` | Que Reportes o Moderación resuelvan departamento y municipio (PostGIS o geocodificación inversa) y los agreguen al evento |
| 2 | ⚠ Ubicación de `fraude.rechazado` | v1 no trae `lat/lng`, así que `fraudesRechazados` en `/patrones` siempre da 0 | Agregar `lat/lng` en los contratos v0.2.0. El servicio ya los lee si llegan |
| 3 | ⚠ Tabla `novedad` | Adicional al modelo del SAD | Documentarla en `servired-infra/docs/data-model.md` |
| 4 | Topología de RabbitMQ | El servicio la declara | Que `definitions.json` (H2) use los mismos argumentos |
| 5 | Ejemplo inválido de infra | `fraude.validado.con-usuario.json` reutiliza el `eventId` del ejemplo válido, así que un consumidor idempotente lo descarta como duplicado antes de validarlo | Darle un `eventId` propio en infra |
| 6 | Tipo de JWT de Supabase (§11.5) | Soporta JWKS y HS256 | Confirmar cuál usa el proyecto y dejar solo esa variable |
