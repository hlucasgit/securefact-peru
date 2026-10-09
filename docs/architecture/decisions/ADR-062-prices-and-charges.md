# ADR-062: Precios, cargos y pagos de la plataforma

- Estado: Aceptada · Fecha: 2026-10-09
- Completa: ADR-042 (planes y consumo), que dejó «sin precios, facturación ni cobro» como decisión comercial pendiente.
- Relacionadas: ADR-063 (comisiones de los revendedores), ADR-064 (suspensión por falta de pago).

## Contexto
Un plan decía qué permite (empresas, usuarios, comprobantes por mes) pero no cuánto cuesta. Para vender la plataforma hace falta fijar el precio de cada plan, cobrar lo que cada cuenta debe cada mes y registrar lo que paga. El requisito del producto es que **cambiar un precio no afecte a los clientes que ya tienen el plan**.

Decisiones comerciales tomadas con el responsable del producto el 2026-10-09: modelo de **cuota mensual fija más excedente**; **cobro manual con registro de pagos** (sin pasarela; ver «Límites»); **suspensión por mora** (ADR-064).

## Decisión

### Precios versionados y que no se reescriben
- `subscription.plan_price`: una **versión** por plan (`version`, `effective_from`, `monthly_fee`, `included_documents`, `overage_unit_price`, nota). Solo se **agregan** versiones; un disparador de la base de datos rechaza editarlas o borrarlas, aun con el dueño del esquema.
- `effective_from` es siempre el **primer día de un mes futuro** y posterior al de la versión anterior del plan. Así ningún mes se cobra con dos precios y una versión nunca cambia lo que ya pasó. Las versiones son datos (regla 2 del proyecto), no código.
- Montos en soles **sin IGV**, con `numeric` (nunca coma flotante): la cuota con dos decimales y el precio del excedente con cuatro.

### Qué precio tiene cada cuenta (sin estado que se desfase)
Es una **función** del día en que la cuenta tomó su plan (`tenancy.tenant.plan_assigned_at`, nuevo) y de las versiones publicadas (`TermsResolver`):
- la versión **vigente ese día**; si el plan aún no tenía precio, su **primera versión** (una cuenta que estaba en el plan antes de que tuviera precio paga la primera);
- se cobra desde **el mes siguiente** al día en que tomó el plan (el resto del mes de alta es gratis, sin prorratear) y nunca antes de que el precio rija;
- volver a asignar el plan que ya tiene **no** mueve la fecha; **cambiar de plan** sí: es un contrato nuevo y toma la versión vigente ese día.

Consecuencia deliberada: una versión nueva solo alcanza a las cuentas que tomen el plan después de su fecha. Subir el precio a una cuenta existente se hace creando otro plan y moviéndola a él (acto de la plataforma, con auditoría), no reescribiendo el suyo.

### Excedente de comprobantes
`tenancy.plan.allows_overage` (se decide al crear el plan y **no cambia**, porque los precios publicados dependen de él):
- Con excedente, **ningún comprobante se rechaza** (`DocumentService` ya no aplica `SF-PLAN-001` al tope mensual) y los que pasan de `included_documents` se cobran a `overage_unit_price`. La versión del precio exige entonces ambos campos; sin excedente, ninguno.
- Sin excedente el plan sigue limitando como en ADR-042. Los topes de empresas y usuarios siguen siendo topes duros en ambos casos.

### Cargos
`subscription.charge`: **un cargo por cuenta y mes** (índice único). Lo crea el pase de cobranza (`CollectionPass`) al cerrar el mes, en hora de Lima, y copia todo lo que lo determinó (nombre de la cuenta, plan, versión del precio, cuota, incluidos, comprobantes emitidos, excedente, tasa y monto del IGV, fechas): **nada de lo que cambie después lo reescribe**. Un disparador deja cambiar de un cargo una sola cosa: marcarlo anulado, una vez.
- Neto = cuota + `round(excedentes × precio, 2)`; IGV = `round(neto × tasa, 2)` con la tasa de la regla versionada `tax.igv.rate` a la fecha de emisión; total = neto + IGV. Redondeo «lejos de cero».
- Comprobantes del mes: `IDocumentService.CountIssuedAsync` (los mismos que cuenta el límite del plan: notas y dadas de baja incluidas).
- Vencimiento y gracia salen de la **política de cobranza** vigente el día de la emisión (`subscription.billing_policy`, versionada como los precios; la primera: vence a los 10 días y suspende a los 15 días de mora). El cargo guarda las fechas calculadas: publicar otra política no toca los cargos emitidos.
- El pase es **idempotente** (un cargo por cuenta y mes, con candado consultivo), tolera varios procesos y crea como máximo 24 cargos por cuenta en cada pasada, así que una caída larga se pone al día sin saturar. No genera cargos para cuentas cerradas ni para planes sin precio o de cuota cero sin excedente. Una cuenta que falla no detiene a las demás.
- Lo ejecuta `SubscriptionWorker` (cada hora por defecto, `Subscriptions:WorkerIntervalSeconds`) o un operador con `POST /api/v1/platform/subscriptions/run`.

