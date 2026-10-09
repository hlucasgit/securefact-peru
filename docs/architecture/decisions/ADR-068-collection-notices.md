# ADR-068: Avisos de cobranza por correo

- Estado: Aceptada · Fecha: 2026-10-09
- Completa: ADR-064 (suspensión por falta de pago), que dejó «sin aviso previo de vencimiento ni de se va a suspender», y ADR-062 (cargos y política de cobranza).
- Relacionadas: ADR-054 (avisos de estado de la cuenta), ADR-055 (avisos del negocio: mismo modelo de destinatarios, clave de no repetición y mejor esfuerzo).

## Contexto
La cuenta se enteraba de lo que debe solo entrando a su pantalla de plan, y de la suspensión cuando ya no podía entrar. Una suspensión por mora que sorprende al cliente es un mal resultado para él (deja de emitir comprobantes ante SUNAT) y para la plataforma (soporte, reclamos). El correo ya existe como canal (ADR-054, ADR-055); faltaba decir a tiempo qué se debe y cuándo.

## Decisión
- **Cinco avisos, a los propietarios activos de la cuenta** (los mismos destinatarios que ADR-055; nunca a usuarios sin ese rol ni a otra cuenta):
  1. **Cargo emitido**: el importe, el vencimiento y la fecha de suspensión si el cargo no se paga. Solo para el cargo del último mes cerrado: si una cuenta se cobra con atraso por varios meses a la vez, no recibe un correo por cada uno.
  2. **Vence pronto**: `reminder_days` días antes del vencimiento, hasta el día del vencimiento.
  3. **Vencido**: pasado el vencimiento y mientras no se llegue al plazo de aviso de la suspensión, con el saldo.
  4. **Suspensión cercana**: `reminder_days` días antes de la fecha de suspensión, una vez vencido el cargo.
  5. **Pago recibido**: cada pago que registra la plataforma, con el saldo que queda o «el cargo quedó pagado». Las reversas de pago son una corrección contable y no se avisan.
- **La cadencia es dato versionado (ADR-008)**: la política de cobranza gana `reminder_days` (0 a 30, por omisión 3; 0 apaga los avisos de «vence pronto» y de «suspensión cercana» y deja solo el de «vencido»). Es la política vigente el día de cada pase la que manda: a diferencia del plazo y la gracia, que son términos del cargo y se guardan en él, la anticipación es solo la cadencia de los avisos y no cambia lo que la cuenta debe ni cuándo.
- **Un aviso por pase y por cargo**: cada pase diario (el `CollectionPass` del `SubscriptionWorker`) le dice a cada cargo impago lo más urgente que es cierto ese día (`BillingReminders.KindFor`: suspensión cercana, si no vencido, si no vence pronto). Si el worker estuvo parado varios días, el siguiente pase manda el que corresponde hoy, no los atrasados.
- **Nunca dos veces**: la clave de no repetición del outbox de correo (ADR-055) es `billing:<tipo>:<cargo>` (y `billing:payment:<pago>` para pagos) por dirección. Correr el pase dos veces el mismo día, o todos los días, no repite ningún aviso.
- **Quién no recibe**: las cuentas que no están activas (suspendidas por cualquier origen, cerradas) ni las que ese mismo pase va a suspender: a ellas les llega el aviso de estado de ADR-054, que es el que corresponde. Un cargo anulado, uno pagado o uno emitido el mismo día del pase tampoco generan recordatorios.
- **Mejor esfuerzo, como todo aviso**: `IBillingNotices` (contrato en `Notifications.Contracts`, con una implementación nula por omisión) nunca deshace el cargo o el pago que anuncia; un fallo al encolar se registra con el tipo del fallo, sin datos de la cuenta, y el pase sigue. Sin canal de correo configurado, el aviso no se encola y no es un error.
- **Contenido**: solo importes, fechas y el enlace a la pantalla de plan del portal de la marca (ADR-055: marca del revendedor y su soporte); ningún secreto, ningún dato fiscal del cliente. El texto no afirma nada tributario: no dice que el cargo sea una factura ni dice «PSE».
- **Acotado**: un pase mira como mucho 1000 cargos impagos, los que vencen más tarde primero, para que un cargo antiguo que nadie paga no ocupe el lugar de los recientes. El resultado del pase informa `noticesQueued`.

## Verificación
`BillingRemindersTests` (unitarias): el calendario día por día para una gracia normal, con anticipación 0, sin suspensión y con gracia corta. `CollectionNoticesApiTests`: el aviso de cargo con importe, fechas y enlace, y que solo llegue al propietario de esa cuenta; la secuencia emitido → vence pronto → vencido → suspensión cercana, cada uno una vez aunque se repitan los pases, y nada más tras la suspensión (solo el aviso de estado); pagos parcial y total con su saldo, sin recordatorios para el cargo pagado y sin aviso por la reversa. `SubscriptionsApiTests`: la política acepta y valida `reminderDays` (0 a 30; la inicial es 3). Contrato OpenAPI actualizado (campo opcional: compatible con `v1`).

## Límites (P)
- Solo correo, solo a los propietarios de la cuenta: sin SMS ni WhatsApp, sin copia al revendedor (que hoy no ve la mora de sus cuentas por correo) ni a un contacto de facturación aparte.
- El texto está en español y no se personaliza por revendedor más allá de la marca y el soporte (ADR-055).
- No hay aviso por la factura que la plataforma emite por el cobro (ADR-065) ni por el cambio de datos de facturación; el cliente la descarga de su pantalla de plan.
- No hay pasarela de pago: el aviso dice cuánto se debe y a quién escribir si ya se pagó, no incluye un enlace de pago.
