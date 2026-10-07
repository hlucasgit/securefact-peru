# STATUS — 2026-10-01 (dos horas autónomas)

## Estado general
Fase 0 completa. **Fase 1 completa** (outbox, bus de mensajes y almacenamiento de objetos en código; el CI de GitHub corre en cada push y pasa, ver «CI»). **Fase 2 en curso**: ya existen el motor tributario, las series, la numeración atómica y la emisión idempotente de facturas/boletas.

## Novedades de las dos últimas horas
- **Rules** (`IRuleProvider`): plazos y tasas como reglas versionadas con estado `Verified`/`Pending`; versiones publicadas inmutables; `GET /api/v1/rules`. **Billing ya no acepta tasas del cliente** (las resuelve por fecha de emisión); prueba con tasas falsas.
- **Customers** y **Products**: datos maestros por tenant con validación contra los catálogos 06 y 07, identidad inmutable, sin borrado, búsqueda con comodines escapados. Los documentos pueden referenciar `customerId` (instantánea del adquirente).
- **UBL**: generador de XML 2.1 sin firmar para factura y boleta (líneas gravadas, exoneradas, inafectas, gratuitas). **Valida contra el XSD oficial UBL 2.1** y contiene **todas las etiquetas obligatorias de las hojas `Factura2_0` y `Boleta2_0`** del libro oficial (la prueba lee el libro versionado). Todo lo demás falla con `SF-CPE-002` en lugar de emitir XML engañoso. **ADR-016**.
- Hallazgos: el libro de reglas es la fuente más fiable (la guía PDF de 2017 usa una estructura anterior); el ejemplo de la guía firma con RSA-SHA1 (algoritmo vigente por confirmar, R-032); `EF.Functions.ILike` sin carácter de escape no escapa comodines.
- **Pruebas: 730 pasan en Release con warnings-as-errors** (411 unitarias, 6 de arquitectura, 6 de integración, 307 de seguridad/API). Cobertura: 95.8 % de líneas y 85.1 % de ramas (ver «Cobertura»).

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
- Acumulado de notas de crédito (control propio): las notas vigentes de un documento no acreditan más que él; no cuentan las rechazadas ni las anuladas (ADR-023). Pruebas: 582 pasan (343 unitarias, 6 arquitectura, 6 integración, 227 seguridad/API).
- Motivo 11 (ajuste de exportación): ver ADR-028.

## IVAP
- Facturas, boletas y notas con líneas IVAP (afectación 17, tributo 1016, tasa 4 % verificada contra SUNAT, leyenda 2007), resumen diario con el tributo 1016, PDF con fila «IVAP» y nota de crédito de motivo 12. Aceptados en el beta (el resumen con observación 4019); ADR-027. Pruebas: 596 pasan (353 unitarias, 6 arquitectura, 6 integración, 231 seguridad/API).
- QR de un comprobante IVAP: 0.00 en el campo IGV (el anexo pide solo la «sumatoria IGV»); ver ADR-027.

