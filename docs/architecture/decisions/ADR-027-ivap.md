# ADR-027: IVAP (arroz pilado) y nota de crédito de motivo 12

- Estado: Aceptada · Fecha: 2026-10-01

## Fuentes
Hojas `Factura2_0`, `Boleta2_0`, `NotaCredito2_0` y `Resumen Diario1_1` de las reglas de validación del 26.08.2026 (S16: reglas 2644, 3103, 3107, 3221, 3230, 3293, 3295, 4264, 4302, 3051), catálogos 05 (1016, «IVAP», VAT), 07 (17 «Gravado - IVAP»), 09 (12 «Ajustes afectos al IVAP») y 52 (2007 «Operación sujeta a IVAP»), la página de orientación de SUNAT sobre el IVAP (S22: tasa 4 %, Ley 28211, art. 3) y la prueba contra el beta del 2026-10-01.

## Decisión
- **Tasa**: 4 % (`tax.ivap.rate`), ahora **verificada** contra S22; antes era `Pending`. El Parámetro 024 la define por fecha y no se ha leído: si cambiara, la regla es un dato versionado.
- **Cálculo**: ya existía en TaxEngine. Una línea con afectación 17 es base IVAP; no se mezcla con líneas IGV en un comprobante (el motor lo rechaza).
- **UBL de factura, boleta y nota**: la línea y el total declaran el tributo `1016` (`IVAP`, `VAT`, categoría S); la línea lleva `cbc:Percent` 4.00 (regla 3103) y `TaxExemptionReasonCode` 17. Un comprobante con una línea IVAP de valor positivo lleva la **leyenda 2007** «Operación sujeta a IVAP» (`cbc:Note languageLocaleID="2007"`), o el beta lo observa (4264). La tasa llega al generador como `IvapRate`; sin ella, una línea IVAP se rechaza (`SF-CPE-003`).
- **Resumen diario**: la boleta con IVAP informa el tributo 1016 con su base, su importe y su tasa (`SummaryLineData.IsIvap`). `SummaryLines.From` construye la línea para el resumen y para la baja con estado 3.
- **Nota de crédito de motivo 12** (ajustes afectos al IVAP): solo lleva líneas con afectación 17 (2644, 3221, 3107) y solo modifica un documento afecto al IVAP; las líneas IVAP son exclusivas de este motivo entre las notas (3230). Billing lo valida antes de numerar (`SF-BIL-006`); el generador lo repite. El tope y el acumulado de notas (ADR-023) incluyen el IVAP.
- **PDF**: fila «IVAP» en lugar de un «IGV» en cero.

## Verificado en el beta (2026-10-01)
- Factura con una línea IVAP (100 + 4 de IVAP): aceptada, código 0, sin observaciones; nota de crédito de motivo 12 sobre ella: aceptada, código 0, sin observaciones.
- Resumen diario con una boleta IVAP: **aceptado con observación 4019** («El calculo del IGV no es correcto - Error en la linea: 1. codigo tributo: 1000»), aunque la línea declara el tributo 1016 con su tasa 4.00 y la hoja pide la comprobación 4302 para ese tributo. Parece que el beta aplica a la línea la comprobación del IGV. No bloquea (código 0). Se deja el XML como lo define la hoja y se anota el hallazgo; queda por ver si producción lo trata igual.

## Límites
- El código QR sigue usando el IGV del comprobante (cero en un comprobante IVAP): falta confirmar en S19 qué monto va (**P**).
- Sin ISC, ICBPER ni exportación; los motivos 11 (exportación) siguen pendientes.
- Los comprobantes IVAP no se han probado en producción ni con descuentos o crédito combinados en el beta.
