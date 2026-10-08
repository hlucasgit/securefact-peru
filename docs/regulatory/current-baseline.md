# Baseline normativo vigente (revisión 2026-09-30)

Este documento resume lo **verificado** en fuentes primarias (ver `sources.md`, IDs S01…). Lo no verificado figura como pendiente y **no se implementa**. Donde este baseline contradiga al contexto maestro del proyecto, prevalece la fuente oficial.

## 1. Modalidades de emisión relevantes

| Modalidad | Qué es | Relevancia para SecureFact |
|-----------|--------|----------------------------|
| SEE – Del Contribuyente | Emisión desde sistemas propios del contribuyente; envío a SUNAT por web service SOAP; firma con certificado digital del emisor **o** vinculación a uno o más PSE. (S02) | Canal `DirectSunatChannel`: SecureFact actúa como el sistema del contribuyente. |
| SEE – OSE | Un Operador de Servicios Electrónicos valida y reenvía a SUNAT. El CDR del OSE tiene plena validez tributaria (RS 117-2017). (S07) | Canal `ThirdPartyOseChannel`. **GRE no puede emitirse por SEE-OSE** (S05). |
| PSE | Presta servicios al emisor "en nombre del emisor", firma con **su propio certificado**; el contribuyente debe dar de alta al PSE con su Clave SOL. (S01) | Canal `ThirdPartyPseChannel` ahora; `FutureSecureFactPseChannel` en Fase 8. |
| SEE – SOL / Facturador SUNAT (SFS) | Emisión por portal SUNAT / aplicativo gratuito. | Fuera de alcance. |

La responsabilidad sobre el contenido del comprobante es siempre del emisor, incluso con PSE (S01).

## 2. Requisitos PSE (S01) — confirma el contexto maestro

Para **presentar la solicitud** (validado en línea por SUNAT Operaciones en Línea): domicilio fiscal habido; RUC sin suspensión/baja; Régimen General del IR de 3.ª categoría; declaraciones determinativas mensuales de los últimos 6 meses (12 si fue retirado antes) con ventas/ingresos en IGV e IR; ser emisor electrónico del SEE.

Para **obtener la inscripción**: mantener lo anterior; declaraciones posteriores a la solicitud; **registrar el certificado digital** que usará; sin sentencia condenatoria vigente por delito tributario o aduanero; **capital o activos netos ≥ 150 UIT** (capital según SUNARP; activos netos según casillero 390 "total activo neto" de la DJ anual del ejercicio anterior; balance inicial si inicia actividades) — **no es un depósito bancario**; **≥ 5 trabajadores en relación de subordinación** declarados en el último PLAME (Form. 601); **certificación ISO/IEC 27001** (certificado presentado dentro de 3 días hábiles de la solicitud).

Plazo de SUNAT: 5 días hábiles; vencido sin notificación opera silencio administrativo negativo.

Obligaciones y permanencia: plataforma de atención y soporte de primer nivel a los emisores; permitir inspección; **tasa de CDR rechazados por emisor ≤ 10 % del total (aceptados + rechazados) mensual durante 12 meses, luego ≤ 5 %**; el incumplimiento causa retiro (90 días calendario para seguir prestando servicio).

Hallazgo: el portal aún dice "inscripción y homologación", pero fuentes secundarias indican que la RS 108-2022 eliminó la homologación (S14, **U**). Se resuelve leyendo el texto de la RS 108-2022 (pendiente).

Implicación de diseño: la tasa de rechazo por emisor es un KPI regulatorio futuro → la validación previa al envío (XSD + reglas de validación SUNAT) es crítica y se mide desde el MVP.

## 3. Calidad de emisor y plazos (S02)

