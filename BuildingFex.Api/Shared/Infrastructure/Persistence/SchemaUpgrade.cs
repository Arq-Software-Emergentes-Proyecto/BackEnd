using BuildingFex.Api.Finances.Domain.Model.Aggregates;
using BuildingFex.Api.Shared.Infrastructure.Persistence.EntityFrameworkCore.Configuration;
using Microsoft.EntityFrameworkCore;

namespace BuildingFex.Api.Shared.Infrastructure.Persistence;

/// <summary>
/// EnsureCreated no altera DBs existentes. Este helper aplica columnas/tablas nuevas de forma idempotente.
/// </summary>
public static class SchemaUpgrade
{
    public static async Task ApplyAsync(AppDbContext db, ILogger logger, CancellationToken ct = default)
    {
        await db.Database.EnsureCreatedAsync(ct);

        await ApplyIncidentsAndCommunicationsAsync(db, logger, ct);
        await ApplyBillingAndBuildingsAsync(db, logger, ct);
        await ApplyTeamAsync(db, logger, ct);
        await ApplyIamAccountStatusAsync(db, logger, ct);

        await EnsureColumnAsync(db, logger, "users", "auth_provider", "VARCHAR(32) NOT NULL DEFAULT 'password'", ct);
        await EnsureColumnAsync(db, logger, "users", "google_sub", "VARCHAR(128) NULL", ct);
        await EnsureColumnAsync(db, logger, "users", "bill_from_next_month", "TINYINT(1) NOT NULL DEFAULT 0", ct);
        await EnsureColumnAsync(db, logger, "users", "invite_token", "VARCHAR(64) NULL", ct);
        await EnsureColumnAsync(db, logger, "users", "invite_expires_at", "DATETIME(6) NULL", ct);
        await EnsureColumnAsync(db, logger, "users", "invite_used_at", "DATETIME(6) NULL", ct);
        await EnsureColumnAsync(db, logger, "users", "invite_email", "VARCHAR(320) NULL", ct);

        await EnsureColumnAsync(db, logger, "import_uploads", "folder_id", "VARCHAR(128) NULL", ct);

        await EnsureTableAsync(db, logger, """
            CREATE TABLE IF NOT EXISTS service_providers (
              id INT NOT NULL AUTO_INCREMENT,
              external_id VARCHAR(128) NOT NULL,
              owner_admin_id VARCHAR(128) NOT NULL,
              name VARCHAR(256) NULL,
              phone VARCHAR(64) NULL,
              specialty VARCHAR(128) NULL,
              created_at DATETIME(6) NULL,
              PRIMARY KEY (id),
              UNIQUE KEY ix_service_providers_external_id (external_id)
            );
            """, ct);

        await EnsureTableAsync(db, logger, """
            CREATE TABLE IF NOT EXISTS import_folders (
              id INT NOT NULL AUTO_INCREMENT,
              external_id VARCHAR(128) NOT NULL,
              owner_admin_id VARCHAR(128) NOT NULL,
              name VARCHAR(256) NULL,
              is_home TINYINT(1) NOT NULL DEFAULT 0,
              created_at DATETIME(6) NULL,
              PRIMARY KEY (id),
              UNIQUE KEY ix_import_folders_external_id (external_id)
            );
            """, ct);

        await EnsureTableAsync(db, logger, """
            CREATE TABLE IF NOT EXISTS resident_extra_charges (
              id INT NOT NULL AUTO_INCREMENT,
              external_id VARCHAR(128) NOT NULL,
              owner_admin_id VARCHAR(128) NOT NULL,
              resident_id VARCHAR(128) NOT NULL,
              title VARCHAR(256) NULL,
              amount DECIMAL(18,2) NOT NULL,
              description LONGTEXT NULL,
              created_at DATETIME(6) NULL,
              PRIMARY KEY (id),
              UNIQUE KEY ix_resident_extra_charges_external_id (external_id)
            );
            """, ct);

        await EnsureTableAsync(db, logger, """
            CREATE TABLE IF NOT EXISTS profiles (
              id INT NOT NULL AUTO_INCREMENT,
              user_external_id VARCHAR(128) NOT NULL,
              name VARCHAR(200) NOT NULL,
              email VARCHAR(320) NOT NULL,
              dni VARCHAR(50) NULL,
              address VARCHAR(500) NULL,
              company VARCHAR(200) NULL,
              ruc VARCHAR(20) NULL,
              created_at DATETIME(6) NULL,
              updated_at DATETIME(6) NULL,
              PRIMARY KEY (id),
              UNIQUE KEY ix_profiles_user_external_id (user_external_id),
              CONSTRAINT fk_profiles_user_external_id
                FOREIGN KEY (user_external_id) REFERENCES users (external_id)
                ON DELETE CASCADE
            );
            """, ct);

        // Migración desde nombre antiguo admin_profiles (si existía).
        try
        {
            await using var conn = db.Database.GetDbConnection();
            if (conn.State != System.Data.ConnectionState.Open)
                await conn.OpenAsync(ct);

            await using var check = conn.CreateCommand();
            check.CommandText = """
                SELECT COUNT(*) FROM information_schema.TABLES
                WHERE TABLE_SCHEMA = DATABASE() AND TABLE_NAME = 'admin_profiles'
                """;
            var legacyExists = Convert.ToInt64(await check.ExecuteScalarAsync(ct)) > 0;
            if (legacyExists)
            {
                await db.Database.ExecuteSqlRawAsync("""
                    INSERT IGNORE INTO profiles
                      (user_external_id, name, email, dni, address, company, ruc, created_at, updated_at)
                    SELECT user_external_id, name, email, dni, address, company, ruc, created_at, updated_at
                    FROM admin_profiles
                    """, ct);
                logger.LogInformation("SchemaUpgrade: migrated admin_profiles → profiles");
            }
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "SchemaUpgrade: profiles migrate from admin_profiles skipped");
        }

