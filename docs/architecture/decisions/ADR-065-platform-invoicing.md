# ADR-065: La factura de la plataforma por sus cobros

- Estado: Aceptada · Fecha: 2026-10-09
- Desarrolla: R-080 de la matriz (fuente S35, Reglamento de Comprobantes de Pago, artículo 5, numeral 5).
- Cierra: el límite de ADR-062 «el cargo no es un comprobante de pago».

## Contexto
SecureFact cobra a sus cuentas un servicio (ADR-062). Como cualquier contribuyente que presta servicios, debe emitir factura o boleta de venta por lo que cobra. El cargo mensual calcula el IGV, pero no es un comprobante. La plataforma ya sabe emitir comprobantes electrónicos válidos (Billing, CpeEngine); el trabajo es que **se emita con la misma maquinaria y no con una copia**, sin que ninguna cuenta vea los datos de otra.

## Oportunidad de emisión (R-080)
El artículo 5, numeral 5, del Reglamento (S35) manda entregar el comprobante en la prestación de servicios cuando ocurra primero: la **culminación del servicio**; la **percepción de la retribución** (por el monto percibido); o el **vencimiento del plazo, o de cada plazo, fijado para el pago** (por el monto de cada vencimiento).

El servicio es mensual y se mide por mes calendario: culmina al cerrar el mes. El cargo se hace en ese momento, con el total del mes. Por eso **se emite un comprobante por cargo, por su total, al crearse el cargo**: lo percibido después no genera otro comprobante (ya está emitido por el total) y no hace falta uno por cada pago parcial. Un comprobante emitido antes de las fechas del numeral 5 no tiene problema, porque el último párrafo del artículo 5 dice que «la entrega de los comprobantes de pago podrá anticiparse a las fechas antes señaladas».

## Decisión

### La plataforma factura con una cuenta propia
`subscription.invoicing_settings` (una fila) nombra **la cuenta emisora** (un tenant normal, con su empresa, su certificado, sus credenciales SOL y sus series) y cuatro series de esa empresa: facturas (`01`, empieza con F), boletas (`03`, B) y las notas de crédito de las facturas (`07`, F) y de las boletas (`07`, B). Solo el superadministrador las cambia (`PUT /api/v1/platform/invoicing`); la plataforma valida que cada serie sea de la empresa, de ese tipo y con esa letra. Sin configuración, o desactivada, los cargos se hacen y se cobran pero no se facturan.

El comprobante lo emite `IDocumentService` en el **ámbito de esa cuenta** (`IssuerAccess`: un ámbito de inyección propio con `UseTenant`), así que numeración, IGV, firma, envío a SUNAT y CDR son los de siempre. La cuenta del cliente nunca toca los datos de la emisora: lee su comprobante a través de un vínculo suyo.

### Datos de facturación de cada cuenta
`subscription.billing_profile` (con `tenant_id` y RLS, como todo dato de negocio): RUC (tipo `6`) o DNI (tipo `1`), razón social o nombre, dirección y correo. Se **copian al comprobante** al emitirlo; cambiarlos no cambia los emitidos. RUC con su dígito verificador, DNI de 8 dígitos. La cuenta los completa en *Plan y consumo* (propietario, administrador y facturación: permiso nuevo `account.billing.manage`); la plataforma puede cargarlos por una cuenta. Un RUC recibe **factura**; un DNI, **boleta de venta** (que identifica al comprador, como pide la regla de más de S/ 700, R-056). Sin datos, el cargo espera: la siguiente pasada lo factura cuando lleguen.

### Qué lleva el comprobante
- Una línea por la cuota («Servicio de la plataforma SecureFact, plan X, mes de año») y otra por el excedente («Comprobantes adicionales de …: N»), cada una con cantidad 1 y el valor de venta que el cargo calculó, afecta al IGV. El IGV de un comprobante es `round(IGV × base total)` y el del cargo es el mismo cálculo (ADR-062): **coinciden al centavo**; si algún día no coincidieran, el comprobante sale igual y queda en el registro.
- Fecha de emisión: el día del pase. Moneda: soles.
- La **factura** va como **venta al crédito** con una cuota por el total en la fecha de vencimiento del cargo (si esa fecha es posterior al día de emisión); la **boleta** no tiene cuotas.
- Clave de idempotencia `platform-charge-<id>`: dos pasadas a la vez no emiten dos comprobantes.

