# Análisis de arquitectura (Fase 0)

## 1. Punto de partida

Repositorio vacío (solo `.git`, remoto `origin` configurado, sin commits). Toolchain local verificada: .NET SDK 10.0.401, Node 24, npm 11, Docker 29, Git 2.37. No hay código previo que preservar.

## 2. Hallazgos regulatorios que condicionan la arquitectura

Ver `docs/regulatory/current-baseline.md`. Los que más pesan:

1. **El transporte SUNAT es SOAP + ZIP + WS-Security con Clave SOL**, síncrono para facturas y asíncrono por ticket para resúmenes/bajas/lotes. La API pública de SecureFact oculta esto (contexto §22, §121).
2. **La clave SOL y el `client_secret` de GRE son secretos de integración de alto valor** → cifrado de envoltura, nunca en logs, rotación (ADR-007).
3. **Una factura rechazada consume su número** y la nota de crédito exige CDR de aceptación → la numeración y la máquina de estados son núcleo, no detalles (ADR-004, ADR-008).
4. **Los códigos de error de SUNAT se clasifican por rango**, lo que habilita una política de reintentos determinista (excepción propia / rechazo / observación / transporte).
5. **Plazo de envío de 3 días calendario** → el sistema debe medir "tiempo restante" por documento y alertar (contexto §40).
6. **Con PSE, SUNAT mide la tasa de rechazo por emisor (≤10 %→≤5 %)** → validación previa y KPI de rechazo desde el MVP.
7. **GRE es otra plataforma (REST/OAuth2) y no pasa por OSE** → bounded context independiente.
8. El Manual del Programador publicado es de **mayo de 2021**; las reglas de validación se actualizan mensualmente (26.08.2026). → reglas y endpoints como **datos versionados**, no código (ADR-008).

## 3. Decisiones de estructura (ver ADRs)

- **Monolito modular** (ADR-001) con un proceso API y workers separados que comparten el mismo código de módulos.
- **PostgreSQL con un esquema por módulo** y **RLS** para aislamiento de tenants (ADR-002, ADR-003).
- **Outbox transaccional** hacia RabbitMQ tras una abstracción `IMessageBus` (ADR-004).
- **Object storage S3-compatible** para XML/CDR/PDF; la BD guarda metadatos y hash (ADR-005).
- **`ICpeSubmissionChannel`** como límite de integración; el dominio nunca ve SOAP (ADR-006).
- **Certificados y claves SOL cifrados con envoltura** tras `ISecretProtector`/`ICertificateStore` (ADR-007).
- **Reglas con vigencia** (`Rule/Version/EffectiveFrom/EffectiveTo/Source`) (ADR-008).
- **Frontend** React + TypeScript + Vite con biblioteca UI mantenida y accesible (ADR-009).
- **Estructura de la solución**: un proyecto por módulo + un proyecto `Contracts` por módulo, con pruebas de arquitectura que prohíben referencias cruzadas (ADR-010).

## 4. Qué NO se hace ahora (anti-sobreingeniería)

- Sin microservicios, sin Kubernetes, sin HSM/KMS reales (solo las abstracciones y un protector local para desarrollo).
- Sin repositorios genéricos por tabla: EF Core `DbContext` por módulo; abstracciones solo en límites reales (storage, mensajería, SUNAT/PSE/OSE, certificados, notificaciones, reloj, pagos).
- Sin SIRE, factoring, IA ni GRE hasta sus fases.
- Sin XML ni lógica tributaria hasta cerrar Fase 1 (regla del contexto §102).

## 5. Riesgos principales

| Riesgo | Mitigación |
|--------|------------|
| Fuga entre tenants | RLS + filtros EF + pruebas automáticas cross-tenant en CI (bloqueantes). |
| Documentación SUNAT desactualizada o ambigua | Matriz normativa, reglas versionadas, simulador, consulta de fuentes por navegador. |
| Dependencia de un canal tercero (PSE/OSE) | Contract tests por adapter; migración de proveedor sin cambiar API. |
| Pérdida o alteración de CDR/XML | Object storage versionado, hash SHA-256, inmutabilidad lógica, backups probados. |
| Costo de ISO 27001 / 150 UIT / 5 trabajadores para PSE | Fase 8 separada; `docs/pse/gap-analysis.md` vivo. |
| Licencias de librerías de mensajería/UI cambiantes | Abstracción propia de mensajería; verificar licencia antes de adoptar. |
