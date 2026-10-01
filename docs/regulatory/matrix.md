# Matriz normativa

Columnas: Requirement · LegalSource (ID en `sources.md`) · TechnicalImpact · Module · Test · ImplementationStatus · EffectiveDate · LastReviewed.

Estados: `Pendiente` (sin implementar), `Parcial`, `Hecho`, `Bloqueado` (falta verificar fuente), `N/A`.

| ID | Requirement | LegalSource | TechnicalImpact | Module | Test | Status | EffectiveDate | LastReviewed |
|----|-------------|-------------|-----------------|--------|------|--------|---------------|--------------|
| R-001 | Factura y notas vinculadas se envían hasta 3 días calendario desde la emisión | S02 | Política `InvoiceSubmissionDeadline` versionada; alarma de vencimiento | ElectronicDocuments / Sunat | `DeadlinePolicyTests` | Pendiente | vigente | 2026-09-30 |
| R-002 | CDR con estados Aceptada / Aceptada con observación / Rechazada | S02, S04 | Máquina de estados; observación ≠ rechazo | ElectronicDocuments | `StateMachineTests` | Pendiente | vigente | 2026-09-30 |
| R-003 | Nota de crédito solo sobre comprobantes con CDR de aceptación | S02 | Validación de documento origen | Billing | `CreditNoteOriginRulesTests` | Pendiente | vigente | 2026-09-30 |
| R-004 | Serie `F…` facturas / `B…` boletas; correlativo desde 1 | S02, S04 | Value Object `Series`; servicio de numeración | Billing | `SeriesTests` | Pendiente | vigente | 2026-09-30 |
| R-005 | Factura rechazada consume el número | S04 | Numeración nunca se reutiliza ni se libera | Billing | `SequenceConsumptionTests` | Pendiente | vigente | 2026-09-30 |
| R-006 | Firma XMLDSig enveloped en UBLExtension, X.509 v3, RUC en OU | S04 | `IXmlSigner`, validación de certificado | Certificates | `XmlSignerTests` (golden) | Pendiente | vigente | 2026-09-30 |
| R-007 | Envío SOAP `sendBill/sendSummary/sendPack/getStatus` con WS-Security UsernameToken | S04 | `DirectSunatChannel` | Sunat | contract tests con simulador | Pendiente | vigente (manual 2021, verificar) | 2026-09-30 |
| R-008 | Clasificación de errores por rango (0100–999, 1000–1999, 2000–3999, 4000+) | S04 | Política de reintentos | Sunat | `ErrorClassificationTests` | Pendiente | vigente | 2026-09-30 |
| R-009 | Conservación de comprobantes y disponibilidad web al adquirente 1 año | S02 | Retención de archivos; portal adquirente | ElectronicDocuments | — | Pendiente | vigente | 2026-09-30 |
| R-010 | Canal GRE REST OAuth2; CDR aceptada antes del traslado; no vía OSE | S05, S06 | Módulo `Gre` independiente | Gre | — | Bloqueado (falta Manual URL-GRE) | vigente | 2026-09-30 |
| R-011 | Con PSE, firma con certificado del PSE y alta del PSE por el contribuyente en SOL | S01 | Modelo de canal y autorizaciones | Sunat / Certificates | — | Pendiente | vigente | 2026-09-30 |
| R-012 | Tasa de rechazo por emisor ≤ 10 % (12 meses) luego ≤ 5 % (solo si PSE) | S01 | KPI `rejection_rate{tenant}` desde el MVP | Reporting | — | Pendiente | si PSE | 2026-09-30 |
| R-013 | Requisitos PSE (150 UIT, 5 trabajadores, ISO 27001, etc.) | S01 | Gap analysis | docs/pse | — | Parcial (doc) | vigente | 2026-09-30 |
| R-014 | Servicio beta solo para estructura XML; sin estrés | S04 | Simulador interno para carga | Sunat | — | Pendiente | vigente | 2026-09-30 |
| R-015 | Receptor único `e-factura.sunat.gob.pe` | S08 | Config de endpoints versionada | Sunat | — | Pendiente | 2021-07-20 | 2026-09-30 |
| R-016 | Catálogos SUNAT vigentes (Anexo N.°8, 47 catálogos en la hoja `Catálogos` de S16) | S16 (V) | Módulo `Catalogs` con vigencias; carga desde el xlsx versionado por hash | Catalogs | — | Desbloqueado (xlsx versionado en `assets/`; falta el importador del módulo `Catalogs`) | 2026-08-26 | 2026-10-01 |
| R-017 | Boletas: envío individual dentro del plazo máximo (Parámetro 004); fuera de plazo (1079) o contingencia, Resumen Diario | S16 (V), S02, S04; guía XML boleta S18 (**P**) | Ambos caminos modelados; `DailySummary` independiente | Sunat | — | Parcial | 2026-08-26 | 2026-10-01 |
| R-018 | Contenido del QR | **P** | `IQrPayloadGenerator` | ElectronicDocuments | — | Bloqueado | — | — |
| R-019 | Detracciones SPOT | S12 **P** | Reglas versionadas | TaxEngine | — | Bloqueado | — | — |
| R-020 | Protección de datos personales (Ley 29733 y reglamento) | **P** | Retención, minimización, exportación | Privacy / Audit | — | Bloqueado | — | — |
| R-021 | Estructura del RUC: 11 dígitos + dígito verificador módulo 11 (validación sintáctica; no implica existencia/habido) | Algoritmo de uso común; ejemplo `20100066603` tomado de S04 | `Ruc` value object | SharedKernel | `RucTests` | Parcial (algoritmo no confirmado en norma SUNAT: **P**) | — | 2026-09-30 |
| R-022 | Serie de 4 caracteres alfanuméricos; correlativo 1–8 dígitos | S04 (nombre de archivo) | `Series`, `DocumentNumber` | SharedKernel | `SeriesAndNumberTests` | Hecho (sintaxis) | vigente | 2026-09-30 |
| R-023 | Código de establecimiento anexo (4 caracteres) y ubigeo (6 dígitos) | Guías XML UBL 2.1 (S03) — **P**: no leídas campo por campo | Validación estructural en `CompanyAdministration`; catálogo oficial de ubigeo pendiente | Organizations | `OrganizationsApiTests` | Parcial (sintaxis) | — | 2026-10-01 |
| R-024 | Modo de redondeo de importes a 2 decimales | **P** (no fijado en las fuentes leídas) | `TaxCalculator` usa AwayFromZero; configurable por política | TaxEngine | `TaxCalculatorTests` | Supuesto | — | 2026-10-01 |
| R-025 | Tolerancia "+ - 1" en validaciones de sumas (unidad por confirmar) | S16 (V) / Guía XML (**P**) | Validador de reglas SUNAT (Fase 3) | TaxEngine / ElectronicDocuments | — | Pendiente | 2026-08-26 | 2026-10-01 |
| R-026 | Fórmulas de totales de factura (valor de venta, precio de venta, importe total, IGV global) | S16 Factura2_0 (V) | `TaxCalculator` | TaxEngine | `TaxCalculatorTests` | Hecho (sin anticipos/IVAP mixto/ISC-03) | 2026-08-26 | 2026-10-01 |
| R-027 | Catálogos 05, 07, 08, 53 (tributos, afectación IGV, sistema ISC, cargos/descuentos) | S16 hoja Catálogos (V) | `TaxCodes`, mapa de afectaciones | TaxEngine | `TaxCalculatorTests` | Hecho (transcritos; importar del xlsx en `Catalogs`) | 2026-08-26 | 2026-10-01 |
| R-028 | Formato de serie por tipo de documento (01→F…, 03→B…, 07/08→F/B…; numéricas de 4 dígitos = contingencia, no soportadas) | S16 General 0151/0159 (V), S02 | `BillingRules.ValidateSeriesCode` | Billing | `BillingApiTests` | Hecho (sin series numéricas) | 2026-08-26 | 2026-10-01 |
| R-029 | Antigüedad máxima de la fecha de emisión al crear (3 días); boleta: valor de la Parámetro 004 por confirmar | S02 (V), S16 Parámetro 004 (valores **P**) | `BillingRules.MaxIssueDateAgeDays` | Billing | `BillingApiTests` | Parcial | vigente | 2026-10-01 |
| R-030 | Factura solo a adquirentes con RUC; DNI de 8 dígitos | S02 (V); DNI **P** | `BillingRules.ValidateBuyer` | Billing | `BillingApiTests` | Parcial | vigente | 2026-10-01 |