- La calidad de emisor electrónico (SEE-Contribuyente) se obtiene por designación o elección; se registra en SOL cargando certificado digital + correo, o vinculando uno o más PSE; opera desde el día calendario siguiente.
- Factura y notas vinculadas: se envían a SUNAT en la fecha de emisión o **hasta 3 días calendario** siguientes.
- Serie alfanumérica de 4 caracteres: `F` para facturas/notas asociadas, `B` para boletas/notas asociadas (el Manual S04 indica `F`/`B` + 3 alfanuméricos); numeración correlativa desde 1.
- Factura solo a adquirentes con RUC. Boletas se informan mediante Resumen Diario (según S02/S04; **verificar vigencia del flujo**, §pendientes).
- Nota de crédito: solo sobre comprobantes con **CDR de aceptación**. Excepción: hasta el 10.º día hábil para anular comprobantes con sujeto distinto o descripción incorrecta.
- Factura rechazable por el adquirente hasta el 9.º día hábil del mes siguiente (solo facturas).
- Conservación: el emisor debe almacenar comprobantes, notas, resúmenes, comunicaciones de baja y constancias; y poner a disposición del adquirente, vía web, por **1 año** desde la emisión, con autenticación (S02).
- Estados del CDR: **Aceptada**, **Aceptada con observación** (válida; observaciones no son rechazo), **Rechazada** (sin validez; se debe emitir un comprobante nuevo con nuevo número).

## 4. Integración técnica SEE-Contribuyente (S04, versión Mayo 2021; reglas de validación al 26.08.2026 en S03)

- **Protocolo**: SOAP/HTTPS con WS-Security `UsernameToken`. `Username = <RUC><usuario SOL>`, `Password = clave SOL`. La Clave SOL debe ser **secundaria** con perfil "Envío de documentos electrónicos – Grandes emisores".
- **Endpoints producción** (el comunicado S08 deja solo `e-factura`):
  - Facturas, notas, resumen diario, baja, servicios públicos, lotes: `https://e-factura.sunat.gob.pe/ol-ti-itcpfegem/billService?wsdl`
  - Retención/percepción/reversión: `https://e-factura.sunat.gob.pe/ol-ti-itemision-otroscpe-gem/billService?wsdl`
  - GRE remitente **(legado SOAP)**: `https://e-guiaremision.sunat.gob.pe/ol-ti-itemision-guia-gem/billService?wsdl` — la GRE vigente usa la plataforma REST (§6).
- **Beta** (solo pruebas de estructura XML, sin certificado registrado; usuario `<RUC>MODDATOS`, clave `MODDATOS`): `e-beta.sunat.gob.pe/ol-ti-itcpfegem-beta/billService`. El proyecto prohíbe usarlo para estrés (usar el simulador).
- **Métodos**: `sendBill` (síncrono, un ZIP con un XML → ZIP con CDR), `sendSummary` (asíncrono, resúmenes/bajas → ticket), `sendPack` (lote → ticket), `getStatus(ticket)`.
- **Nombres de archivo**: `<RUC>-<tipo>-<serie>-<correlativo>.ZIP/.XML` (tipos 01, 03, 07, 08; correlativo 1–8 dígitos); baja `<RUC>-RA-<YYYYMMDD>-<n>`; resumen diario `<RUC>-RC-<YYYYMMDD>-<n>` (bloques de 500 líneas desde 2018).
- **Firma**: X.509 v3, XMLDSig *enveloped* dentro de `ext:UBLExtensions/ext:UBLExtension/ext:ExtensionContent` (una sola extensión de firma), sobre todo el documento; el RUC en `OU` del Subject; certificado previamente comunicado a SUNAT; vigente y no revocado; la firma usa la misma codificación del XML; después de firmar no se puede modificar nada.
- **Errores**: `0100–0999` excepciones SUNAT; `1000–1999` excepciones de formato/estructura del contribuyente (documento "no informado", corregir y reenviar); `2000–3999` errores que generan rechazo (CDR rechazada; en facturas **el número queda consumido**); `4000+` observaciones (CDR aceptada con advertencias). Tabla de parámetro 742 = catálogo oficial de errores.
- **Validaciones**: XSD UBL 2.1 (28/02/2022) + XSL (06/09/2022) + reglas de validación `.xlsx` (26.08.2026).

Consecuencias de diseño (ya reflejadas en ADRs): política de reintento por **clase de error** (transporte vs. `1000–1999` vs. `2000–3999`); numeración **consumida** al ser rechazada una factura (la serie no se "libera"); la clave SOL es un secreto de integración cifrado; el manual de 2021 puede estar parcialmente desactualizado → toda regla va versionada y trazable a la fila de `sources.md`.

## 5. PSE vs. certificado del contribuyente (§20 del contexto maestro)

