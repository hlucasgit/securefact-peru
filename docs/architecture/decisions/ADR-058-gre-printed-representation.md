# ADR-058: Representación impresa de las guías de remisión (09 y 31)

- Estado: Aceptada · Fecha: 2026-10-09
- Desarrolla: R-073 de la matriz (fuentes S32 y S33, leídas el 2026-10-09).
- Completa: ADR-056 y ADR-057, que entregaban el XML firmado y el CDR pero no un documento para llevar en el vehículo.

## Contexto
La guía de remisión sustenta un traslado: hay que poder mostrarla en el camino. Lo que dice la norma es poco y está en las resoluciones, no en un anexo de diseño:
- La GRE lleva por norma el emisor (nombre y RUC), la denominación («GRE - remitente» o «GRE - transportista»), la fecha y la hora de emisión, la numeración y, del transportista, el registro MTC. «El Sistema genera el código QR, cuya representación impresa o digital puede ser portada durante el traslado» (art. 4.1 de la RS 123-2022, art. 18 de la RS 188-2010).
- El traslado se sustenta exhibiendo el QR, impreso o digital, **el que genera el contribuyente «a partir de la información proporcionada por la SUNAT en el CDR»** cuando emite por su propio sistema, o **indicando el RUC del remitente, la serie y el número** de la guía (art. 6 de la RS 255-2015 modificado). Con la CDR aceptada SUNAT «remite al emisor electrónico la información necesaria para generar el código QR» (art. 35); antes del traslado hay que tener la CDR aceptada (art. 34).
- El anexo 12 de la RS 123-2022 lista los campos de la guía con su «requisito mínimo», pero **no tiene columna de representación impresa ni dice cómo se compone el QR**. Ningún documento leído (manual de servicios, manual URL, libro de reglas, XSL, resolución y anexo) nombra el elemento de la CDR que trae esa información ni el contenido del QR.

## Decisión
`GET /api/v1/gre/guides/{id}/pdf` (permiso `documents.read`, como el XML y el CDR) devuelve el PDF de una guía en **cualquier estado**; la interfaz lo abre con «Ver PDF» en el detalle.

### Qué lleva
- **Encabezado:** emisor, domicilio fiscal, `RUC`, la denominación del documento y la serie y el número; fecha y hora de emisión (la hora sale del XML firmado) y, de la guía del transportista, el registro MTC.
- **El traslado**, tal como se emitió (se lee de la solicitud guardada, que no cambia): en la guía del remitente, motivo, modalidad, fechas, peso, bultos, observaciones, destinatario, proveedor y comprador, puntos, transportista o vehículos y conductores, bienes y documentos relacionados; en la del transportista, remitente, destinatario, puntos, vehículos y conductores, subcontratador, quién paga el flete, indicadores, bienes (o la guía del remitente que los lista) y documentos relacionados. Los nombres de motivo, modalidad, documento de identidad y documento relacionado salen de los catálogos.
- **Constancia de SUNAT:** estado, respuesta y observaciones de la CDR, fecha de la respuesta (hora de Lima) y el resumen (hash) de la firma.
- **El QR**, solo si la CDR de una guía aceptada trae la dirección. Sin QR la hoja dice que «el QR lo entrega SUNAT con la constancia de la guía aceptada» y que la guía se identifica por el RUC, la serie y el número, que es la otra forma de sustentarla que admite la norma. Nunca se inventa un QR.
- **Una guía que SUNAT no aceptó** (preparada, pendiente, rechazada o fallida) lleva la marca diagonal «SIN VALIDEZ» en cada página y una línea que dice por qué no sustenta el traslado. Las aceptadas (con o sin observaciones) no la llevan.

### Cómo se obtiene la dirección del QR (P)
`GreQrUrl` recorre los elementos hoja de la CDR y toma el primero cuyo **texto completo** es una dirección `https` de un servidor de `sunat.gob.pe` (sin espacios, hasta 2 000 caracteres). No supone el elemento: ninguna fuente lo dice. Se dibuja tal cual la entrega SUNAT, como QR Code 2005 con corrección Q y UTF-8 (los parámetros del QR de los comprobantes, S19; la GRE no los fija). **Es el punto que no se puede cerrar sin una CDR real de una GRE aceptada**; el simulador no inventa ninguna dirección, así que en desarrollo y en las pruebas de extremo a extremo la hoja sale sin QR.

Un hallazgo de las pruebas: si SUNAT pone la dirección en un `cbc:Note`, el lector de CDR de los comprobantes lo toma por una observación y la guía queda «aceptada con observaciones». Se corrige cuando se vea la CDR real.

### Estructura
- `SecureFact.Platform.Printing` (nuevo, compartido): `PdfWriter`, `PdfPage`, `PdfFont`, `Helvetica` (antes internos de CpeEngine) y `PdfQr.Draw`, que dibuja el QR como cuadrados vectoriales. CpeEngine lo usa igual que antes (sus pruebas de PDF no cambian) y se queda sin la dependencia de QRCoder.
- `GrePdfRenderer` (Gre): ordena los bloques, parte el texto en líneas por el ancho real de Helvetica y abre una página nueva con el encabezado cuando no cabe lo siguiente. Es determinista: la misma guía da los mismos bytes.
- `GreService.GetPdfAsync` reúne la guía, la empresa, los catálogos y la dirección del QR.

## Verificación
- Unidad (`GrePdfTests`): lo que muestra cada tipo de guía, la marca por estado, el QR dibujado con y sin dirección, varias páginas con encabezado y marca, validez y determinismo del PDF, y la dirección del QR (qué se acepta y qué no: otros hosts, `http`, texto con otras palabras, atributos, XML inválido y entidades externas).
- API (`GrePdfApiTests`, PostgreSQL): guía preparada con marca y sin QR, aceptada sin marca, QR con una CDR que trae la dirección, guía del transportista, y aislamiento entre cuentas, guía inexistente y sin sesión.
- Interfaz: «Ver PDF» en los dos recorridos de extremo a extremo.
- Se miró el PDF dibujado de las dos guías (la del remitente aceptada con QR y la del transportista preparada con marca).

## Límites (P)
- **El contenido del QR y el elemento de la CDR que lo trae no están documentados**: ver arriba. Cuando se tenga una CDR real hay que fijar el elemento (y ver el efecto en el estado de la guía).
- **No hay plantilla oficial**: el diseño de la hoja es propio. Muestra los campos de la norma y los del traslado, no todos los de la guía (faltan, por ejemplo, la georreferencia y la autorización especial del transportista, que tampoco se emiten).
- El formato es A4 en una tinta; no hay versión en ticket ni etiqueta.
- La impresión de la guía de un tercero (la del remitente que el transportista relaciona) no se une: se cita por su serie y número.
- La fuente de texto es Helvetica estándar (WinAnsi): lo que no está en Latin-1 sale como «?».
- No se guarda el PDF: se compone cada vez desde la guía, que no cambia, así que no hay copia que pueda diferir de ella; la hoja de una guía pendiente cambia al llegar su CDR.
