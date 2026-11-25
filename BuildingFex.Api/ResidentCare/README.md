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

## Contexto IA (RAG)

1. En Importación (Communications) aparece la carpeta fija **Contexto IA** (no se borra).
2. Sube `.txt`, `.md`, `.csv`, `.json` o `.pdf` ahí.
3. El servidor extrae texto → chunks → embeddings Gemini (`gemini-embedding-001`, 768 dims; los chunks indexados con `text-embedding-004` deben re-subirse para re-indexar) → tabla `ai_context_chunks`.
4. En soporte, el asistente recupera los chunks del edificio con similitud ≥ `Gemini:MinRelevanceScore` (o keyword si falla el embedding) + secciones de la guía de uso (`Domain/Services/AppUsageGuide.cs`). Sin fuentes → deriva sin llamar a la IA (`AnswerPolicy`). Con fuentes → responde citando `Fuente: <archivo>` (se guarda en `support_messages.cited_document_id/cited_source`).

**Tu config:** misma `GEMINI_API_KEY` / `Gemini:ApiKey` que ya usas. En Railway añade la variable si falta.

## Estados (US032)

`support_chats.status`: `open` (pendiente) · `answered` (atendido) · `derived` (chat IA derivado) · `closed`.
Admin responde → `answered`; residente escribe → `open`. Endpoints: `PATCH /api/v1/support/chats/{id}/status` (admin, body `{ status }`),
`PATCH /api/v1/support/chats/{id}/close`, `GET /api/v1/support/chats/summary` (contadores), `GET /api/v1/support/chats?status=pending|answered|closed`.

## Tope mensual (CON11)

Tabla `ai_usages` (owner_admin_id, period yyyy-MM hora Lima, query_count; único por owner+period). Cada respuesta de la IA consume 1
(UPDATE condicional atómico); si no se llega a usar la IA (sin fuentes, error del proveedor) se devuelve. Tope = `SubscriptionPlans.MonthlyAiQueryCap(plan)`.
Al alcanzarlo: 429 `AI_MONTHLY_CAP_REACHED` + derivación al chat admin (`chat` en el body). `GET /api/v1/support/ai-usage` (admin).

## Resiliencia (QAS05)

`Gemini:TimeoutSeconds` (3 s por llamada), 1 reintento con backoff ante 429/503, circuit breaker (`CircuitBreakerFailures`/`OpenSeconds`).
Fallas → 503 `AI_UNAVAILABLE` (o `GEMINI_API_KEY_MISSING`) sin guardar el mensaje: el cliente reintenta sin duplicar.
