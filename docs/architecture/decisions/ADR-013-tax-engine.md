# ADR-013: Motor tributario puro y determinista

- Estado: Aceptada · Fecha: 2026-10-01

## Decisión
`SecureFact.TaxEngine` es un cálculo **puro** (sin E/S, sin reloj, sin constantes ocultas) con contrato `ITaxCalculator` en `SecureFact.TaxEngine.Contracts`. Solo usa `decimal`. Tasas (IGV, IVAP, monto ICBPER) y ajustes llegan como entrada: el llamador las resuelve desde reglas con vigencia a la **fecha de emisión** (ADR-008; Parámetros 012 y 024 del libro oficial, S16).

## Modelo (derivado de las reglas oficiales Factura2_0, S16 — 26.08.2026)
- Valor de la línea = cantidad × valor unitario − descuentos (código 00) + cargos (47), redondeado a 2 decimales.
- ISC por línea: sistema al valor = tasa × valor de línea; monto fijo = monto × cantidad. La base del IGV es valor de línea + ISC.
- IGV de línea = tasa × base; IGV del documento = tasa × (Σ bases de línea − descuentos globales 02 + cargos globales 49), redondeado a 2 decimales (se calcula sobre el total, no sumando líneas).
- ICBPER (7152) = monto unitario × cantidad de bolsas; fuera de la base del IGV, dentro del total.
- Operaciones gratuitas (afectaciones 11–16, 21, 31–37) se valorizan con el valor referencial, se reportan bajo el tributo 9996 y **nunca** suman al importe a pagar; solo 11–16 calculan un IGV informativo.
- Totales: valor de venta (LineExtensionAmount) = Σ líneas 1000/1016/9995/9997/9998 ± ajustes globales; precio de venta (TaxInclusiveAmount) = valor de venta + 1000 + 1016 + 2000 + 7152 + 9999; importe total (PayableAmount) = precio de venta + cargos − descuentos que no afectan la base + redondeo (|redondeo| ≤ 1).
- Precio unitario con tributos = (valor + tributos de la línea − descuento 01 + cargo 48) / cantidad, 10 decimales.
- Formatos: importes n(12,2); cantidades y valores unitarios hasta 10 decimales; entradas con más precisión se **rechazan** (no se redondean en silencio).

## Supuestos explícitos (a confirmar, ver matriz)
- **R-024** Modo de redondeo `MidpointRounding.AwayFromZero` (la norma publicada que leímos solo fija decimales y una tolerancia).
- **R-025** Las reglas oficiales validan sumas con "tolerancia ± 1"; la unidad (¿1 sol?) debe confirmarse en la Guía XML. El motor produce valores exactos al centavo, por lo que queda dentro de cualquier tolerancia razonable; la tolerancia solo se usará en el validador de reglas SUNAT (Fase 3).
- No soportados todavía (devuelven `SF-TAX-002`): mezcla IGV + IVAP, ISC sistema 03, anticipos (códigos 04/05/06/20), percepciones/retenciones, detracciones.

## Verificación
40 pruebas unitarias con casos calculados a mano más 3 000 documentos aleatorios que comprueban las identidades de las reglas oficiales (suma de líneas, precio de venta, importe total, deriva del IGV ≤ 1 centavo por línea).
