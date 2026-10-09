# ADR-063: Comisiones de los revendedores

- Estado: Aceptada · Fecha: 2026-10-09
- Completa: ADR-043 (revendedores), que dejó «sin facturación ni comisiones» como pendiente.
- Relacionadas: ADR-062 (precios y cargos).

## Contexto
El responsable del producto pidió «un criterio válido en el mercado actual, dinámico para modificarlo en el tiempo, sin afectar a los clientes anteriores». Esto fija quién cobra y cómo se calcula lo que gana el revendedor.

## Decisión

### Quién cobra
**La plataforma cobra a la cuenta final** (ADR-062) y el revendedor gana una **comisión recurrente** sobre lo que esa cuenta paga. El precio al cliente es el del plan, único; no hay precio mayorista. Un revendedor sigue pudiendo suspender a su cliente por una deuda propia (ADR-045); la mora con la plataforma la suspende la plataforma (ADR-064).

### Criterio de mercado
Comisión sobre el pago **sin IGV**, **recurrente** y **escalonada por el tamaño de la cartera**: 20 % de base, 25 % desde 10 cuentas activas y 30 % desde 25. Los programas de afiliados y reventa de software como servicio se mueven en un rango habitual de 20 % a 30 % recurrente, con tramos que suben al llegar a metas de volumen ([Reditus](https://www.getreditus.com/help/what-is-a-good-commission-rate-in-b2b-saas), [Hamster Garage](https://www.hamstergarage.com/article/saas-affiliate-commission-rates-benchmarks-formulas)). **Son cifras de referencia de afiliados**, no de revendedores del Perú: dan el orden de magnitud, no una fuente verificada; por eso son datos que la plataforma cambia (abajo) y no código. Los términos iniciales son una decisión comercial, no normativa.

### Términos versionados y que no alcanzan a las cuentas ya hechas
- `subscription.commission_schedule` + `commission_tier`: una versión con fecha de vigencia y sus tramos (`min_accounts`, `rate`; el primero en 0; porcentajes de 0 a 1 con hasta cuatro decimales; hasta 10 tramos). Solo se agregan versiones; un disparador rechaza editarlas o borrarlas. La vigencia es futura y posterior a la anterior, como en los precios. La primera versión se siembra con los términos de arriba.
- **A cada cuenta le aplica la versión vigente el día en que quedó bajo su revendedor** (`tenancy.tenant.reseller_assigned_at`, nuevo; o la primera versión si no había ninguna). El cargo la copia al emitirse (`commission_schedule_id`) y el revendedor al que pertenece la cuenta ese día (`reseller_id`). Publicar términos nuevos solo afecta a las cuentas que lleguen después; mover una cuenta a otro revendedor es un contrato nuevo.
- El **tramo** dentro de esos términos depende de cuántas cuentas **activas** tiene el revendedor **cuando se registra el pago**.

### Cómo se gana
Al registrar un pago, en la misma transacción, se agrega un **asiento** (`commission_entry`): `base = round(pago × neto / total del cargo, 2)` (la parte sin IGV), `comisión = round(base × tasa, 2)`. Una **reversa** del pago agrega el asiento opuesto con la misma tasa (la comisión se recupera exactamente). Sin revendedor, sin términos o con un cargo de total cero, no hay asiento. Los asientos solo se agregan.
- El asiento pertenece al **mes** (Lima) en que se registró el pago; el estado de cuenta y la liquidación de un mes se componen de sus asientos.

### Liquidación
`commission_settlement`: **una por revendedor y mes**, solo de un mes **ya cerrado** y con fecha posterior al cierre y no futura; su total es la suma de los asientos del mes y no cambia (no entran asientos nuevos a un mes cerrado: una reversa posterior cae en el mes en que se registra). Registra que la plataforma pagó la comisión (referencia y nota); **no mueve dinero**. Un mes sin comisiones no se liquida. Un total negativo (más reversas que pagos) se liquida igual: es lo que el revendedor debe compensar.

### Permisos y API
Platform: `subscriptions.read` lee, `subscriptions.manage` publica y liquida. El revendedor lee **lo suyo** con `reseller.commissions.read`; su identidad sale del token, nunca de la solicitud (ADR-043), y las tablas son de ámbito de plataforma con filtro por revendedor en la aplicación (RLS `platform_only`: ninguna cuenta las lee).

`GET|POST /api/v1/platform/commission-schedules`, `GET /api/v1/platform/resellers/{id}/commissions` (resumen y meses), `GET …/commissions/{aaaa-mm}` (estado de cuenta), `POST …/commissions/{aaaa-mm}/settle`, `GET /api/v1/reseller/commissions` y `…/{aaaa-mm}`. Códigos `SF-SUB-006` (términos) y `SF-SUB-007` (liquidación). Auditoría: `subscriptions.commission_schedule.published`, `subscriptions.commission.settled`.

## Verificación
`CommissionsApiTests` (PostgreSQL): términos iniciales y su validación, permisos; comisión de un pago parcial redondeada sobre la base sin IGV, términos de la cuenta que una versión nueva no altera, reversa que la recupera, aislamiento entre revendedores y cuentas; tramo de 25 % con 10 cuentas activas y cuenta sin revendedor que no gana; liquidación de un mes cerrado (validaciones, una sola vez, vista del revendedor, auditoría); un revendedor no levanta la suspensión por mora.

## Límites (P)
- Las cifras iniciales son una referencia de mercado de afiliados, no una oferta verificada para revendedores peruanos. La plataforma las cambia con una versión nueva.
- El tramo se calcula al registrar cada pago con las cuentas activas de ese momento; un revendedor que baja de tramo cobra el porcentaje menor desde ahí.
- La comisión es sobre lo **cobrado**, no sobre lo facturado: una cuenta que no paga no genera comisión.
- No hay pago de comisiones: la liquidación solo registra que se hizo. Tampoco condiciones por revendedor (un acuerdo especial se haría con otra versión de términos, que aplicaría a todos los que lleguen).
- No hay retención del impuesto a la renta del revendedor ni comprobante de su comisión: el tratamiento tributario de lo que la plataforma paga a un tercero se verifica con contabilidad antes de pagar la primera liquidación.
