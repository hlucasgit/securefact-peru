# API pública de SecureFact Perú

Guía para quien integra un programa con la plataforma. El contrato exacto está en [`openapi.v1.json`](openapi.v1.json) (la plataforma también lo publica en `GET /openapi/v1.json`, sin autenticación); esta guía explica cómo se usa. La colección [Bruno](bruno/) tiene las llamadas de ejemplo.

## Versión y compatibilidad (ADR-066)
- Todo está bajo `/api/v1/`. La versión `v1` **solo crece**: se agregan rutas, campos de respuesta y valores nuevos; no se quita ni se renombra nada ni se cambia lo que significa un campo. Un cliente debe **ignorar los campos que no conoce** y tolerar valores nuevos en las listas de estados.
- Lo que rompa a un cliente va en `v2`, y `v1` sigue funcionando junto a ella. Antes de retirar algo se avisa con meses de anticipación; las respuestas de lo que se retira llevarán los encabezados `Deprecation` y `Sunset`.
- El contrato de `v1` es el archivo `openapi.v1.json`: una prueba automática falla si la API publicada deja de coincidir con él, de modo que un cambio solo llega por una decisión que se ve en la revisión del archivo.

## Autenticación
Dos formas, las dos con `Authorization: Bearer …`:

| Quién | Cómo | Para qué |
|---|---|---|
| Una persona | `POST /api/v1/auth/login` con correo y clave (y código TOTP si lo tiene) devuelve `accessToken` (dura minutos) y `refreshToken`; se renueva con `POST /api/v1/auth/refresh` | La interfaz web y lo que hace una persona |
| Un programa | **Llave de API** `sfk_…` | Integraciones que emiten, consultan o descargan sin que nadie ingrese |

### Llaves de API
- La crea una persona con permiso (propietario o administrador de la cuenta) en **Cuenta → Integraciones**, o con `POST /api/v1/api-keys` `{name, role, expiresAt?}`. La respuesta trae el `secret` completo **una sola vez**: guárdelo en el gestor de secretos de su sistema. Después solo se ve el prefijo.
- Se manda como `Authorization: Bearer sfk_…` o en el encabezado `X-Api-Key: sfk_…`.
- Tiene **un rol de la cuenta** (`BillingAdmin`, `Sales`, `Accountant`, `Auditor` o `ReadOnly`) y los permisos de ese rol, nunca más que quien la creó. **Ninguna llave administra usuarios, llaves ni webhooks**: si se filtra, no puede crear otra ni redirigir sus eventos.
- Solo ve los datos de su cuenta. Una cuenta suspendida o cerrada deja sus llaves sin efecto.
- Puede vencer (hasta dos años) y se **revoca** al instante con `POST /api/v1/api-keys/{id}/revoke`. Una cuenta tiene hasta 20 llaves activas. Una llave inválida, vencida o revocada recibe siempre el mismo `401`.
- Del lado de SecureFact solo se guarda el SHA-256 del secreto.

## Errores
Problem Details (RFC 9457), `Content-Type: application/problem+json`, con un código estable `SF-<ÁREA>-nnn` en el campo `code`:

```json
{ "type": "https://docs.securefact.pe/errors/SF-PLAN-001", "title": "Límite del plan alcanzado", "status": 403, "detail": "…", "code": "SF-PLAN-001", "correlationId": "…" }
```
Decida por `code`, no por el texto. Los códigos están en `src/SecureFact.SharedKernel/ErrorCodes.cs` y nunca se reutilizan. Todas las respuestas llevan `X-Correlation-Id`: cítelo al pedir soporte.

| Estado | Significado |
|---|---|
| 401 | Sin credencial, o inválida, vencida o revocada |
| 403 | Credencial válida sin el permiso, o límite del plan |
| 404 | No existe **o no es de su cuenta** (es la misma respuesta) |
| 409 | Conflicto: repetido, o en un estado que no lo permite |
| 422 | La solicitud no cumple una regla; el `code` dice cuál |
| 429 | Demasiadas solicitudes |

## Idempotencia
Emitir un comprobante (`POST /api/v1/documents`) o una nota (`POST /api/v1/notes`) pide un encabezado `Idempotency-Key` de 8 a 100 caracteres alfanuméricos, `.`, `_`, `:` o `-`, único por intención. Repetir la misma solicitud con la misma clave **devuelve el documento original** sin emitir otro; la misma clave con contenido distinto es un conflicto (`409`). Reintente siempre con la misma clave tras un corte de red.

## Límites de uso
Por **credencial** (la llave o el token): 600 solicitudes por minuto. Por dirección de origen: 3 000 por minuto. Lo que pase recibe `429` con `Retry-After` en segundos. `/health` no cuenta. Si su carga necesita más, pídalo a soporte: el límite es configurable.

## Páginas
Las listas reciben `skip` y `take` (hasta 100 por página).

## Flujo típico de emisión
1. `POST /api/v1/documents` con `Idempotency-Key` → `201` con el documento numerado.
2. La plataforma lo prepara (XML, firma) y lo envía a SUNAT por su cuenta. Entérese de una de dos maneras: **webhook** (recomendado) o consultando `GET /api/v1/documents/{id}/electronic` hasta que `state` sea `Accepted`, `AcceptedWithObservations` o `Rejected`.
3. Descargue el XML firmado, el CDR y el PDF desde `/api/v1/electronic-documents/{id}/xml|cdr|pdf`.

## Webhooks (ADR-067)
Para que la plataforma avise en vez de consultarla.