### Pagos
`subscription.payment` solo se agrega. Un pago es un monto en soles con IGV, medio (`Transfer`, `Deposit`, `Cash`, `Card`, `Other`; la plataforma **registra** lo cobrado, no cobra), fecha, referencia y nota. No supera el saldo ni tiene fecha futura ni anterior a la emisión. Un error se corrige con una **reversa**: otro pago del monto opuesto que apunta al primero (una vez, y una reversa no se revierte). El saldo es la suma de los pagos; el **estado** (`Pending`, `Partial`, `Overdue`, `Paid`, `Void`) se **deriva** del saldo y del vencimiento, no se guarda. Un cargo sin pagos netos puede anularse con motivo. Toda operación toma un candado consultivo del cargo, así dos pagos simultáneos no caben ambos en el mismo saldo.

### Permisos y aislamiento
`subscriptions.read` (superadministrador y soporte) y `subscriptions.manage` (superadministrador). La cuenta ve **sus** precios (`GET /api/v1/subscription`) y **sus** cargos (`GET /api/v1/charges`) con `tenants.read`, como su plan. `charge` y `payment` llevan `tenant_id` y RLS `TenantOrPlatform`; los precios y la política son datos de plataforma (lectura para todos, escritura de la plataforma, como los planes).

### API
`GET|POST /api/v1/platform/plans/{id}/prices`, `GET|POST /api/v1/platform/billing-policies`, `GET /api/v1/platform/tenants/{id}/terms`, `GET /api/v1/platform/charges` (filtros `tenantId`, `status`, `period=aaaa-mm`), `GET /api/v1/platform/charges/{id}`, `POST …/charges/{id}/payments`, `POST …/charges/{id}/void`, `POST /api/v1/platform/payments/{id}/reverse`, `POST /api/v1/platform/subscriptions/run`. Códigos `SF-SUB-001` (precio), `-002` (política), `-003` (cargo no encontrado), `-004` (pago o anulación), `-005` (pago no encontrado). Auditoría: `subscriptions.price.published`, `…policy.published`, `…payment.recorded`, `…payment.reversed`, `…charge.voided`.

## Verificación
- Unidad (`SubscriptionRulesTests`): resolución del precio por fecha (sin precio, antes del primero, entre versiones, primer mes cobrado), cálculo de cargo (incluidos, excedente, redondeo, IGV sobre el total, sin excedente), fechas de la política y día de Lima.
- API con PostgreSQL (`SubscriptionsApiTests`, `CollectionPassTests`): permisos por rol, validación de versiones, precio que no alcanza a quien ya lo tiene, cargo único por mes y visible solo para su cuenta, pagos parciales, reversas y anulación, comprobantes que no se rechazan con excedente, excedente cobrado, tope de 24 cargos por pasada, fallo de una cuenta y falta de tasa, inmutabilidad (disparadores) y RLS con el rol de ejecución.

## Límites (P)
- **No hay pasarela de cobro.** El cobro es manual: una persona registra la transferencia o el depósito. Integrar Culqi, Niubiz, Izipay o Stripe necesita elegir proveedor y una cuenta de pruebas, y no se puede verificar contra un tercero sin ellas.
- ~~El cargo no es un comprobante de pago~~: el comprobante de cada cargo lo emite la plataforma con una cuenta propia (ADR-065). El cargo calcula el IGV y el comprobante lo emite Billing; coinciden al centavo.
- Solo soles. Los precios son decisión comercial y no fuente normativa; no se registran en `matrix.md`.
- Una cuenta que cambia de plan a mitad de mes se cobra con el plan que tiene al cerrar el mes (el pase corre al comenzar el siguiente); no se prorratea.
- La cuota se cobra aunque la cuenta esté suspendida (por mora, revendedor o plataforma) hasta que se cierre.
- Sin avisos de cargo emitido ni de vencimiento por correo (solo el aviso de suspensión y reactivación de ADR-054).
- El conteo de comprobantes de meses pasados se lee de los datos al correr el pase; un comprobante emitido después del cierre del mes no se cobra en él.
