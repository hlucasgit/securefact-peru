# Changelog

Formato [Keep a Changelog](https://keepachangelog.com/es-ES/1.1.0/); versionado semántico cuando haya releases.

## [Unreleased]
### Added
- IVAP (arroz pilado): líneas con afectación 17 y tributo 1016 en facturas, boletas, notas y resumen diario, leyenda 2007, fila en el PDF y nota de crédito de motivo 12; tasa 4 % verificada; aceptado en el beta (ADR-027).
- Acumulado de notas de crédito: las notas de crédito vigentes de un documento no pueden acreditar más que el documento (`SF-BIL-010`); no cuentan las rechazadas ni las anuladas; comprobación atómica con bloqueo consultivo (ADR-023).
- Nota de crédito de motivo 13 (ajuste de montos y/o fechas de cuotas) sobre facturas al crédito: sin líneas, con las cuotas nuevas e importe total cero; aceptada en el beta (ADR-026).
- Venta al crédito con cuotas en facturas: validación del plan en Billing, UBL con `Credito` y `CuotaNNN`, PDF con las cuotas; aceptada en el beta (ADR-026).
- Descuentos y cargos de línea y globales en facturas y boletas: UBL con `AllowanceCharge`, base y factor, totales, resumen diario, PDF y Billing; aceptados en el beta (ADR-025). Las líneas exoneradas, inafectas y gratuitas llevan ahora su tasa (rechazo 2992 del beta).
- El PDF de un documento anulado (baja aceptada por SUNAT) lleva «ANULADO» en cada página (ADR-024).
- Billing rechaza notas sobre documentos anulados o con baja en curso (`SF-BIL-011`) mediante el puerto `IVoidStatusProvider` que implementa CpeEngine (ADR-024).
- Baja de boletas y de notas de boletas por resumen diario con líneas de estado 3 (`POST /api/v1/voids` genera `RA` y/o `RC` según el tipo); `summary_item.line_status`; aceptado en el beta (ADR-024).
- Comunicación de baja de facturas y notas de facturas: generador UBL 2.0 validado, creación por fecha de emisión con reglas de SUNAT, envío/seguimiento como el resumen, estado «anulado» derivado; aceptada en el beta (ADR-024).
- Notas de crédito y débito de boletas por resumen diario: líneas 07/08 en el resumen, la nota espera a que su boleta esté informada y el worker la incluye en un resumen posterior; aceptado en el beta (ADR-023).
- Notas de crédito y de débito (ADR-023): emisión en Billing (`POST /api/v1/notes`), UBL 2.1 validado, documento electrónico que espera a que el original sea aceptado, PDF y envío por `sendBill` para notas de facturas; aceptadas en el beta de SUNAT.
- `GET /api/v1/electronic-documents/{id}/pdf`: representación impresa A4 (emisor, adquirente, ítems, totales, importe en letras, QR y resumen de la firma), generada bajo demanda con los importes de Billing y sin tocar el documento electrónico.
- Primera aceptación real: factura enviada al **beta de SUNAT** y aceptada (CDR código 0, sin observaciones). Herramienta `tools/SecureFact.BetaSmoke`; correcciones de forma de pago (3244), atributos del UBL y lectura del CDR real (`docs/regulatory/beta-findings.md`).
- Representación impresa: importe en letras y renderizador PDF A4 con QR vectorial (sin integrar en la API todavía).
- Outbox transaccional (Billing → CPE): el documento emitido prepara su documento electrónico sin pasos manuales; entrega al menos una vez, reintentos con espera, mensajes muertos con reencolado por un operador, y corrección de la inanición del lote del worker (ADR-022).
- Worker de documentos electrónicos: resúmenes de días cerrados, envíos con espera exponencial, consulta de tickets y detección de envíos atascados con recuperación por un operador (ADR-021).
- Resumen diario de boletas: generador UBL 2.0 validado contra el XSD y la hoja oficial, resumen firmado, enviado con `sendSummary`, consulta del ticket con `getStatus`, boletas que siguen a su resumen y se liberan si se rechaza (ADR-020).
- Tubería del documento electrónico: preparar (UBL + firma con el certificado de la empresa), enviar a SUNAT con credenciales SOL cifradas, registrar el CDR validado, historial de estados, reintentos con espera exponencial y disparadores de inmutabilidad (ADR-019).
- Almacén de certificados digitales (`Certificates`): PKCS#12 cifrado en reposo, validación, un activo por empresa, alertas de vencimiento, permisos y auditoría (ADR-018).
- Motor CPE: firma XMLDSig (`IXmlSigner`), empaquetado ZIP seguro, parser de CDR con clasificación de códigos SUNAT y canal SOAP `billService` (`sendBill`, `sendSummary`, `getStatus`) probado con un simulador sin red (ADR-017).
- Rules (reglas con vigencia servidas por `IRuleProvider`; Billing deja de aceptar tasas del cliente), Customers y Products (datos maestros con catálogos oficiales), referencia de cliente en documentos, y generador UBL 2.1 sin firmar de factura/boleta validado contra el XSD oficial y las etiquetas obligatorias del libro de reglas.
- Catalogs (42 catálogos oficiales importados del libro de reglas con vigencias y versiones, API de lectura, pruebas anti-deriva con TaxEngine/Billing) y CpeEngine (contenido del QR según el Anexo N.° 6). Activos regulatorios oficiales versionados con hashes.
- TaxEngine (cálculo puro con `decimal`, derivado de las reglas oficiales de factura; 40 pruebas incl. 3 000 documentos aleatorios) y Billing (series por tipo, numeración atómica sin huecos, creación idempotente de facturas/boletas, documentos insert-only, endpoints `/api/v1/series` y `/api/v1/documents`).
- Identity/RBAC (login, sesiones con rotación y detección de reutilización, MFA TOTP, recuperación de contraseña, usuarios y roles con anti-escalada), módulo Audit (cadena de hashes append-only con verificación), módulo Organizations (empresas y establecimientos), OpenTelemetry y logging estructurado sin datos sensibles, `SecretProtector` con cifrado de envoltura, stack completo en `docker compose` con migraciones y bootstrap.
- Tenancy: `SecureFact.Platform` (ámbito de datos, RLS), módulo `Tenancy` con registro de tenants, migración inicial con RLS forzado y 21 pruebas de seguridad cross-tenant contra PostgreSQL real.
- Fase 0: baseline normativo, matriz, C4, ERD, ADR-001…010, documentación PSE, roadmap.