## Exportación
- Facturas de exportación de bienes (`operationTypeCode` `0200`: líneas con afectación 40, tributo 9995, adquirente del exterior sin RUC), PDF con fila «Op. exportación» y nota de crédito de motivo 11. Aceptadas en el beta (con RUC, rechazada con 2800 como dice la regla); ADR-028. Pruebas: 618 pasan (369 unitarias, 6 arquitectura, 6 integración, 235 seguridad/API).
- Exportación de servicios (0201, 0203, 0204, 0206, 0207 y 0208) en facturas, con país del uso en 0201 y 0208 y adquirente según el tipo (ADR-031); las seis aceptadas en el beta. Pruebas: 650 pasan (388 unitarias, 6 arquitectura, 6 integración, 250 seguridad/API).
- Boletas de exportación en los siete tipos (ADR-031): adquirente nunca con RUC, resumen diario con el valor de exportación bajo el código 04; aceptadas en el beta. Pruebas: 660 pasan (397 unitarias, 6 arquitectura, 6 integración, 251 seguridad/API).
- Boletas (y notas de boletas) en soles de más de S/ 700 deben identificar al adquirente: Billing lo rechaza al emitir con el monto de la regla versionada `billing.receipt_identification_threshold` (R-056). Pruebas: 663 pasan (397 unitarias, 6 arquitectura, 6 integración, 254 seguridad/API).
- Leyendas de las ventas exoneradas 2001, 2002, 2003 (Amazonía) y 2008 (zona comercial de Tacna) en facturas y boletas (ADR-032); aceptadas en el beta. Pruebas: 673 pasan (404 unitarias, 6 arquitectura, 6 integración, 257 seguridad/API).
- Exportación de hospedaje (0202) y de paquete turístico (0205) en facturas, con el huésped no domiciliado en cada línea (ADR-033); aceptadas en el beta. Pruebas: 680 pasan (408 unitarias, 6 arquitectura, 6 integración, 260 seguridad/API).
- Tramos y vehículos del transporte de carga (1004): un tramo es un registro con su vehículo, emitido como `cac:Consignment` en `cac:Shipment` (ADR-034); aceptados en el beta. Pruebas: 685 pasan (411 unitarias, 6 arquitectura, 6 integración, 262 seguridad/API).
- Pendientes de la lista de ampliaciones: ninguno; lo que falta está en «Riesgos y deuda».

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
- Entrega inicial (`initialPayment`): parte pagada al emitir; las cuotas suman lo pendiente (ADR-026, S23); aceptada en el beta.
- Nota de crédito de motivo 13 (ajuste de cuotas): sin líneas, con las cuotas nuevas e importe total cero; aceptada en el beta. Pruebas: 578 pasan (343 unitarias, 6 arquitectura, 6 integración, 223 seguridad/API).
- Detracción y retención del IGV en facturas (ADR-029): porcentajes, montos y cuenta son datos del emisor que la plataforma comprueba; el crédito deja ambas fuera del monto neto pendiente; aceptadas en el beta (que no comprueba el padrón de agentes). Pruebas: 627 pasan (374 unitarias, 6 arquitectura, 6 integración, 241 seguridad/API).
- La empresa guarda su cuenta de detracciones (`detractionAccount`); una detracción sin cuenta propia usa la de la empresa y la factura conserva la usada. Pruebas: 629 pasan (374 unitarias, 6 arquitectura, 6 integración, 243 seguridad/API).
- Tipos de operación de detracción 1002 (recursos hidrobiológicos), 1003 y 1004 (transporte): el tipo sigue al código de la detracción, con datos de pesca o de viaje por línea (ADR-030); una factura de cada tipo aceptada en el beta. Pruebas: 640 pasan (381 unitarias, 6 arquitectura, 6 integración, 247 seguridad/API).

## CI
- GitHub Actions (`ci.yml`) corre en cada push a `main` y en cada pull request: build en Release y pruebas (6 de arquitectura, 411 unitarias, 262 de seguridad/API, 6 de integración), paquetes vulnerables, SBOM, gitleaks y construcción y escaneo Trivy de las dos imágenes. Primera corrida completa en verde el 2026-10-05.
- Hallazgos al ponerlo en verde: la tarea de contenedores no arrancaba porque `aquasecurity/trivy-action@0.28.0` ya no existe (se reescribieron sus etiquetas tras el ataque a la cadena de suministro de marzo de 2026; ahora `v0.36.0`, fijada a un commit, igual que gitleaks); dos corridas fallaban en Release por advertencias tratadas como errores que Debug no muestra; y una prueba del worker era intermitente porque el worker consultaba también los comprobantes de un resumen, que esperan sin ticket propio (ahora solo consulta quien tiene el ticket).
- Pendiente: las acciones oficiales siguen en etiquetas mayores (`@v4`; Dependabot propone las nuevas) y GitHub avisa de que Node 20 está en desuso.