Confirmado por S01/S02: con un PSE, la firma se hace con el certificado del PSE y el contribuyente lo autoriza en SOL. Por tanto el modelo `Tenant → certificado/credencial/canal` (hoy) y `Tenant → autorización → SecureFact PSE → certificado PSE` (futuro) es correcto. Feature flag `OwnPseMode=false` mientras no exista inscripción oficial.

## 6. GRE (S05, S06)

- Plataforma **REST** con tokens OAuth2: se inscribe la aplicación en SOL (*Credenciales de API SUNAT*) → `client_id`/`client_secret`; token vía `POST https://api-seguridad.sunat.gob.pe/v1/clientessol/<client_id>/oauth2/token/` con `grant_type=password`, `scope=https://api-cpe.sunat.gob.pe`, `username=<RUC><usuarioSOL>`, `password=<claveSOL>`; el token dura ~1 hora. Las URI del manual son «referenciales».
- **Envío** (S29, leído el 2026-10-08): `POST https://api-cpe.sunat.gob.pe/v1/contribuyente/gem/comprobantes/{numRucEmisor}-{codCpe}-{numSerie}-{numCpe}` con `Authorization: Bearer`, `codCpe` `09` (remitente) o `31` (transportista), serie `T###` (remitente) o `V###` (transportista) y número de 1 a 8 dígitos. Cuerpo JSON `{"archivo":{"nomArchivo":"RUC-09-T001-1.zip","arcGreZip":"<zip en base64>","hashZip":"<SHA-256 del zip>"}}`; el zip lleva un solo XML `RUC-09-T001-1.xml` firmado. Respuesta: `{"numTicket":"<uuid>","fecRecepcion":"…"}`.
- **Consulta del ticket** (S29): `GET https://api-cpe.sunat.gob.pe/v1/contribuyente/gem/comprobantes/envios/{numTicket}` → `codRespuesta` `98` (en proceso), `99` (con error: `error.numError`/`desError`, y `arcCdr` si `indCdrGenerado` es `1`) o `0` (correcto: `arcCdr` en base64, `indCdrGenerado` `1`).
- Errores del envío: forma `501` (codCpe), `502` (serie), `503`–`506` (campos vacíos o nombre del zip), `507` (hash), `155`–`161` (zip vacío o corrupto, sin comprobantes, con más de uno, nombre del XML incorrecto o distinto del zip); consulta `508`/`509`. Los errores 4xx/5xx de la plataforma llegan como `{cod, msg, exc}`; los de validación, con `422` y `errors`. Los códigos de validación del contenido son los de S27.
- La GRE debe tener **CDR aceptada antes del inicio del traslado**. No se puede emitir por SEE-OSE.
- Bounded context `Gre` independiente (Fase 7). Reglas de validación GRE 25.09.2026 (S27); XSD GRE 13/07/2022 (S30, idéntico al UBL 2.1 de S17); XSL 2.0.1 del 01.10.2026 (S31, solo de lectura).
- **Quién la emite**: el contribuyente con RUC activo, domicilio habido y régimen de tercera categoría (S05); un PSE vinculado puede enviar la GRE de sus emisores (regla 0154 de la hoja General de S27, vigencia hasta el 7.º día del mes siguiente de la revocación).
- La GRE remitente es `DespatchAdvice` UBL 2.1 con `CustomizationID` `2.0` y tipo `09`; la del transportista, tipo `31`, serie `V###`. Ver `matrix.md` R-062 a R-068 para el detalle por campo.

## 7. OSE como canal tercero (S07)

OSE inscrito: capital/activos ≥ 300 UIT, carta fianza, controles Anexo A RS 117-2017, pruebas SUNAT. Sirve como referencia: un `ThirdPartyOseChannel` solo puede integrarse con OSE inscritos en el registro SUNAT, y no cubre GRE.

## 8. Contingencia (S09)

La "contingencia" oficial es la emisión en formatos preimpresos autorizados cuando el emisor está imposibilitado por causas ajenas (límite de impresión: 10 % del promedio mensual de 6 meses o 100 formatos por tipo y establecimiento, lo mayor; informar a SUNAT hasta el 7.º día calendario). **No es** simplemente "reintentar". El modelo de contingencia de SecureFact se limita a: (a) estado de disponibilidad de canales y cola con **tiempo restante hasta el vencimiento del plazo de envío** (3 días calendario) y alertas, y (b) soporte a registro/resumen de comprobantes de contingencia en fase posterior, previa lectura de RS 113-2018 y modificatorias.