        await EnsureForeignKeyAsync(
            db,
            logger,
            table: "profiles",
            constraint: "fk_profiles_user_external_id",
            ddl: """
                ALTER TABLE profiles
                  ADD CONSTRAINT fk_profiles_user_external_id
                  FOREIGN KEY (user_external_id) REFERENCES users (external_id)
                  ON DELETE CASCADE
                """,
            ct);

        await EnsureColumnAsync(db, logger, "support_chats", "kind", "VARCHAR(16) NOT NULL DEFAULT 'admin'", ct);
        await EnsureColumnAsync(db, logger, "support_chats", "source", "VARCHAR(32) NULL", ct);
        await EnsureColumnAsync(db, logger, "support_chats", "linked_chat_id", "VARCHAR(128) NULL", ct);
        await EnsureColumnAsync(db, logger, "support_chats", "title_summarized", "TINYINT(1) NOT NULL DEFAULT 0", ct);
        await EnsureColumnAsync(db, logger, "support_chats", "closed_at", "DATETIME(6) NULL", ct);
        await EnsureColumnAsync(db, logger, "support_chats", "incident_external_id", "VARCHAR(64) NULL", ct);
        // Bandeja admin / conversación IA activa: (owner, kind, status). EnsureCreated lo crea en DBs nuevas.
        await EnsureIndexAsync(db, logger, "support_chats", "i_x_support_chats_owner_admin_id_kind_status",
            "CREATE INDEX i_x_support_chats_owner_admin_id_kind_status ON support_chats (owner_admin_id, kind, status)", ct);

        await EnsureTableAsync(db, logger, """
            CREATE TABLE IF NOT EXISTS support_messages (
              id INT NOT NULL AUTO_INCREMENT,
              external_id VARCHAR(64) NOT NULL,
              chat_id INT NOT NULL,
              author_role VARCHAR(32) NOT NULL,
              body LONGTEXT NOT NULL,
              author_name VARCHAR(256) NULL,
              created_at DATETIME(6) NOT NULL,
              PRIMARY KEY (id),
              UNIQUE KEY ix_support_messages_external_id (external_id),
              KEY ix_support_messages_chat_id (chat_id),
              KEY ix_support_messages_created_at (created_at),
              CONSTRAINT fk_support_messages_chat
                FOREIGN KEY (chat_id) REFERENCES support_chats (id)
                ON DELETE CASCADE
            );
            """, ct);

        await MigrateSupportMessagesFromJsonAsync(db, logger, ct);
        await EnsureColumnAsync(db, logger, "support_messages", "cited_document_id", "VARCHAR(128) NULL", ct);
        await EnsureColumnAsync(db, logger, "support_messages", "cited_source", "VARCHAR(512) NULL", ct);

        // ResidentCare (CON11): contador mensual de consultas IA por cuenta, único por (owner, periodo).
        await EnsureTableAsync(db, logger, """
            CREATE TABLE IF NOT EXISTS ai_usages (
              id INT NOT NULL AUTO_INCREMENT,
              owner_admin_id VARCHAR(128) NOT NULL,
              period VARCHAR(7) NOT NULL,
              query_count INT NOT NULL DEFAULT 0,
              created_at DATETIME(6) NOT NULL,
              updated_at DATETIME(6) NOT NULL,
              PRIMARY KEY (id),
              UNIQUE KEY ix_ai_usages_owner_admin_id_period (owner_admin_id, period)
            );
            """, ct);

        // ResidentCare (US045): mensajes filtrados sin IA y contador diario de consultas por residente.
        await EnsureColumnAsync(db, logger, "ai_usages", "filtered_count", "INT NOT NULL DEFAULT 0", ct);
        await EnsureTableAsync(db, logger, """
            CREATE TABLE IF NOT EXISTS resident_ai_usages (
              id INT NOT NULL AUTO_INCREMENT,
              owner_admin_id VARCHAR(128) NOT NULL,
              resident_id VARCHAR(128) NOT NULL,
              `day` VARCHAR(10) NOT NULL,
              query_count INT NOT NULL DEFAULT 0,
              created_at DATETIME(6) NOT NULL,
              updated_at DATETIME(6) NOT NULL,
              PRIMARY KEY (id),
              UNIQUE KEY ix_resident_ai_usages_owner_resident_day (owner_admin_id, resident_id, `day`)
            );
            """, ct);

        // IAM: cuentas de edificio del plan Portafolio, tipo de cuenta (administración o junta) y miembros.
        await EnsureColumnAsync(db, logger, "users", "parent_admin_id", "INT NULL", ct);
        await EnsureColumnAsync(db, logger, "users", "account_kind", "VARCHAR(20) NULL", ct);
        await EnsureTableAsync(db, logger, """
            CREATE TABLE IF NOT EXISTS building_members (
              id INT NOT NULL AUTO_INCREMENT,
              owner_admin_id INT NOT NULL,
              user_id INT NOT NULL,
              role VARCHAR(20) NOT NULL,
              created_at DATETIME(6) NOT NULL,
              PRIMARY KEY (id),
              UNIQUE KEY ix_building_members_owner_admin_id_user_id (owner_admin_id, user_id),
              KEY ix_building_members_user_id (user_id),
              CONSTRAINT fk_building_members_users_owner_admin_id FOREIGN KEY (owner_admin_id) REFERENCES users (id) ON DELETE CASCADE,
              CONSTRAINT fk_building_members_users_user_id FOREIGN KEY (user_id) REFERENCES users (id) ON DELETE CASCADE
            );
            """, ct);
        await EnsureTableAsync(db, logger, """
            CREATE TABLE IF NOT EXISTS member_invitations (
              id INT NOT NULL AUTO_INCREMENT,
              external_id VARCHAR(64) NOT NULL,
              owner_admin_id INT NOT NULL,
              email VARCHAR(320) NOT NULL,
              name LONGTEXT NOT NULL,
              role VARCHAR(20) NOT NULL,
              token VARCHAR(64) NOT NULL,
              expires_at DATETIME(6) NOT NULL,
              accepted_at DATETIME(6) NULL,
              revoked_at DATETIME(6) NULL,
              invited_by_user_id INT NOT NULL,
              created_at DATETIME(6) NOT NULL,
              PRIMARY KEY (id),
              UNIQUE KEY ix_member_invitations_external_id (external_id),
              UNIQUE KEY ix_member_invitations_token (token),
              KEY ix_member_invitations_owner_admin_id (owner_admin_id),
              CONSTRAINT fk_member_invitations_users_owner_admin_id FOREIGN KEY (owner_admin_id) REFERENCES users (id) ON DELETE CASCADE
            );
            """, ct);

