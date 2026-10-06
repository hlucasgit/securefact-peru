# ADR-035: Bus de mensajes (RabbitMQ) y retención del outbox

- Estado: Aceptada · Fecha: 2026-10-06
- Completa: ADR-004 (abstracción de mensajería) y ADR-022 (outbox transaccional)

## Contexto
El outbox (ADR-022) entrega cada evento a los consumidores de la propia plataforma. Quedaban dos pendientes: publicar los eventos a sistemas externos a través de un broker, tras la abstracción `IMessageBus` que fijó el ADR-004, y dar una política de retención a los mensajes ya entregados, que se acumulaban sin límite.

## Decisión

### `IMessageBus` y RabbitMQ
- **Contrato** (`SharedKernel`): `IMessageBus.PublishAsync(BusMessage, ct)`. `BusMessage` lleva el id del mensaje del outbox (estable entre reintentos), el tenant, el tipo de evento, la carga JSON (sin secretos) y la fecha del cambio. Una implementación confirma que el broker aceptó el mensaje o lanza.
- **Adaptador** (`SecureFact.Messaging.RabbitMq`, referido solo por los workers): cliente oficial `RabbitMQ.Client` 7.2.2 (licencia `Apache-2.0 OR MPL-2.0`, comprobada en el nuspec). No se adoptó ningún framework de mensajería (ADR-004: abstracción propia).
  - Un *exchange* `topic` durable (`RabbitMq:Exchange`, por defecto `securefact.events`); la clave de enrutamiento es el tipo de evento (`billing.document.issued`).
  - Mensajes **persistentes**, JSON UTF-8, con `message-id` = id del outbox, `type`, marca de tiempo y las cabeceras `tenant-id` y `event-type`.
  - **Confirmaciones del publicador** activadas: `PublishAsync` vuelve solo cuando el broker se hizo responsable del mensaje y lanza si lo rechaza o no responde, de modo que el outbox reintenta en lugar de perderlo. Plazo de `RabbitMq:PublishTimeoutSeconds` (10 s) por publicación, conexión incluida.
  - Una conexión y un canal, abiertos al primer uso y **reconstruidos tras cualquier fallo**; las publicaciones se serializan. Credenciales (`RabbitMq:UserName`, `RabbitMq:Password`) solo por entorno o almacén de secretos, nunca en el repositorio; no se registran. `RabbitMq:UseTls` para TLS.
  - La configuración se valida al arrancar el host (`ValidateOnStart`).
- **Activación**: los workers publican al bus **solo si `RabbitMq:Host` está configurado**; sin él el outbox entrega a los consumidores propios y nada más. `docker-compose.yml` lo configura con las credenciales del contenedor del broker.

### El outbox con varios consumidores
- Un mensaje se entrega cuando **todos** los consumidores que le corresponden volvieron: primero los de su tipo de evento y después los de **todos los tipos** (`IIntegrationEventConsumer.AnyEvent`, que es el publicador al bus, `BusPublishingConsumer`). El primer fallo detiene la entrega; así nada sale de la plataforma antes de que ocurra su propio efecto. Un tipo de evento sin ningún consumidor sigue fallando de forma visible.
- **Entrega al menos una vez en ambos destinos**: si el bus falla (o la plataforma cae entre ambos pasos) el mensaje se reintenta con la espera exponencial de ADR-022 y se vuelve a ejecutar **todo** su recorrido; los consumidores propios ya eran idempotentes y los suscriptores externos deben descartar duplicados por `message-id`. El mensaje solo se completa cuando todo salió bien.
- **Decisión consciente**: no se lleva el estado de entrega por destino (una columna o tabla por consumidor). Habría evitado re-ejecutar el consumidor propio cuando solo falla el bus, a costa de un esquema más complejo y de cómo tratar los mensajes anteriores a activar el bus. Con consumidores idempotentes el coste es un reintento barato. Una caída larga del broker (más de unas tres horas con la espera tope de una hora) deja los mensajes **muertos**: un operador los reencola con el endpoint de ADR-022 cuando el broker vuelve.

### Retención del outbox
- Los mensajes **entregados** se conservan `Outbox:RetentionDays` días (30 por defecto, mínimo 1) y el `OutboxWorker` los purga cada hora. Los pendientes y los muertos **nunca** se borran.
- La tabla sigue siendo de solo anexar para todos, también para el dueño del esquema (ADR-022). La única excepción es la función `billing.purge_outbox_messages(retention)` (migración `AddOutboxPurge`): `SECURITY DEFINER`, exige una retención de al menos un día, marca su propia transacción (`app.outbox_purge`) y fija el ámbito de plataforma solo dentro de ella; el disparador de la tabla permite un `DELETE` únicamente con esa marca y sobre un mensaje entregado hace más de un día. El rol de ejecución no recibe `DELETE` y solo puede ejecutar la función; con el rol de ejecución o con el dueño, un `DELETE` directo falla aunque el rol ponga la marca.
- `IOutboxSource.PurgeDeliveredAsync` y `IOutboxProcessor.PurgeAsync` recorren los orígenes (hoy solo Billing).

## Verificación
- Pruebas con un RabbitMQ real (Testcontainers `rabbitmq:4`): el evento llega solo a los suscriptores de su clave, persistente y con el id del outbox y el tenant; una publicación a un broker inalcanzable o con clave errónea lanza; la cancelación del llamador no se traga; tras cerrar el broker todas las conexiones, los intentos siguientes abren una nueva. De extremo a extremo, con el host real de los workers: emitir una factura entrega el evento al bus con el id del mensaje del outbox, el documento electrónico queda preparado y el mensaje completo.
- Pruebas del orden y de los fallos (sin base de datos): consumidores propios antes del bus aunque se registren al revés, el bus no recibe nada mientras falla el consumidor propio, un bus caído deja el mensaje pendiente y el reintento vuelve a ejecutar todo, un evento solo para el bus se entrega, uno sin consumidores falla.
- Pruebas de la purga contra PostgreSQL: se borra lo entregado hace más de la retención; no se borra lo reciente, lo pendiente ni lo muerto; menos de un día de retención se rechaza; un `DELETE` directo falla para el dueño y para el rol de ejecución aunque ponga la marca.

## Límites
- **Un solo origen** (Billing) y **un solo evento** (`billing.document.issued`): los demás módulos no publican eventos todavía.
- **Sin consumidores externos de referencia**: el contrato de las cabeceras y de la carga está probado, pero no hay colas declaradas por la plataforma; cada suscriptor declara su cola y su enlace.
- **Sin orden garantizado** entre mensajes (ADR-022), y sin esquema de versión de la carga más allá del tipo de evento.
- **Auditoría por el outbox** (ADR-012: el evento de auditoría se escribe fuera de la transacción del negocio) sigue pendiente; el outbox ya permite hacerlo con un origen propio.
- La purga solo existe para Billing; un origen nuevo debe traer la suya.
