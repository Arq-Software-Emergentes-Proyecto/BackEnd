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

## Tope mensual (CON11)

Tabla `ai_usages` (owner_admin_id, period `yyyy-MM` en hora de Lima, query_count; única por owner + period). Cada respuesta de la IA
consume 1 con un UPDATE condicional atómico; si al final no se usa la IA (falla del proveedor) se devuelve. Tope =
`SubscriptionPlans.MonthlyAiQueryCap` del plan efectivo; en Portafolio se cuenta para toda la cuenta. Al alcanzarlo: 429
`AI_MONTHLY_CAP_REACHED` y la consulta se deriva a la administración (`chat` derivado en el body). Consumo para el admin:
`GET /api/v1/support/ai-usage`.

## Derivación a la administración (US044)

El chat IA se deriva (fila `kind=admin` ligada por `linked_chat_id`) cuando:
1. el residente lo pide ("pásame con el administrador", "hablar con la junta"…): sin IA;
2. es una emergencia (incendio, gas, inundación…): mensaje con los números de emergencia y derivación, sin IA;
3. no hay base documental: sin IA y sin descontar del tope;
4. el edificio alcanzó el tope mensual;
5. el modelo responde con `[[ESCALATE_ADMIN]]` en la última línea (el marcador se quita del texto).

## Contexto IA (RAG)

1. La carpeta fija **Contexto IA** (`isContext: true`) no puede eliminarse (`CONTEXT_FOLDER_PROTECTED`) y sus documentos vigentes alimentan al asistente.
2. Formatos indexables: `.txt`, `.md`, `.csv`, `.json` y `.pdf` (extracción de texto por página con PdfPig).
3. Pipeline de indexación: texto → chunks de hasta 900 caracteres (solape 120) → embeddings `gemini-embedding-001` de 768 dims → tabla `ai_context_chunks`.
4. Recuperación por edificio (`ownerAdminId`) sobre documentos `PUBLISHED` usando similitud coseno (`≥ Gemini:MinRelevanceScore`) o respaldo por palabras clave, más la guía de uso de BuildingFex. Con fuentes responde citando `Fuente: <archivo>`, que se guarda en `support_messages.cited_document_id` / `cited_source`.

Configuración de embeddings: misma `GEMINI_API_KEY` / `Gemini:ApiKey` (opcional `Gemini:EmbeddingModel`, `Gemini:EmbeddingDimensions`, `Gemini:MinRelevanceScore`).