### Anular un cargo facturado
Anular un cargo que tiene comprobante emite primero la **nota de crédito** de anulación de la operación (motivo `01`) con las mismas líneas, en la serie de notas de ese tipo; sin la facturación configurada, el cargo no se anula (`SF-SUB-010`). Un comprobante emitido nunca se borra ni se edita. Las reversas de pagos no tocan los comprobantes: el cargo sigue debiéndose por el mismo total.

### Vínculo y consulta
`subscription.charge_document` (solo se agrega; RLS por cliente): cargo, tipo (`Invoice`, `CreditNote`), cuenta emisora, documento, serie y número. El cargo muestra el número y, en el detalle, el estado del documento electrónico ante SUNAT. PDF y XML firmado se bajan por `GET /api/v1/charges/{id}/documents/{Invoice|CreditNote}/{pdf|xml}` (la cuenta) y `/platform/charges/…` (la plataforma); los lee `IElectronicDocumentService` en el ámbito de la emisora.

### Sin comprobante sin cuenta emisora
Se factura en el pase de cobranza (`InvoicesIssued` en su resultado, hasta 100 por pasada) y cada documento sigue su ciclo normal: lo prepara y envía el pipeline de CpeEngine.

## Verificación
- Unidad: el pedido de factura (crédito con una cuota, vence el mismo día, boleta sin cuotas) y las líneas (cuota, excedente, solo excedente).
- API con PostgreSQL (`ChargeInvoicingApiTests`): la configuración y sus series, los datos de facturación (validación, privacidad, roles), factura a una empresa y boleta a una persona con los importes del cargo, el cargo que espera sus datos y la configuración desactivada, el excedente en dos líneas con el mismo total, la anulación con nota de crédito, descarga del PDF y XML y su aislamiento. El reloj de las pruebas se mueve (`ShiftedClock`) para ejecutar el mes que cierra con los servicios reales.
- Web: la configuración de la plataforma, los datos de facturación, el comprobante en el cargo con sus descargas; e2e de la configuración y del llenado de datos.

## Límites (P)
- **La fecha del comprobante es la del pase** (el día del cierre del mes o el siguiente), no el último día del mes. La norma permite adelantar la entrega y no exige una fecha concreta; si contabilidad prefiere el último día del mes, se cambia la fecha de emisión (con la regla de antigüedad máxima de Billing).
- **Fuente**: el numeral 5 y el párrafo que permite anticipar la entrega se leyeron en el texto de la RS 007-99/SUNAT tal como se publicó en 1999 (portal de SUNAT, página «007_anterior»); la versión concordada vigente no se descargó (S35). Las modificaciones posteriores del Reglamento a ese artículo no se verificaron.
- **Detracción**: algunos servicios con comprobante de más de S/ 700 están sujetos al sistema de detracciones. Si los servicios de SecureFact lo están, y con qué porcentaje y código, lo decide el asesor tributario con el anexo de SUNAT; **no se aplica detracción** (el sistema de emisión sí la soporta, ADR-029).
- Solo compradores con RUC o DNI y operación gravada con IGV. No hay compradores del exterior (exportación de servicios), carnet de extranjería ni pasaporte.
- La operación del cliente exonerado, inafecto o con beneficios no se contempla.
- Cada comprobante consume un lugar del plan de la cuenta emisora; si esa cuenta tiene tope de comprobantes, es el mismo que la plataforma se pone.
- El estado del documento ante SUNAT se lee de la cuenta emisora al abrir el cargo, no se guarda.
- Una cuenta que emite al mismo tiempo sus propios comprobantes de SecureFact y los suyos comparte numeración: es la de una serie común, por eso la cuenta emisora debería ser exclusiva de la plataforma.
