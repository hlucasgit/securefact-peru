# STATUS — 2026-10-01 (dos horas autónomas)

## Estado general
Fase 0 completa. **Fase 1 casi completa** (falta outbox, bus de mensajes y almacenamiento S3 de código; el CI no se ha ejecutado). **Fase 2 en curso**: ya existen el motor tributario, las series, la numeración atómica y la emisión idempotente de facturas/boletas. Último commit en `origin/main`: `3bfc54b`. El trabajo de esta hora está **sin commitear**.

## Novedades de las dos últimas horas
- **Rules** (`IRuleProvider`): plazos y tasas como reglas versionadas con estado `Verified`/`Pending`; versiones publicadas inmutables; `GET /api/v1/rules`. **Billing ya no acepta tasas del cliente** (las resuelve por fecha de emisión); prueba con tasas falsas.
- **Customers** y **Products**: datos maestros por tenant con validación contra los catálogos 06 y 07, identidad inmutable, sin borrado, búsqueda con comodines escapados. Los documentos pueden referenciar `customerId` (instantánea del adquirente).
- **UBL**: generador de XML 2.1 sin firmar para factura y boleta (líneas gravadas, exoneradas, inafectas, gratuitas). **Valida contra el XSD oficial UBL 2.1** y contiene **todas las etiquetas obligatorias de las hojas `Factura2_0` y `Boleta2_0`** del libro oficial (la prueba lee el libro versionado). Todo lo demás falla con `SF-CPE-002` en lugar de emitir XML engañoso. **ADR-016**.
- Hallazgos: el libro de reglas es la fuente más fiable (la guía PDF de 2017 usa una estructura anterior); el ejemplo de la guía firma con RSA-SHA1 (algoritmo vigente por confirmar, R-032); `EF.Functions.ILike` sin carácter de escape no escapa comodines.
- **Pruebas: 319 pasan en Release con warnings-as-errors** (191 unitarias, 6 de arquitectura, 6 de integración, 116 de seguridad/API). Cobertura no medida.

## Novedades de la tercera tanda
- **Firma XMLDSig** (SHA-256 por defecto, SHA-1 configurable): una firma envuelta en `ext:ExtensionContent`, certificado validado (clave privada, vigencia, RSA ≥ 2048). El XML firmado valida contra el XSD oficial. Expone el `DigestValue` para el QR.
- **ZIP** seguro (una entrada, nombre validado, límites de tamaño, sin rutas peligrosas).
- **CDR**: parser del `ApplicationResponse` con los ejemplos del manual; solo el código 0 es aceptado; clasificación por rangos 0100–0999 / 1000–1999 / 2000–3999 / 4000+.
- **Canal SOAP** a `billService` con WS-Security (`sendBill`, `sendSummary`, `getStatus`), sin excepciones por fallos remotos, HTTPS obligatorio, contraseña SOL nunca impresa, límites de respuesta. Probado con simulador (no hay red ni beta). **ADR-017**.

## Novedades de la cuarta tanda
- **Certificados** (ADR-018): PKCS#12 cifrado en reposo, validación (clave privada, RSA ≥ 2048, vigencia, RUC ajeno rechazado), uno activo por empresa, nunca se borran, alertas de vencimiento, permisos y auditoría. **Credenciales SOL** cifradas por empresa.
- **Tubería** (ADR-019): preparar (UBL + firma) → enviar (`sendBill`) → CDR validado contra el documento → estado final inmutable (disparadores en la base de datos). Historial de eventos, reintentos con espera exponencial, reintento manual, concurrencia (un solo envío a SUNAT por documento). Probada con un simulador de SUNAT en proceso.
- **Pruebas: 363 pasan** (202 unitarias, 6 arquitectura, 6 integración, 149 seguridad/API).

## Novedades de la quinta tanda
- **Resumen diario de boletas** (ADR-020): UBL 2.0 validado contra el XSD oficial y la hoja `Resumen Diario1_1`; el resumen es un documento electrónico firmado que se envía con `sendSummary` y se sigue con `getStatus`; las boletas reflejan su resumen y, si se rechaza, vuelven a la cola.
- **Worker** (ADR-021): host real (`SecureFact.Workers`) con resúmenes de días cerrados, envíos vencidos, tickets y detección de atascados (recuperación solo por un operador).
- **Pruebas: 405 pasan** (226 unitarias, 6 arquitectura, 6 integración, 167 seguridad/API).

## Novedades de la sexta tanda
- **Outbox transaccional** (ADR-022): Billing escribe el evento con el documento; el despachador (Platform) lo entrega al consumidor de CPE, que prepara el documento electrónico. Cadena completa probada con el host real y el simulador: emitir → preparar → enviar → aceptado.
- **Corregido**: documentos con credenciales faltantes llenaban el lote del worker y bloqueaban a los demás; ahora se aplazan 5 minutos.
- **Pruebas: 415 pasan** (226 unitarias, 6 arquitectura, 6 integración, 177 seguridad/API).

