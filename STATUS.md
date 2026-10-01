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

## Riesgos y deuda (resumen actual)
- Valores `Pending` en reglas: IVAP 4 %, ICBPER S/ 0,50, plazo de boletas (ver `/api/v1/rules`).
- Aceptación de SUNAT del XML y de la firma no probada (solo XSD y etiquetas obligatorias; el canal SOAP solo se probó con simulador); algoritmo de firma, base64 en línea y `SOAPAction` por confirmar en el beta (R-032, R-034).
- Auditoría fuera de la transacción de negocio (ADR-012) hasta el outbox; sin outbox, bus ni S3 en código; CI sin ejecutar en GitHub; cobertura sin medir.
- Descuentos y cargos (línea y globales con base mixta), ISC, ICBPER, IVAP, exportación: el motor tributario los calcula parcialmente y el generador UBL no los emite aún. El caso oficial de la guía (descuentos porcentuales por línea y descuento global sobre base mixta) requiere factor de descuento en el motor.
- Notas de crédito/débito esperan el CDR (Fase 4).

## Pendientes
1. Commit y push.
2. Factor de descuento porcentual y descuentos globales por categoría (casos oficiales de la guía) en TaxEngine y UBL.
3. Almacén de certificados (PFX cifrado, RUC en OU, alertas de vencimiento), máquina de estados del documento electrónico, tubería con worker, prueba en el beta de SUNAT (con credenciales del usuario).
4. Outbox + bus + S3 de código; ejecutar el CI; medir cobertura.
5. PDF y renderizado del QR.
