# ADR-049: Detracción en la nota de débito, entrega inicial del crédito y vista previa de notas

- Estado: Aceptada · Fecha: 2026-10-06
- Completa: ADR-026 (venta al crédito), ADR-029 (detracción y retención) y ADR-046 (vista previa). Cierra lo que ADR-046 a ADR-048 dejaron «por la API».

## Fuentes
Hojas `NotaDebito2_0` y `NotaCredito2_0` de las reglas de validación del 26.08.2026 (S16, `docs/regulatory/assets/reglas-de-validacion-2026-08-26.xlsx`), leídas completas para esta decisión, y la prueba contra el beta del 2026-10-06 (R-061). La hoja de la **nota de débito** tiene la sección «Información adicional - detracciones» (campos 61 a 63, filas 438 a 454): `cac:PaymentTerms` con `cbc:ID` «Detraccion» (código del catálogo 54 en `PaymentMeansID`, monto en soles, porcentaje sin validación) y `cac:PaymentMeans` con `cbc:ID` «Detraccion» (medio de pago del catálogo 59 y cuenta en `PayeeFinancialAccount/ID`), con las reglas 3313 y 3314 (una sin la otra es error), 3127 y 3033 (el código existe), 3034 (la cuenta), 3035 y 3037 (el monto positivo con 2 decimales) y 3208 (moneda PEN). **La hoja de la nota de crédito no tiene ninguna sección de detracción.** Ninguna de las dos hojas liga la detracción a un tipo de operación (la nota no lo tiene), exige datos de línea ni una leyenda 2006.

## Decisión

### Detracción en la nota de débito
- `CreateNoteRequest` acepta `detraction` (el mismo objeto de las facturas). Solo **una nota de débito sobre una factura, en soles**:
  - una **nota de crédito la rechaza** (`SF-BIL-006`): su hoja no la admite;
  - una **boleta** no la lleva (la detracción es de las facturas: ADR-029; la hoja no lo dice y es una decisión de producto conservadora);
  - una factura en **dólares** tampoco (3208).
- Los datos se comprueban **como en una factura** (código del catálogo 54, porcentaje, monto positivo con dos decimales que coincida con el porcentaje del **importe total de la nota** con el redondeo de hasta un sol, cuenta): es una sola función (`ValidateDetractionData`) para ambos documentos. Sin cuenta en la solicitud se usa la de la empresa, y **la nota emitida conserva la cuenta usada**, como la factura.
- **Sin tipo de operación**: una nota no lo tiene; la detracción no le da uno (el DTO de la nota sigue diciendo `0101`, y la interfaz no muestra «Tipo de operación» en una nota). **Todos los códigos del catálogo 54** valen, también 004, 027 y 028, **sin datos de línea**: la hoja de la nota no los pide, y una nota sigue sin llevar datos de pesca, de transporte ni de huésped.
- **UBL** (`DebitNote`): `cac:PaymentMeans` y luego `cac:PaymentTerms` de la detracción, después del adquirente y antes del total de impuestos (orden UBL), con las mismas formas que en la factura. El generador repite las comprobaciones y **valida contra el XSD oficial**. La **representación impresa** ya imprimía la detracción de cualquier documento que la tenga.
- **Verificado en el beta de SUNAT (2026-10-06)**: factura con detracción aceptada y nota de débito sobre ella con su propia detracción (código 037, 12 %, cuenta de once ceros): **aceptada, código 0, sin observaciones** (R-061).

### Vista previa de notas y monto neto pendiente
- `POST /api/v1/notes/preview` (`documents.create`): como la vista previa del comprobante (ADR-046), pasa por el mismo código que la emisión (`PrepareNoteAsync`), no numera nada y devuelve el importe total de la nota y el monto sugerido de su detracción; las reglas de la nota (nota de crédito, boleta, moneda) las aplica igual.
- La respuesta de ambas vistas previas trae ahora **`netPendingAmount`**: el monto neto pendiente de pago de una venta al crédito, es decir, el importe total **menos la detracción** (la escrita, o la sugerida si no hay) **o la retención, y menos la entrega inicial**. Las cuotas deben sumar exactamente eso (ADR-026, regla 3319). La interfaz **no lo calcula**.

### Interfaz (`web/`)
- **Entrega inicial**: en la venta al crédito, un campo opcional. **«Calcular el monto neto pendiente»** pide la vista previa y muestra lo que las cuotas deben sumar; el resultado se descarta apenas cambia el comprobante, pero **escribir las cuotas no lo descarta** (no son parte de lo previsualizado). El detalle del comprobante muestra la entrega inicial.
- **Nota de débito**: sobre una factura en soles, una tarjeta **Detracción** con los mismos campos que la factura (`DetractionFields`, un componente compartido) y **Calcular el monto**. No se ofrece en una nota de crédito ni sobre una boleta. El detalle de la nota muestra la detracción.

## Verificación
- Generador: 3 pruebas nuevas (la detracción de la nota de débito en su orden, con el XSD; sin detracción no hay medios de pago y la nota de crédito no la lleva; y las cuatro formas que la hoja no admite se rechazan).
- API (`NoteDetractionApiTests`, 5 pruebas): la nota con detracción llega al documento, al XML y al PDF; la nota de crédito, la boleta y la factura en dólares la rechazan; los datos se comprueban como en una factura y una solicitud repetida devuelve la nota original; la cuenta de la empresa se usa y se conserva; y la vista previa de la nota da el total, el monto sugerido y el neto pendiente sin numerar nada. `PreviewApiTests`: el neto pendiente con entrega inicial, detracción, retención y un monto ajustado.
- Extremo a extremo (`credit-notes.spec.ts`, 4 recorridos): venta al crédito con entrega inicial cuyo neto pendiente da el servidor y que SUNAT (simulador) acepta; el cálculo que se descarta y unas cuotas que no suman lo pendiente que la API rechaza; una nota de débito con su detracción calculada, emitida, mostrada y aceptada; y la boleta que no ofrece detracción. Accesibilidad (axe).

## Límites (P)
- La regla de que la boleta no lleva detracción en una nota es **de producto**: la hoja de la nota no la menciona. No se probó contra el beta una nota de débito sobre una boleta con detracción (no se emite).
- No se probó contra el beta una nota con los códigos 004, 027 o 028: la hoja no impone nada, pero tampoco se verificó.
- El porcentaje y el monto de la detracción de la nota siguen siendo del emisor: la hoja no los valida (`<<< SIN VALIDACIÓN >>>`).
- Una nota de crédito de un comprobante con detracción **no** devuelve ni ajusta la detracción: la hoja de la nota de crédito no tiene dónde.
