# Hallazgos contra el servicio beta de SUNAT

Prueba funcional (no de carga) del 2026-10-01 con `tools/SecureFact.BetaSmoke`: una factura generada, firmada con un certificado **autofirmado**, empaquetada y enviada con `sendBill` a `https://e-beta.sunat.gob.pe/ol-ti-itcpfegem-beta/billService`. Las credenciales se leen de variables de entorno (`SF_BETA_RUC`, `SF_BETA_USER`, `SF_BETA_PASSWORD`) y nunca se guardan ni se imprimen.

## Confirmado
- **Transporte** (R-034): el sobre SOAP 1.1 con WS-Security UsernameToken (usuario = RUC + usuario SOL), el paquete en **base64 en línea** (sin adjunto `cid:`) y `SOAPAction` vacío son aceptados.
- **Firma** (R-032): XMLDSig envuelta, C14N 1.0, **RSA-SHA256 / SHA-256**, aceptada por el beta; el beta no exige un certificado de una entidad certificadora reconocida.
- La respuesta llega como ZIP con el CDR; el CDR de aceptación trae código `0` y «La Factura numero F001-N, ha sido aceptada». Sin observaciones tras las correcciones de abajo.

## Errores que el beta destapó (y se corrigieron)
| Código | Causa | Corrección |
|--------|-------|-----------|
| **3244** (rechazo) | Falta la forma de pago de la factura (error desde el 01.01.2022; la hoja `Factura2_0` la marca «C» pero es obligatoria en la práctica) | `cac:PaymentTerms` con `cbc:ID` = `FormaPago` y `cbc:PaymentMeansID` = `Contado`, solo en facturas. El crédito (cuotas) queda rechazado por el generador (R-042) |
| 4252 / 4255 / 4256 (observaciones) | Valores de atributos distintos de los de la hoja de reglas | `listName` «Tipo de Documento» y «Afectacion del IGV»; en `cac:TaxScheme/cbc:ID`: `schemeName` «Codigo de tributos», `schemeAgencyName` «PE:SUNAT»; los números de identidad llevan `schemeName` «Documento de Identidad», `schemeAgencyName` y `schemeURI` del catálogo 06 |

## Particularidades del CDR del beta (R-043)
- El ZIP trae además una carpeta vacía `dummy/` junto al XML `R-{RUC}-{tipo}-{serie}-{número}.xml`: el desempaquetador ignora entradas de directorio vacías y sigue exigiendo exactamente un archivo.
- `cbc:IssueDate` llega como marca de tiempo completa (`2026-10-01T11:42:05`) y `cbc:IssueTime` como `00:00:00`: el analizador acepta ambas formas.
- `ReferenceID` del CDR de factura = `SERIE-NÚMERO` (confirma el supuesto de R-038 para facturas). El CDR de un resumen sigue sin probarse.

## Lección
La hoja de reglas **no basta** para saber qué es obligatorio: las condiciones «C» pueden ser error por fecha de vigencia. Cada cambio del generador debe probarse contra el beta (de forma funcional, nunca con carga).
