# ADR-037: ISC e ICBPER en el UBL

- Estado: Aceptada · Fecha: 2026-10-06
- Completa: ADR-013 (el motor tributario ya calculaba ISC e ICBPER) y ADR-016 (generador UBL)

## Contexto
El `TaxCalculator` calculaba el ISC (tributo 2000) y el impuesto al consumo de las bolsas de plástico (ICBPER, tributo 7152), pero el generador UBL los rechazaba (`CPE-UNSUPPORTED`): un documento con esos impuestos no podía llegar a SUNAT. El valor del ICBPER (S/ 0,50) estaba `Pending`.

## Decisión

### Qué se emite (hojas `Factura2_0`, `Boleta2_0`, `NotaCredito2_0`, `NotaDebito2_0`, `Resumen Diario1_1` y catálogos 05 y 08)
- **ISC en la línea**: un `cac:TaxSubtotal` con base = valor de venta de la línea, monto = ISC, `cbc:Percent` (nunca cero, regla 3104) y `cbc:TierRange` con el sistema del catálogo 08 (`01` al valor, `02` monto fijo; el sistema 03, precio de venta al público, no existe en el motor); sin `TaxExemptionReasonCode` (regla 3050). Un ISC de monto fijo no tiene tasa: se declara el porcentaje que resulta del monto sobre la base, de modo que el monto siga siendo tasa × base (regla 3108).
- **El ISC entra en la base del IGV de la línea** (regla 3272): el subtotal 1000 de la línea lleva base = valor + ISC. **El total global** 1000 lleva solo el valor de venta (regla 3277) y el 2000 global la suma de las bases y de los montos de las líneas (reglas 3296 y 3298).
- **ICBPER en la línea**: un subtotal sin base ni tasa, con `cbc:TaxAmount`, `cbc:BaseUnitMeasure unitCode="NIU"` = cantidad de bolsas y `cbc:PerUnitAmount` = monto por bolsa en `cac:TaxCategory` (reglas 3236–3238, 4318, 4320). Globalmente, un subtotal solo con el monto (regla 3306). El ICBPER **no** forma parte de la base del IGV y sí del total.
- **Monto del tributo de la línea** = suma de los tributos de la línea (regla 3292); el subtotal del IGV de la línea lleva solo el IGV.
- **Resumen diario**: una boleta con ISC o ICBPER lleva un `cac:TaxTotal` más por cada uno (sin tasa; reglas 133–156 de la hoja), y el importe total los incluye (observación 4027). El valor de venta gravado no incluye el ISC.
- **PDF**: filas «ISC» e «ICBPER» en los totales cuando existen. El QR sigue llevando solo el IGV.

### API y validaciones
- La línea del documento ya traía `tax.isc` (sistema y tasa o monto) y `tax.plasticBagCount`; ahora se **conservan en la lectura** (`DocumentLineDto.Isc`, `PlasticBagCount`) y pasan al XML.
- **Billing** rechaza (422, `SF-BIL-006`) una línea con bolsas cuya cantidad no sea la de la línea o pase de cinco dígitos (reglas 3236 y 2892).
- **El generador** comprueba además: el ISC de la línea se declara si y solo si el cálculo lo tiene, el monto por bolsa vigente es positivo, y no hay ICBPER antes del 2019-08-01 (regla 2949). Las exportaciones y los documentos con IVAP no pueden llevar ISC (el motor lo limita a líneas gravadas con IGV; reglas 3107 y 2650).
- **Monto por bolsa**: lo resuelve `ElectronicDocumentService` por la fecha de emisión y lo declara en el XML (observación 4237).

### La regla `tax.icbper.unit_amount` pasa a `Verified`
Ley 30884, art. 12.5 (texto en El Peruano, fuente S26): S/ 0,10 en 2019, 0,20 en 2020, 0,30 en 2021, 0,40 en 2022 y 0,50 desde 2023; el art. 12.6 dice que no forma parte de la base del IGV. Las versiones 2 a 6 de la regla llevan esas fechas (`2019-08-01`, `2020-01-01`, `2021-01-01`, `2022-01-01`, `2023-01-01`). La versión 1 (S/ 0,50 sin fecha, `Pending`) se queda porque las versiones cargadas son inmutables; solo rige antes del 2019-08-01, donde el generador no admite ICBPER.

## Verificación
- Pruebas unitarias del generador (esquema UBL 2.1 oficial): ISC al valor y de monto fijo, bolsas, ISC y bolsas juntos, factura y boleta, notas de crédito y débito, resumen diario y los rechazos; pruebas de API de punta a punta (cálculo, XML firmado, aceptación, PDF, resumen, nota); y una prueba del calendario de la regla.
- **Beta de SUNAT (2026-10-06)**: aceptadas con código 0 y sin observaciones: factura con ISC al valor, con ISC de monto fijo, con bolsas, con ISC y bolsas más su nota de crédito, boleta con ISC y bolsas más su nota de débito, y un resumen diario con una boleta con ISC y bolsas.

## Límites (P)
- **El beta no comprobó el monto por bolsa**: una factura con S/ 0,10 por bolsa (distinto del vigente) también fue aceptada, sin la observación 4237. El valor sale de la ley, no del beta.
- **Sistema 03 del ISC** (precio de venta al público), **anticipos de ISC** (descuento 20) y **otros tributos** (9999) no están soportados.
- Un ISC de monto fijo declara el porcentaje que resulta del cálculo; qué espera SUNAT en ese campo no está documentado (el beta lo aceptó).
- Una nota debe repetir por su cuenta el ISC y las bolsas de sus líneas: no se derivan del documento que modifica.
