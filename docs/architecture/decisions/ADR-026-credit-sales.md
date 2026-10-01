# ADR-026: Venta al crédito con cuotas

- Estado: Aceptada · Fecha: 2026-10-01

## Fuentes
Hoja `Factura2_0` de las reglas de validación del 26.08.2026 (S16), bloques «Información adicional - Forma de pago al contado» y «Forma de pago al crédito» (reglas 3244–3256, 3265–3267, 3319, 2071), hoja `Boleta2_0` (sin forma de pago), hojas de notas (el crédito solo aparece en el motivo 13) y la prueba contra el beta del 2026-10-01.

## Decisión
- **Alcance**: facturas (01). La boleta no tiene forma de pago en su hoja; una boleta con cuotas se rechaza.
- **Entrada** (`POST /api/v1/documents`): `installments`, lista de `{ amount, dueDate }`. Sin la lista, la venta es al contado (como hasta ahora). Con ella, es al crédito.
- **Validación en Billing, antes de numerar** (`SF-BIL-006`, sin hueco en la serie): de 1 a 999 cuotas (el identificador es `Cuota` más tres dígitos, regla 3246); cada monto mayor que cero, con hasta 2 decimales y sin superar el importe total (3253, 3266); cada vencimiento posterior a la fecha de emisión (3267); la suma de las cuotas igual al importe total.
- **UBL**: un `cac:PaymentTerms` `FormaPago`/`Credito` con el **monto neto pendiente de pago** (`Amount`, con `currencyID`) y uno más por cuota, `FormaPago`/`Cuota001`, `Cuota002`…, con su `Amount` y `PaymentDueDate` (3245–3256, 3319). El generador repite las comprobaciones y rechaza (`SF-CPE-003`) cuotas incoherentes, y (`SF-CPE-002`) crédito en una boleta o una forma de pago desconocida.
- **Persistencia**: como los descuentos (ADR-025), las cuotas viven en la solicitud original que se conserva con el documento; `DocumentDto.Installments` las expone. No hay migración. La idempotencia ya cubre el plan: la misma clave con otro plan es un conflicto.
- **PDF**: tras las líneas, «Forma de pago: Crédito», el monto neto pendiente y la lista de cuotas con su vencimiento (pagina como las líneas).

## Nota de crédito de motivo 13 (ajuste de montos y/o fechas de cuotas)
Fuentes: hoja `NotaCredito2_0` (reglas 3257, 3259–3261, 3315, 3319–3321, 1080, 3280), catálogo 09 y el beta del 2026-10-01. La guía XML de la nota de crédito no trae ejemplo de este motivo.
- **Qué es**: una nota de crédito (07) sobre una **factura vendida al crédito** (3259, 3260) que reemplaza su plan de cuotas. No vende ni devuelve nada: su **importe total es cero** (3315) y lleva las cuotas nuevas (3257).
- **Entrada** (`POST /api/v1/notes`): `reasonCode` `13`, `installments` y **sin `lines`**; la nota lleva una única línea de valor cero («Ajuste de montos y/o fechas de cuotas», ZZ, afectación 10) que Billing agrega. Con líneas se rechaza. Los demás motivos no admiten cuotas. El motivo 13 existe solo en el catálogo 09: una nota de débito con 13 se rechaza.
- **Validación en Billing, antes de numerar** (`SF-BIL-006`): el documento modificado es una factura con plan de cuotas (se lee de su solicitud original); de 1 a 999 cuotas con monto positivo y 2 decimales; cada vencimiento posterior a la **fecha de emisión de la factura** (3321); la suma de las cuotas, que es el nuevo monto neto pendiente, no supera el importe de la factura (3320). Se pueden emitir varias; vale la última.
- **UBL**: `CreditNote` con `PaymentTerms` `Credito` (monto neto pendiente = suma) y una `CuotaNNN` por cuota, entre el adquirente y los totales, `ResponseCode` 13 y total cero. El generador exige cuotas, total cero y factura como documento modificado.
- **PDF y flujo**: como cualquier nota de factura (espera a que la factura esté aceptada, se envía con `sendBill`); el PDF imprime las cuotas nuevas.
- **Beta**: nota 13 con dos cuotas nuevas sobre una factura al crédito aceptada: aceptada, código 0, sin observaciones, con una línea gravada (10) de valor cero.
- **Regla 1080** («Debe enviar su comprobante por el SEE-Empresas supervisadas»): el emisor afiliado a ese sistema no puede usar esta vía. No hay dato de afiliación en el sistema; no se comprueba (**P**).

## Supuestos (P)
- El monto neto pendiente es el importe total, porque no se soportan detracción ni retención (la hoja lo define como el importe menos ellas). La suma de las cuotas debe igualar el importe total: es más estricto que la regla 3265 (que solo exige que no lo supere). Una entrega inicial pagada al emitir no se puede modelar todavía.
- Se exige al menos una cuota siempre; la hoja lo exige cuando el adquirente tiene RUC (3249, 3251, 3254, 3256) y lo deja opcional en otros casos.
- No se exige orden creciente de los vencimientos (la hoja no lo pide); el número de cuota es la posición en la lista.
- La impresión de la forma de pago en la representación impresa es una decisión de producto: la norma consultada (S21) no la fija.

## Verificado en el beta (2026-10-01)
Una factura al crédito con dos cuotas (a 30 y 60 días): aceptada, código 0, sin observaciones.

## Límites
- La nota de motivo 13 no lleva descuentos, cargos ni líneas propias; no modela una entrega inicial ni cambia el importe de la factura.
- Las notas de otros motivos sobre una factura al crédito no cambian las cuotas; la factura conserva su plan.
- Sin entrega inicial, sin detracción ni retención sobre el monto neto.
