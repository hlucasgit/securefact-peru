# ADR-071: Exportación de comprobantes de un período

- Estado: Aceptada · Fecha: 2026-10-11
- Relacionadas: ADR-036 (archivo del XML firmado y del CDR), ADR-066 (API pública), ADR-045/041 (quién lee los datos de una cuenta).

## Contexto
El contador de una empresa pide «los XML y los CDR de septiembre». Hasta hoy cada comprobante se bajaba uno por uno (`/xml`, `/cdr`, `/pdf`). La obligación de conservar el XML firmado y el CDR es de la empresa, no de la plataforma, y una cuenta que se va (o que cambia de proveedor) tiene que poder llevarse lo suyo. Es también la base de un cierre de cuenta con sus datos (queda pendiente).

## Decisión
- **`GET /api/v1/electronic-documents/export?companyId&from&to[&documentTypeCode]`** devuelve un ZIP del período (por fecha de emisión) de una empresa, con permiso `documents.read`, por cuenta (la plataforma no lo usa: es de una cuenta, y otra cuenta recibe 404):
  - `xml/<RUC>-<tipo>-<serie>-<número>.xml`: el XML firmado **tal como se guarda y se envió**;
  - `cdr/R-<RUC>-<tipo>-<serie>-<número>.zip`: el CDR **tal como lo devolvió SUNAT**, solo de los que ya lo tienen;
  - `indice.csv` (UTF-8 con BOM, abre en Excel): archivo, tipo, serie, número, fecha, estado, código y descripción de SUNAT, tipo y número de documento y nombre del comprador, moneda, total, huella (`DigestValue`) y si tiene CDR.
- **Sin cálculo ni transformación**: no se vuelve a generar nada; son los bytes inmutables (ADR-036, fase 1: la fuente sigue siendo la base). No incluye PDF (se genera bajo demanda, uno por comprobante).
- **Acotado**: hasta **500 comprobantes** y **366 días** por exportación; si el período trae más, `422` `SF-CPE-013` pide un período más corto o un solo tipo; un período al revés, demasiado largo o un tipo que no es de dos caracteres es `SF-CPE-012`. Se arma en memoria (pocos megabytes) y se devuelve de una vez, sin trabajo en segundo plano ni enlace: lo que se pide se baja.
- **El índice no ejecuta lo que escribió un comprador**: una celda que empieza con `=`, `+`, `-`, `@`, tabulación o retorno de carro se escribe como texto (con un apóstrofo) y las comas y comillas se escapan (CSV de RFC 4180).
- **Auditado**: cada exportación deja un evento `cpe.documents.exported` en la cadena de la cuenta con el período, el tipo y cuántos comprobantes y CDR llevó; nunca el contenido ni los datos de los compradores.
- **Interfaz**: «Exportar» en *Documentos*: empresa, desde, hasta (por omisión el mes en curso) y tipo; baja `comprobantes-<RUC>-<desde>-<hasta>.zip`.

## Verificación
`DocumentExportApiTests` (API real con PostgreSQL): el ZIP trae el XML de cada comprobante, el CDR solo del aceptado y el índice con sus filas (estado, comprador entre comillas, total 118.00, `Accepted` y `ReadyToSend`); el XML del ZIP es idéntico al de `/xml`; un comprador cuyo nombre empieza con `=` queda como texto; el tipo y las fechas acotan y un período vacío es un ZIP con solo el índice; período al revés, de 366 días o tipo erróneo se rechazan y 365 días pasa; la empresa de otra cuenta es 404, un rol sin lectura de documentos 403, uno de solo lectura 200 y sin sesión 401; la auditoría lleva el período y la cantidad y nada del comprador. Interfaz (Vitest): la ruta, la descarga con los datos elegidos y el motivo de un rechazo. e2e: el propietario baja el ZIP del mes con el XML, el CDR y el índice, y un período de más de un año se rechaza con su motivo.

## Límites (P)
- Sin PDF, sin comprobantes de GRE, ni resúmenes diarios y comunicaciones de baja como archivos propios del ZIP (el índice no los lista).
- El índice no dice si el comprobante fue dado de baja o anulado con una nota de crédito: lo que tiene es el estado del envío a SUNAT.
- Tope de 500 comprobantes por exportación; una empresa con más movimiento los pide por partes. Si hiciera falta uno mayor, sería un trabajo en segundo plano que deja el ZIP en el almacén y avisa por correo.
- No es todavía la exportación de los datos de una cuenta (clientes, productos, usuarios, auditoría) ni su baja definitiva.
