# ADR-047: Leyendas de venta exonerada y transporte de carga en la interfaz

- Estado: Aceptada · Fecha: 2026-10-06
- Completa: ADR-046 (detracción, retención y exportación en la interfaz). Las reglas son las de la API (ADR-030 para el transporte de carga, y las filas de `docs/regulatory/matrix.md` para las leyendas); **este ADR no añade ninguna regla tributaria**.

## Decisión

### Leyendas de venta exonerada (catálogo 52: 2001, 2002, 2003 y 2008)
- El formulario ofrece una tarjeta **Leyendas de venta exonerada** **solo cuando alguna línea tiene una afectación exonerada** (la del código de tributo 9997 en el catálogo 07). La regla de la API es que una leyenda exige operaciones exoneradas en el documento; la tarjeta no se ofrece donde no puede servir y el servidor sigue siendo quien decide.
- **Una leyenda que quedó marcada no viaja** si ninguna línea sigue exonerada (el estado se conserva en el formulario por si el usuario vuelve atrás, pero la solicitud la omite) ni en una exportación, que no puede tener operaciones exoneradas.
- Los textos de cada leyenda salen del **catálogo 52 que sirve la API**; el formulario no los escribe. El detalle del comprobante muestra las leyendas en la tarjeta *Operación*.

### Transporte de carga (detracción 027, tipo de operación 1004)
- Con el bien o servicio **027** elegido en la detracción, **cada ítem** pide los datos del transporte que la API exige: ubigeo y dirección de **origen** y de **destino** (6 dígitos; 3 a 200 caracteres), **detalle del viaje** (3 a 500) y los **tres valores referenciales** en soles (servicio, carga efectiva y carga útil nominal). Opcionalmente, los **tramos con su vehículo**, hasta 99: ubigeos, configuración vehicular (1 a 15 caracteres), carga útil en toneladas y, sin obligación, descripción, carga efectiva, los dos valores referenciales y el retorno vacío.
- **Usar el transporte del ítem 1 en todos** copia los datos a los demás ítems (con tramos propios), porque un viaje suele tener varios ítems con el mismo origen y destino.
- Los datos viajan **solo** con la detracción 027: con cualquier otro código desaparecen del formulario y de la solicitud (el servidor rechazaría una línea con datos de transporte que no corresponden a la operación 1004).
- El tipo de operación **1004 no se envía**: sigue del código de la detracción (ADR-026). El detalle del comprobante muestra el origen, el destino, el viaje y el número de tramos de cada ítem.
- Los ubigeos son un campo de 6 dígitos: el catálogo 13 solo remite a la lista del INEI y no la trae.

### La vista previa conoce la operación
Para calcular el monto de una detracción 027, la vista previa (ADR-046) tiene que saber que la operación es 1004, o rechazaría los datos del transporte de las líneas. Por eso el documento que se previsualiza **conserva su detracción** (el código, el porcentaje) y la vista previa solo omite **lo que depende del total**: la comprobación del monto de la detracción y de la retención, y las cuotas. Ese cambio corrige el ADR-046, que decía que la detracción se quitaba del documento previsualizado. El monto escrito no forma parte de lo previsualizado ni de lo que vuelve «viejo» el resultado.

## Verificación
- 1 prueba de API nueva (`PreviewApiTests`): la vista previa sabe la operación que fija la detracción 027 (acepta el transporte con 027 y da 1180.00; lo rechaza sin detracción, con otro código y cuando falta en una línea del 027, con `SF-BIL-006`).
- 5 pruebas unitarias nuevas (`operations.test.ts`, `LinesEditor.test.ts`): las leyendas elegidas, ninguna en una exportación, el transporte que viaja solo con 027, los tramos con lo no escrito omitido y la copia con tramos propios.
- 4 recorridos de extremo a extremo (`legends-transport.spec.ts`) contra el simulador: una venta exonerada con su leyenda 2001, mostrada y aceptada; la leyenda que no viaja cuando la línea vuelve a ser gravada; un transporte de dos ítems (copiado del primero) con un tramo, con el monto de detracción que da el servidor, emitido, mostrado y aceptado; y los datos del transporte que aparecen solo con 027 y no viajan con otro código. Accesibilidad (axe) de los formularios.

## Límites (P)
- Siguen por la API: la detracción **004** (recursos hidrobiológicos: embarcación y especie en cada línea), los tipos **0202** y **0205** (huésped), la entrega inicial de una venta al crédito y las notas con detracción.
- El formulario no comprueba el contenido de los ubigeos, de las direcciones ni de las configuraciones vehiculares más allá del largo y del formato; el servidor tampoco, porque SUNAT no los valida contra una lista pública que la plataforma tenga.
- Las leyendas del catálogo 52 que no son de venta exonerada (por ejemplo 2006, «operación sujeta a detracción», o 2011) no se ofrecen: la API solo admite las cuatro de exoneración.