## Mensajería
- **Bus de mensajes** (ADR-035): `IMessageBus` en `SharedKernel` y su implementación sobre RabbitMQ (`SecureFact.Messaging.RabbitMq`, cliente oficial 7.2.2): un *exchange* `topic` durable, mensajes persistentes con el id del outbox y el tenant, confirmaciones del publicador y reconexión tras fallos. Los workers publican cada evento del outbox al bus **solo si `RabbitMq:Host` está configurado** (`docker-compose.yml` lo configura); sin él, el outbox entrega solo a los consumidores propios.
- **Outbox con varios consumidores**: primero los consumidores del tipo de evento (CPE), después el bus; el primer fallo detiene la entrega y el reintento repite todo (los consumidores son idempotentes; los suscriptores externos descartan duplicados por `message-id`).
- **Retención del outbox**: los mensajes entregados se purgan a los `Outbox:RetentionDays` días (30 por defecto) cada hora, por una función de PostgreSQL que es la única vía para borrar; pendientes y muertos nunca se borran y la tabla sigue siendo de solo anexar para todos los demás.
- Pruebas con un RabbitMQ real (Testcontainers), de extremo a extremo con el host de los workers y de la purga contra PostgreSQL. Pruebas: 714 pasan al cerrar esta etapa (730 con el archivo de objetos, ver «Almacenamiento de objetos»).
- Pendiente: un solo origen (Billing) y un solo evento publicados; la auditoría por el outbox; suscriptores de referencia.

## Almacenamiento de objetos
- **`IObjectStorage` y adaptador S3** (ADR-036): escritura única, SHA-256 calculado antes de subir y comprobado al leer, enlaces de descarga prefirmados, cifrado y Object Lock configurables; validado contra un SeaweedFS real. Sin *bucket* configurado solo se admite el almacenamiento en memoria fuera de producción.
- **Archivo del XML firmado y del CDR**: cada documento electrónico encola, en la misma transacción que lo guarda, un evento en el **outbox propio del módulo CPE**; un consumidor idempotente sube el archivo y lo registra en `cpe.archived_file` (solo inserción, un archivo por clase y documento). Una red de seguridad en el worker encola lo que no tenga copia (documentos anteriores y eventos perdidos). `GET /api/v1/electronic-documents/{id}/archive` lista los archivos con enlaces de 5 minutos.
- **`PostgresOutboxSource`**: el SQL del outbox ya no se copia por módulo; Billing y CPE lo comparten.
- **La base sigue siendo la fuente** (fase 1 de ADR-005): los bytes siguen también en `signed_xml` y `cdr_zip`. Quitarlos es la fase 2, con condiciones en el ADR-036.
- Pendiente: conciliación periódica del contenido, Object Lock y período de retención legal sin probar ni confirmar, fase 2.

## Interfaz web (primera entrega)
- **`web/`** (React + TypeScript + Vite, ADR-038): ingreso con segundo factor, panel, empresas (datos, establecimientos, series, certificado digital y credenciales SOL), clientes, productos, **emisión** de facturas y boletas (ítems con afectación, descuento, ISC y bolsas de plástico; venta al crédito), documentos (lista, detalle, generar y firmar, enviar, consultar, reintentar, XML, PDF, CDR, archivo conservado, baja), **notas** de crédito y débito, resumen diario, usuarios, segundo factor y reglas. Claro, oscuro y móvil.
- La interfaz **no calcula impuestos**: muestra lo que calcula la API. Un solo origen (proxy de Vite / nginx), CSP estricta, sesión con el *access token* en memoria.
- Empaquetada (`web/Dockerfile`, servicio `web` de `docker-compose.yml`) y en el CI (lint, tipos, 16 pruebas, compilación, `npm audit`, Trivy).
- **Pruebas de extremo a extremo** (Playwright, ADR-040): 64 recorridos contra la API real, los workers y el **simulador de SUNAT** (ADR-039), 3 corridas seguidas sin fallos; el CI las corre (trabajo `e2e`). Encontraron y corrigieron un 500 en inicios de sesión simultáneos de una misma cuenta.
- Pendiente: el envío a SUNAT desde la interfaz solo está probado contra el simulador (el beta, por API), administración de plataforma, importación masiva, planes, recuperación de contraseña, *refresh token* en cookie `HttpOnly`.

