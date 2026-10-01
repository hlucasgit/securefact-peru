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

- Plataforma **REST** con tokens OAuth2: se inscribe la aplicación en SOL (*Credenciales de API SUNAT*) → `client_id`/`client_secret`; token vía `POST https://api-seguridad.sunat.gob.pe/v1/clientessol/<client_id>/oauth2/token/` con `grant_type=password`, `scope=https://api-cpe.sunat.gob.pe`, `username=<RUC><usuarioSOL>`, `password=<claveSOL>`; el token dura ~1 hora. URIs del manual son "referenciales": confirmar en "Manual URL – GRE.xlsx" antes de implementar.
- La GRE debe tener **CDR aceptada antes del inicio del traslado**. No se puede emitir por SEE-OSE.
- Bounded context `Gre` independiente (Fase 7). Reglas de validación GRE 25.09.2026; XSD GRE 13/07/2022.

## 7. OSE como canal tercero (S07)

OSE inscrito: capital/activos ≥ 300 UIT, carta fianza, controles Anexo A RS 117-2017, pruebas SUNAT. Sirve como referencia: un `ThirdPartyOseChannel` solo puede integrarse con OSE inscritos en el registro SUNAT, y no cubre GRE.

## 8. Contingencia (S09)

La "contingencia" oficial es la emisión en formatos preimpresos autorizados cuando el emisor está imposibilitado por causas ajenas (límite de impresión: 10 % del promedio mensual de 6 meses o 100 formatos por tipo y establecimiento, lo mayor; informar a SUNAT hasta el 7.º día calendario). **No es** simplemente "reintentar". El modelo de contingencia de SecureFact se limita a: (a) estado de disponibilidad de canales y cola con **tiempo restante hasta el vencimiento del plazo de envío** (3 días calendario) y alertas, y (b) soporte a registro/resumen de comprobantes de contingencia en fase posterior, previa lectura de RS 113-2018 y modificatorias.

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
