# ADR-004: Outbox transaccional y abstracción de mensajería

- Estado: Aceptada · Fecha: 2026-09-30 · Implementada en ADR-022 (outbox) y ADR-035 (`IMessageBus` y RabbitMQ). Lo que se implementó difiere en tres puntos: el despachador marca `processed_at` (no `published_at`) y entrega a consumidores propios y al bus; el contrato es `IMessageBus` de solo publicación (no hay `IMessageConsumer` ni `inbox_message`: la idempotencia está en cada consumidor); y los eventos todavía no llevan versión más allá de su tipo.

## Decisión
- La emisión sigue: `API → transacción BD (documento + outbox) → publicador → cola → worker → canal → resultado → webhook`.
- **Outbox transaccional propio y delgado** por módulo: tabla `outbox_message` escrita en la misma transacción que el cambio de estado; un `OutboxPublisher` (BackgroundService) la lee con `FOR UPDATE SKIP LOCKED`, publica y marca `published_at`. Entrega **al menos una vez**.
- **Inbox/idempotencia** en consumidores (`inbox_message(consumer, message_id)`).
- La mensajería se oculta tras `IMessageBus`/`IMessageConsumer` en `SharedKernel`. Implementación inicial: **RabbitMQ** (cliente oficial `RabbitMQ.Client`). Implementación `InMemory` para pruebas.
- Se evita adoptar un framework de mensajería cuya licencia pueda cambiar; si se evalúa uno (MassTransit, Wolverine…), se verifica la licencia vigente antes y se aísla tras la abstracción.
- Eventos de integración **versionados** (`type`+`version`), distintos de los eventos de dominio.
- Reintentos con backoff exponencial + jitter; mensajes envenenados a dead-letter.

## Consecuencias
+ Sin pérdida de eventos ante caída entre commit y publicación.
− Orden total no garantizado; los consumidores deben ser idempotentes y tolerar reordenamiento por agregado (clave de partición = `electronic_document_id`).
