# Activos regulatorios oficiales

Copias **byte a byte** de documentos oficiales de SUNAT, descargadas el 2026-10-01 con autorización del propietario del proyecto. No se editan nunca; el hash SHA-256 de cada archivo está en `SHA256SUMS.txt` y forma parte de su procedencia. Cuando SUNAT publique una versión nueva se añade al lado (otra carpeta o nombre con fecha) y se registra en `../sources.md`; no se reemplaza.

| Archivo | Fuente | Vigencia / versión | Notas |
|---------|--------|--------------------|-------|
| `reglas-de-validacion-2026-08-26.xlsx` | https://cpe.sunat.gob.pe/sites/default/files/2026-08/Reglas%20de%20validaci%C3%B3n%20-%20actualizado%20al%2026.08.2026.xlsx | 26.08.2026 | Reglas de validación CPE, **Catálogos (Anexo N.°8)**, códigos de retorno, parámetros. SHA-256 coincide con el calculado al leerlo en el navegador (S16). |
| `guia-xml-factura-2.1.pdf`, `guia-xml-boleta-2.1.pdf`, `guia-xml-nota-credito-2.1.pdf`, `guia-xml-nota-debito-2.1.pdf` | https://cpe.sunat.gob.pe/guias-y-manuales (sección *Anexos y guías*) | UBL 2.1 | Guías de elaboración de XML (S18). |
| `guia-resumen-diario-2.0.pdf` | idem | UBL 2.0, 11.01.2018 | Resumen diario de boletas y notas. |
| `xsd/2.1/**` | https://docs.oasis-open.org/ubl/os-UBL-2.1/xsd/ | OASIS UBL 2.1 | Ver abajo. |

## XSD de SUNAT
`xsd-ubl-2.1-actualizado-2022-02-28.zip` (609 363 bytes, SHA-256 `5cac9d9353521340fbc15d23f465a4946b004535b9ff411541c87da01694dbd5`, "actualizado al 28/02/2022") fue descargado a mano por el propietario del proyecto desde `https://cpe.sunat.gob.pe/sites/default/files/inline-files/Archivos%20XSD%20(1).zip` (el servidor bloquea la descarga automatizada con 403) y se verificó contra el hash que SUNAT sirve. Se conserva intacto junto a su contenido extraído:

- `xsd/2.1/` — 19 archivos UBL 2.1 (14 de `common/` más `Invoice`, `CreditNote`, `DebitNote`, `DespatchAdvice`, `ApplicationResponse`). Se obtuvieron además de OASIS (`https://docs.oasis-open.org/ubl/os-UBL-2.1/xsd/`) y son **idénticos byte a byte** a los del zip de SUNAT.
- `xsd/2.0/` — 27 archivos UBL 2.0 con extensiones SUNAT `UBLPE-*` extraídos del zip sin modificar (`Invoice`, `CreditNote`, `DebitNote`, `Perception`, `Retention`, `SummaryDocuments`, `VoidedDocuments`, `ApplicationResponse`, `SunatAggregateComponents`, `xmldsig`, listas de códigos). Los usan el resumen diario, la comunicación de baja, retenciones y percepciones.
