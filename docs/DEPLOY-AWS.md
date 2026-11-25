# Despliegue serverless en AWS

Este documento describe la arquitectura, configuración y procedimiento de despliegue continuo de la API de BuildingFex en Amazon Web Services (AWS) usando AWS Lambda, API Gateway HTTP API y AWS SAM con autenticación OIDC de GitHub Actions.

---

## Arquitectura

La API se ejecuta sobre una infraestructura serverless en **AWS Lambda (.NET 10, ARM64)** ubicada en la región de **Ohio (`us-east-2`)**, expuesta mediante **API Gateway (HTTP API)**. La base de datos es **TiDB Cloud Starter** (compatible con MySQL 8.0/8.4), la autenticación de usuarios opera contra **Firebase Auth**, y todo el ciclo de entrega continua se automatiza mediante el workflow [.github/workflows/deploy-aws.yml](file:///.github/workflows/deploy-aws.yml) y la plantilla [template.yaml](file:///template.yaml).

| Pieza | Servicio |
| :--- | :--- |
| **API REST** | `ApiFunction` + API Gateway HTTP API |
| **Cobro mensual** (diario 06:15 Lima) | `JobsFunction` + EventBridge Scheduler (`cron(15 6 * * ? *)`) |
| **Limpieza de importaciones** (cada 30 min) | `JobsFunction` + EventBridge Scheduler (`rate(30 minutes)`) |
| **Barrido de indexación IA** (cada 30 min) | `JobsFunction` + EventBridge Scheduler (US042) |
| **Indexación IA al subir un documento** | La API invoca `JobsFunction` en modo asíncrono (US042) |
| **Migración del esquema** | Job `migrate` ejecutado en `JobsFunction` tras cada despliegue |
| **Secretos y configuración** | `appsettings.Local.json` empaquetado en el artefacto (desde GitHub Secrets; SSM bloqueado por la organización AWS) |
| **Tiempo real de chat de soporte** | Polling HTTP (SignalR no mantiene conexiones persistentes en Lambda) |

---

## Lo que se configura una sola vez (a mano)

### 1. Base de datos: TiDB Cloud Starter
1. En la consola de TiDB Cloud, ve a **Connect** → **Public** → **Generate Password**.
2. Copia la cadena de conexión generada y reemplaza las credenciales por marcadores:
   ```
   Server=<host>;Port=4000;Database=buildingfex;Uid=<usuario>;Pwd=<contraseña>;SslMode=VerifyFull;
   ```
   > [!CAUTION]
   > Nunca subas la cadena de conexión real al repositorio ni la registres en commits.

### 2. AWS IAM (Rol OIDC para GitHub Actions)
1. En AWS IAM, registra un proveedor de identidad OIDC:
   - **Provider URL**: `https://token.actions.githubusercontent.com`
   - **Audience**: `sts.amazonaws.com`
2. Crea el rol IAM `buildingfex-github-deploy` con una Trust Policy que permita asumir el rol únicamente desde el repositorio y rama designada:
   ```json
   {
     "Version": "2012-10-17",
     "Statement": [
       {
         "Effect": "Allow",
         "Principal": {
           "Federated": "arn:aws:iam::<ID-CUENTA>:oidc-provider/token.actions.githubusercontent.com"
         },
         "Action": "sts:AssumeRoleWithWebIdentity",
         "Condition": {
           "StringEquals": {
             "token.actions.githubusercontent.com:aud": "sts.amazonaws.com"
           },
           "StringLike": {
             "token.actions.githubusercontent.com:sub": "repo:Arq-Software-Emergentes-Proyecto@*:ref:refs/heads/main"
           }
         }
       }
     ]
   }
   ```
   > [!NOTE]
   > Esta organización de GitHub incluye IDs numéricos fijos en el claim `sub` (ej. `repo:Arq-Software-Emergentes-Proyecto@<id-org>/BackEnd@1468048790:ref:refs/heads/main`). Para obtener el claim exacto, ejecuta el workflow manualmente desde Actions (`workflow_dispatch`), el cual ejecutará el paso *Show OIDC claims* imprimiendo el `sub` exacto sin exponer tokens.
3. Asigna las siguientes políticas administradas al rol `buildingfex-github-deploy`:
   - `AWSCloudFormationFullAccess`
   - `AWSLambda_FullAccess`
   - `AmazonAPIGatewayAdministrator`
   - `AmazonS3FullAccess`
   - `AmazonEventBridgeSchedulerFullAccess`
   - `CloudWatchLogsFullAccess`
   - `IAMFullAccess` (necesario para que SAM gestione los roles de ejecución de las Lambdas y de EventBridge).

### 3. GitHub Secrets y Variables
En el repositorio de GitHub (**Settings → Secrets and variables → Actions**), configura:

- **Variables (Actions Variables)**:
  - `AWS_REGION`: `us-east-2` (Ohio; las restricciones de la organización de AWS exigen esta región).
  - `FRONTEND_ORIGINS` (opcional): orígenes CORS permitidos (por defecto `https://fronted-seven-opal.vercel.app`).
  - `FRONTEND_BASE_URL` (opcional): URL base del frontend para redirecciones de pago.

- **Secretos (Actions Secrets)**:
  - `AWS_DEPLOY_ROLE_ARN`: ARN del rol `buildingfex-github-deploy` creado en IAM.
  - `DB_CONNECTION_STRING`: Cadena de conexión MySQL a TiDB Cloud con `SslMode=VerifyFull`. *(Obligatorio)*
  - `JWT_SECRET`: Clave simétrica de al menos 32 caracteres para el firmado de tokens de sesión. *(Obligatorio)*
  - `MP_ACCESS_TOKEN`, `MP_PUBLIC_KEY`, `MP_WEBHOOK_SECRET`: Credenciales de Mercado Pago. *(Opcionales)*
  - `FIREBASE_CREDENTIALS_JSON`: Contenido completo del archivo JSON de la clave de servicio de Firebase Admin SDK. *(Opcional)*
  - `GEMINI_API_KEY`: Clave de Google AI Studio para el chat con IA. Sin ella la API arranca, pero el chat responde `GEMINI_API_KEY_MISSING`. *(Opcional)*

### 4. Firebase Auth
- Verifica en la consola de Firebase que los proveedores **Google** y **Correo/contraseña** estén habilitados.
- En **Authentication → Settings → Authorized domains**, añade el dominio del frontend desplegado en Vercel.

---

## Despliegue

- **Automático**: Cada `push` a la rama `main` que afecte `BuildingFex.Api/**`, `template.yaml` o `.github/workflows/deploy-aws.yml` dispara el despliegue serverless de forma desatendida.
- **Manual**: Desde la pestaña **Actions** en GitHub → seleccionar **Deploy API to AWS** → clic en **Run workflow**.

Al finalizar, el resumen del run en GitHub Actions (*GITHUB_STEP_SUMMARY*) muestra la URL pública generada para la API:
```
### API desplegada: https://<api-id>.execute-api.us-east-2.amazonaws.com
```
Esta URL debe colocarse en las variables de entorno / rewrite de Vercel del frontend (`VITE_API_BASE_URL`).

Para actualizar secretos o credenciales: actualiza el secreto en GitHub Actions y vuelve a ejecutar el despliegue.

---

## Probar los jobs a mano

Puedes disparar manualmente cualquiera de los jobs periódicos de `JobsFunction` utilizando el AWS CLI:

```bash
aws lambda invoke \
  --function-name <JobsFunctionName> \
  --cli-binary-format raw-in-base64-out \
  --payload '{"job":"monthly-billing"}' \
  response.json && cat response.json
```

Jobs disponibles:
- `migrate`: Aplica las migraciones de esquema EF Core y datos iniciales.
- `monthly-billing`: Emisión y cálculo de cuotas de mantenimiento para todos los condominios.
- `import-cleanup`: Eliminación de archivos temporales huérfanos que excedieron el TTL.

---

## Local y Railway

Fuera del entorno AWS Lambda nada cambia:
- Al correr localmente (`dotnet run`) o en contenedores (Railway/Docker), el servidor aplica el esquema automáticamente al iniciar en frío.
- Los procesos periódicos son ejecutados por los `BackgroundService` en segundo plano en memoria.
- SignalR opera con WebSockets continuos para el soporte en vivo.
