"""Generate appsettings.Local.json for AWS Lambda deployment packages.

Reads configuration from environment variables (GitHub secrets) and generates
appsettings.Local.json in the specified target directory. Never prints secret values.
"""

import json
import os
import sys


def main() -> int:
    if len(sys.argv) < 2:
        print("Usage: python lambda_settings.py <publish-dir> [mercado-pago-notification-url]", file=sys.stderr)
        return 1

    publish_dir = sys.argv[1]
    mp_notification_url = sys.argv[2].strip() if len(sys.argv) > 2 and sys.argv[2].strip() else None

    db_connection = os.environ.get("DB_CONNECTION_STRING", "").strip()
    jwt_secret = os.environ.get("JWT_SECRET", "").strip()

    missing = []
    if not db_connection:
        missing.append("DB_CONNECTION_STRING")
    if not jwt_secret:
        missing.append("JWT_SECRET")

    if missing:
        print(f"::error::Faltan secretos obligatorios en GitHub: {', '.join(missing)}", file=sys.stderr)
        return 1

    config = {
        "ConnectionStrings": {
            "DefaultConnection": db_connection,
        },
        "TokenSettings": {
            "Secret": jwt_secret,
        },
    }

    mercado_pago = {}
    mp_access_token = os.environ.get("MP_ACCESS_TOKEN", "").strip()
    mp_public_key = os.environ.get("MP_PUBLIC_KEY", "").strip()
    mp_webhook_secret = os.environ.get("MP_WEBHOOK_SECRET", "").strip()

    if mp_access_token:
        mercado_pago["AccessToken"] = mp_access_token
    if mp_public_key:
        mercado_pago["PublicKey"] = mp_public_key
    if mp_webhook_secret:
        mercado_pago["WebhookSecret"] = mp_webhook_secret
    if mp_notification_url:
        mercado_pago["NotificationUrl"] = mp_notification_url

    if mercado_pago:
        config["MercadoPago"] = mercado_pago

    firebase_credentials = os.environ.get("FIREBASE_CREDENTIALS_JSON", "").strip()
    if firebase_credentials:
        try:
            json.loads(firebase_credentials)
        except Exception as exc:
            print(f"::error::FIREBASE_CREDENTIALS_JSON no es un JSON valido: {exc}", file=sys.stderr)
            return 1
        config["FirebaseStorage"] = {
            "CredentialsJson": firebase_credentials,
        }

    os.makedirs(publish_dir, exist_ok=True)
    target_path = os.path.join(publish_dir, "appsettings.Local.json")

    with open(target_path, "w", encoding="utf-8") as f:
        json.dump(config, f, indent=2, ensure_ascii=False)

    try:
        os.chmod(target_path, 0o644)
    except OSError:
        pass

    written_sections = list(config.keys())
    print(f"Generated {target_path} with sections: {', '.join(written_sections)}")
    return 0


if __name__ == "__main__":
    sys.exit(main())
