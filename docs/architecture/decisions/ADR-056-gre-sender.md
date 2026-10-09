# ADR-056: Guía de remisión electrónica remitente (09)

- Estado: Aceptada · Fecha: 2026-10-08
- Desarrolla: R-010 y R-062 a R-068 de la matriz (fuentes S27 a S31, leídas el 2026-10-08).
- Relacionada: ADR-039 (simulador de SUNAT), ADR-007 (secretos), ADR-008 (reglas como datos).

## Contexto
La guía de remisión electrónica remitente es el documento que ampara el traslado de bienes. SUNAT la recibe por una plataforma distinta de la de las facturas: no es el servicio SOAP `billService` sino una API REST con OAuth2, y la constancia (CDR) que da validez al traslado se consulta con un ticket. No pasa por OSE. Hasta hoy el producto solo emitía comprobantes (factura, boleta, notas).

## Decisión
Un módulo propio, `Gre` (`src/Modules/Gre`), con su esquema `gre`, que reutiliza lo que ya existe: la firma XMLDSig (`IXmlSigner`), el empaquetado (`ICpePackager`), el lector de CDR (`ICdrParser`), el certificado activo, los catálogos y las reglas versionadas. No toca el módulo de comprobantes.

### Alcance de esta entrega
- **Remitente `09`**, series `T` + 3 caracteres, motivos **01, 02, 03, 04, 05, 06, 07, 13, 14 y 17**, transporte público (01) y privado (02).
- Los motivos **08 (importación), 09 (exportación), 18 (emisor itinerante) y 19 (mercancía extranjera)** se rechazan con `SF-GRE-003` y un texto que manda al contribuyente a SUNAT Operaciones en Línea; no se emiten mal. La guía del **transportista (`31`)** se hizo después (ADR-057).

### Flujo
1. **Crear** (`POST /api/v1/gre/guides`): valida con las reglas del libro de validación de SUNAT que se aplican a la forma del archivo (`GreValidator`, cada mensaje lleva el código de la regla); toma el número **en la base** (`UPDATE … SET last_number = last_number + 1 … RETURNING`, dentro de la transacción que guarda la guía; nunca máximo más uno); genera el `DespatchAdvice` (`GreUblGenerator`, validado contra el XSD UBL 2.1 oficial), lo firma con el certificado activo y lo guarda **preparada**.
2. **Enviar** (`POST …/{id}/submit`): arma el zip `RUC-09-Tnnn-n.zip`, pide el token OAuth2 y lo envía; SUNAT contesta un **ticket** y la guía queda **pendiente**; se consulta una vez de inmediato.
3. **Consultar** (`POST …/{id}/refresh`, y el worker): `98` en proceso, `0` correcto y `99` con error; con CDR, se lee, se comprueba que corresponde a esta guía (serie-número) y a este contribuyente, y la guía pasa a **aceptada**, **aceptada con observaciones** o **rechazada** según el código de respuesta. Sin CDR, un error es **falla**.

Estados: `Prepared`, `Pending`, `Accepted`, `AcceptedWithObservations`, `Rejected`, `Failed`. Los cuatro últimos son finales.

### Reintentos y errores
- Una respuesta que sí fue un **rechazo** de SUNAT (4xx de forma: 501 a 507, 155 a 161, 422) deja la guía **fallida** y el número consumido; hay que emitir otra.
- Un fallo **transitorio** (red, 5xx, 429, vencimiento del tiempo) o de **autenticación** deja la guía **preparada** con la fecha del próximo intento (30 s, 2 min, 10 min, 30 min, 1 h) y devuelve `SF-GRE-011`. Reenviar el mismo archivo es seguro. Una autenticación rechazada es transitoria a propósito: la persona corrige las credenciales y reenvía sin perder el número. A los 12 intentos la guía falla.
- El **worker** (`GreWorker`, `Gre:WorkerIntervalSeconds`, 15 s por omisión) reenvía las guías preparadas que tienen fecha de intento y consulta los tickets pendientes. Una guía preparada **sin fecha** (la persona todavía no la envió) no se toca. Un tenant suspendido o cerrado no genera tráfico hacia SUNAT.

### Credenciales
Además del usuario y la clave SOL, la API de SUNAT pide `client_id` y `client_secret`. Se guardan junto a las credenciales SOL de la empresa (`PUT/DELETE /api/v1/sol-credentials/{companyId}/api`, permiso de certificados), el secreto **cifrado** con su propio propósito (`certificates.api-client-secret`), nunca devuelto (la lectura dice solo `hasApiSecret`), y su alta y baja quedan en la auditoría. La clave del caché de tokens es un hash de todas las credenciales.

