# ADR-031: Exportación de servicios (tipos de operación 0201, 0203, 0204, 0206, 0207 y 0208)

- Estado: Aceptada · Fecha: 2026-10-05

## Fuentes
Hojas `Factura2_0` y `Boleta2_0` de las reglas de validación del 26.08.2026 (S16): reglas 2642 (líneas de exportación con afectación 40), 2800 (tipo de documento del adquirente), 3097 (padrón), 3098 y 3099 (país del uso del servicio), 3107, 3273 y 4041; catálogos 51 (0201–0208 y los documentos en que aplican), 52 (leyenda 2008) y 4. Prueba contra el beta del 2026-10-05.

## Decisión
- **Se soportan 0201, 0203, 0204, 0206, 0207 y 0208 en facturas**, además de 0200 (ADR-028). Todas son exportaciones: líneas con afectación 40 y tributo 9995, sin IGV, sin detracción ni retención. `OperationTypes.IsExport` agrupa los tipos soportados.
- **País del uso** (`usageCountryCode`): obligatorio en **0201** y **0208** (regla 3098) y rechazado en los demás tipos. Código ISO 3166-1 de dos letras mayúsculas y distinto de `PE` (regla 3099). El UBL lo emite en `cac:Delivery/cac:DeliveryLocation/cac:Address/cac:Country/cbc:IdentificationCode`, entre el adquirente y los medios de pago (orden UBL). Se guarda en la solicitud original y vuelve en `DocumentDto.UsageCountryCode`.
- **Adquirente según el tipo**: la hoja prohíbe el RUC (regla 2800) solo en **0200, 0201 y 0204**, salvo con la leyenda 2008. En 0203, 0206, 0207 y 0208 acepta el RUC y los tipos del exterior (0, 4, 7, A). El beta lo confirmó: 0206 y 0207 con adquirente RUC fueron aceptados. Billing aplica la regla por tipo (`BillingRules.ValidateBuyer`, parámetros `export` y `mayBeForeign`).
- **La leyenda 2008 no es de exportación**: el catálogo 52 la define como «Venta exonerada del IGV-ISC-IPM. Prohibida la venta fuera de la zona comercial de Tacna» (con las reglas 3289 y 4244 sobre el total exonerado). Es la excepción de la regla 2800, pero esa excepción es inerte (la 2008 exige un total exonerado y una exportación no puede tenerlo, regla 3107). La leyenda se soporta como leyenda de venta exonerada: ADR-032.
- **Boletas de exportación** (2026-10-05, segunda parte): el catálogo 51 admite boletas en 0200, 0201, 0203, 0204, 0206, 0207 y 0208 (no en 0202 ni 0205), y se emiten con el mismo generador. La hoja `Boleta2_0` prohíbe el RUC del adquirente en **todos** esos tipos (regla 2800, a diferencia de la factura): Billing exige en una boleta de exportación un adquirente tipo 0, 4, 7 o A. El país del uso (0201 y 0208) y las líneas con afectación 40 se exigen igual. Una boleta no lleva forma de pago.
- **Resumen diario**: la boleta de exportación (y su nota) entra al resumen con el valor de venta bajo el código **04** del catálogo 11 (`SummaryLineData.ExportAmount`), IGV 0.00 y el importe total igual al valor de exportación; la moneda es la de la boleta.
- **0202 (hospedaje a no domiciliados) y 0205 (paquete turístico)**: la hoja exige en cada línea propiedades del catálogo 55 con los datos del huésped (4000–4009 en 0202; 4000, 4007, 4008 y 4009 en 0205, reglas 3136–3145); son solo de facturas. Se soportan en ADR-033.
- **Notas**: la nota de crédito de motivo 11 ya funciona sobre cualquier factura con importe de exportación (ADR-028); no se probó contra el beta sobre una exportación de servicios.

## Verificado en el beta (2026-10-05)
Una factura de cada tipo soportado, línea de afectación 40 en dólares: 0201 y 0208 con país `US`; 0203 y 0204 con adquirente tipo 0; 0206 y 0207 con adquirente RUC. Las seis aceptadas, código 0, sin observaciones.

**Boletas** (2026-10-05, `sendBill`): 0200 y 0208 (país `US`) con adquirente tipo 0, aceptadas con código 0 y sin observaciones. Una boleta 0207 con adquirente RUC fue **rechazada con el error 2800** («el tipo de documento de identidad del receptor no esta permitido»), lo que confirma la regla de la hoja. Un resumen diario con una boleta de exportación en dólares (código 04, IGV 0.00) fue aceptado, código 0.

## Límites (P)
- **Padrón de exportadores** (regla 3097: en 0201 el emisor debe figurar en el padrón con indicador 05): la plataforma no tiene el padrón y el beta no lo exigió al emisor de prueba. En producción SUNAT puede rechazar un 0201 de un emisor que no esté en el padrón.
- **Código de país**: el catálogo 4 sembrado no trae la lista ISO 3166-1; se comprueba solo el formato (dos letras mayúsculas) y que no sea `PE`.
- La plataforma no decide si un servicio califica como exportación según la norma del IGV: lo declara el emisor al elegir el tipo de operación. Las fuentes de este proyecto no incluyen esos requisitos.
