# Profiles — Perfil de negocio del administrador

## Rol

- **Firebase Auth** = solo identidad (`uid` + `idToken`).
- **BuildingFex.Api `/api/v1/profiles`** = **unica fuente de verdad** del perfil (DNI, empresa, RUC, direccion).
- **Firestore** ya no es fuente del perfil.

## Compuerta de rol (registro de condominios, RF015)

- El body de `POST /api/v1/profiles` **nunca** incluye `role`.
- Todo uid Firebase nuevo que completa su perfil queda como **admin de un condominio independiente**
  (tenant = su `users.id`) y arranca la prueba Free de 30 días (Billing). Antes solo el primer admin del
  sistema podía registrarse (`ADMIN_BOOTSTRAP_CLOSED`, ya no se devuelve).
- Un uid/email que ya pertenece a un residente no puede convertirse en admin.

## Edificios (US015 / TS001 / TS002)

Agregado `Building` (tabla `buildings`: name, address, department_count, owner_admin_id → users.id).
Fuera del plan Portafolio, un condominio tiene un solo edificio (409 `BUILDING_ALREADY_REGISTERED`).

| Metodo | Ruta | Auth | Uso |
|--------|------|------|-----|
| POST | `/api/v1/buildings` | AdminOnly | 201 + id; 400 ValidationProblem con `errors.name/address/departmentCount` |
| GET | `/api/v1/buildings/mine` | AdminOrResident | Edificios del tenant |
| GET | `/api/v1/buildings/{id}` | AdminOrResident | 403 si es de otro tenant |
| PUT | `/api/v1/buildings/{id}` | AdminOnly | Editar |
| GET | `/api/v1/buildings/{id}/residents` | AdminOnly | Padrón (por ahora = residentes del tenant) |

## Importación del padrón (US016)

| Metodo | Ruta | Auth | Uso |
|--------|------|------|-----|
| POST | `/api/v1/residents/import` | AdminOnly | multipart `file` (.xlsx/.csv). Valida todas las filas; si alguna falla → 400 con `errors[{row,column,code,message}]` y no crea nada. Si todo es válido crea todo en una transacción (CreateResidentCommand → token de invitación) |
| GET | `/api/v1/residents/import/template?format=xlsx\|csv` | AdminOnly | Plantilla |

Columnas: `nombre`*, `departamento`* (3–4 dígitos, piso = dígitos sin los 2 últimos), `piso`, `email`
(fija `invite_email`), `fecha_ingreso`, `facturar_desde_mes_siguiente`. Sin proveedor de correo: la respuesta
trae los `inviteToken` y el frontend muestra/exporta los enlaces para compartirlos.

## Endpoints

| Metodo | Ruta | Auth | Uso |
|--------|------|------|-----|
| POST | `/api/v1/profiles` | Bearer (Firebase o JWT) | Transaccion: asegura User IAM + upsert Profile |
| GET | `/api/v1/profiles/me` | Bearer | Perfil del token actual |
| GET | `/api/v1/profiles/{userId}` | Bearer | Perfil por uid |

## Persistencia (nombres alineados)

| Capa | Nombre |
|------|--------|
| Modulo | Profiles |
| Agregado | `Profile` |
| Tabla | `profiles` |
| API | `/api/v1/profiles` |

- `profiles.user_external_id` = **FK** unica a `users.external_id` (1:1 garantizado por la BD).
- `POST` envuelve User + Profile en **una sola transaccion**.