### Canal
`IGreChannel` con tres implementaciones:
- `GreRestChannel`: token `password` (alcance `https://api-cpe.sunat.gob.pe`, usuario = RUC + usuario SOL, un token se reutiliza hasta dos minutos antes de vencer y se pide otro una vez si SUNAT lo revoca), envío y consulta. HTTPS obligatorio salvo loopback. Nunca repite en un mensaje lo que recibió del servicio de tokens.
- `SandboxGreChannel` (`Sunat:Environment=Sandbox`, ADR-039): acepta sin validar las reglas que solo SUNAT conoce (registros de transportistas, vehículos, conductores); las marcas `[sandbox:rechazar]` (rechazo del envío, 502), `[sandbox:rechazar-cdr]` (CDR con 2800) y `[sandbox:observar]` (aceptación con 4030) provocan los otros desenlaces. El ticket lleva lo que el simulador necesita, así que la API y los workers se contestan entre sí. Se rechaza en producción.
- `UnconfiguredGreChannel`: sin ambiente nombrado, SUNAT no se contacta; todo es transitorio y la guía queda preparada. `Sunat:Environment=Production` configura el canal REST; `Beta` no configura ninguno de las guías (SUNAT no documenta un beta de la GRE, R-067).

### Datos y garantías
- Tablas `gre.series` y `gre.guide`, con `tenant_id` y RLS (`TenantOrPlatform`: el worker descubre trabajo en el ámbito de la plataforma y trabaja en el de cada cuenta). El runtime no tiene `DELETE`.
- Un disparador (`gre.guard_guide`) hace **inmutable** una guía con respuesta final y, siempre, su XML firmado, su resumen, su serie, su número, su fecha, su empresa y su solicitud; otro (`gre.guard_series`) impide que la numeración retroceda. Ninguna guía se borra (`42501`).
- Reglas como datos (ADR-008): el plazo de emisión (`gre.max_issue_lag_days`, 1 día, verificada) y las descripciones genéricas del motivo que SUNAT no admite (`gre.generic_motive_descriptions`, **pendiente de verificación**).
- Códigos `SF-GRE-001` a `SF-GRE-011`; auditoría `gre.guide.created/submitted/processed` y `gre.series.created/deactivated`.

### API y pantallas
`/api/v1/gre/series` (listar, crear, desactivar) y `/api/v1/gre/guides` (listar con filtros, crear, leer, enviar, consultar, XML y CDR). Permisos existentes: lectura con `documents.read`, emisión y envío con `cpe.send`, series con `series.manage`. En la interfaz: *Guías de remisión* (lista con filtros), *Emitir guía* (datos del traslado, partes, transporte, bienes y documentos relacionados; el proveedor y el comprador solo aparecen para los motivos que los usan), el detalle con el estado, el ticket, la respuesta de SUNAT y las descargas, las series y las credenciales de API en la empresa.

## Verificación
- Unidad: el validador y el generador (reglas por motivo y modalidad, XSD) y el canal REST (cuerpo, hash, token y su caché, 401, 98/0/99, 4xx/5xx, red, HTTPS) con el simulador.
- API (`GreApiTests`, contra PostgreSQL): series (formato y repetidas), numeración, XML firmado, motivos no soportados sin consumir número, reglas incumplidas con motivos, envío sin credenciales de API, aceptación con CDR, observada y rechazada, rechazo del envío, fallo transitorio y reintento por el worker, ticket cerrado por el worker, aislamiento entre cuentas, inmutabilidad y no borrado en la base, y que el secreto no sale en ninguna respuesta ni en el registro.
- Interfaz: pruebas del armado de la solicitud (`guide.test.ts`) y de extremo a extremo (`guides.spec.ts`) con el simulador.

## Límites (P)
- **Nunca se probó contra SUNAT real.** No hay beta documentado de la GRE; lo implementado sigue el Manual de Servicios y el Manual URL (S28, S29). Son supuestos: la representación hexadecimal en minúsculas del `hashZip`; que el CDR de la GRE es un `ApplicationResponse` como el de las facturas; y que el `DespatchAdvice` lleva el elemento `cac:Signature` como el UBL de las facturas.
- **Lo que solo SUNAT sabe** (que el transportista, el vehículo o el conductor estén registrados, la licencia, el establecimiento anexo, el estado del destinatario) no se valida aquí: SUNAT lo contesta en el CDR.
- **Ubigeo** solo por formato (la lista del INEI no viene en el libro) y **unidad de medida del bien** solo por forma (el catálogo 03 es la Recomendación 20 de la ONU, una lista externa); SUNAT observa (4320) una unidad desconocida.
- **Sin representación impresa** (PDF con QR) de la guía; se entrega el XML firmado y el CDR.
- **Sin idempotencia al crear**: dos envíos del formulario crean dos guías y consumen dos números (el botón se bloquea mientras espera).
- La interfaz no ofrece vehículos ni conductores secundarios, el trasbordo programado, el indicador de vehículo de categoría M1/L ni el de retorno con envases o vehículo vacío, aunque la solicitud de la API sí los admite.
- El caché de tokens es **por proceso**: la API y cada worker piden su propio token.
- La guía **no** se vincula todavía con el comprobante de venta en la base (solo se declara como documento relacionado, sin comprobar que exista).
- Faltan la importación, la exportación, el emisor itinerante y la mercancía extranjera, y el indicador de traslado en vehículos de registro previo.
