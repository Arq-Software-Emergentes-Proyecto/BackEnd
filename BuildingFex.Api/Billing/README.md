# Billing — Planes de suscripción

**Responsable:** _asignar_

Bounded context de la suscripción del administrador del edificio (plan contratado,
límite de residentes y checkout). Namespace raíz: `BuildingFex.Api.Billing`.

## Estructura

```
Billing/
├── Domain/Model/SubscriptionPlans.cs            ← ids + reglas (límite, precio PEN, topes IA)
├── Domain/Model/PlanCatalog.cs                  ← ficha comercial (módulos, soporte) + plan sugerido
├── Domain/Model/SubscriptionStatus.cs           ← TRIAL | ACTIVE | PAYMENT_FAILED | SUSPENDED | CANCELLED
├── Domain/Model/ValueObjects/PlanLimits.cs
├── Domain/Model/Aggregates/Subscription.cs      ← tabla subscriptions (1:1 con el admin)
├── Application/Internal/SubscriptionService.cs  ← única vía de escritura del plan (+ espejo users.subscription_*)
├── Infrastructure/Persistence/…                 ← SubscriptionRepository + ApplyBillingConfiguration
└── Interfaces/Rest/{Plans,Subscription}Controller.cs
```

## Endpoints

| Método | Ruta | Auth | Descripción |
|--------|------|------|-------------|
| GET | `/api/v1/plans` | Anónimo | Catálogo (precios, límites, topes IA, módulos, soporte) |
| GET | `/api/v1/plans/suggest?departments=n` | Anónimo | Plan sugerido por cantidad de dptos. |
| GET | `/api/v1/subscription` | AdminOnly | Plan + status, trialEndsAt, trialDaysLeft, requiresPlanSelection, canAddResidents, topes |
| GET | `/api/v1/subscription/limits` | AdminOrResident | maxDepartments, departmentsUsed, monthlyAiQueries |
| PATCH | `/api/v1/subscription` | AdminOnly | Volver a Free (no reinicia la prueba) |
| POST | `/api/v1/subscription/checkout` | AdminOnly | Checkout Mercado Pago (Portafolio: S/4 × dptos., mín. S/600) |
| POST | `/api/v1/subscription/confirm` | AdminOnly | Confirmar pago y activar |

## Reglas

- **Prueba Free:** 30 días con funciones de Essential desde el alta del admin. Al vencer, `status` = SUSPENDED:
  se bloquea ampliar el padrón (crear/importar residentes → `SUBSCRIPTION_TRIAL_EXPIRED`), nunca las lecturas.
- **Backfill:** admins existentes sin fila reciben suscripción en SchemaUpgrade: plan de pago → ACTIVE con su
  `paid_until`; Free → prueba de gracia de 30 días desde el despliegue (`users.created_at` no es fiable).
- **Portafolio:** la referencia del pago es `SUBSCRIPTION:{admin}:portafolio:{dptos}`; checkout, confirm y webhook
  verifican el mismo monto `max(450, 3 × dptos)`. Dptos. facturables = máx(dptos. de edificios, residentes).
- `users.subscription_plan_id` / `subscription_paid_until` se mantienen como espejo (los leen IAM y Resident Care).
