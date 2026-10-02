# Hallazgos contra el servicio beta de SUNAT

Prueba funcional (no de carga) del 2026-10-01 con `tools/SecureFact.BetaSmoke`: una factura generada, firmada con un certificado **autofirmado**, empaquetada y enviada con `sendBill` a `https://e-beta.sunat.gob.pe/ol-ti-itcpfegem-beta/billService`. Las credenciales se leen de variables de entorno (`SF_BETA_RUC`, `SF_BETA_USER`, `SF_BETA_PASSWORD`) y nunca se guardan ni se imprimen.

## Confirmado
- **Transporte** (R-034): el sobre SOAP 1.1 con WS-Security UsernameToken (usuario = RUC + usuario SOL), el paquete en **base64 en línea** (sin adjunto `cid:`) y `SOAPAction` vacío son aceptados.
- **Firma** (R-032): XMLDSig envuelta, C14N 1.0, **RSA-SHA256 / SHA-256**, aceptada por el beta; el beta no exige un certificado de una entidad certificadora reconocida.
- La respuesta llega como ZIP con el CDR; el CDR de aceptación trae código `0` y «La Factura numero F001-N, ha sido aceptada». Sin observaciones tras las correcciones de abajo.

- **Boleta con `sendBill`** (R-039): el beta aceptó una boleta (03, serie B001, DNI) enviada directamente («La Boleta numero B001-N, ha sido aceptada»). Que producción lo permita para el emisor **no está confirmado**; el pipeline sigue enviando boletas por resumen.
- **Resumen diario** (R-040, R-041): `sendSummary` devolvió ticket, `getStatus` devolvió el CDR en el primer intento (código `0`, «El Resumen diario RC-…, ha sido aceptado») y su `ReferenceID` es el identificador `RC-AAAAMMDD-n`. Aceptó un resumen cuyo nombre lleva la **fecha de generación** (hoy) con las boletas del día anterior (`ReferenceDate` = ayer): resuelto el supuesto de R-040.

- **Notas** (R-045): nota de crédito (motivo 01) y de débito (motivo 02) de una factura, y nota de crédito (motivo 07) de una boleta, enviadas con `sendBill` después de aceptarse el original, aceptadas sin observaciones. Series `FC01`, `FD01` y `BC01`.
- **HTTP 401 intermitente**: una de las llamadas (un `sendBill` justo después de otro con las mismas credenciales) devolvió 401 y la siguiente, con las mismas credenciales, fue aceptada. El canal sigue tratando el 401 como no reintentable: reintentar con una clave SOL incorrecta podría bloquear la cuenta.

- **Comunicación de baja** (R-046): una factura aceptada y su baja (`sendSummary` + `getStatus`): aceptada con código 0; `ReferenceID` = `RA-AAAAMMDD-n`; el CDR trae `cbc:IssueDate` vacío (el analizador lo admite).
- **Baja de boleta con resumen de estado 3** (R-041): una boleta aceptada con `sendBill` y un `RC` posterior con esa boleta en estado 3 (importes originales repetidos) fue aceptado con código 0. El beta no comprueba que la boleta haya sido informada en un resumen antes (producción sí, regla 2989 para las notas).
- **HTTP 401 tras una llamada reciente**: dos veces, una segunda llamada hecha a los pocos segundos de la primera con las mismas credenciales recibió 401; con unos segundos de espera fue aceptada. Parece un límite de ritmo del beta, no un error de credenciales.

- **Descuentos y cargos** (R-047): una factura con descuentos y cargos de línea (00, 47, 01, 48) y globales (02, 49, 03, 50), más una línea exonerada, aceptada con código 0 y sin observaciones; igual como boleta con `sendBill`; un resumen diario con cargo y descuento que no afectan la base, aceptado.

- **Venta al crédito** (R-048): una factura con forma de pago `Credito` y dos cuotas (`Cuota001`, `Cuota002`, con monto y fecha) aceptada con código 0, sin observaciones.

- **Nota de crédito de motivo 13** (R-049): sobre una factura al crédito aceptada, con dos cuotas nuevas, una línea gravada de valor cero e importe total cero: aceptada, código 0, sin observaciones.

- **IVAP** (R-051): factura con una línea IVAP (afectación 17, tributo 1016, leyenda 2007) y nota de crédito de motivo 12 sobre ella: aceptadas con código 0, sin observaciones. El resumen diario con una boleta IVAP fue aceptado con la **observación 4019** («El calculo del IGV no es correcto … codigo tributo: 1000») pese a declarar el tributo 1016 con su tasa; el beta parece aplicar la comprobación del IGV. No bloquea.

- **Exportación** (R-052): factura de exportación de bienes (0200, línea de afectación 40, tributo 9995, adquirente tipo 0) y nota de crédito de motivo 11 sobre ella: aceptadas con código 0, sin observaciones. Con un adquirente RUC el beta rechaza con **2800** (`cbc:ID/schemeID` valor 6), como dice la regla.

## Errores que el beta destapó (y se corrigieron)
| Código | Causa | Corrección |
|--------|-------|-----------|
| **3244** (rechazo) | Falta la forma de pago de la factura (error desde el 01.01.2022; la hoja `Factura2_0` la marca «C» pero es obligatoria en la práctica) | `cac:PaymentTerms` con `cbc:ID` = `FormaPago` y `cbc:PaymentMeansID` = `Contado`, solo en facturas. El crédito (cuotas) queda rechazado por el generador (R-042) |
| **2992** (rechazo) | La línea exonerada (tributo 9997) no llevaba `cbc:Percent`; el defecto estaba oculto porque solo se había probado con líneas gravadas | Toda línea lleva su tasa: la del IGV en gravadas y gratuitas 11–16, `0.00` en el resto |
| 4252 / 4255 / 4256 (observaciones) | Valores de atributos distintos de los de la hoja de reglas | `listName` «Tipo de Documento» y «Afectacion del IGV»; en `cac:TaxScheme/cbc:ID`: `schemeName` «Codigo de tributos», `schemeAgencyName` «PE:SUNAT»; los números de identidad llevan `schemeName` «Documento de Identidad», `schemeAgencyName` y `schemeURI` del catálogo 06 |

## Particularidades del CDR del beta (R-043)
- El ZIP trae además una carpeta vacía `dummy/` junto al XML `R-{RUC}-{tipo}-{serie}-{número}.xml`: el desempaquetador ignora entradas de directorio vacías y sigue exigiendo exactamente un archivo.
- `cbc:IssueDate` llega como marca de tiempo completa (`2026-10-01T11:42:05`) y `cbc:IssueTime` como `00:00:00`: el analizador acepta ambas formas.
- `ReferenceID` del CDR de factura = `SERIE-NÚMERO` (confirma el supuesto de R-038 para facturas). El CDR de un resumen sigue sin probarse.

## Lección
La hoja de reglas **no basta** para saber qué es obligatorio: las condiciones «C» pueden ser error por fecha de vigencia. Cada cambio del generador debe probarse contra el beta (de forma funcional, nunca con carga).
