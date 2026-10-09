# ADR-067: Webhooks

- Estado: Aceptada · Fecha: 2026-10-09
- Completa: ADR-022 y ADR-035 (outbox transaccional y publicación al bus) y ADR-036 (archivo): reutiliza el outbox, no lo reemplaza.
- Relacionada: ADR-066 (API pública y llaves).

## Contexto
Quien integra un programa debe enterarse de que SUNAT aceptó o rechazó un comprobante sin consultar la API cada pocos segundos. El outbox ya entrega, al menos una vez y dentro de la transacción del cambio, los eventos `billing.document.issued` y `cpe.document.answered`; falta llevarlos fuera de la plataforma al servidor del cliente **sin abrir un camino hacia dentro**: la plataforma hace la llamada, así que una dirección mal elegida puede apuntar a su propia red.

## Decisión

### Qué se publica
Eventos con nombre público estable, independiente de los internos (que pueden cambiar): `document.issued`, `document.accepted` (con o sin observaciones) y `document.rejected`, de facturas, boletas y notas (`01`, `03`, `07`, `08`; los resúmenes y las bajas no son comprobantes del cliente). El cuerpo lleva el id del evento, el tipo, la versión de la API, la fecha, la cuenta y datos mínimos: identificadores, serie, número, estado y la respuesta de SUNAT (código, descripción, observaciones). **No lleva el contenido del comprobante**: para eso se consulta la API con el id. Un cuerpo con datos de más sería un dato del cliente fuera de la plataforma sin necesidad.

### Registro
`webhook.endpoint` (con `tenant_id` y RLS): URL, descripción, eventos y secreto. Una persona con `webhooks.manage` (propietario y administrador; ninguna llave, ADR-066) los administra: crear, editar, apagar, borrar, rotar el secreto, probar y ver y reenviar entregas. Hasta 10 por cuenta. El secreto `whsec_…` (256 bits) se muestra **una vez**; se guarda **cifrado** (ADR-007, `ISecretProtector` con un propósito propio) porque hay que leerlo para firmar, y nunca va al registro de auditoría ni a los logs.

### De evento a entrega
`WebhookFanOut` es un consumidor de cada uno de los dos eventos del outbox que se anuncian (no de todos: un evento que nadie consume sigue fallando a la vista en el outbox): corre en el ámbito de la cuenta del evento, busca sus webhooks activos suscritos y escribe una **entrega** por cada uno (`webhook.delivery`, con el cuerpo ya fijado). Es **idempotente**: el índice único (webhook, evento) hace que un evento se entregue una vez a cada destino aunque el outbox lo presente varias veces. Solo escribe filas: de aquí no sale nada. Si el documento electrónico del evento no se puede leer, falla y el outbox reintenta, en lugar de perder el aviso.

### Envío
`WebhookDispatcher` (el `WebhookWorker`, cada 5 s) toma las entregas vencidas de todas las cuentas con `FOR UPDATE SKIP LOCKED` y un arriendo de 5 minutos (varios workers no toman la misma; el de uno que muere vence) y envía cada una en el ámbito de su cuenta. `POST` con `X-SecureFact-Event`, `-Delivery`, `-Timestamp` y `-Signature`.
- **Firma**: `v1=` + hexadecimal de `HMAC-SHA256(secreto, "{timestamp}.{cuerpo}")`. Se firman el instante y el cuerpo exactos; el receptor compara en tiempo constante y rechaza instantes de más de 5 minutos (anti-repetición). La guía trae ejemplos en C#, Node.js y Python.
- **Resultado**: un `2xx` entrega. Otro estado, sin respuesta (10 s), error de conexión o dirección rechazada: falla y se reintenta a los **1 min, 5 min, 30 min, 2 h, 6 h, 12 h y 24 h** (8 intentos en total); tras el octavo queda `Dead` y una persona la reenvía. Un `410` la da por muerta y apaga el webhook; a los **40 fallos seguidos** también se apaga, con el motivo visible. Reactivarlo limpia la cuenta. No se siguen redirecciones y el cuerpo de la respuesta no se lee ni se guarda.
- El error que ve el cliente es un texto fijo («No se pudo conectar con la dirección»), nunca el de la excepción, que podría nombrar una dirección interna.

