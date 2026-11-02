# Incidents — Incidencias

**Responsable:** _asignar_

## Frontend relacionado

- `Fronted-1/src/incidents/infrastructure/incidentsApi.js`
- `Fronted-1/src/incidents/domain/model/incident.entity.js`

## Endpoints (compat json-server)

También expuestos en `/api/v1/incidents` (diseño táctico).

| Método | Ruta | Rol |
|--------|------|-----|
| GET | `/incidents?residentId=&status=` | admin (todo el condominio) / residente (solo las suyas, filtrado en BD — RF018) |
| GET | `/incidents/{id}` | detalle + `events` (timeline, RNF010) |
| POST | `/incidents` | `title` (asunto) y `description` obligatorios (RF024) → `TITLE_REQUIRED` / `DESCRIPTION_REQUIRED` |
| PUT | `/incidents/{id}` | admin; pasar a `resolved` exige `resolutionComment` |
| PATCH | `/incidents/{id}/assign` | admin; `{ providerId }` (vacío = desasignar); `open` → `in-progress` |
| PATCH | `/incidents/{id}/resolve` | admin; `{ resolutionComment }` obligatorio → `RESOLUTION_COMMENT_REQUIRED` (400) |
| POST | `/incidents/{id}/comments` | admin o residente que reportó; comentario de seguimiento |
| DELETE | `/incidents/{id}` | admin |

Tiempo real: reutiliza `SupportHub` (`/hubs/support`). `incidentCreated` → grupo del admin;
`incidentUpdated` → admin + residente que reportó.

## Estados (persistidos → diseño táctico)

| Valor | IncidentStatus |
|-------|----------------|
| `open` | REPORTED |
| `in-progress` | ASSIGNED / IN_PROGRESS |
| `resolved` | RESOLVED |

Prioridad opcional: `low` / `medium` / `high` / `critical`. Historial en `incident_events`
(`reported`, `status_changed`, `assigned`, `unassigned`, `resolved`, `comment`).

## Fase 3 — completado

- [x] Aggregate `Incident` con `ExternalId` (compat json-server)
- [x] Repositorio + command/query services
- [x] `IncidentsCompatController`
- [x] Migración `IncidentsInitial`
- [x] Seed desde `Fronted-1/server/db.json`
- [x] `incidentsApi.js` funciona sin cambios

## Campos (API / db.json)

- `id`, `ownerAdminId`, `residentId`, `residentName`, `title`, `description`, `status`, `createdAt`, `provider`
- Nuevos: `providerId` (assigneeId → `service_providers.external_id`), `providerPhone`, `providerSpecialty`,
  `category`, `priority`, `resolutionComment`, `resolvedAt`, `updatedAt`, `events` (solo en el detalle)
