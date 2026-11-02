# SocialSpaces — Espacios comunes y reservas

**Responsable:** _asignar_

## Colecciones en db.json

- `socialSpaces`, `reservations`

## Frontend relacionado

- `Fronted-1/src/socialSpaces/infrastructure/spacesApi.js`
- `Fronted-1/src/socialSpaces/infrastructure/reservationsApi.js`

## Endpoints

| Ruta | Descripción |
|------|-------------|
| `/socialSpaces` | CRUD espacios (`rules`, `status` AVAILABLE/MAINTENANCE, `maxReservationsPerDay`) |
| `/reservations` | GET (excluye canceladas salvo `includeCancelled=true`), POST (201 / 409 traslape·aforo·cupo·mantenimiento), PATCH invitados, `POST /{id}/cancel`, DELETE = cancelar (`?purge=true` admin = borrado físico) |
| `GET /api/v1/guest-passes/{token}` | Público: `{valid, status: VALID/EXPIRED/CANCELLED, expiresAt, reservation, guests}` sin datos privados |
| `POST /api/v1/guest-passes/{token}/check-in` | Admin/portería: `{guestId?, checkedIn?}`; 410 `GUEST_PASS_EXPIRED` si el evento terminó hace más de 2 h |

## Estado (Fase 5)

- [x] Aggregates `SocialSpace`, `Reservation`
- [x] `SocialSpacesCompatController` — GET/POST/PATCH/DELETE + GET `/{id}`
- [x] `ReservationsCompatController` — GET (filtros), GET `/{id}`, POST, PATCH, DELETE
- [x] Validación de solapamiento en `POST /reservations`
- [x] Lookup público por `guestInviteToken` (sin `ownerAdminId`)
- [x] `DbJsonSocialSpacesSeeder`
- [x] Migración `20260610300000_SocialSpacesInitial`

## Reglas (Fase 6 — RF026/RF027, RNF009/QAS02)

- `TimeSlot` (VO) + `ReservationPolicy` (servicio de dominio): traslape, cupo diario, aforo (residente + invitados ≤ capacidad, máx. 5 invitados), mantenimiento.
- Creación atómica en `ReservationCommandService`: transacción READ COMMITTED + `SELECT … FOR UPDATE` sobre la fila del espacio + índice único `ux_reservations_owner_slot (owner_admin_id, slot_key)`; `slot_key` = `space|date|start` solo para reservas activas (NULL al cancelar).
- `Reservation.Status` CONFIRMED/CANCELLED; cancelar libera el turno y conserva el historial.
- Pase QR: un token por reserva (compartido por sus invitados, generado por el servidor), vence fin del evento + 2 h (`SocialSpaces:TimeZone`, default America/Lima). No se modeló un `GuestPass` por invitado para no romper `guests_json` ni los links ya compartidos.
