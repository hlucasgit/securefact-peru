# ADR-032: Leyendas de las ventas exoneradas (2001, 2002, 2003 y 2008)

- Estado: Aceptada · Fecha: 2026-10-05

## Fuentes
Hojas `Factura2_0` y `Boleta2_0` de las reglas de validación del 26.08.2026 (S16): reglas 3027 (la leyenda es del catálogo 52), 3006 (descripción de 1 a 200 caracteres), 3283–3285 y 3289 en facturas (error) y 4022–4024 y 4244 en boletas (observación), 3107 y 2800; catálogo 52 (textos de 2001, 2002, 2003 y 2008). Prueba contra el beta del 2026-10-05.

## Decisión
- **Se soportan cuatro leyendas con la misma estructura**: 2001 (bienes transferidos en la Amazonía), 2002 (servicios prestados en la Amazonía), 2003 (contratos de construcción en la Amazonía) y 2008 (venta exonerada en la zona comercial de Tacna). Para las cuatro la hoja exige lo mismo: que el documento tenga operaciones exoneradas del IGV (`cac:TaxSubtotal` 9997 con base positiva). Se pidió la 2008; las otras tres se agregaron porque el mecanismo y la regla son idénticos y salen del mismo catálogo.
- **API**: `legendCodes` en `POST /api/v1/documents` (lista de códigos; facturas y boletas). Se rechaza (`SF-BIL-006`, sin consumir número) un código que no sea uno de los cuatro, un código repetido, una exportación (la regla 3107 prohíbe el total exonerado en una exportación) y un documento sin operaciones exoneradas. En boletas la hoja solo observa la falta de operaciones exoneradas (4022–4024, 4244); la plataforma las rechaza igual para no emitir con observaciones. Admite varias leyendas a la vez y documentos mixtos (exonerado y gravado).
- **UBL**: un `cbc:Note` por leyenda con `languageLocaleID` = código y el texto del catálogo 52, sin las comillas tipográficas del catálogo (`ExemptionLegends.Texts`). El PDF imprime el texto de cada leyenda entre las líneas de información adicional (decisión de producto: el anexo no fija su ubicación).
- **Persistencia**: viven en la solicitud original y vuelven en `DocumentDto.LegendCodes`.
- **Notas y resumen diario**: las hojas de las notas no definen estas leyendas, y el resumen diario no lleva leyendas: no se soportan en notas y no cambian el resumen.
- **La excepción de la regla 2800 es inerte**: la hoja permite un adquirente con RUC en 0200, 0201 y 0204 si hay leyenda 2008, pero la 2008 exige un total exonerado (3289) y una exportación no puede tenerlo (3107). La plataforma no soporta esa combinación (ADR-031).

## Verificado en el beta (2026-10-05)
Factura y boleta exoneradas con la leyenda 2008, y una factura con las tres leyendas de la Amazonía (2001, 2002 y 2003) a la vez: las tres aceptadas, código 0, sin observaciones.

## Límites (P)
- **2009** («Primera venta de mercancía identificable entre usuarios de la zona comercial») y los códigos 7000 y 7001 del catálogo 55 (propiedades del ítem de esas ventas): la hoja no define reglas para ellos; no se soportan.
- **Código de usuario de Zofratacna** (boleta: `cbc:AdditionalAccountID` del adquirente, «sin validación» en la hoja): no se soporta.
- Que el emisor o la operación califiquen de verdad para la exoneración de la Amazonía o de la zona comercial de Tacna no lo comprueba la plataforma ni lo comprueba el beta: las fuentes de este proyecto no traen los requisitos; lo declara el emisor.
- Las leyendas 2004 (agencia de viajes), 2005 (venta itinerante), 2010 y 2011 siguen sin soporte.