        await EnsureColumnAsync(db, logger, "import_uploads", "storage_path", "VARCHAR(1024) NOT NULL DEFAULT ''", ct);
        await EnsureColumnAsync(db, logger, "import_uploads", "download_url", "LONGTEXT NULL", ct);
        await EnsureColumnAsync(db, logger, "import_uploads", "upload_status", "VARCHAR(32) NOT NULL DEFAULT 'confirmed'", ct);
        await EnsureColumnAsync(db, logger, "import_uploads", "indexing_status", "VARCHAR(32) NOT NULL DEFAULT 'none'", ct);
        await EnsureColumnAsync(db, logger, "import_uploads", "indexing_error", "VARCHAR(1024) NULL", ct);
        await EnsureColumnAsync(db, logger, "import_folders", "is_context", "TINYINT(1) NOT NULL DEFAULT 0", ct);

        // Después de upload_status (el backfill PENDING_UPLOAD lo lee): en DBs muy antiguas la columna aún no existía.
        await ApplyOfficialDocumentsAsync(db, logger, ct);

        // Finances: snapshot de checkout MP (idempotencia de webhook + monto/ítems fijados).
        await EnsureTableAsync(db, logger, """
            CREATE TABLE IF NOT EXISTS maintenance_checkouts (
              id INT NOT NULL AUTO_INCREMENT,
              external_id VARCHAR(64) NOT NULL,
              owner_admin_id INT NOT NULL,
              resident_external_id VARCHAR(128) NOT NULL,
              items_json LONGTEXT NOT NULL,
              total_amount DECIMAL(18,2) NOT NULL,
              status VARCHAR(20) NOT NULL,
              mp_payment_id VARCHAR(64) NULL,
              preference_id VARCHAR(128) NULL,
              paid_at DATETIME(6) NULL,
              created_at DATETIME(6) NULL,
              updated_at DATETIME(6) NULL,
              PRIMARY KEY (id),
              UNIQUE KEY ix_maintenance_checkouts_external_id (external_id),
              UNIQUE KEY ix_maintenance_checkouts_mp_payment_id (mp_payment_id),
              KEY ix_maintenance_checkouts_owner_admin_id_resident_external_id (owner_admin_id, resident_external_id)
            );
            """, ct);

