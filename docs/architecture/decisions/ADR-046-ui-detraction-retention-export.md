# ADR-046: Detracción, retención y exportación en la interfaz, y vista previa del comprobante

- Estado: Aceptada · Fecha: 2026-10-06
- Completa: ADR-038 (interfaz web), que dejó estas operaciones «por API». Las reglas de cada una son las de ADR-026, ADR-030 y las filas de `docs/regulatory/matrix.md`; **este ADR no añade ninguna regla tributaria**.

## Contexto
La API emite facturas con detracción, con retención del IGV y de exportación, pero el formulario de emisión solo hacía ventas internas. Un emisor que necesitaba una de ellas tenía que salir de la interfaz. Hay un obstáculo de diseño: el monto de una detracción (y el de una retención) depende del **importe total**, y la interfaz **no calcula impuestos** (ADR-038): el total lo calcula el servidor.

## Decisión

### Vista previa: `POST /api/v1/documents/preview`
- Calcula **lo que calcularía la emisión** y no emite nada: no toma número, no pide `Idempotency-Key`, no cuenta contra el plan (ADR-042) y no deja rastro. Permiso `documents.create`.
- Pasa por **el mismo código** que la emisión (`PrepareAsync`: adquirente, reglas del documento, tasas de la fecha, totales, boleta sin identificar), de modo que una vista previa no puede decir que sí a lo que la emisión rechaza, ni al revés. Los errores son los mismos (`SF-BIL-006`…).
- Cuerpo: `{document, detractionPercentage?, retentionPercentage?}`. Se comprueba el documento entero **salvo lo que depende del total**: el monto de la detracción, el de la retención y las cuotas, que piden el total ya conocido. La detracción escrita se conserva para que el código fije el tipo de operación y los datos de cada línea (ADR-047).
- Respuesta: los totales y, si se dio el porcentaje, **el monto de la detracción** (el porcentaje del importe total, a centavos, como **sugerencia**: el emisor puede ajustarlo dentro de lo que la API acepta, ±1 sol) o **el monto exacto de la retención** (el cálculo de la regla de la retención).
- **No se afirma ninguna regla de redondeo de SUNAT** para la detracción: el catálogo 54 no trae porcentajes ni SUNAT revisa su valor (solo su estructura), así que el porcentaje y el monto siguen siendo datos del emisor; la vista previa solo le ahorra la aritmética.

### Formulario de emisión (`web/`)
- **Tipo de operación**: venta interna o una exportación. Se ofrecen 0200, 0201, 0203, 0204, 0206, 0207 y 0208; el hospedaje (0202) y el paquete turístico (0205), que piden el huésped en cada línea, se agregaron en el ADR-048.
  - Una exportación **fija la afectación 40** en todas las líneas (el selector queda deshabilitado), pide un adquirente del exterior (documento 0, 4, 7 o A: sin RUC) en los tipos 0200, 0201 y 0204 y en toda boleta, pide el **país del uso** (2 letras, no `PE`) en 0201 y 0208, y **no ofrece** detracción ni retención.
- **Detracción o retención** (solo facturas en soles que no son exportación): elegir una u otra, como la regla pide. La detracción: bien o servicio del catálogo 54, porcentaje, monto y cuenta en el Banco de la Nación (vacía: la de la empresa; obligatoria si la empresa no tiene una). **Calcular el monto** llama a la vista previa y rellena el monto; muestra el importe total.
  - El código **004 (recursos hidrobiológicos)** **no se ofrece**: exige datos de la embarcación y de la especie en cada línea (ADR-030) y sigue por la API. El **027 (transporte de carga)** se ofrece desde el ADR-047.
  - El resultado del cálculo **se descarta apenas el comprobante cambia** (la clave del cálculo es el cuerpo completo): nunca se ve un monto que ya no corresponde.
- El **detalle del comprobante** muestra una tarjeta *Operación* con el tipo de operación, el país del uso, la detracción (bien o servicio, porcentaje, monto, cuenta) y la retención (porcentaje, base y monto).
- La construcción del cuerpo es una función pura con pruebas (`lib/operations.ts`): una venta común no envía tipo de operación, una detracción nunca lo envía, solo una exportación lo nombra, el país solo viaja en 0201 y 0208, y una exportación no lleva detracción ni retención aunque haya quedado elegida.

## Verificación
- 4 pruebas de API (`PreviewApiTests`): la vista previa da el total que luego da la emisión y **no numera** (el primer documento real es el 1); los montos de detracción (12 % de 236.00 = 28.32) y de retención (3 % = 7.08) vienen del servidor, y una detracción ya escrita en el documento no impide el cálculo; las mismas reglas y los mismos códigos que la emisión; no necesita `Idempotency-Key`, **no toma nada del plan**, es de quien emite y no ve la serie de otra cuenta.
- 9 pruebas unitarias nuevas (`operations.test.ts`) y 5 recorridos de extremo a extremo (`operations.spec.ts`) contra el simulador: detracción calculada, emitida, mostrada y aceptada; retención; el cálculo que se descarta y un monto fuera de lo aceptado rechazado por la API; exportación de bienes (afectación fija, adquirente del exterior, sin detracción) y de servicios con país de uso. Accesibilidad (axe) del formulario con detracción y con exportación.

## Límites (P)
- La entrega inicial de una venta al crédito y la detracción de la nota de débito se agregaron en el ADR-049. Las leyendas y el transporte de carga se agregaron en el ADR-047.
- La vista previa muestra el importe total y estos dos montos; no hay aún una vista previa de todo el comprobante (por línea, por impuesto) antes de emitir.
- El monto de la detracción no se redondea al sol aunque SUNAT lo haga en su operación de depósito: la fuente de esa regla no está registrada en `docs/regulatory/sources.md` y no se codifica sin ella.
- La lista de países del uso es un campo libre de 2 letras (ISO 3166-1): el catálogo 04 solo remite al estándar y no trae la lista.