### Protección contra SSRF
La llamada la hace la plataforma, así que el destino se controla **dos veces**: al registrar (`https`, sin usuario ni clave ni fragmento, hasta 500 caracteres, fuera nombres y direcciones locales o privadas) y **al conectar**, con un `ConnectCallback` propio que resuelve el nombre y **rechaza todo el destino si alguna de sus direcciones no es pública**, y conecta a esas mismas direcciones ya revisadas. Esto último detiene un nombre que apuntaba a una dirección pública al aprobarlo y se cambia después a una interior (DNS *rebinding*). Se consideran no públicas: loopback, 10/8, 172.16/12, 192.168/16, 169.254/16 (incluye el servicio de metadatos de las nubes), 100.64/10, las de documentación y pruebas, multicast y reservadas, y en IPv6 loopback, local de enlace, local única, multicast y documentación; una IPv4 dentro de IPv6 se juzga como IPv4. Sin proxy. `Webhooks:AllowLocalTargets` (solo desarrollo y pruebas) levanta esto y **el arranque lo rechaza en producción**, como al simulador de SUNAT.

### API
`GET|POST /api/v1/webhooks`, `PUT|DELETE /api/v1/webhooks/{id}`, `POST …/{id}/rotate-secret`, `POST …/{id}/test` (envía un `webhook.ping` ahora y contesta cómo le fue), `GET …/{id}/deliveries?state=`, `POST …/deliveries/{id}/redeliver`, `GET …/events`. Códigos `SF-WHK-001` (inválido), `-002` (no existe), `-003` (demasiados). Auditoría: `webhooks.webhook.created|updated|deleted|secret_rotated`.

## Verificación
`WebhooksApiTests` (PostgreSQL y un receptor real en la máquina de la prueba): el secreto se muestra una vez; direcciones y eventos inválidos; el tope de 10; permisos (una persona sin permiso, una llave, la plataforma y un anónimo no administran nada) y aislamiento entre cuentas; la prueba llega firmada (el receptor recalcula la firma con el secreto, escrita desde la regla documentada y no con el código de la plataforma) y dice cómo le fue; una factura emitida se anuncia solo a los webhooks que la pidieron, una vez; aceptado y rechazado con su código; los reintentos con sus esperas hasta `Dead` y el reenvío; `410` y 40 fallos apagan; la rotación; el secreto cifrado y fuera de la auditoría y los logs; y, con los destinos locales prohibidos, doce direcciones que apuntan adentro rechazadas. `WebhookSecurityTests`: las direcciones IP en cada rango reservado y en los públicos vecinos, las URL aceptadas y rechazadas y la firma.

## Límites (P)
- **Al menos una vez y sin orden garantizado** entre eventos: el receptor debe ser idempotente y no suponer secuencia (la guía lo dice).
- Solo eventos de comprobantes. No hay de la guía de remisión (no tiene outbox propio), de la baja, ni del cobro de la plataforma.
- **Un solo secreto vigente**: al rotarlo, el anterior deja de firmar en el acto (no hay doble vigencia).
- La entrega se prueba contra un receptor local, no contra servidores reales de terceros ni con certificados de red de internet; la salida a internet es la de la infraestructura (reglas de red), que debería además limitar los destinos.
- El conteo de fallos seguidos es por intento, no por entrega: un destino que falla 8 veces una misma entrega suma 8.
- Sin *replay* masivo: cada entrega muerta se reenvía una por una.
- El cuerpo se guarda tal cual junto a la entrega mientras exista el webhook; no hay purga por antigüedad todavía.