## Administración de plataforma
- **Inquilinos** (ADR-041): lista con búsqueda y estado, alta con su propietario, detalle con usuarios, y **suspender, reactivar y cerrar** con motivo (el cierre es definitivo y pide escribir el nombre). Un inquilino suspendido o cerrado no ingresa, no renueva y no usa la API (el estado se cachea 10 s por proceso); todo cambio queda en la auditoría. El soporte lee y no cambia.
- **Auditoría** con filtros y **verificación de la cadena**, y **mensajes fallidos** con reencolado, en la interfaz.
- **Workers**: un inquilino suspendido o cerrado no recibe envíos ni resúmenes nuevos hacia SUNAT; el sondeo de tickets y el archivo continúan.
- **Planes y consumo** (ADR-042): catálogo de planes con tope de empresas, de usuarios activos y de comprobantes por mes (vacío es ilimitado); toda cuenta existente pasó al plan **Piloto**, sin límites. La plataforma crea planes y los asigna; Organizations, Identity y Billing rechazan lo que el plan no permite (`SF-PLAN-001`) y el conteo mensual es exacto con solicitudes simultáneas. La cuenta ve su plan y su consumo.
- **Revendedores** (ADR-043): entidad de la plataforma con sus administradores (rol `ResellerAdmin`, permisos propios), que abren cuentas para sus clientes con un plan que pueden asignar (catálogo público u ofertas privadas suyas), crean el propietario una sola vez, cambian planes y ven el consumo, y **solo ven las cuentas que llevan su id** (las de otro o de nadie contestan 404). La plataforma los crea, los desactiva y mueve cuentas entre ellos; un revendedor desactivado no ingresa y sus clientes siguen funcionando.
- **Detracción, retención y exportación en la interfaz** (ADR-046): el formulario de emisión elige el tipo de operación (venta interna o exportación 0200, 0201, 0203, 0204, 0206, 0207, 0208, con afectación 40 fija, adquirente del exterior y país del uso) y, en facturas en soles, una detracción o una retención. El monto lo da `POST /api/v1/documents/preview`, que calcula como la emisión y no emite nada; el detalle del comprobante muestra la operación. Con el ADR-048, toda operación que emite la API se emite también desde el formulario.
- **Leyendas de venta exonerada y transporte de carga en la interfaz** (ADR-047): las leyendas 2001, 2002, 2003 y 2008 se ofrecen cuando alguna línea está exonerada; con la detracción 027 cada ítem pide los datos del transporte (origen, destino, viaje, valores referenciales y, si se quiere, tramos con vehículo), que se copian del primer ítem, y el detalle del comprobante los muestra.
- **Pesca, hospedaje y paquete turístico en la interfaz** (ADR-048): la detracción 004 pide en cada ítem la embarcación, la especie, la descarga y las toneladas; el hospedaje (0202) y el paquete (0205), solo de facturas, el huésped no domiciliado y, en el hospedaje, su estadía. Se copian del primer ítem y el detalle del comprobante los muestra.
- **Entrega inicial del crédito y detracción de la nota de débito** (ADR-049): la hoja `NotaDebito2_0` de SUNAT tiene una sección de detracción y la de la nota de crédito no, así que solo una nota de débito sobre una factura en soles la lleva (aceptada en el beta el 2026-10-06). `POST /api/v1/notes/preview` y el campo `netPendingAmount` de las vistas previas dan el total, el monto sugerido y lo que las cuotas deben sumar. Con esto, todo lo que emite la API se emite desde el formulario.
- **Marca blanca** (ADR-044): cada revendedor pone nombre, color (con contraste mínimo de 4.5:1 con el texto blanco), logotipo (PNG, JPEG o WebP por su contenido, hasta 200 KB; sin SVG) y correo de soporte; la plataforma le asigna un dominio, y el ingreso en ese dominio ya muestra su marca, igual que el menú y el título para sus clientes. Siempre dice «Con tecnología SecureFact».
- **Suspensión por el revendedor** (ADR-045): suspende sus cuentas con un motivo auditado y reactiva las que él suspendió; no cierra y **no levanta una suspensión de la plataforma** (`SF-TEN-004`). La plataforma ve quién suspendió y puede tomar la suspensión del revendedor.
- Pendiente: aprovisionar el dominio (DNS y certificado, hoy del operador), correos y plantillas por revendedor, precios, comisiones y facturación de la plataforma a sus cuentas, «entrar como», exportar o dar de baja definitiva una cuenta.

