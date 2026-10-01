# ADR-025: Descuentos y cargos de línea y globales

- Estado: Aceptada · Fecha: 2026-10-01

## Fuentes
Hojas `Factura2_0` y `Boleta2_0` de las reglas de validación del 26.08.2026 (S16: filas «Cargo/descuento por ítem», «Cargos y/o descuentos globales», totales, reglas 3052, 3053, 3114, 3270, 3277–3279, 3280, 3290, 3291, 3300, 3301, 3307), hoja `Resumen Diario1_1` (reglas 4027, «Sumatoria otros cargos del item»), catálogo 53, XSD UBL 2.1 y la prueba contra el beta del 2026-10-01. La guía XML de 2017 usa el código `00` para el descuento global; la hoja vigente usa `02`, y manda la hoja.

## Decisión
- **Cálculo**: ya existía en TaxEngine (ADR-013): descuento 00 y cargo 47 de línea cambian la base; 01 y 48 no la cambian y entran en el precio unitario con tributos (regla 3270); globales 02/49 cambian la base gravada y el IGV (3277, 3278, 3291); 03/50 solo el importe total (3300, 3301, 3280). Los importes son montos; no hay factor porcentual de entrada.
- **Billing**: las líneas y los ajustes globales se guardan como parte de la solicitud original, que ya se conservaba con el documento (`original_request_json`); `DocumentLineDto` expone los cuatro importes de línea y `DocumentDto.Adjustments` los globales. No hay columnas nuevas ni migración.
- **Notas**: no llevan descuentos ni cargos. Las hojas `NotaCredito2_0` y `NotaDebito2_0` no definen los nodos `cac:AllowanceCharge` de línea ni globales; `POST /api/v1/notes` rechaza (`SF-BIL-006`) una nota con ellos y el generador también los rechaza.
- **UBL de factura y boleta**: cada importe distinto de cero produce un `cac:AllowanceCharge` con `ChargeIndicator` (false descuento, true cargo), `AllowanceChargeReasonCode` del catálogo 53, `Amount` y, cuando corresponde, `BaseAmount` y `MultiplierFactorNumeric`. La **base** de 00/47 es el valor de la línea antes de ellos; la de 01/48 es el valor de la línea; la de 02/49 es la base gravada antes del ajuste. 03/50 no llevan base ni factor (opcionales a nivel global). El **factor** se deriva (importe ÷ base, 5 decimales) para que cumpla la regla 3290; no es una entrada. En línea van los cuatro tipos, en este orden: 00, 47, 01, 48; globales: 02, 49, 03, 50. `LegalMonetaryTotal` agrega `AllowanceTotalAmount` y `ChargeTotalAmount` cuando son mayores que cero.
- **Coherencia**: el generador no recalcula; comprueba que los ajustes recibidos coinciden con los totales calculados (si no, `SF-CPE-003`) y rechaza (`SF-CPE-002`) un descuento o cargo sin base positiva o con factor fuera de formato (por ejemplo, un cargo que no toca la base sobre una línea de valor cero). El redondeo del importe total sigue sin soportarse.
- **Resumen diario**: la boleta con cargos y descuentos que no afectan la base informa los cargos en `cac:AllowanceCharge` (`ChargeIndicator` true) y su importe total incluye cargos menos descuentos; el generador comprueba esa suma (la regla 4027 solo observa, con tolerancia de 5, y no resta los descuentos). Los descuentos que afectan la base ya están en los valores de venta.
- **PDF**: el pie imprime «Otros cargos» y «Otros descuentos» cuando existen (los que no afectan la base, los que alteran el importe total); los que afectan la base ya están dentro de los valores de venta por operación.

## Hallazgo del beta (regla 2992)
Al probar una factura con una línea exonerada junto a otra gravada con descuentos, el beta la rechazó con **2992** («el XML no contiene el tag de la tasa del tributo de la línea», tributo 9997). Las líneas exoneradas, inafectas y gratuitas no llevaban `cbc:Percent`. Ahora toda línea lo lleva: la tasa del IGV para las gravadas y las gratuitas con IGV informativo (11–16; la regla 2993 prohíbe 0) y `0.00` para las demás. No dependía de los descuentos: era un defecto del generador que ninguna prueba anterior había tocado.

## Verificado en el beta (2026-10-01)
Una factura con los ocho códigos (línea 00, 47, 01, 48; globales 02, 49, 03, 50) más una línea exonerada: aceptada, código 0, sin observaciones. La misma como boleta enviada con `sendBill`: aceptada. Un resumen diario con una boleta con cargo y descuento que no afectan la base (importe total 108.20, cargo 5.00): aceptado, código 0.

## Límites
- Sin descuentos en notas, sin factor porcentual como entrada, sin descuentos sobre bases distintas de la gravada (la hoja solo permite 02/49 sobre la base IGV/IVAP), sin anticipos (04, 05, 06, 20), FISE (45), percepción (51–53) ni retenciones (62, 63).
- Las operaciones gratuitas con descuentos no se han probado en el beta.
- El PDF no desglosa los descuentos por línea ni los globales que afectan la base.
