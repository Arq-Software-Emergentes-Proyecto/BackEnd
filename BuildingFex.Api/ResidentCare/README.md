# Resident Care (antes Support) — Chat IA + admin

Namespace raíz: `BuildingFex.Api.ResidentCare`. Las clases `SupportChat`, `SupportController`, `SupportHub`, las tablas `support_*` y el hub `/hubs/support` conservan su nombre (contratos).

## Modelo

| Tabla | Rol |
|-------|-----|
| `support_chats` | Cabecera: kind (`ai`\|`admin`), topic, status, `linked_chat_id` |
| `support_messages` | Filas append-only: `chat_id` FK, `author_role`, `body`, `created_at` |

**Escalado:** el chat AI **no** cambia de `kind`. Se crea una **segunda fila** `kind=admin` y ambas quedan ligadas por `linked_chat_id` (1 TX).

## Tiempo real

- **SignalR** hub `/hubs/support` (evento `chatUpdated`)
- Fallback: `GET /chats?since=` + `afterMessageId` solo si la pestaña está visible y SignalR no conectó

## Auth

- Bearer = App JWT o Firebase idToken (JWKS)
- SignalR: `?access_token=`
- AuthZ: admin solo su `ownerAdminId`; residente solo sus chats

## Estados (US032)

`support_chats.status`: `open` (pendiente) · `answered` (atendido) · `derived` (chat IA derivado) · `closed`.
Admin responde → `answered`; residente escribe → `open`. Endpoints: `PATCH /api/v1/support/chats/{id}/status` (admin, body `{ status }`),
`PATCH /api/v1/support/chats/{id}/close`, `GET /api/v1/support/chats/summary` (contadores), `GET /api/v1/support/chats?status=pending|answered|closed`.

## Resiliencia (QAS05)

El cliente de Gemini (`ResidentCare/Infrastructure/Gemini/GeminiClient.cs`) protege al chat cuando el proveedor falla:

- **Timeout** por llamada (`Gemini:TimeoutSeconds`, entre 1 y 120 s).
- **Reintento**: 1 vez, tras ~400 ms, ante 429/503.
- **Modelos alternativos**: si el modelo responde 404 se prueban `Gemini:FallbackModels` y se recuerda el que funcionó.
- **Circuit breaker**: tras `CircuitBreakerFailures` fallos seguidos (>=500 o timeout) se abre `CircuitBreakerOpenSeconds` y falla rápido sin llamar. El resumen de título es accesorio: sus fallos no cuentan.
- Los errores salen como `GeminiException` con códigos estables (`GEMINI_API_KEY_MISSING`, `GEMINI_EMPTY_PROMPT`, `GEMINI_UNAVAILABLE`, `GEMINI_TIMEOUT`, `GEMINI_REQUEST_FAILED`, `GEMINI_EMPTY_REPLY`, `GEMINI_BUSY`).
- La API devuelve **503 `AI_UNAVAILABLE`** (o `GEMINI_API_KEY_MISSING`) **sin guardar el mensaje**, para que el cliente reintente sin duplicar.

Configuración: `GEMINI_API_KEY` (o `Gemini__ApiKey`) y opcional `GEMINI_MODEL`. En local, en `appsettings.Local.json`; nunca en un commit.
