# Gap Analysis — SecureFact vs. requisitos PSE

Documento vivo. Última actualización: **2026-09-30**. SecureFact **no es PSE** y no solicitará serlo durante el MVP (`OwnPseMode=false`). Estado: ✅ cumple · 🟡 parcial · ❌ brecha · ❓ sin verificar.

| # | Requisito | Estado | Brecha / evidencia | Acción | Fase |
|---|-----------|--------|--------------------|--------|------|
| 1 | Persona jurídica con RUC activo y habido | ❓ | Dato de negocio; no verificado | Confirmar con el negocio | 8 |
| 2 | Régimen General IR 3.ª categoría | ❓ | Idem | Confirmar | 8 |
| 3 | Emisor electrónico del SEE | ❌ | Se obtiene tras cargar certificado o vincular PSE | Tramitar en SOL | 8 |
| 4 | Declaraciones mensuales con ventas/ingresos (6 meses) | ❌ | Requiere operación comercial previa | Plan comercial/contable | 8 |
| 5 | Certificado digital registrado en SOL | ❌ | Procedimiento externo; el sistema ya modela `ICertificateStore` | Adquirir y registrar | 8 |
| 6 | Sin sentencia por delito tributario/aduanero | ❓ | Declaración societaria | Confirmar | 8 |
| 7 | Capital o activos netos ≥ 150 UIT (S/ 825,000 con UIT 5,500 — **U**) | ❌ | No es efectivo en banco: capital en SUNARP o casillero 390 | Planificación societaria/financiera | 8 |
| 8 | ≥ 5 trabajadores subordinados en PLAME | ❌ | Equipo actual < 5 (asumir) | Plan de contratación | 8 |
| 9 | Certificación ISO/IEC 27001 | ❌ | Se prepara desde ya: ver `iso27001.md`, `docs/security/` | Programa SGSI, auditor acreditado | 8 |
| 10 | Soporte de primer nivel a emisores | 🟡 | Backoffice/soporte planificado (Fase 5–6) | Implementar y medir SLA | 5–8 |
| 11 | Tasa de rechazo ≤ 10 % → 5 % por emisor | 🟡 | Se medirá desde el MVP (KPI `rejection_rate`) | Dashboard + alerta | 4–5 |
| 12 | Firma con certificado propio por cuenta del emisor | 🟡 | Diseño listo (ADR-006/007); canal `FutureSecureFactPseChannel` inactivo | Activar con flag tras inscripción | 8 |
| 13 | Inspección SUNAT / evidencias | 🟡 | `evidence-register.md`, auditoría append-only | Mantener evidencias | continuo |
| 14 | Homologación (si aplica tras RS 108-2022) | ❓ | Texto de la RS pendiente | Leer RS 108-2022 | 8 |

## Conclusión provisional
Las brechas son mayoritariamente **organizacionales y financieras** (7, 8, 9, 4), no técnicas. La arquitectura ya evita retrabajo técnico (canal, certificados, auditoría, KPI de rechazo). Revisar este análisis cada trimestre y ante cualquier cambio normativo.