## ISC e ICBPER
- **ISC** (al valor y de monto fijo) **e ICBPER** en el UBL de facturas, boletas, notas y resumen diario, y en el PDF (ADR-037): subtotales de línea y globales según las hojas de reglas; el ISC entra en la base del IGV de la línea. Aceptados por el beta de SUNAT (factura, boleta, notas y resumen).
- El monto por bolsa del ICBPER es `Verified` con el calendario de la Ley 30884 (S26): el beta **no** valida ese monto.
- Pendiente: sistema 03 del ISC (precio de venta al público), anticipos de ISC y otros tributos (9999).

## Cobertura
- Medida con coverlet (`coverage.runsettings`) sobre las cuatro suites juntas y unida con ReportGenerator: **95.8 % de líneas y 85.2 % de ramas** (96.0 % y 85.5 % antes del archivo en el almacén de objetos), el 2026-10-06. Excluye migraciones, fábricas de tiempo de diseño, herramientas y pruebas. El CI la mide en cada corrida, la publica en el resumen del job y como artefacto, y **falla bajo un piso de 95 % de líneas y 84 % de ramas** (`.github/scripts/check_coverage.py`; los pisos solo suben).
- Antes de este trabajo: 93.5 % y 82.3 %. Se agregaron pruebas donde la falta de cobertura era de riesgo: la guarda de inquilino de la capa de aplicación (`TenantDbContext`: escrituras y borrados de filas de otro inquilino, filas de la plataforma, API síncrona) y el intercepto de RLS, el manejador global de excepciones (que no filtra detalles internos), las reglas de entrada de empresas, establecimientos, clientes, productos, usuarios, series y notas, y los errores de refresco, MFA y restablecimiento de contraseña.
- Lo que sigue bajo: `Program` de la API (arranque, migraciones, exportador OTLP; 77 %) y el ensamblado de los workers (78 %), ramas de la API (68 %) y de `Products` (62 %), y los caminos de error por servicio no disponible de `ElectronicDocumentService`, `VoidService` y `SummaryService`.

## Riesgos y deuda (resumen actual)
- Valores `Pending` en reglas: plazo de boletas (ver `/api/v1/rules`). El monto del ICBPER ya es `Verified` (Ley 30884, ADR-037).
- Aceptación de SUNAT confirmada **solo en el beta** para factura, boleta y resumen simples; producción sin probar.
- Auditoría fuera de la transacción de negocio (ADR-012; el outbox ya permitiría un origen propio para ella); la base conserva los bytes del XML y del CDR hasta la fase 2 del archivo (ADR-036).
- ISC (sistemas al valor y de monto fijo) e ICBPER se emiten en el UBL (ADR-037); faltan el sistema 03 del ISC (precio de venta al público), los anticipos de ISC y otros tributos (9999).
- Notas de crédito/débito esperan el CDR (Fase 4).

## Pendientes
1. Commit y push.
2. Sistema 03 del ISC, anticipos y otros tributos (9999) en el UBL.
3. Prueba en el beta de SUNAT (con credenciales del usuario), bajas y notas, PDF.
4. Fase 2 del archivo: leer el XML y el CDR desde el almacén y dejar solo metadatos en la base (condiciones en ADR-036).
5. PDF y renderizado del QR.
