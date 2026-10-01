# ADR-017: Firma, empaquetado, CDR y canal de envío

- Estado: Aceptada · Fecha: 2026-10-01

## Firma XMLDSig (`IXmlSigner`)
- Firma envuelta (*enveloped*) sobre el documento completo, una sola firma dentro de `ext:UBLExtensions/ext:UBLExtension/ext:ExtensionContent`, `Reference URI=""`, canonicalización C14N 1.0 y certificado X.509 en `KeyInfo` (Manual del programador, S04).
- Algoritmo por defecto **RSA-SHA256/SHA-256**; **RSA-SHA1/SHA-1** disponible solo para compatibilidad (el ejemplo de 2017 lo usa). Las reglas de validación no fijan el algoritmo, de modo que la aceptación de SUNAT debe confirmarse en el beta (R-032).
- Se rechazan certificados sin clave privada, no vigentes, RSA de menos de 2048 bits, documentos mal formados, sin extensión vacía o ya firmados. `Verify` exige una única firma con referencia vacía y valida contra el certificado incluido; **no decide la confianza de la cadena** (eso corresponde al almacén de certificados).
- El `DigestValue` se expone para imprimirlo en el QR.
- El XML firmado valida contra el XSD oficial UBL 2.1 junto con el XSD de xmldsig.

## Empaquetado (`ICpePackager`)
- ZIP con una única entrada `{nombre}.xml` (UTF-8 sin BOM). Nombre seguro (`^[A-Za-z0-9._-]{5,80}$`). Al descomprimir se rechazan archivos con más de una entrada, tamaño descomprimido superior a 20 MB, nombres con `/`, `\` o `..` y archivos corruptos.

## CDR (`ICdrParser`)
- Lee el `ApplicationResponse` (identificador de proceso, fechas de recepción y respuesta, RUC de SUNAT y del contribuyente, documento, código, descripción y notas). Sin DTD ni entidades externas.
- **Conservador**: solo el código de respuesta 0 es aceptado (con notas: aceptado con observaciones); cualquier otro valor es rechazo; un código no numérico invalida el CDR. Las notas sin código se conservan.
- `SunatCodes.Classify` agrupa los códigos por los rangos del manual (excepción de SUNAT, excepción del contribuyente, rechazo, observación). `IsRetryable` solo es verdadero para el rango 0100–0999.

## Canal de envío (`ICpeSubmissionChannel`, `SunatSoapChannel`)
- SOAP 1.1 hacia `billService`: `sendBill`, `sendSummary`, `getStatus`, con WS-Security UsernameToken (usuario = RUC + usuario SOL). `sendPack` y `getStatusCdr` no se implementan todavía.
- No lanza excepciones por fallos remotos: devuelve `ChannelReply` (`CdrReceived`, `TicketIssued`, `InProgress`, `Fault`, `Unreachable`). Los fallos de red, tiempo de espera, 5xx y respuestas ilegibles son `Unreachable` y reintentables; un 200 sin CDR válido **nunca** cuenta como aceptado.
- Reintento: falla de lado `Server` o código 0100–0999. El resto (1000–1999 y 4xx) no se reintenta sin cambiar el paquete.
- Seguridad: HTTPS obligatorio (HTTP solo en loopback para simuladores), respuesta limitada a 40 MB, sin DTD, la contraseña SOL no se imprime (`ToString` redactado) ni aparece en errores, validación previa de parámetros (el servicio lanza excepción si falta alguno).
- El endpoint se pasa de forma explícita (`SunatChannelOptions.Production` o `.Beta`); nunca se elige por defecto. **El beta es solo para pruebas funcionales, no para carga** (contexto maestro).
- Supuestos por confirmar contra el beta (R-034): contenido en base64 en línea en vez de adjunto `cid:`, y `SOAPAction` vacío. Hasta entonces las pruebas usan un simulador sin red.

## Ciclo de vida del documento electrónico (`IEDocumentStateMachine`)
- Función pura de transición: `Pending → ReadyToSend → Sending → (AwaitingTicket) → Accepted | AcceptedWithObservations | Rejected`, más `Failed` (falla permanente o intentos agotados; solo `ManualRetry` lo deja). Máximo 5 envíos por documento.
- Aceptado, aceptado con observaciones y rechazado son **terminales**: cualquier evento posterior devuelve `SF-CPE-004` (el CPE aceptado es inmutable). Un rechazo no se edita: se emite un documento nuevo.
- Una falla transitoria en `Sending` vuelve a `ReadyToSend`; reenviar el mismo paquete cuando SUNAT pudo haberlo recibido es un supuesto **sin verificar** (la respuesta de SUNAT a un duplicado y `getStatusCdr` se probarán en el beta, R-034). Un trabajador que muera en `Sending` deja el resultado desconocido: debe consultarse, no asumirse.
- La persistencia (historial de estados por documento, RLS) y el worker llegan con el almacén de certificados y la tubería.