## Novedades de la séptima tanda — primera aceptación real
- **Factura, boleta (`sendBill`) y resumen diario (`sendSummary` + `getStatus`) aceptados por el beta de SUNAT** (CDR código 0) con `tools/SecureFact.BetaSmoke`. Hallazgos en `docs/regulatory/beta-findings.md`: faltaba la forma de pago (3244), valores de atributos del UBL, carpeta `dummy/` y marca de tiempo en el CDR real; todo corregido.
- Confirmado en el beta: base64 en línea, `SOAPAction` vacío, firma RSA-SHA256, certificado autofirmado, nombre del resumen con fecha de generación.
- Pendiente de prueba: la política de producción (cadena de certificados, boletas con `sendBill`).

## Representación impresa
- `GET /api/v1/electronic-documents/{id}/pdf` (permiso `documents.read`, aislado por tenant): PDF A4 determinista con los datos mínimos del Anexo II de la RS 114-2019 (fuente S21), QR según S19 y el `DigestValue` firmado. Pruebas: 455 pasan.
- Sin confirmar: vigencia posterior a 2019 del anexo y la leyenda de la factura (R-044); el PDF no incluye código de establecimiento anexo ni datos adicionales (detracciones, anticipos…).

## Notas de crédito y de débito
- Emisión, UBL, documento electrónico (espera a que el original esté aceptado), PDF y envío de notas de facturas; aceptadas en el beta (crédito 01, débito 02, crédito 07 de boleta). Pruebas: 491 pasan.
- Notas de boletas: por resumen diario (esperan a que la boleta esté informada); aceptadas en el beta.
- Pendiente: motivos 11–12, acumulado de notas de crédito.

## Comunicación de baja
- `POST /api/v1/voids`: baja de facturas y notas de facturas (comunicación `RA`) y de boletas y notas de boletas (resumen `RC` con líneas de estado 3), aceptados (≤ 7 días), un archivo por fecha y tipo; se envía y sigue como un resumen; «anulado» se deriva del archivo aceptado. Aceptadas en el beta. Pruebas: 545 pasan (319 unitarias, 6 arquitectura, 6 integración, 214 seguridad/API).
- Una nota sobre un documento anulado o con baja en curso se rechaza (`SF-BIL-011`, puerto `IVoidStatusProvider`).
- El PDF de un documento anulado lleva «ANULADO» (marca de agua y rótulo).

## Descuentos y cargos
- Facturas y boletas con descuentos y cargos de línea (00, 01, 47, 48) y globales (02, 03, 49, 50): el generador los emite (`cac:AllowanceCharge`, base, factor, totales), el resumen diario informa los cargos, el PDF imprime «Otros cargos» y «Otros descuentos». Aceptados en el beta (ADR-025). Las notas no llevan descuentos ni cargos (`SF-BIL-006`).
- El beta destapó que las líneas exoneradas, inafectas y gratuitas necesitaban `cbc:Percent` (2992); corregido. Pruebas: 560 pasan (330 unitarias, 6 arquitectura, 6 integración, 218 seguridad/API).
- Pendiente: factor porcentual como entrada, desglose de descuentos en el PDF, anticipos.

## Venta al crédito
- Facturas al crédito con cuotas (`installments` en `POST /api/v1/documents`): Billing valida el plan antes de numerar, el UBL lleva `FormaPago` `Credito` y una `CuotaNNN` por vencimiento, el PDF imprime las cuotas. Aceptada en el beta (ADR-026). Pruebas: 569 pasan (336 unitarias, 6 arquitectura, 6 integración, 221 seguridad/API).
- Nota de crédito de motivo 13 (ajuste de cuotas): sin líneas, con las cuotas nuevas e importe total cero; aceptada en el beta. Pruebas: 578 pasan (343 unitarias, 6 arquitectura, 6 integración, 223 seguridad/API).
- Pendiente: entrega inicial, detracción y retención; motivos 11–12 de notas.

## Riesgos y deuda (resumen actual)
- Valores `Pending` en reglas: IVAP 4 %, ICBPER S/ 0,50, plazo de boletas (ver `/api/v1/rules`).
- Aceptación de SUNAT confirmada **solo en el beta** para factura, boleta y resumen simples; producción sin probar.
- Auditoría fuera de la transacción de negocio (ADR-012) hasta el outbox; sin outbox, bus ni S3 en código; CI sin ejecutar en GitHub; cobertura sin medir.
- ISC, ICBPER, IVAP y exportación: el motor tributario los calcula parcialmente y el generador UBL no los emite aún.
- Notas de crédito/débito esperan el CDR (Fase 4).

## Pendientes
1. Commit y push.
2. ISC, ICBPER, IVAP, exportación y operaciones gratuitas con valor referencial en el UBL.
3. Prueba en el beta de SUNAT (con credenciales del usuario), bajas y notas, PDF, retención del outbox.
4. Outbox + bus + S3 de código; ejecutar el CI; medir cobertura.
5. PDF y renderizado del QR.