## 9b. Hallazgos de la hoja de reglas de validación (S16, 2026-10-01)

- **Los plazos y la tasa del IGV son datos del parámetro oficial**, no constantes: *Parámetro 004* = "Plazo máximo de envío" por código de comprobante (días); *Parámetro 012* = tasa de IGV por fecha de inicio de vigencia; *Parámetro 024* = tasa vigente del IVAP; *Parámetros 001–003* = tipo de cambio, régimen de percepción, régimen de retención. Esto confirma ADR-008: se modelan como reglas con vigencia.
- **Boletas** (resuelve R-017 en parte): existe validación `Boleta2_0` para enviar la boleta como comprobante individual; si la fecha de emisión excede el plazo máximo vigente, SUNAT responde *1079 "Solo puede enviar el comprobante en un resumen diario"*; además *2329* rechaza fechas de emisión a más de dos días del envío. El Resumen Diario (`Resumen Diario1_1`, UBL 2.0) sigue vigente para el caso fuera de plazo y para series numéricas de contingencia. **Verificar el flujo exacto con la Guía XML de Boleta (S18) antes de implementar.**
- **Reglas de nombre de archivo y serie por tipo de documento** (código de retorno 0151/0159): tipos 01, 03, 07, 08, 30, 34 y 42 → serie numérica de 4 dígitos o alfanumérica que empieza con F o B; tipo 04 → empieza con L; 20 → R; 40 → P; 25 → F; 28 → 4 alfanuméricos; 56 → empieza con C. El RUC del nombre de archivo empieza con 1 o 2. Correlativo de hasta 8 dígitos (9 para tipos 28 y 56). Tipos de archivo de resumen: RA, RC, RR (correlativo 1–99 999).
- **PSE ↔ emisor**: código *0154*; la relación PSE–emisor se considera vigente hasta el **7.º día calendario del mes siguiente** a la revocación de la autorización. Código *0111* si el usuario no es emisor ni PSE. *2325/2326* certificado no comunicado / de baja. *2335* "documento electrónico alterado" (firma no coincide).
- **Catálogos oficiales** (Anexo N.°8, 47 catálogos en la hoja `Catálogos`): tipos de documento, monedas, unidades de medida, países, tributos, tipos de documento de identidad, afectación del IGV, sistema de cálculo del ISC, notas de crédito y débito, valores de venta (resumen), ubigeo (INEI), otros conceptos tributarios, elementos adicionales, tipo de precio, tipo de operación, modalidad de transporte, estado del ítem, motivo de traslado, regímenes de percepción y retención, tarifas de servicios públicos, **producto SUNAT**, leyendas, **cargos/descuentos**, **bienes y servicios sujetos a detracción**, medios de pago, tipos de dirección, documentos de transporte, puertos y aeropuertos, entre otros. Fuente única para el módulo `Catalogs` (Fase 2).
- **XSD** (S17): UBL 2.1 estándar para Invoice/CreditNote/DebitNote/DespatchAdvice; resumen diario y baja usan UBL 2.0 con `UBLPE-*`.
- No aparece ninguna mención de **QR** en las hojas de validación: su contenido hay que leerlo en la Guía XML / normas de representación impresa (R-018 sigue abierto).

## 9. Pendientes que bloquean fases

| Pendiente | Bloquea | Responsable de resolver |
|-----------|---------|-------------------------|
| Catálogos vigentes + parámetro 742 | Fase 2 (catálogos) | Revisión regulatoria |
| Reglas de validación (xlsx) y XSD | Fase 3 | Revisión regulatoria |
| Flujo vigente de boletas (RC vs envío directo) | Fase 3/4 | Revisión regulatoria |
| Contenido del QR | Fase 3 | Revisión regulatoria |
| Elección del canal real inicial (SUNAT directo / PSE / OSE) | Fase 4 | Decisión de negocio + ADR |
| Detracciones, retenciones, percepciones | Fase 7 | Revisión regulatoria |
| Texto de RS 108-2022 (homologación PSE) | Fase 8 | Revisión regulatoria |
