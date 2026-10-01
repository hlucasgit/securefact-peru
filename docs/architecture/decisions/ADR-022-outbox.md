# ADR-022: Outbox transaccional

- Estado: Aceptada · Fecha: 2026-10-01

## Decisión
- **Escritura**: Billing guarda el evento `billing.document.issued` (`DocumentIssuedEvent`) en `billing.outbox_message` **en la misma transacción** que el documento. Si el documento no se emite (serie agotada, conflicto de idempotencia) tampoco hay evento; una repetición idempotente no crea otro. Un evento no puede perderse ni anunciar un documento inexistente.
- **Entrega al menos una vez**: `IOutboxProcessor` (Platform) reclama lotes de 50, el más antiguo primero, con `FOR UPDATE SKIP LOCKED` y un arrendamiento de 5 minutos (un despachador caído libera sus mensajes por expiración), cuenta el intento y entrega a `IIntegrationEventConsumer` en el ámbito del tenant del mensaje (RLS sigue vigente). Solo tras volver el consumidor se marca procesado; una caída entre ambos pasos entrega de nuevo, por eso los consumidores son idempotentes.
- **Reintentos**: espera exponencial (30 s, 60 s, … tope 1 h); al décimo intento fallido el mensaje queda **muerto** (`dead_at`), se registra y espera a un operador. Un tipo de evento sin consumidor falla de forma visible, no desaparece. En el registro solo va el tipo de la falla, no el mensaje (puede llevar datos del documento).
- **Operación**: `GET /api/v1/outbox/dead` y `POST /api/v1/outbox/{source}/{id}/requeue` (permiso `cpe.send`, por tenant con RLS; reencolar reinicia los intentos).
- **Consumidor de CPE** (`DocumentIssuedHandler`): prepara (UBL + firma) cada factura y boleta emitida. Sin certificado el mensaje se reintenta y, si se carga uno, se completa solo.
- **Base de datos**: RLS que admite el ámbito de plataforma solo para reclamar; el rol de la aplicación puede insertar y actualizar solo la contabilidad de entrega (intentos, próxima fecha, arrendamiento, procesado, muerto, error); un disparador impide cambiar el evento (tipo, carga, tenant, fecha) o borrarlo, también al dueño del esquema.
- **Worker**: `OutboxWorker` en `SecureFact.Workers` (cada 2 s en reposo, sin pausa mientras entrega); luego `CpeWorker` envía lo preparado. Cadena completa probada: emitir factura → outbox → documento preparado → enviado → aceptado, sin pasos manuales.

## Corrección asociada (inanición)
Si faltaba una credencial, el worker volvía a elegir siempre los mismos documentos y llenaba el lote de 50, bloqueando a los demás. Ahora una precondición fallida (credenciales SOL, canal, empresa) **conserva el estado y los intentos pero aplaza el siguiente intento 5 minutos** y deja el código del error en el documento.

## Límites
- Sin retención: los mensajes procesados se conservan; falta una tarea de depuración con política de retención.
- Un solo origen (Billing) y un solo consumidor; `IMessageBus` (RabbitMQ) para publicar a sistemas externos queda para la fase de integración. La interfaz `IOutboxSource` permite sumar orígenes por módulo.
- Sin orden garantizado entre mensajes de distintos documentos.
