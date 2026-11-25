"""Writes appsettings.Local.json into the Lambda package from the GitHub secrets (env vars).

Usage: python3 lambda_settings.py <publish-dir> [mercado-pago-notification-url]
The app loads appsettings.Local.json on start (Program.cs). Values are trimmed (secrets pasted
with a trailing newline would otherwise break the connection string). Never printed.
"""
import json
import os
import sys


def env(name):
    return (os.environ.get(name) or "").strip()


def main():
    out_dir = sys.argv[1]
    notification_url = sys.argv[2].strip() if len(sys.argv) > 2 else ""

    missing = [n for n in ("DB_CONNECTION_STRING", "JWT_SECRET") if not env(n)]
    if missing:
        print(f"::error::Faltan secretos obligatorios en GitHub: {', '.join(missing)}")
        sys.exit(1)

    settings = {
        "ConnectionStrings": {"DefaultConnection": env("DB_CONNECTION_STRING")},
        "TokenSettings": {"Secret": env("JWT_SECRET")},
    }
    if env("GEMINI_API_KEY"):
        settings["Gemini"] = {"ApiKey": env("GEMINI_API_KEY")}

    mercado_pago = {
        key: env(var)
        for key, var in (
            ("AccessToken", "MP_ACCESS_TOKEN"),
            ("PublicKey", "MP_PUBLIC_KEY"),
            ("WebhookSecret", "MP_WEBHOOK_SECRET"),
        )
        if env(var)
    }
    if notification_url:
        mercado_pago["NotificationUrl"] = notification_url
    if mercado_pago:
        settings["MercadoPago"] = mercado_pago

    firebase_json = env("FIREBASE_CREDENTIALS_JSON")
    if firebase_json:
        json.loads(firebase_json)  # fail early on a truncated paste
        settings["FirebaseStorage"] = {"CredentialsJson": firebase_json}

    path = os.path.join(out_dir, "appsettings.Local.json")
    with open(path, "w", encoding="utf-8") as fh:
        json.dump(settings, fh)
    os.chmod(path, 0o644)
    print("wrote", path, "sections:", ", ".join(settings))


if __name__ == "__main__":
    main()
