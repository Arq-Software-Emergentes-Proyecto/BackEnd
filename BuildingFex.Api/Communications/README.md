# Communications — Comunicados y documentos oficiales

**Responsable:** _asignar_

Bounded context que agrupa la comunicación oficial del edificio: los **comunicados**
(antes módulo `Information`) y los **documentos / carpetas oficiales** subidos por el
administrador (antes módulo `Import`). Namespace raíz: `BuildingFex.Api.Communications`.

> Solo se renombraron carpetas y namespaces. Las rutas HTTP, tablas, columnas, contratos
> JSON y variables de entorno se mantienen exactamente igual.

## Estructura

```
Communications/
├── Domain/Model/Aggregates/   ← Announcement, ImportFolder, ImportUpload
├── Domain/Model/Commands/     ← Create/Update/DeleteAnnouncementCommand
├── Domain/Repositories/       ← IAnnouncementRepository
├── Application/               ← servicios de comandos / consultas de comunicados
├── Infrastructure/
│   ├── Configuration/         ← ImportSettings, FirebaseStorageSettings
│   ├── Storage/               ← FirebaseStorageService (URLs firmadas)
│   ├── Persistence/           ← EF Core (ApplyCommunicationsConfiguration) + seed db.json
│   └── ImportOrphanCleanupWorker.cs
└── Interfaces/Rest/           ← AnnouncementsCompatController, ImportFolders/UploadsController
```

## Comunicados (announcements)

- Colección en db.json: `announcements`
- Endpoints: `GET/POST /announcements`, `PUT/DELETE /announcements/{id}`
- Campos clave: `title`, `body`, `priority`, `authorId`, `authorName`, `duration`, `expiresAt`, `ownerAdminId`,
  `status`, `publishedAt`, `isActive`, `priorityRank`
- Vigencia (RF019 / RNF011): el dominio calcula `expiresAt = createdAt + duration` (días) al crear **y** al editar;
  `createdAt`/`expiresAt` enviados por el cliente se ignoran. `duration` 0 en PUT conserva la vigencia actual.
- `GET /announcements`: un **residente** recibe solo vigentes (`expiresAt > now`, filtrado en BD). El admin recibe
  todos (los vencidos con `status = "expired"`), o solo vigentes con `?activeOnly=true` / `?includeExpired=false`.
- Orden (RF020): `urgent` > `important` > `normal`, luego fecha de publicación desc (constantes en
  `Domain/Model/AnnouncementConstants.cs` y `src/communications/domain/model/announcementConstants.js`).
- Estados: `published` (persistido, default), `draft`/`archived` reservados; `expired` es derivado.
- Frontend: `src/communications/infrastructure/announcementsApi.js`,
  `src/communications/domain/model/announcement.entity.js`

## Documentos y carpetas oficiales (import)

- Colección en db.json: `importUploads`
- Endpoints:
  - `GET/POST /api/v1/import/folders`, `PATCH/DELETE /api/v1/import/folders/{id}`
  - `GET /api/v1/import/uploads`, `POST /api/v1/import/uploads/signed-url`,
    `POST /api/v1/import/uploads/{id}/confirm`, `GET /api/v1/import/uploads/{id}/download-url`,
    `PATCH/DELETE /api/v1/import/uploads/{id}`
- Archivos en Firebase Storage; `ImportOrphanCleanupWorker` limpia subidas no confirmadas.
- La carpeta fija Contexto IA alimenta el RAG de Resident Care (ver ResidentCare/README.md).
- Frontend: `src/communications/infrastructure/importUploadsApi.js`
