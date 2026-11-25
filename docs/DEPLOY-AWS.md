# Despliegue serverless en AWS

La API corre en **AWS Lambda** (.NET 10, ARM) en **Ohio (us-east-2)** detrás de **API Gateway HTTP API**. La base de datos es
**TiDB Cloud Starter** (compatible con MySQL) y el login sigue en **Firebase Auth**. Todo se despliega con
`.github/workflows/deploy-aws.yml` usando `template.yaml` (AWS SAM).

| Pieza | Servicio |
| --- | --- |
| API REST | Lambda `ApiFunction` + API Gateway HTTP API |
| Cobro mensual (diario 06:15 Lima), limpieza de importaciones (30 min), barrido de indexación IA (30 min) | Lambda `JobsFunction` + EventBridge Scheduler |
| Indexación IA al subir un documento | La API invoca `JobsFunction` en modo asíncrono |
| Migración del esquema | Job `migrate`, lo invoca el workflow después de cada despliegue |
| Secretos | `appsettings.Local.json` dentro del paquete de la Lambda (lo escribe el workflow desde los secretos de GitHub; la organización de AWS bloquea SSM) |
| Tiempo real del chat de soporte | Por ahora **polling** (SignalR no funciona en Lambda) |
| Monitoreo | Trazas de X-Ray en ambas funciones, alarmas de CloudWatch (errores y throttles de la API, 5xx de API Gateway, errores de jobs) y la cola SQS `FailedJobsQueue` para jobs asíncronos y programaciones fallidas |

## Lo que se configura una sola vez (a mano)

1. **TiDB**: en el clúster `buildingfex` → *Connect* → *Public* → *Generate Password*. Arma la cadena:
   `server=<host>;port=4000;database=buildingfex;user=<usuario>;password=<contraseña>;SslMode=VerifyFull`
2. **AWS IAM**:
   - Proveedor OIDC `token.actions.githubusercontent.com` (audiencia `sts.amazonaws.com`).
   - Rol `buildingfex-github-deploy` cuya política de confianza permita el `sub`
     `repo:Arq-Software-Emergentes-Proyecto@334218460/BackEnd@1389344396:ref:refs/heads/main`
     (esta organización de GitHub incluye los IDs fijos en el `sub`; un run manual muestra los claims en el paso
     "Show OIDC claims").
   - Políticas del rol: `AWSCloudFormationFullAccess`, `AWSLambda_FullAccess`, `AmazonAPIGatewayAdministrator`,
     `AmazonS3FullAccess`, `AmazonEventBridgeSchedulerFullAccess`, `CloudWatchLogsFullAccess`,
     `CloudWatchFullAccess`, `AmazonSQSFullAccess`, `AmazonSNSFullAccess`
     e `IAMFullAccess` (SAM crea los roles de las funciones y del scheduler).
3. **GitHub** (repo backend → *Settings → Secrets and variables → Actions*):
   - Variable `AWS_REGION` = `us-east-2` y secreto `AWS_DEPLOY_ROLE_ARN`. La organización de AWS (SCP `AdvancedModeRegionRestrictionSecurityControlPolicy`) solo permite servicios regionales en us-east-2.
   - Secretos obligatorios: `DB_CONNECTION_STRING`, `JWT_SECRET` (32+ caracteres).
   - Secretos opcionales: `GEMINI_API_KEY`, `MP_ACCESS_TOKEN`, `MP_PUBLIC_KEY`, `MP_WEBHOOK_SECRET`,
     `FIREBASE_CREDENTIALS_JSON` (JSON de la cuenta de servicio de Firebase, para Storage y custom claims).
   - Variables opcionales: `FRONTEND_ORIGINS`, `FRONTEND_BASE_URL` (por defecto `https://fronted-seven-opal.vercel.app`),
     `ALARM_EMAIL` (correo que recibe las alarmas; hay que confirmar la suscripción desde el correo de AWS).
4. **Firebase** (`emergentes-36306`): proveedores Google y Correo/contraseña activos y
   `fronted-seven-opal.vercel.app` en *Authentication → Settings → Authorized domains*.

## Desplegar

Cada push a `main` que toque la API despliega solo; también se puede lanzar a mano en *Actions → Deploy API to AWS
→ Run workflow*. Al terminar, el resumen del run muestra la URL de la API
(hoy `https://xafqa6wdb2.execute-api.us-east-2.amazonaws.com`). Esa URL va en el front (rewrite de Vercel).

Los secretos se leen de GitHub en cada despliegue: para cambiar uno, actualízalo en GitHub y vuelve a desplegar.

## Probar los jobs a mano

```bash
aws lambda invoke --function-name <JobsFunctionName> --payload '{"job":"monthly-billing"}' \
  --cli-binary-format raw-in-base64-out out.json && cat out.json
```

Jobs: `migrate`, `monthly-billing`, `import-cleanup`, `incident-escalation` (cada hora), `ai-index` (opcional `"uploadId"`).

## Local y Railway

Nada cambia fuera de Lambda: el servidor normal sigue aplicando el esquema al arrancar y corriendo los
`BackgroundService`, y SignalR sigue activo.

## Monitoreo y fallos

- **Trazas**: X-Ray activo en `ApiFunction` y `JobsFunction` (consola de CloudWatch → *X-Ray traces*).
- **Alarmas** (tema SNS `AlarmTopic`): errores de la API (≥ 5 en 5 min), throttles de la API, 5xx de API Gateway
  (≥ 10 en 5 min), cualquier error de `JobsFunction` en 1 h y mensajes en la cola de fallos.
- **Cola de fallos** (`FailedJobsQueue`, 14 días): recibe las invocaciones asíncronas de `JobsFunction` que fallan tras el
  reintento y las programaciones de EventBridge que no se pudieron entregar. Para reprocesar, lee el mensaje y vuelve a
  invocar el job con el mismo payload.