        // Finances: facturación mensual automática (RF021 / RNF011) — mes facturado + índice único anti-duplicados.
        await EnsureColumnAsync(db, logger, "receipts", "billing_month", "VARCHAR(7) NULL", ct);
        await EnsureColumnAsync(db, logger, "receipts", "payment_open", "VARCHAR(20) NULL", ct);
        await ApplyReceiptKindAsync(db, logger, ct);
        await EnsureColumnAsync(db, logger, "finance_settings", "late_fee_per_day", "DECIMAL(18,2) NOT NULL DEFAULT 0", ct);
        await EnsureColumnAsync(db, logger, "finance_settings", "admission_pay_from_day", "INT NOT NULL DEFAULT 1", ct);
        await EnsureColumnAsync(db, logger, "finance_settings", "admission_pay_to_day", "INT NOT NULL DEFAULT 7", ct);
        await EnsureColumnAsync(db, logger, "finance_settings", "restrict_reservations_when_delinquent", "TINYINT(1) NOT NULL DEFAULT 0", ct);
        await EnsureColumnAsync(db, logger, "finance_settings", "monthly_charges_json", "LONGTEXT NULL", ct);
        try
        {
            await db.Database.ExecuteSqlRawAsync(
                "UPDATE finance_settings SET monthly_charges_json = '[]' WHERE monthly_charges_json IS NULL", ct);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "SchemaUpgrade: finance_settings.monthly_charges_json backfill skipped");
        }

        // Social Spaces: reglamento/estado de espacios, estado de reservas, pase QR con vencimiento (RF025-RF027)
        // e índice único de turno activo (RNF009 / QAS02).
        await EnsureColumnAsync(db, logger, "social_spaces", "rules", "VARCHAR(2000) NOT NULL DEFAULT ''", ct);
        await EnsureColumnAsync(db, logger, "social_spaces", "status", "VARCHAR(20) NOT NULL DEFAULT 'AVAILABLE'", ct);
        await EnsureColumnAsync(db, logger, "social_spaces", "max_reservations_per_day", "INT NULL", ct);
        await EnsureColumnAsync(db, logger, "reservations", "status", "VARCHAR(20) NOT NULL DEFAULT 'CONFIRMED'", ct);
        await EnsureColumnAsync(db, logger, "reservations", "slot_key", "VARCHAR(160) NULL", ct);
        await EnsureColumnAsync(db, logger, "reservations", "cancelled_at", "DATETIME(6) NULL", ct);
        await EnsureColumnAsync(db, logger, "reservations", "guest_invite_expires_at", "DATETIME(6) NULL", ct);
        await EnsureIndexAsync(db, logger, "reservations", "ix_reservations_owner_space_date",
            "CREATE INDEX ix_reservations_owner_space_date ON reservations (owner_admin_id, space_external_id, `date`)", ct);
        if (await BuildingFex.Api.SocialSpaces.Infrastructure.Persistence.SocialSpacesSchemaUpgrade
                .PrepareReservationSlotKeysAsync(db, logger, ct))
        {
            await EnsureIndexAsync(db, logger, "reservations", "ux_reservations_owner_slot",
                "CREATE UNIQUE INDEX ux_reservations_owner_slot ON reservations (owner_admin_id, slot_key)", ct);
        }

        await EnsureTableAsync(db, logger, """
            CREATE TABLE IF NOT EXISTS ai_context_chunks (
              id INT NOT NULL AUTO_INCREMENT,
              owner_admin_id VARCHAR(128) NOT NULL,
              upload_external_id VARCHAR(128) NOT NULL,
              file_name VARCHAR(512) NULL,
              chunk_index INT NOT NULL,
              content LONGTEXT NOT NULL,
              embedding_json LONGTEXT NOT NULL,
              created_at DATETIME(6) NOT NULL,
              PRIMARY KEY (id),
              KEY ix_ai_context_chunks_owner (owner_admin_id),
              KEY ix_ai_context_chunks_upload (upload_external_id)
            );
            """, ct);
        await EnsureColumnAsync(db, logger, "ai_context_chunks", "embedding_model", "VARCHAR(64) NOT NULL DEFAULT 'text-embedding-004'", ct);

        await ApplySubscriptionPaymentsAsync(db, logger, ct);
    }

    /// <summary>
    /// Finances (RF021 / RNF011): tipo de recibo MONTHLY | EXTRA y unicidad solo para la cuota mensual.
    /// monthly_key = billing_month en recibos MONTHLY (NULL en EXTRA) con índice único (owner, residente, monthly_key);
    /// reemplaza al índice único anterior sobre billing_month, que hacía chocar los cargos extra con la cuota del mes.
    /// Backfill: amount = 0 y extra_charges &gt; 0 → EXTRA (así los emitían los cargos extra / servicios compartidos).
    /// </summary>
    private static async Task ApplyReceiptKindAsync(AppDbContext db, ILogger logger, CancellationToken ct)
    {
        var kindAdded = await EnsureColumnAsync(db, logger, "receipts", "kind", "VARCHAR(16) NOT NULL DEFAULT 'MONTHLY'", ct);
        await EnsureColumnAsync(db, logger, "receipts", "monthly_key", "VARCHAR(7) NULL", ct);

        // Solo al crear la columna: después el tipo lo fija la app (un MONTHLY explícito de monto 0 no se reclasifica).
        if (kindAdded)
        {
            await RunBackfillAsync(db, logger, "receipts.kind_extra",
                "UPDATE receipts SET kind = 'EXTRA' WHERE amount = 0 AND extra_charges > 0 AND monthly_key IS NULL", ct);
        }

        // Una clave por (owner, residente, mes): si datos legados tuvieran duplicados, solo el más antiguo la recibe.
        await RunBackfillAsync(db, logger, "receipts.monthly_key", """
            UPDATE receipts r
            JOIN (
              SELECT MIN(id) AS id FROM receipts
              WHERE kind = 'MONTHLY' AND billing_month IS NOT NULL
              GROUP BY owner_admin_id, resident_external_id, billing_month
            ) f ON f.id = r.id
            SET r.monthly_key = r.billing_month
            WHERE r.monthly_key IS NULL
              AND NOT EXISTS (
                SELECT 1 FROM (
                  SELECT DISTINCT owner_admin_id, resident_external_id, monthly_key FROM receipts WHERE monthly_key IS NOT NULL
                ) k
                WHERE k.owner_admin_id = r.owner_admin_id
                  AND k.resident_external_id = r.resident_external_id
                  AND k.monthly_key = r.billing_month)
            """, ct);

        await EnsureIndexAsync(db, logger, "receipts", ReceiptIndexes.UniqueMonthly,
            $"CREATE UNIQUE INDEX {ReceiptIndexes.UniqueMonthly} ON receipts (owner_admin_id, resident_external_id, monthly_key)", ct);
        await EnsureIndexAsync(db, logger, "receipts", ReceiptIndexes.ByBillingMonth,
            $"CREATE INDEX {ReceiptIndexes.ByBillingMonth} ON receipts (owner_admin_id, resident_external_id, billing_month)", ct);

        // El índice único anterior se elimina solo cuando el nuevo ya protege RNF011 (y el FK de owner_admin_id
        // tiene otro índice que lo cubra).
        if (await IndexExistsAsync(db, "receipts", ReceiptIndexes.UniqueMonthly, ct))
            await DropIndexIfExistsAsync(db, logger, "receipts", ReceiptIndexes.LegacyUniqueBillingMonth, ct);
    }

    /// <summary>
    /// Billing: cobros MP de suscripción (mp_payment_id único → anti-replay) y snapshot del monto de cada checkout.
    /// </summary>
    private static async Task ApplySubscriptionPaymentsAsync(AppDbContext db, ILogger logger, CancellationToken ct)
    {
        await EnsureTableAsync(db, logger, """
            CREATE TABLE IF NOT EXISTS subscription_payments (
              id INT NOT NULL AUTO_INCREMENT,
              mp_payment_id VARCHAR(64) NOT NULL,
              owner_admin_id INT NOT NULL,
              plan_id VARCHAR(20) NOT NULL,
              amount DECIMAL(18,2) NOT NULL,
              expected_amount DECIMAL(18,2) NOT NULL,
              currency VARCHAR(8) NULL,
              department_count INT NULL,
              checkout_external_id VARCHAR(64) NULL,
              status VARCHAR(32) NOT NULL,
              source VARCHAR(16) NOT NULL,
              paid_until DATETIME(6) NULL,
              created_at DATETIME(6) NULL,
              updated_at DATETIME(6) NULL,
              PRIMARY KEY (id),
              UNIQUE KEY ux_subscription_payments_mp_payment_id (mp_payment_id),
              KEY ix_subscription_payments_owner_admin_id (owner_admin_id),
              CONSTRAINT fk_subscription_payments_users_owner_admin_id
                FOREIGN KEY (owner_admin_id) REFERENCES users (id)
                ON DELETE CASCADE
            );
            """, ct);

        await EnsureTableAsync(db, logger, """
            CREATE TABLE IF NOT EXISTS subscription_checkouts (
              id INT NOT NULL AUTO_INCREMENT,
              external_id VARCHAR(64) NOT NULL,
              owner_admin_id INT NOT NULL,
              plan_id VARCHAR(20) NOT NULL,
              department_count INT NULL,
              amount DECIMAL(18,2) NOT NULL,
              status VARCHAR(20) NOT NULL,
              mp_payment_id VARCHAR(64) NULL,
              created_at DATETIME(6) NULL,
              updated_at DATETIME(6) NULL,
              PRIMARY KEY (id),
              UNIQUE KEY ux_subscription_checkouts_external_id (external_id),
              KEY ix_subscription_checkouts_owner_admin_id (owner_admin_id),
              CONSTRAINT fk_subscription_checkouts_users_owner_admin_id
                FOREIGN KEY (owner_admin_id) REFERENCES users (id)
                ON DELETE CASCADE
            );
            """, ct);
    }

    /// <summary>
    /// Incidents (RF024/RF031/RNF010): asunto, clasificación, assignee, cierre + historial incident_events.
    /// Communications (RF019/RNF011): status/published_at y backfill de expires_at = created_at + duration.
    /// </summary>
    private static async Task ApplyIncidentsAndCommunicationsAsync(AppDbContext db, ILogger logger, CancellationToken ct)
    {
        await EnsureColumnAsync(db, logger, "incidents", "title", "VARCHAR(200) NOT NULL DEFAULT ''", ct);
        await EnsureColumnAsync(db, logger, "incidents", "category", "VARCHAR(50) NULL", ct);
        await EnsureColumnAsync(db, logger, "incidents", "priority", "VARCHAR(20) NULL", ct);
        await EnsureColumnAsync(db, logger, "incidents", "assignee_id", "VARCHAR(128) NULL", ct);
        await EnsureColumnAsync(db, logger, "incidents", "resolution_comment", "VARCHAR(2000) NULL", ct);
        await EnsureColumnAsync(db, logger, "incidents", "resolved_at", "DATETIME(6) NULL", ct);
        await EnsureColumnAsync(db, logger, "incidents", "source_chat_id", "VARCHAR(128) NULL", ct);

        await EnsureTableAsync(db, logger, """
            CREATE TABLE IF NOT EXISTS incident_events (
              id INT NOT NULL AUTO_INCREMENT,
              incident_id INT NOT NULL,
              type VARCHAR(32) NOT NULL,
              status VARCHAR(20) NULL,
              comment VARCHAR(2000) NULL,
              author_id VARCHAR(128) NULL,
              author_name VARCHAR(200) NULL,
              author_role VARCHAR(20) NULL,
              created_at DATETIME(6) NOT NULL,
              PRIMARY KEY (id),
              KEY ix_incident_events_incident_id (incident_id),
              CONSTRAINT fk_incident_events_incidents_incident_id
                FOREIGN KEY (incident_id) REFERENCES incidents (id)
                ON DELETE CASCADE
            );
            """, ct);

        await EnsureColumnAsync(db, logger, "announcements", "status", "VARCHAR(20) NOT NULL DEFAULT 'published'", ct);
        await EnsureColumnAsync(db, logger, "announcements", "published_at", "DATETIME(6) NULL", ct);

        await RunBackfillAsync(db, logger, "incidents.title",
            "UPDATE incidents SET title = LEFT(TRIM(description), 80) WHERE title IS NULL OR title = ''", ct);
        await RunBackfillAsync(db, logger, "incidents.resolved_at",
            "UPDATE incidents SET resolved_at = COALESCE(updated_at, reported_at) WHERE status = 'resolved' AND resolved_at IS NULL", ct);
        await RunBackfillAsync(db, logger, "incident_events.reported", """
            INSERT INTO incident_events (incident_id, type, status, author_id, author_name, author_role, created_at)
            SELECT i.id, 'reported', 'open', i.resident_external_id, NULLIF(i.resident_name, ''), 'resident', i.reported_at
            FROM incidents i
            WHERE NOT EXISTS (SELECT 1 FROM incident_events e WHERE e.incident_id = i.id)
            """, ct);

        // created_at faltante (altas antiguas vía API no lo guardaban): se deriva de expires_at - duration.
        await RunBackfillAsync(db, logger, "announcements.created_at",
            "UPDATE announcements SET created_at = DATE_SUB(expires_at, INTERVAL IF(duration <= 0, 7, duration) DAY) WHERE created_at IS NULL AND expires_at IS NOT NULL", ct);
        await RunBackfillAsync(db, logger, "announcements.published_at",
            "UPDATE announcements SET published_at = created_at WHERE published_at IS NULL AND created_at IS NOT NULL", ct);
        // Sin ninguna fecha base (altas legadas sin created_at ni expires_at, visibles en la versión anterior):
        // se toma el despliegue como inicio (UTC_TIMESTAMP) para que sigan vigentes `duration` días en vez de ocultarse.
        await RunBackfillAsync(db, logger, "announcements.created_at_now",
            "UPDATE announcements SET created_at = UTC_TIMESTAMP(6) WHERE created_at IS NULL AND expires_at IS NULL AND published_at IS NULL", ct);
        await RunBackfillAsync(db, logger, "announcements.published_at_now",
            "UPDATE announcements SET published_at = created_at WHERE published_at IS NULL AND created_at IS NOT NULL", ct);
        await RunBackfillAsync(db, logger, "announcements.expires_at",
            "UPDATE announcements SET expires_at = DATE_ADD(COALESCE(published_at, created_at), INTERVAL IF(duration <= 0, 7, duration) DAY) WHERE expires_at IS NULL AND COALESCE(published_at, created_at) IS NOT NULL", ct);
    }

    /// <summary>
    /// Billing (RF004/RF005, 5.4): tabla subscriptions (prueba Free de 30 días, estado, paidUntil) +
    /// Profiles (RF015/TS001): tabla buildings. Backfill: cada admin existente sin fila recibe su suscripción —
    /// plan de pago → ACTIVE con su paid_until; Free → prueba de gracia de 30 días desde el despliegue
    /// (users.created_at no es fiable, así nadie queda bloqueado al actualizar).
    /// </summary>
    private static async Task ApplyBillingAndBuildingsAsync(AppDbContext db, ILogger logger, CancellationToken ct)
    {
        await EnsureTableAsync(db, logger, """
            CREATE TABLE IF NOT EXISTS buildings (
              id INT NOT NULL AUTO_INCREMENT,
              owner_admin_id INT NOT NULL,
              name VARCHAR(200) NOT NULL,
              address VARCHAR(500) NOT NULL,
              department_count INT NOT NULL,
              created_at DATETIME(6) NULL,
              updated_at DATETIME(6) NULL,
              PRIMARY KEY (id),
              KEY ix_buildings_owner_admin_id (owner_admin_id),
              CONSTRAINT fk_buildings_users_owner_admin_id
                FOREIGN KEY (owner_admin_id) REFERENCES users (id)
                ON DELETE CASCADE
            );
            """, ct);

        await EnsureTableAsync(db, logger, """
            CREATE TABLE IF NOT EXISTS subscriptions (
              id INT NOT NULL AUTO_INCREMENT,
              owner_admin_id INT NOT NULL,
              building_id INT NULL,
              plan_id VARCHAR(20) NOT NULL DEFAULT 'free',
              status VARCHAR(20) NOT NULL DEFAULT 'TRIAL',
              trial_ends_at DATETIME(6) NULL,
              paid_until DATETIME(6) NULL,
              failed_charges INT NOT NULL DEFAULT 0,
              billed_departments INT NULL,
              created_at DATETIME(6) NULL,
              updated_at DATETIME(6) NULL,
              PRIMARY KEY (id),
              UNIQUE KEY ix_subscriptions_owner_admin_id (owner_admin_id),
              KEY ix_subscriptions_status_trial_ends_at (status, trial_ends_at),
              CONSTRAINT fk_subscriptions_users_owner_admin_id
                FOREIGN KEY (owner_admin_id) REFERENCES users (id)
                ON DELETE CASCADE
            );
            """, ct);
        await EnsureColumnAsync(db, logger, "subscriptions", "billed_departments", "INT NULL", ct);

        await RunBackfillAsync(db, logger, "subscriptions.from_users", """
            INSERT INTO subscriptions
              (owner_admin_id, building_id, plan_id, status, trial_ends_at, paid_until, failed_charges, created_at, updated_at)
            SELECT u.id, NULL,
              CASE WHEN u.subscription_plan_id IN ('essential', 'standard', 'scale', 'portafolio') THEN u.subscription_plan_id ELSE 'free' END,
              CASE WHEN u.subscription_plan_id IN ('essential', 'standard', 'scale', 'portafolio') THEN 'ACTIVE' ELSE 'TRIAL' END,
              CASE WHEN u.subscription_plan_id IN ('essential', 'standard', 'scale', 'portafolio') THEN NULL
                   ELSE DATE_ADD(UTC_TIMESTAMP(6), INTERVAL 30 DAY) END,
              u.subscription_paid_until, 0, UTC_TIMESTAMP(6), UTC_TIMESTAMP(6)
            FROM users u
            WHERE u.role = 'admin'
              AND NOT EXISTS (SELECT 1 FROM subscriptions s WHERE s.owner_admin_id = u.id)
            """, ct);
    }

    /// <summary>
    /// Team (5.8, Tablas 80-83): puesto y estado del trabajador + tareas de mantenimiento y turnos.
    /// Las filas antiguas quedan ACTIVE / MAINTENANCE.
    /// </summary>
    private static async Task ApplyTeamAsync(AppDbContext db, ILogger logger, CancellationToken ct)
    {
        await EnsureColumnAsync(db, logger, "team_workers", "position", "VARCHAR(20) NOT NULL DEFAULT 'MAINTENANCE'", ct);
        await EnsureColumnAsync(db, logger, "team_workers", "status", "VARCHAR(20) NOT NULL DEFAULT 'ACTIVE'", ct);
        await EnsureColumnAsync(db, logger, "team_workers", "deactivated_at", "DATETIME(6) NULL", ct);

        await EnsureTableAsync(db, logger, """
            CREATE TABLE IF NOT EXISTS maintenance_tasks (
              id INT NOT NULL AUTO_INCREMENT,
              external_id VARCHAR(128) NOT NULL,
              owner_admin_id VARCHAR(128) NOT NULL,
              staff_id VARCHAR(128) NOT NULL,
              incident_id VARCHAR(128) NULL,
              description VARCHAR(2000) NOT NULL,
              scheduled_for DATETIME(6) NULL,
              status VARCHAR(20) NOT NULL,
              started_at DATETIME(6) NULL,
              completed_at DATETIME(6) NULL,
              created_at DATETIME(6) NOT NULL,
              updated_at DATETIME(6) NULL,
              PRIMARY KEY (id),
              UNIQUE KEY ix_maintenance_tasks_external_id (external_id),
              KEY ix_maintenance_tasks_owner_staff_status (owner_admin_id, staff_id, status),
              KEY ix_maintenance_tasks_incident_id (incident_id)
            );
            """, ct);

        await EnsureTableAsync(db, logger, """
            CREATE TABLE IF NOT EXISTS shifts (
              id INT NOT NULL AUTO_INCREMENT,
              external_id VARCHAR(128) NOT NULL,
              owner_admin_id VARCHAR(128) NOT NULL,
              staff_id VARCHAR(128) NOT NULL,
              starts_at DATETIME(6) NOT NULL,
              ends_at DATETIME(6) NOT NULL,
              notes VARCHAR(500) NULL,
              created_at DATETIME(6) NOT NULL,
              PRIMARY KEY (id),
              UNIQUE KEY ix_shifts_external_id (external_id),
              KEY ix_shifts_owner_staff_starts_at (owner_admin_id, staff_id, starts_at)
            );
            """, ct);
    }

    /// <summary>IAM (Tabla 52): estado de la cuenta (ACTIVE | DEACTIVATED). Las cuentas existentes quedan ACTIVE.</summary>
    private static async Task ApplyIamAccountStatusAsync(AppDbContext db, ILogger logger, CancellationToken ct)
    {
        await EnsureColumnAsync(db, logger, "users", "account_status", "VARCHAR(20) NOT NULL DEFAULT 'ACTIVE'", ct);
        await EnsureColumnAsync(db, logger, "users", "deactivated_at", "DATETIME(6) NULL", ct);
    }

    /// <summary>
    /// Communications (Tabla 76 – OfficialDocument): tipo, versión, fecha y estado de vigencia de los documentos
    /// (import_uploads). Los archivos existentes ya confirmados quedan PUBLISHED (vigentes) para que el asistente
    /// siga respondiendo con ellos; los pendientes de subida, PENDING_UPLOAD.
    /// </summary>
    private static async Task ApplyOfficialDocumentsAsync(AppDbContext db, ILogger logger, CancellationToken ct)
    {
        await EnsureColumnAsync(db, logger, "import_uploads", "document_type", "VARCHAR(20) NOT NULL DEFAULT 'OTHER'", ct);
        await EnsureColumnAsync(db, logger, "import_uploads", "version", "INT NOT NULL DEFAULT 1", ct);
        await EnsureColumnAsync(db, logger, "import_uploads", "published_at", "DATETIME(6) NULL", ct);
        await EnsureColumnAsync(db, logger, "import_uploads", "document_status", "VARCHAR(20) NOT NULL DEFAULT 'PUBLISHED'", ct);
        await EnsureColumnAsync(db, logger, "import_uploads", "supersedes_id", "VARCHAR(128) NULL", ct);
        await EnsureIndexAsync(db, logger, "import_uploads", "ix_import_uploads_owner_document_status",
            "CREATE INDEX ix_import_uploads_owner_document_status ON import_uploads (owner_admin_id, document_status)", ct);

        await RunBackfillAsync(db, logger, "import_uploads.document_status_pending",
            "UPDATE import_uploads SET document_status = 'PENDING_UPLOAD' WHERE upload_status = 'pending_upload' AND document_status = 'PUBLISHED' AND published_at IS NULL", ct);
        await RunBackfillAsync(db, logger, "import_uploads.published_at",
            "UPDATE import_uploads SET published_at = uploaded_at WHERE published_at IS NULL AND document_status = 'PUBLISHED'", ct);
    }

    private static async Task RunBackfillAsync(AppDbContext db, ILogger logger, string name, string sql, CancellationToken ct)
    {
        try
        {
            var rows = await db.Database.ExecuteSqlRawAsync(sql, ct);
            if (rows > 0)
                logger.LogInformation("SchemaUpgrade: backfill {Name} ({Rows} rows)", name, rows);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "SchemaUpgrade: backfill {Name} skipped", name);
        }
    }

    private static async Task MigrateSupportMessagesFromJsonAsync(
        AppDbContext db,
        ILogger logger,
        CancellationToken ct)
    {
        try
        {
            await using var conn = db.Database.GetDbConnection();
            if (conn.State != System.Data.ConnectionState.Open)
                await conn.OpenAsync(ct);

            await using var colCheck = conn.CreateCommand();
            colCheck.CommandText = """
                SELECT COUNT(*) FROM information_schema.COLUMNS
                WHERE TABLE_SCHEMA = DATABASE()
                  AND TABLE_NAME = 'support_chats'
                  AND COLUMN_NAME = 'messages_json'
                """;
            if (Convert.ToInt64(await colCheck.ExecuteScalarAsync(ct)) == 0)
                return;

            await using var countMsg = conn.CreateCommand();
            countMsg.CommandText = "SELECT COUNT(*) FROM support_messages";
            if (Convert.ToInt64(await countMsg.ExecuteScalarAsync(ct)) > 0)
                return;

            await using var select = conn.CreateCommand();
            select.CommandText = "SELECT id, messages_json FROM support_chats WHERE messages_json IS NOT NULL AND messages_json <> '' AND messages_json <> '[]'";
            await using var reader = await select.ExecuteReaderAsync(ct);
            var rows = new List<(int ChatId, string Json)>();
            while (await reader.ReadAsync(ct))
                rows.Add((reader.GetInt32(0), reader.GetString(1)));
            await reader.DisposeAsync();

            foreach (var (chatId, json) in rows)
            {
                List<JsonMsg>? list;
                try
                {
                    list = System.Text.Json.JsonSerializer.Deserialize<List<JsonMsg>>(json);
                }
                catch
                {
                    continue;
                }

                if (list is null || list.Count == 0) continue;

                foreach (var m in list)
                {
                    var role = string.IsNullOrWhiteSpace(m.Role)
                        ? (m.AuthorRole ?? "user")
                        : m.Role;
                    var ext = string.IsNullOrWhiteSpace(m.Id)
                        ? $"migrated-{chatId}-{Guid.NewGuid():N}"[..40]
                        : m.Id.Trim();
                    var created = DateTimeOffset.TryParse(m.CreatedAt, out var at)
                        ? at.UtcDateTime
                        : DateTime.UtcNow;

                    await db.Database.ExecuteSqlRawAsync(
                        """
                        INSERT IGNORE INTO support_messages
                          (external_id, chat_id, author_role, body, author_name, created_at)
                        VALUES ({0}, {1}, {2}, {3}, {4}, {5})
                        """,
                        ext,
                        chatId,
                        role,
                        m.Body ?? string.Empty,
                        (object?)m.AuthorName ?? DBNull.Value,
                        created);
                }
            }

            logger.LogInformation("SchemaUpgrade: migrated support_chats.messages_json → support_messages ({Count} chats)", rows.Count);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "SchemaUpgrade: support_messages migrate skipped");
        }
    }

    private sealed class JsonMsg
    {
        public string? Id { get; set; }
        public string? Role { get; set; }
        public string? AuthorRole { get; set; }
        public string? Body { get; set; }
        public string? AuthorName { get; set; }
        public string? CreatedAt { get; set; }
    }

    private static async Task EnsureForeignKeyAsync(
        AppDbContext db,
        ILogger logger,
        string table,
        string constraint,
        string ddl,
        CancellationToken ct)
    {
        try
        {
            await using var conn = db.Database.GetDbConnection();
            if (conn.State != System.Data.ConnectionState.Open)
                await conn.OpenAsync(ct);

            await using var check = conn.CreateCommand();
            check.CommandText = """
                SELECT COUNT(*) FROM information_schema.TABLE_CONSTRAINTS
                WHERE TABLE_SCHEMA = DATABASE()
                  AND TABLE_NAME = @table
                  AND CONSTRAINT_NAME = @constraint
                  AND CONSTRAINT_TYPE = 'FOREIGN KEY'
                """;
            AddParam(check, "@table", table);
            AddParam(check, "@constraint", constraint);
            var exists = Convert.ToInt64(await check.ExecuteScalarAsync(ct)) > 0;
            if (exists) return;

            await db.Database.ExecuteSqlRawAsync(ddl, ct);
            logger.LogInformation("SchemaUpgrade: added FK {Constraint} on {Table}", constraint, table);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "SchemaUpgrade: could not ensure FK {Constraint} on {Table}", constraint, table);
        }
    }

    /// <returns>true si la columna se creó en esta corrida.</returns>
    private static async Task<bool> EnsureColumnAsync(
        AppDbContext db,
        ILogger logger,
        string table,
        string column,
        string sqlType,
        CancellationToken ct)
    {
        try
        {
            await using var conn = db.Database.GetDbConnection();
            if (conn.State != System.Data.ConnectionState.Open)
                await conn.OpenAsync(ct);

            await using var check = conn.CreateCommand();
            check.CommandText = """
                SELECT COUNT(*) FROM information_schema.COLUMNS
                WHERE TABLE_SCHEMA = DATABASE()
                  AND TABLE_NAME = @table
                  AND COLUMN_NAME = @column
                """;
            AddParam(check, "@table", table);
            AddParam(check, "@column", column);
            var exists = Convert.ToInt64(await check.ExecuteScalarAsync(ct)) > 0;
            if (exists) return false;

            await using var alter = conn.CreateCommand();
            alter.CommandText = $"ALTER TABLE `{table}` ADD COLUMN `{column}` {sqlType}";
            await alter.ExecuteNonQueryAsync(ct);
            logger.LogInformation("SchemaUpgrade: added {Table}.{Column}", table, column);
            return true;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "SchemaUpgrade: could not ensure {Table}.{Column}", table, column);
            return false;
        }
    }

    private static async Task<bool> IndexExistsAsync(AppDbContext db, string table, string index, CancellationToken ct)
    {
        try
        {
            await using var conn = db.Database.GetDbConnection();
            if (conn.State != System.Data.ConnectionState.Open)
                await conn.OpenAsync(ct);

            await using var check = conn.CreateCommand();
            check.CommandText = """
                SELECT COUNT(*) FROM information_schema.STATISTICS
                WHERE TABLE_SCHEMA = DATABASE()
                  AND TABLE_NAME = @table
                  AND INDEX_NAME = @index
                """;
            AddParam(check, "@table", table);
            AddParam(check, "@index", index);
            return Convert.ToInt64(await check.ExecuteScalarAsync(ct)) > 0;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>Elimina un índice si existe. Idempotente.</summary>
    private static async Task DropIndexIfExistsAsync(
        AppDbContext db,
        ILogger logger,
        string table,
        string index,
        CancellationToken ct)
    {
        try
        {
            if (!await IndexExistsAsync(db, table, index, ct)) return;
            await db.Database.ExecuteSqlRawAsync($"DROP INDEX `{index}` ON `{table}`", ct);
            logger.LogInformation("SchemaUpgrade: dropped index {Index} on {Table}", index, table);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "SchemaUpgrade: could not drop index {Index} on {Table}", index, table);
        }
    }

    /// <summary>Crea un índice si no existe (information_schema.STATISTICS). Idempotente.</summary>
    private static async Task EnsureIndexAsync(
        AppDbContext db,
        ILogger logger,
        string table,
        string index,
        string ddl,
        CancellationToken ct)
    {
        try
        {
            await using var conn = db.Database.GetDbConnection();
            if (conn.State != System.Data.ConnectionState.Open)
                await conn.OpenAsync(ct);

            await using var check = conn.CreateCommand();
            check.CommandText = """
                SELECT COUNT(*) FROM information_schema.STATISTICS
                WHERE TABLE_SCHEMA = DATABASE()
                  AND TABLE_NAME = @table
                  AND INDEX_NAME = @index
                """;
            AddParam(check, "@table", table);
            AddParam(check, "@index", index);
            if (Convert.ToInt64(await check.ExecuteScalarAsync(ct)) > 0) return;

            await db.Database.ExecuteSqlRawAsync(ddl, ct);
            logger.LogInformation("SchemaUpgrade: added index {Index} on {Table}", index, table);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "SchemaUpgrade: could not ensure index {Index} on {Table}", index, table);
        }
    }

    private static async Task EnsureTableAsync(AppDbContext db, ILogger logger, string ddl, CancellationToken ct)
    {
        try
        {
            await db.Database.ExecuteSqlRawAsync(ddl, ct);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "SchemaUpgrade: table ensure failed");
        }
    }

    private static void AddParam(System.Data.Common.DbCommand command, string name, string value)
    {
        var p = command.CreateParameter();
        p.ParameterName = name;
        p.Value = value;
        command.Parameters.Add(p);
    }
}
