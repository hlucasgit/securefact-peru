# ADR-028: Exportación de bienes y nota de crédito de motivo 11

- Estado: Aceptada · Fecha: 2026-10-01

## Fuentes
Hojas `Factura2_0` y `NotaCredito2_0` de las reglas de validación del 26.08.2026 (S16: reglas 2642, 2800, 2992, 3000, 3003, 3105, 3107, 3110, 3194, 3221, 3223, 3273, 3278, 3503), catálogos 05 (9995 «Exportación», FRE, EXP), 06, 07 (40 «Exportación de Bienes o Servicios»), 09 (11 «Ajustes de operaciones de exportación») y 51 (0200 «Exportación de Bienes») y la prueba contra el beta del 2026-10-01. La guía XML de 2017 usa el tipo 0102 para la exportación; el libro vigente usa 0200, y manda el libro.

## Decisión
- **Alcance**: tipo de operación **0200** (exportación de bienes) en **facturas**. Los servicios (0201–0208) y las boletas de exportación se rechazan explícitamente: los primeros exigen datos propios (país de uso, propiedades del ítem, padrón; reglas 3098, 3136–3145, 3097) y las segundas, el resumen diario con tipo de valor de venta de exportación.
- **Entrada** (`POST /api/v1/documents`): `operationTypeCode` opcional, `0101` por defecto o `0200`. Una exportación lleva **solo líneas con afectación 40** (regla 2642) y las líneas 40 existen solo en una exportación; se rechaza (`SF-BIL-006`, antes de numerar) cualquier mezcla. El tipo de operación se guarda en la solicitud original y `DocumentDto.OperationTypeCode` lo expone.
- **Adquirente**: del exterior. La regla 2800 prohíbe el RUC en una exportación sin la leyenda 2008 (Tacna), que no se soporta; Billing acepta los tipos 0, 4, 7 y A. El beta lo confirmó: con un RUC rechazó la factura con 2800.
- **Cálculo**: ya existía (tributo 9995, total de exportación; sin IGV, importe total = valor de exportación).
- **UBL**: `InvoiceTypeCode@listID` = 0200; el tributo `9995` (`EXP`, `FRE`, categoría **G** de UN/ECE 5305, el código «exportación sin impuesto») en el total y en la línea; la línea lleva `cbc:Percent` 0.00 (2992, 3110) y `TaxExemptionReasonCode` 40; el total de tributos es 0.00 (3000). El generador exige que todas las líneas sean de exportación si el tipo es 0200 y ninguna si es 0101.
- **Nota de crédito de motivo 11**: solo líneas de exportación y solo sobre una factura de exportación (3221, 3107, 3194; Billing lo comprueba con el total de exportación del original). Otros motivos pueden llevar líneas de exportación, pero una nota no mezcla líneas de exportación con otras. El tope por nota y el acumulado (ADR-023) incluyen la exportación (regla 3503).
- **PDF**: fila «Op. exportación».

## Verificado en el beta (2026-10-01)
Factura de exportación (0200, línea 40, adquirente tipo 0 sin documento) aceptada, código 0, sin observaciones; nota de crédito de motivo 11 sobre ella: aceptada, código 0, sin observaciones. Una factura de exportación con adquirente RUC: rechazada, **2800** (`cbc:ID/schemeID` valor 6).

## Límites
- Servicios de exportación: ADR-031. Sin boletas de exportación, leyenda 2008, operaciones gratuitas de exportación ni descuentos de exportación probados en el beta.
- La letra «G» de la categoría la acepta el beta; la hoja no la fija (**P**).
- El adquirente con tipo `-` (guion) que la regla 2800 menciona no se soporta; se usa el tipo 0.
