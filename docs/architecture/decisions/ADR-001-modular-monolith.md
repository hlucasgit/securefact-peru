# ADR-001: Monolito modular

- Estado: Aceptada · Fecha: 2026-09-30

## Contexto
Plataforma crítica con muchos bounded contexts (Identity, Billing, TaxEngine, Sunat, Gre…). El equipo inicial es pequeño y los límites de dominio todavía se están descubriendo. Los microservicios multiplicarían la complejidad operativa (despliegue, transacciones distribuidas, observabilidad) antes de tener evidencia de necesidad.

## Decisión
Un **monolito modular**: un proceso API y procesos worker que cargan los mismos módulos. Cada módulo encapsula su dominio, aplicación e infraestructura, expone solo un proyecto `Contracts` (interfaces, DTO, eventos de integración) y tiene su propio `DbContext` y esquema PostgreSQL. La comunicación entre módulos es por **contratos** (llamadas a interfaces de `Contracts` para consultas síncronas, eventos de integración vía outbox para el resto). Las pruebas de arquitectura (NetArchTest) hacen cumplir los límites.

## Consecuencias
+ Despliegue y depuración simples; transacciones locales dentro de un módulo.
+ Módulos extraíbles a servicio: los contratos ya son la frontera.
− Requiere disciplina; sin las pruebas de arquitectura el acoplamiento se degrada. Esas pruebas son bloqueantes en CI.
− Un despliegue escala todo junto; mitigado separando procesos worker de la API.

## Alternativas descartadas
Microservicios desde el inicio (sobreingeniería); monolito sin módulos (deuda estructural).