**Registro** (una persona de la cuenta, **Cuenta → Integraciones** o `POST /api/v1/webhooks`): una URL `https` **pública** (no se aceptan direcciones de la máquina ni de redes privadas, ni `http`, ni usuario y clave en la URL) y los eventos que quiere. La respuesta trae el secreto de firma `whsec_…` **una sola vez**. `POST /api/v1/webhooks/{id}/test` envía un `webhook.ping` y dice cómo le fue.

**Eventos** (`GET /api/v1/webhooks/events`):

| Evento | Cuándo |
|---|---|
| `document.issued` | Se numeró y guardó una factura, boleta o nota (aún no está en SUNAT) |
| `document.accepted` | SUNAT aceptó el documento, con o sin observaciones |
| `document.rejected` | SUNAT lo rechazó |

**Entrega.** `POST` con `Content-Type: application/json` y estos encabezados:

| Encabezado | Contenido |
|---|---|
| `X-SecureFact-Event` | El tipo, por ejemplo `document.accepted` |
| `X-SecureFact-Delivery` | Id de esta entrega |
| `X-SecureFact-Timestamp` | Segundos Unix en que se firmó |
| `X-SecureFact-Signature` | `v1=` y el HMAC-SHA256 en hexadecimal |
| `User-Agent` | `SecureFact-Webhooks/1` |

Cuerpo:
```json
{
  "id": "0198…",            // id del evento: el mismo en cada reintento y para cada destino
  "type": "document.accepted",
  "apiVersion": "v1",
  "createdAt": "2026-10-09T15:04:05Z",
  "tenantId": "…",
  "data": {
    "documentId": "…", "electronicDocumentId": "…", "companyId": "…",
    "documentTypeCode": "01", "series": "F001", "number": 45, "issueDate": "2026-10-09",
    "state": "Accepted",
    "sunat": { "code": 0, "description": "La Factura numero F001-45, ha sido aceptada", "observations": [] }
  }
}
```
Los eventos llevan identificadores y el estado, no los datos del comprobante: para el contenido, consulte la API con el `documentId`.

**Verifique la firma** antes de confiar en un mensaje: calcule `HMAC-SHA256(secreto, timestamp + "." + cuerpo_tal_cual_llegó)` en hexadecimal, antepóngale `v1=` y compárelo **en tiempo constante** con `X-SecureFact-Signature`. Rechace los mensajes con un `Timestamp` de más de 5 minutos de antigüedad, para que uno capturado no se pueda repetir.

```csharp
// C# (.NET 8+)
static bool IsAuthentic(string secret, string timestamp, string body, string signature)
{
    var expected = "v1=" + Convert.ToHexStringLower(HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), Encoding.UTF8.GetBytes($"{timestamp}.{body}")));
    var fresh = Math.Abs(DateTimeOffset.UtcNow.ToUnixTimeSeconds() - long.Parse(timestamp)) <= 300;
    return fresh && CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(expected), Encoding.UTF8.GetBytes(signature));
}
```
```javascript
// Node.js
import { createHmac, timingSafeEqual } from 'node:crypto'
export function isAuthentic(secret, timestamp, rawBody, signature) {
  const expected = 'v1=' + createHmac('sha256', secret).update(`${timestamp}.${rawBody}`).digest('hex')
  const a = Buffer.from(expected), b = Buffer.from(signature)
  return Math.abs(Date.now() / 1000 - Number(timestamp)) <= 300 && a.length === b.length && timingSafeEqual(a, b)
}
```
```python
# Python
import hmac, hashlib, time
def is_authentic(secret: str, timestamp: str, raw_body: bytes, signature: str) -> bool:
    expected = "v1=" + hmac.new(secret.encode(), timestamp.encode() + b"." + raw_body, hashlib.sha256).hexdigest()
    return abs(time.time() - int(timestamp)) <= 300 and hmac.compare_digest(expected, signature)
```
Use el **cuerpo crudo**, no el que resulta de volver a serializar el JSON.

**Respuesta y reintentos.** Conteste con un `2xx` en menos de 10 segundos y procese después. Cualquier otra cosa (otro estado, sin respuesta, error de conexión) cuenta como fallo y se reintenta con una espera creciente: **1 minuto, 5, 30, 2 horas, 6, 12 y 24** (8 intentos en total, unos dos días). Tras el octavo la entrega queda `Dead` y una persona puede reenviarla desde la pantalla o con `POST /api/v1/webhooks/deliveries/{id}/redeliver`. Un `410 Gone` la da por muerta y **apaga el webhook**; con 40 fallos seguidos también se apaga (y se ve el motivo). No se siguen redirecciones.

**Su receptor debe ser idempotente.** La entrega es *al menos una vez*: el mismo evento (mismo `id`) puede llegar más de una vez, y el orden entre eventos no está garantizado (un `document.accepted` puede llegar antes que su `document.issued` si este se reintentó). Guarde los `id` que ya procesó.

**Rotar el secreto**: `POST /api/v1/webhooks/{id}/rotate-secret` da uno nuevo y el anterior deja de firmar **en el acto**; actualice su receptor primero si no puede tolerar entregas fallidas.

## Qué falta (P)
- Eventos de la guía de remisión y del cobro de la plataforma: hoy solo comprobantes.
- Entorno de pruebas (*sandbox*) público para integradores con su propio portal: hoy el simulador de SUNAT (`Sunat:Environment=Sandbox`) es de desarrollo y no se ofrece a terceros.
- Paquetes de cliente (SDK) y secretos de firma con doble vigencia durante la rotación.
