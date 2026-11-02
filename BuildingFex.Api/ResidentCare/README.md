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
