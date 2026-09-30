# Registro de bugs del POS (TruckScale.Pos)

> **Índice maestro de bugs abiertos.** Se actualiza en cada sesión.
> Última actualización: **2026-09-29** (sesión 3: fase de corrección, Fase 0 en revisión).

## Ramas de corrección

- **Base:** rama `stable`, creada desde `1e04a54f` (el código de la báscula). **master no se usa como base**; de master solo se traen parches puntuales por cherry-pick. No hay merge de master hacia stable. Cuando `stable` esté probada en báscula, sustituye a master.
- Una sub-rama por fix, con commit propio. Cada fix se revisa antes de seguir con el siguiente.
- Este archivo vive en `stable` (`docs/bugs-pos.md`); cada rama de fix lo actualiza en un commit separado.

| Fase | Rama | Commit | Bugs | Estado |
|------|------|--------|------|--------|
| 0 | `fix/logging-visibility` | `df97361`, `4f7aadf` | M, E (visibilidad), apoyo a C | en revisión |
| — | `tools/test-harness` | ver `docs/entorno-pruebas.md` | entorno local para reproducir B/V | propuesta en revisión (sin código) |
| 1 | `fix/save-sale-snapshot` | — | B, V, A | pendiente |
| 2 | `fix/reweigh-and-flags` | — | R, I, A (match por nombre) | pendiente |
| 3 | `fix/sync-push` | — | S1, S2, G | pendiente |

Estados de corrección usados abajo: `abierto` · `corregido en stable (rama/commit)` · `probado en báscula`.

## ⚠ Versión en producción

| | Commit | Fecha | Nota |
|---|---|---|---|
| **PRODUCCIÓN (báscula)** | `1e04a54f86d258342ca58f9af06927367a64a829` | 31-mar-2026 16:23 | "Merge branch 'master'…". Confirmado por el equipo. |
| master | `9712ea0` | 3-sep-2026 | **No desplegado.** |

- **Todas las líneas de este documento son de PRODUCCIÓN (`1e04a54f`)** salvo que diga "master".
- Para revisar el código de producción sin tocar tu árbol:
  `git worktree add --detach <carpeta-fuera-del-repo> 1e04a54f` … y al terminar `git worktree remove <carpeta>`.
- `MainWindow.xaml.cs` es **casi idéntico** entre ambas versiones (una sola línea añadida en master, la 1242). Regla práctica: **línea de master = línea de producción + 1** para todo lo que está después de la línea 1242.

## Cómo leer este archivo (para quien llega nuevo)

- El POS es una app WPF (`TruckScale.Pos/`). Casi toda la lógica está en `MainWindow.xaml.cs` (~8,800 líneas, code-behind).
- Hay dos bases MySQL: **MAIN** (central) y **LOCAL** (en la báscula). Cada operación intenta MAIN y, si falla la conexión (timeout de 8 s, `MainWindow.xaml.cs:865/885`), cae a LOCAL ("modo offline"). **No escribe en ambas.**
- El sync (`Sync/SyncService.cs`) corre con un timer (`AutoSyncAsync`, `MainWindow.xaml.cs:2954`): primero push LOCAL→MAIN, luego pull MAIN→LOCAL, siempre ambos.
- Log de texto: `C:\TruckScaleLogs\log_YYYY-MM-DD.txt`, **se borra a los 2 días** (bug M). El resultado de cada pull queda ahí como `[Sync] PULL OK` / `[Sync] PULL ERROR: …`.
- Estados: `abierto` · `en análisis` · `entendido` (causa en código, falta confirmar con datos) · `confirmado` · `corregido en master (sin desplegar)` · `corregido`.
- Columna **Versión**: `ambas` = existe en producción y en master · `solo prod` = master ya lo corrige · `solo master` = introducido después de producción.

## Tabla resumen

| Id | Título | Versión | Estado | Dificultad real | Impacto |
|----|--------|---------|--------|-----------------|---------|
| B | Venta COMPLETED sin fila en `payments` (carrera UI↔guardado) | ambas | entendido | media | alto |
| V | La validación "Business requiere cliente" lee `_pagos` en vivo y se puede saltar | ambas | entendido (nuevo) | baja | alto |
| A | Identidad de cliente desincronizada | ambas | entendido (parcial) | media-alta | alto |
| A2 | Huérfanos totales: Business RECEIVED sin cliente | ambas | en análisis — sin camino directo en el POS | ? | alto |
| R | `_reweighSourceSaleUid` nunca se limpia | ambas | entendido | baja | medio |
| C | Pull MAIN→LOCAL: **en prod existe pero falla**; en master se eliminó | prod: falla · master: eliminado | en análisis | media-alta | **crítico** |
| S1 | Push marca `synced=1` sin verificar que llegó a MAIN (pérdida silenciosa) | solo prod | corregido en master (sin desplegar) | — | **alto** |
| S2 | Push/pull copian `tickets.ticket_id` y `scale_session_axles.id` entre bases (colisión de PK) | solo prod | corregido en master (sin desplegar) | — | alto |
| S3 | Pull incremental omite filas con `updated_at` NULL | solo prod | entendido | baja | medio |
| K | Consumo de crédito offline nunca llega a MAIN (y el pull lo pisa en LOCAL) | ambas | entendido | media | alto |
| D | `tickets` de LOCAL sin filas desde 9-jun | prod | entendido (parcial) | — | medio |
| E | `sync_logs` sin escribir (MAIN 12-jun, LOCAL abril) | prod | en análisis · **visibilidad corregida en stable** (`fix/logging-visibility` `df97361`+`4f7aadf`): los fallos del INSERT ya quedan en el log de archivo; `error_details` siempre JSON válido; `related_uid` con sale_uid; logs ya no disparan push. Descartado: el CHECK `json_valid` no es la causa en producción (ver sección Fase 0) | baja | bajo |
| F | Ticket 16862 sin venta pese a FK | ambas | en análisis — no hay camino en el POS | ? | medio |
| G | Ticket 7891 duplicado | ambas | entendido (mecanismo) | media | medio |
| H | Ventas COMPLETED con pago VOIDED (16047, 16188) | prod (más probable) | en análisis | media | medio |
| I | `is_reweigh` nunca se escribe | ambas | confirmado | baja | bajo |
| J | VOID no revierte `customer_credit` | ambas | entendido | media | alto (latente) |
| L | Actualizar chofer "tiene éxito" con 0 filas | ambas | entendido | baja | medio |
| M | Logs de texto se borran a los 2 días | ambas | **corregido en stable** (`fix/logging-visibility` `df97361`) · pendiente prueba en báscula | baja | medio |

---

## Diferencias entre producción (`1e04a54f`) y master (`9712ea0`)

Commits posteriores a producción:

| Commit | Fecha | Descripción | Toca | Efecto sobre bugs |
|--------|-------|-------------|------|-------------------|
| `05bc9ce` | 2-abr | fx: bug en botón de OK | báscula/UI | revertido por `963e7ea` → neto 0 |
| `963e7ea` | 14-abr | Revert "fx:bug en boton de ok" | báscula/UI | neto 0 |
| `2be60b8` | 14-abr | `SetReweighEditable(true)` en `ResetDriverContext` (PlacasRegText bloqueada tras reweigh) | reweigh/UI | residuo de UI entre ventas; **no** toca cuenta ni `_reweighSourceSaleUid` |
| `b8ed204` | 5-may | TEST (comentario en `MainWindow.xaml`) | — | ninguno |
| `e024e8a` | 11-jun | "fx:ajuste para synced" — reescritura de `SyncService.cs` (−719 líneas) y `SyncModels.cs` | **sync, tickets, logging, crédito** | corrige S1 y S2; **elimina el pull** (C) y `SyncCatalogsAsync`; deja de subir `sync_logs`; agrega push de `customers`; agrega reconciliación del contador de tickets |
| `7c80501` | 12-jun | push de `scale_session_axles` por `uuid_weight` | sync | ajuste de S2 |
| `e1d637a` | 3-ago | histéresis botón OK | báscula | revertido por `9712ea0` → neto 0 |
| `9712ea0` | 3-sep | Revert histéresis | báscula | neto 0 |

**Conclusión:** entre producción y master **no cambió nada** de pagos, crédito, identidad de cliente, reweigh (salvo el bloqueo de un textbox) ni logging (`AppLogger.cs` idéntico). **Todo el cambio relevante es el sync.** Los bugs B, V, A, A2, R, I, J, L, M existen **igual en producción y en master**.

---

## Respuesta a la pregunta central (producción)

**¿El POS cierra la venta en una sola transacción con líneas, pago, ticket y crédito? Sí, en ambas versiones, online y offline** (el mismo método, solo cambia la cadena de conexión). El hueco no es la transacción sino que se alimenta de estado de UI mutable leído en momentos distintos.

`SaveSaleAsync()` — producción `MainWindow.xaml.cs:4111-4465`:

| Paso | Línea (prod) | En `tx` |
|------|--------------|---------|
| Snapshot `recibido = SumReceived()` + validación `recibido >= total` | 4121-4123 | antes de conectar |
| Abrir MAIN (8 s), si falla LOCAL | 4137-4173 | — |
| `BeginTransaction` | 4176 | ✔ |
| `acc = ClienteRegCombo.SelectedItem` (combo en vivo) | 4181 | ✔ |
| `INSERT sales` (COMPLETED, `customer_id` de `acc`) | 4200-4247 | ✔ |
| `INSERT sale_lines` | 4259-4283 | ✔ |
| `dbPagos` = `_pagos` agrupado (**relectura en vivo**) → `INSERT payments` | 4298-4361 | ✔ |
| `businessAmount` = `_pagos` (**otra relectura en vivo**) | 4364-4366 | ✔ |
| Si `businessAmount > 0`: exige cliente o lanza; `UpdateCustomerCreditAsync` | 4368-4388 | ✔ |
| `LinkDriverToSaleAsync` | 4393-4402 | ✔ |
| `UPDATE sales SET amount_paid = recibido` (snapshot) | 4405-4420 | ✔ |
| `GenerateTicketNumberAsync` + `INSERT tickets` | 4423-4438 | ✔ |
| `Commit` / `Rollback`+rethrow | 4441 / 4459-4463 | ✔ |

Fuera de la transacción: `sale_driver_info` (al guardar chofer, `RegisterSave_Click` 1880) y todo el sync.

---

## El sync en producción (`Sync/SyncService.cs` @ `1e04a54f`)

### Push LOCAL→MAIN — `PushTransactionsAsync` (92-162)

Solo corre si hay filas `synced=0` (106). Orden exacto:

| # | Tabla | PK usada | Línea |
|---|-------|----------|-------|
| 1 | `scale_session_axles` | `uuid_weight` | 121 |
| 2 | `sales` | `sale_uid` | 122 |
| 3 | `sale_lines` | `line_id` | 123 |
| 4 | `sale_driver_info` | `id_driver_info` | 124 |
| 5 | `tickets` | `ticket_uid` | 126 |
| 6 | `ticket_signatures` | `sig_uid` | 127 |
| 7 | `sync_logs` | `log_uid` | 128 |
| 8 | `customer_credit` | `credit_id` | 129 |
| 9 | `number_sequences` | `sequence_id` | 131 |
| 10 | `payments` | `payment_uid` | 132 (**último**) |

- **No sube:** `customers`, `customer_credit_movements` (la tabla no aparece en ningún archivo del POS, en ninguna versión), `cash_sessions`, `settings`.
- Mecánica (`SyncBatchAsync` 275-348): lee LOCAL, `INSERT IGNORE` fila por fila en MAIN (320-331) y **luego marca `synced=1` todo el lote sin verificar nada** (334-345). → **Bug S1**.
- Columnas excluidas: `synced*` (305) y las de `ShouldSkipAutoId` (769-779): `sales.sale_id`, `payments.payment_id`, `sale_lines.line_id`, `sale_driver_info.id_driver_info`, `ticket_signatures.sig_id`. **Faltan `tickets.ticket_id` y `scale_session_axles.id`** → se envía el autoincremental de LOCAL a MAIN → **Bug S2**.
- Cualquier excepción aborta el resto del push; como `payments` va **al final**, es la tabla que más fácilmente se queda atrás.

### Pull MAIN→LOCAL — `PullUpdatesAsync` (384-472): **SÍ existe en producción**

Lee `sync.last_pull_ts` de LOCAL (395); si LOCAL.sales está vacía fuerza full (399-410). Orden, **sin transacción** (cada tabla se confirma sola; si una falla, se aborta el resto y **no** se actualiza `last_pull_ts`):

1. `sites` 415 · `status_catalogo` 416 · `tax_rates` 417 · `products` 418 · `customers` 419 · `vehicle_types` 420 · `driver_products` 421 · `license_states` 422 · `payment_methods` 423 · `number_sequences` 424 · `users` 425 — incrementales (`updated_at > last_pull_ts`)
2. `customer_credit` 429 — **full** (sin filtro), sobrescribe LOCAL con MAIN
3. `settings` (salvo `sync.*`) 436 — full
4. `sales` 439 · `sale_driver_info` 440 · `sale_lines` 442 · `tickets` 446 — incrementales
5. `ticket_signatures` 448 (full) · `scale_session_axles` 449 · `sync_logs` 450
6. `payments` 453 (por `COALESCE(updated_at, created_at)`, trae ventas padre faltantes) · `number_sequences` 454
7. `UPDATE settings sync.last_pull_ts = NOW()` 459 · log `PULL_SUCCESS` 461

Upsert: `INSERT … ON DUPLICATE KEY UPDATE` (757), copiando todas las columnas de MAIN salvo las de `ShouldSkipAutoId`.

### ¿Por qué los datos locales están congelados desde junio? (Bug C en producción)

**No es que se borrara**: esa versión (`e024e8a`) nunca se desplegó. **El pull de producción corre en cada tick del timer y falla.** Cómo lo deduzco:

- `customer_credit` (429) es un **full upsert en cada pull**. Si el pull llegara a esa línea, los saldos locales serían copia exacta de MAIN cada pocos minutos. Además, el `updated_at` de los otros dos clientes del 26-sep sigue igual a la hora de su venta offline; un pull exitoso lo habría sobrescrito con el de MAIN. ⇒ **el pull aborta antes de la línea 429** (o no conecta).
- El ticket offline 16608 salió con número 16608, coherente con MAIN. `number_sequences` se trae en 424. ⇒ es probable que **sí pase la 424** (o que el contador local nunca se desfasara).
- Candidato más fuerte: **falla en `users` (425) o en `customer_credit` (429)**. Típicamente por un cambio de esquema en MAIN hacia el 11/12-jun (columna nueva que LOCAL no tiene → `Unknown column` en el INSERT a LOCAL) o un conflicto de clave única. Coincide en fecha con el trabajo de `e024e8a`/`7c80501` (11-12-jun), que probablemente vino con cambios de BD en MAIN que no se replicaron en LOCAL.
- **Cómo confirmarlo en 1 minuto:** en la PC de la báscula abrir `C:\TruckScaleLogs\log_2026-09-29.txt` y buscar `[Sync] PULL ERROR: Pull failed: …` (`MainWindow.xaml.cs:3013-3017`). El mensaje dice la tabla/columna exacta. También `SELECT value FROM settings WHERE `key`='sync.last_pull_ts';` en LOCAL (debería ser ~12-jun).
- Pruebas cruzadas: (a) un cliente dado de alta en MAIN después de junio, ¿existe en LOCAL? Si sí, el pull pasa de 419. (b) `SHOW COLUMNS FROM users` y `FROM customer_credit` en ambas bases y comparar.
- Riesgo operativo confirmado: en offline, `GetCreditSnapshotAsync` (≈7097) y `GetAvailableCreditAsync` (4560) leen saldos de junio.

### ¿El crédito consumido offline llega a MAIN? — **No** (Bug K, ambas versiones)

`UpdateCustomerCreditAsync` (6740-6797) hace `UPDATE` en LOCAL sin marcar `synced=0`, así que ni siquiera queda pendiente; y aunque lo estuviera, el push usa `INSERT IGNORE` por `credit_id` (129) sobre una fila que ya existe en MAIN → se ignora. En producción, además, cuando el pull funciona, el full upsert de 429 **pisa** el saldo local con el de MAIN, borrando el consumo offline también de LOCAL. El pago (PENDING/RECEIVED) sí sube, así que AR lo ve; `customer_credit` de MAIN no se mueve.

### ¿Qué escribe `sync_logs`?

`LogSyncEventAsync` (818-843): `INSERT INTO sync_logs (log_uid, log_type, severity, message, records_affected, created_at)` **en LOCAL**, errores tragados (`catch {}`, 839). Se llama con `PUSH_SUCCESS` (144), `PUSH_ERROR` (158), `PULL_SUCCESS` (461), `PULL_ERROR` (469). **No** registra: fallos de filas individuales, `INSERT IGNORE` descartados, ni nada del flujo de venta/pago. `DatabaseHelper.LogConnectionErrorAsync` existe pero nadie la llama. Esas filas locales se suben a MAIN en el push (128) y vuelven por el pull (450).

---

## Bugs — veredicto contra producción

### Bug B — Venta COMPLETED sin fila en `payments`
- **Versión:** ambas (código idéntico). **Estado:** entendido. **Veredicto: existe en producción** — y los tres casos (9-jul, 24-ago, 26-sep) los produjo esta versión.
- **Síntoma:** COMPLETED, `amount_paid = total`, ticket impreso, sin `payments` en MAIN ni LOCAL, `customer_credit` sin tocar. 10270 (eddie, Trisa PREPAID, online), 13875 (anthonya, RAPID POSTPAID, online), 16608 (johnn, TRAMUC POSTPAID, offline).
- **Causa (líneas de producción):**
  1. `recibido` se toma en 4121; `_pagos` se relee en 4298, tras varios `await`.
  2. `TryAutoCompleteSaleAsync` (1467) no bloquea la UI; solo `_isCompletingSale` impide doble *Done*.
  3. Mientras tanto, estos caminos vacían `_pagos`: `UpdatePaymentMethodsVisibility(false)` con Business seleccionado (6722-6729), llamado desde `ReevaluateBusinessAccountAsync` (7087; se lanza dos veces en paralelo al pulsar Business: 4021 y 4058), `ClienteRegCombo_SelectionChanged` (5005, 5011, 5020; la 5005 usa el saldo en memoria del login), `OkButton_Click` (≈4788); y cambio de método con la cuenta cubierta (4005-4009).
  4. `_pagos` vacío en 4298 ⇒ no se inserta pago, `businessAmount = 0` ⇒ no se toca crédito, `amount_paid = recibido` (4405) ⇒ commit.
- **Online vs offline:** mismo código. Offline la ventana crece ≥8 s por el timeout de MAIN en 4143 más los timeouts de las re-evaluaciones de crédito concurrentes.
- **Descartado en prod también:** try/catch que trague (no hay), commit parcial, pérdida en sync (LOCAL tampoco la tiene; S1 no aplica porque la fila nunca existió).
- **Fix (archivos):** `MainWindow.xaml.cs` — `SaveSaleAsync` (copia inmutable de `_pagos` y `acc` antes de cualquier `await`; `amount_paid` = suma insertada; `throw` si no hay pagos), `TryAutoCompleteSaleAsync` (`SetPosEnabled(false)` durante el guardado), guardas en `UpdatePaymentMethodsVisibility`/`SelectPaymentByCode` si `_isCompletingSale`. **Requiere fix nuevo** (master no lo corrige).
- **Riesgos:** UI bloqueada 8 s offline; no romper split Cash+Card; resultados tardíos de re-evaluaciones en vuelo.
- **Falta verificar:** [ ] escenarios B-1/B-2 · [ ] `SELECT s.sale_uid FROM sales s LEFT JOIN payments p USING(sale_uid) WHERE s.sale_status_id=2 AND p.payment_uid IS NULL;` en ambas bases.

#### 1.5 — Logs del 26-sep
`AppLogger.cs:40-49` (idéntico en prod): cada escritura borra archivos con más de 2 días. El log del 26-sep casi seguro ya no existe. Si aparece (backup/snapshot), buscar entre 11:11 y 11:15: `[SaveSale] MAIN_DB failed`, `Using LOCAL_DB`, `[BusinessCredit] Reevaluate failed`, y la **ausencia** de `[CustomerCredit] Updating credit`.

### Bug V — La validación "Business requiere cliente" se puede saltar (nuevo)
- **Versión:** ambas. **Estado:** entendido.
- **La validación SÍ existe en producción:** `MainWindow.xaml.cs:4370-4371` (`if (acc == null || acc.IdCustomer <= 0) throw … "Business Account payment requires a selected customer."`). Revisé todos los commits desde el 19-dic-2025 (`d7bd0b7`) hasta `1e04a54f`: **presente sin interrupción**, incluido el 20-mar. No hay una versión vieja sin ella en ese periodo.
- **Pero se evalúa sobre `_pagos` en vivo** (4364-4366), no sobre `dbPagos` (lo que realmente se insertó en 4298-4361). Entre ambas hay `await ExecuteNonQueryAsync` por cada pago. Si `_pagos` se vacía en ese intervalo (mismos disparadores que B): el pago Business **ya se insertó**, `businessAmount = 0`, **se salta la validación y el consumo de crédito**, commit.
- **Qué puede producir:** pago Business sin consumo de crédito (siempre). Con `customer_id` NULL solo si además `acc` era nulo en 4181; en ese caso `creditType` cae en POSTPAID y el pago queda **PENDING**, no RECEIVED (4335-4352). Por eso **no explica por sí solo** los huérfanos RECEIVED (ver A2).
- **Fix:** calcular `businessAmount` desde `dbPagos` y validar **antes** del `foreach` de inserción. Dificultad baja. Requiere fix nuevo.

### Bug A — Identidad de cliente desincronizada
- **Versión:** ambas (código idéntico). **Estado:** entendido (parcial).
- **Causa:** `sale_driver_info.account_*` se escribe al pulsar Save/Update driver (`RegisterSave_Click` 1880; rama de cuenta 1927-1944; con combo en índice 0 usa texto libre de `AccountNameText` y `account_number = NULL`). `sales.customer_id` se lee del combo al pulsar Done (4181, 4218-4221). Nada los reconcilia.
- **Reweigh (donde nace):** `PrepareReweighFromTicketAsync` fija `_driverLinked = true` con el chofer precargado sin insertar fila (≈6167-6179); `LinkDriverToSaleAsync` (3442) no encuentra fila por `weight_uuid` y **clona** la de la venta original (3515-3530). El combo solo se sincroniza si el cliente original está activo y cargado al login (6156-6162; `_accounts` se carga una sola vez, `LoadAccountsAsync` en 2858). Si no, conserva lo que tenía → cruzado.
- **ACC-4 (4910):** hipótesis principal: `FillDriverFormFromInfo` (5204) empata la cuenta por `account_name` con `FirstOrDefault` cuando no encuentra el número (5258-5268) → con nombres repetidos selecciona a otro cliente, y "Update driver" inserta esa cuenta.
- **Residuo entre ventas:** `ResetDriverContext` (1198) limpia combo y campos de cuenta al completar (1548) y en *New*; **no** se ejecuta si `SaveSaleAsync` lanza (1550-1556). La diferencia con master (`2be60b8`) solo afecta a `PlacasRegText`.
- **Fix:** `SaveSaleAsync` escribe/valida `sale_driver_info.account_*` con el mismo `acc`; `PrepareReweighFromTicketAsync` fuerza el combo; quitar el match por nombre. Requiere fix nuevo.
- **Falta verificar:** [ ] 4910: `customer_id`, `sale_driver_info.match_key/created_at` (¿clon o "Update driver"?), y `SELECT id_customer, account_number, is_active FROM customers WHERE account_name = (SELECT account_name FROM customers WHERE account_number='ACC-4');`

### A2 — Huérfanos totales (20-mar, 10-jul, 21-jul, 8-sep ×2)
- **Versión:** ambas. **Estado:** en análisis.
- **Conclusión:** en producción, como en master, `SaveSaleAsync` **no** puede crear un pago Business **RECEIVED** con `customer_id` NULL: RECEIVED exige `method_id=3`, `creditType=="PREPAID"` y `availableCredit >= monto` (4335-4345), todo derivado del mismo `acc` que escribe `customer_id` (4218-4221); `availableCredit` solo se consulta con `acc.IdCustomer > 0` (4292). La validación existe desde dic-2025, así que el caso del 20-mar tampoco viene de una versión más vieja.
- **2.2:** sí se acepta texto libre en `AccountNameText` con combo en índice 0; no hay flujo de "cliente nuevo" (el POS nunca inserta en `customers`).
- **Patrón que sí encaja con el POS:** chofer guardado "de contado" con nombre libre (`account_number` NULL) y luego cliente PREPAID elegido en el combo al pagar → pago RECEIVED **con** `customer_id` (= Bug A). Para que termine en NULL, algo tuvo que **nulificar `sales.customer_id` después**.
- **Hipótesis (en orden):**
  1. FK `sales.customer_id` con `ON DELETE SET NULL` y el cliente fue borrado/fusionado. `SHOW CREATE TABLE sales;` y buscar los nombres en `customers`.
  2. Escritor externo (backoffice/AR/SQL manual). Pista: el POS siempre llena `payments.received_by` y `received_at` al insertar RECEIVED (4343-4344).
  3. Pago PENDING creado vía Bug V con `acc` nulo y luego pasado a RECEIVED por cobranza externa. Pista: `received_at` muy posterior a `sales.created_at`.
- **Falta verificar:** [ ] las tres consultas anteriores para los 5 casos · [ ] `ar_payment_allocations` de esos tickets.

### Bug R — `_reweighSourceSaleUid` nunca se limpia
- **Versión:** ambas. Asignación única en producción `MainWindow.xaml.cs:6150-6151`; `ResetDriverContext` no lo limpia (`2be60b8` tampoco). Uso en `LinkDriverToSaleAsync` 3515-3530 (clona chofer y cuenta de una venta vieja cuando no encuentra fila por `weight_uuid`, p. ej. chofer guardado en MAIN y venta caída a LOCAL). **Existe en producción.** Fix de baja dificultad, requiere fix nuevo.

### Bug C — Pull MAIN→LOCAL
- **Producción:** existe (`SyncService.cs:384-472`) y **falla**, con toda probabilidad antes de la línea 429 (ver sección de sync). **Master:** se eliminó (`e024e8a`); desplegar master **no** lo arregla, solo cambia "falla" por "no existe" y deja LOCAL congelado para siempre.
- **Dificultad real:** media-alta. **Primer paso sin código:** leer el `PULL ERROR` del log de hoy y alinear el esquema de LOCAL con MAIN; eso podría reactivar el pull de producción sin desplegar nada.
- **Riesgo al reactivarlo:** el primer pull exitoso traerá ~3.5 meses de cambios y, por S2/S3, puede sobrescribir tickets/axles locales (ver S2). Hacer **backup de LOCAL** antes.

### Bug S1 — Push marca `synced=1` sin verificar (solo producción)
- `SyncService.cs:320-345` (prod): `INSERT IGNORE` descarta en silencio filas que violan FK o PK, y el lote se marca `synced=1` igual. Esas filas **nunca llegarán a MAIN** y no queda rastro. Ejemplos: venta offline cuyo `customer_id` o `cash_session_uid` no existe en MAIN; ticket cuyo `ticket_id` ya usa otro ticket en MAIN (S2).
- **Corregido en master** (`e024e8a`: verifica en MAIN qué UIDs quedaron, solo marca esos y registra `PUSH_WARN`). **Desplegable.**
- **Verificar:** [ ] LOCAL rows `synced=1` que no existen en MAIN: `SELECT sale_uid FROM local.sales WHERE synced=1` vs MAIN (igual para `tickets`, `payments`).

### Bug S2 — Autoincrementales copiados entre bases (solo producción)
- `ShouldSkipAutoId` (prod 769-779) no excluye `tickets.ticket_id` ni `scale_session_axles.id`. Consecuencias:
  - **Push:** el ticket offline llega a MAIN con el `ticket_id` de LOCAL; si MAIN ya lo usó, `INSERT IGNORE` lo descarta y S1 lo marca como subido → **ticket offline perdido en MAIN**.
  - **Pull (cuando funciona):** el upsert de un ticket de MAIN cuyo `ticket_id` coincide con un ticket offline de LOCAL choca por PK y **sobrescribe esa fila local** (conserva `ticket_uid`, pero cambia `sale_uid`, `ticket_number`, etc.) → el ticket offline "desaparece" de LOCAL.
- **Corregido en master** (ambas columnas añadidas a `ShouldSkipAutoId`). **Desplegable.**

### Bug S3 — Pull incremental omite filas con `updated_at` NULL (solo producción)
- `PullTableUpsertAsync` filtra `WHERE updated_at > @ts` (671). Si una tabla deja `updated_at` NULL al insertar, las filas nuevas **nunca** se traen en modo incremental (solo en full pull). El propio código lo reconoce para `payments` (usa `COALESCE(updated_at, created_at)`, 487), pero no para `tickets`, `sales`, etc. `INSERT tickets` (4426) no fija `updated_at`. Además `last_pull_ts` es `NOW()` de LOCAL comparado contra `updated_at` de MAIN: con zonas horarias distintas se saltan cambios.
- **Verificar:** [ ] `SELECT COUNT(*) FROM tickets WHERE updated_at IS NULL;` en MAIN · [ ] `SELECT NOW(), @@time_zone;` en ambas bases.

### Bug K — Crédito offline
- Ambas versiones (ver sección de sync). Requiere fix nuevo (movimientos de crédito en lugar de saldos absolutos). Dificultad media.

### Bug D — `tickets` de LOCAL sin filas desde 9-jun
- **Producción escribe tickets en LOCAL** por dos vías: (1) venta offline, `INSERT tickets` dentro de la transacción (4426); (2) pull, línea 446.
- Explicación: el pull aborta antes (C) y, aun cuando corría, el filtro incremental omite tickets con `updated_at` NULL (S3); el 9-jun probablemente fue el último full pull.
- **16608:** se guardó offline, así que su ticket **tuvo que** insertarse en LOCAL en la misma transacción que la venta (que sí está). Que falte significa: (a) S2: un pull lo sobrescribió por colisión de `ticket_id` (en ese caso la fila existe con su `ticket_uid` pero con otro número/venta), o (b) la base auditada no es la del POS. Consulta decisiva en LOCAL: `SELECT * FROM tickets WHERE ticket_uid = '<ticket_uid de 16608 en MAIN>';`. Si (a) se confirma, el pull **sí** ha llegado a la línea 446 después del 26-sep y la hipótesis de C (falla antes de 429) debe revisarse.
- Nota: si en MAIN el ticket 16608 existe con el mismo `ticket_uid`, S2 no lo perdió en el push.

### Bug E — `sync_logs`
- **LOCAL (desde abril):** `LogSyncEventAsync` (818-843) inserta en LOCAL en cada push/pull y traga errores. Sin filas desde abril ⇒ el INSERT falla (esquema de `sync_logs` LOCAL ≠ columnas de 822) o la base auditada no es la del POS. Nota: `PULL_ERROR` (469) también se pierde, por eso el pull falla sin rastro en BD.
- **MAIN (desde 12-jun):** en producción MAIN solo recibe `sync_logs` subidos desde LOCAL (128). Si LOCAL no tiene filas desde abril, las filas abr-jun de MAIN vienen de **otro cliente** (probablemente una PC de desarrollo con la versión vieja apuntando a MAIN hasta el 11/12-jun, cuando pasó a `e024e8a`, que ya no sube logs). **Revisar su contenido:** puede incluir `PULL_ERROR` con la causa. `SELECT created_at, log_type, message FROM sync_logs ORDER BY created_at DESC LIMIT 50;` en MAIN.
- **Verificar:** [ ] `SHOW CREATE TABLE sync_logs;` en LOCAL e intentar a mano el INSERT de la línea 822.

### Bug F — Ticket 16862 sin venta
- **Ambas versiones:** no hay `FOREIGN_KEY_CHECKS` ni ningún `DELETE`/`TRUNCATE` en todo el código de producción (ni en master, ni en la historia git). El push usa `INSERT IGNORE`, que con la FK activa **descarta** el ticket huérfano (y S1 lo marcaría como subido), no lo inserta.
- **Hipótesis:** borrado de la venta en MAIN con FK desactivada (limpieza manual/backoffice) u otro escritor. **Verificar:** [ ] ¿la venta existe en LOCAL? [ ] binlog de MAIN del 26-sep · [ ] `SHOW CREATE TABLE tickets;`

### Bug G — Ticket 7891 duplicado
- **Ambas versiones**, más probable en producción: `GenerateTicketNumberAsync` (prod 5713) incrementa `number_sequences` de la base donde se guarda la venta. En producción LOCAL solo se alinea con MAIN vía pull (424/454); **no** hay reconciliación (master sí la tiene, `ReconcileTicketNumberSequenceAsync`, que corrige **parcialmente** esto al desplegar). Venta offline con LOCAL atrasado ⇒ número repetido; el push acepta ambos porque la clave es `ticket_uid`.
- **Verificar:** [ ] ¿una de las dos filas 7891 vino de LOCAL (offline)? [ ] `SHOW INDEX FROM tickets` · [ ] `SELECT ticket_number, COUNT(*) FROM tickets GROUP BY 1 HAVING COUNT(*)>1;`

### Bug H — COMPLETED con pago VOIDED (16047, 16188)
- **Ambas versiones:** el único camino a VOIDED, `VoidReasonConfirm_Click` (prod 7563-7654), cancela venta y pagos en la misma transacción y exige venta COMPLETED. En una sola base no deja COMPLETED+VOIDED.
- **Hipótesis (más probable en prod):** venta offline; un push abortó después de `sales`/`tickets` y antes de `payments` (último en prod, 132); se anula en LOCAL; el siguiente push sube el pago ya VOIDED, pero la venta en MAIN no cambia (`INSERT IGNORE` nunca actualiza). **Verificar:** [ ] estado de 16047/16188 en LOCAL, `synced_at` de venta vs pago.

### Bug I — `is_reweigh`
- **Ambas versiones:** el `INSERT sales` (prod 4200-4205) no lo incluye; solo se lee (prod 7380). **El POS nunca lo escribe.** Las 12 filas con 1 vienen de otro escritor. Fix de baja dificultad + backfill con `reweigh_of_sale_id`.

### Bug J — VOID no revierte crédito
- Ambas versiones. `VoidReasonConfirm_Click` (7563-7654) no toca `customer_credit`. Latente: `CanVoid` solo permite anular cash/card.

### Bug L — Actualizar chofer con 0 filas
- Ambas versiones. `GetDriverByWeightUuidAsync` busca LOCAL primero; `UpdateDriverInfoWithFallbackAsync` (prod 2217) actualiza MAIN primero y devuelve 0 filas sin error si la fila está solo en LOCAL.

### Bug M — Retención de logs
- Ambas versiones, `AppLogger.cs:40-49` idéntico: borra a los 2 días.
- **Corregido en stable** — rama `fix/logging-visibility`, commit `df97361`: retención de 30 días, configurable con `"log_retention_days": N` en `C:\ProgramData\TruckScale\config.json` (opcional; si falta o es ≤0 se usan 30). La purga corre una vez al día por proceso, no en cada línea. `ConfigManager.Save` conserva el valor. Pendiente: prueba en báscula (escenario F0-1).

### Fase 0 — visibilidad del sync (`fix/logging-visibility`, `df97361`)
- **No cambia la lógica** del push ni del pull (mismas tablas, mismo orden, mismas llamadas). Solo agrega diagnóstico.
- Push y pull registran el paso en curso y las filas por tabla. `PUSH_SUCCESS`/`PULL_SUCCESS` llevan el detalle (`sales:3 payments:3 …`); `PULL_SUCCESS` además el `last_pull_ts` previo.
- `PUSH_ERROR`/`PULL_ERROR` llevan: `step=<tabla que falló>`, `last_pull_ts`, las tablas que sí se aplicaron y el error completo (tipo, número MySQL, inner exceptions). **Esto responde directamente a C:** el primer `PULL_ERROR` tras desplegar dirá en qué tabla y por qué falla el pull.
- `LogSyncEventAsync` ya no se traga errores: cada evento se copia **siempre** al log de archivo (`[SyncLog] …`); si el `INSERT` en `sync_logs` falla, el error queda en el archivo (`[SyncLog] FAILED to write sync_logs …`), lo que resuelve la pregunta de E (LOCAL sin filas desde abril). **No relanza**: un fallo al registrar no aborta el sync (decisión deliberada).

#### Ajuste con el esquema real de `sync_logs` (commit `4f7aadf`)

Esquema (SHOW CREATE TABLE, 2026-09-29): `message text`, `error_details longtext CHECK (json_valid(error_details))` (patrón típico de MariaDB para columnas JSON), `log_type varchar(20)`, `severity varchar(10)`, `related_entity varchar(50)`, `related_uid char(36)` (vacío en las ~517 mil filas históricas), `last_sync_error varchar(500)`.

- **A.1 — ¿el CHECK `json_valid` tumba los INSERT desde abril? Descartado para producción.** El código de `1e04a54f` **nunca** escribe `error_details` (ni `LogSyncEventAsync` ni `DatabaseHelper.LogConnectionErrorAsync`); inserta NULL, y NULL pasa el CHECK. Quien sí escribía texto crudo ahí era `e024e8a` (master, 11-jun; `SQL_FULL` con fallback a `SQL_SHORT`), que no está en la báscula. La causa real de E sigue abierta; la dirá `[SyncLog] FAILED to write sync_logs …` en el primer despliegue. Otra candidata: alguna columna `NOT NULL` sin default que el INSERT no incluye (hace falta el `SHOW CREATE TABLE` completo para descartarla).
- **Ahora:** `error_details` se genera con `JsonSerializer` (paso, tabla, clave de fila, sale_uid, cadena de excepciones con número MySQL, stack) y se valida con `JsonDocument.Parse` antes de enviarlo; si algo falla va NULL. Nunca texto crudo. Solo se llena en eventos de error.
- **A.2:** `message` va completo (se quitó el truncado a 250).
- **A.3:** `log_type`, `severity` y `related_entity` se recortan a 20/10/50. Los valores actuales ya cabían (`PUSH_SUCCESS`=12, `PULL_ERROR`=10, `ERROR`=5), así que **tampoco son la causa de E**. `last_sync_error` no se escribe: su semántica es "error al subir esta fila a MAIN" y el push lo excluye.
- **A.4:** los errores de una fila concreta en push/pull se envuelven en `SyncRowException` (tabla, clave, sale_uid) y el `PUSH_ERROR`/`PULL_ERROR` llena `related_entity` = tabla y `related_uid` = sale_uid (o la clave de la fila si no pertenece a una venta, p. ej. `uuid_weight`). Consulta de correlación: `SELECT * FROM sync_logs WHERE related_uid = '<sale_uid>';`. Los eventos de resumen (SUCCESS) no tienen una venta única y quedan NULL.
- Si las columnas extendidas no existen en alguna base o el CHECK rechaza el valor (1054/3819/4025), reintenta con el INSERT básico y lo anota en el archivo.
- **A.5 — tráfico:** en producción `TotalPending` contaba `sync_logs`; cada push/pull escribe una fila de log ⇒ la fila dispara el siguiente push ⇒ **push en cada tick solo para subir sus propios logs** (bucle que probablemente explica las ~517 mil filas hasta abril, cuando el INSERT dejó de funcionar). Opciones: (a) excluir `sync_logs` del push por completo (lo que hizo master) — MAIN pierde los logs de la báscula; (b) **no dejar que los logs disparen el push, pero subirlos cuando haya datos reales que subir**. Se implementó **(b)**: `QueueInfo.TotalPending` ya no incluye `PendingLogs`; el paso `sync_logs` del push se conserva. Tráfico nuevo: ninguno sin ventas; con ventas, hasta 500 filas de log por push (límite existente de `GetPendingUidsAsync`). Riesgo a vigilar: si hay un backlog grande de logs con `synced=0`, se irá subiendo en bloques de 500 con cada push de datos. Verificar antes: `SELECT COUNT(*) FROM sync_logs WHERE synced=0 OR synced IS NULL;` en LOCAL.

### Fase 0.3 — `catch` que ocultan fallos en guardado de venta y sync (inventario, **sin cambios todavía**)

Líneas de `stable`/producción.

| Prioridad | Ubicación | Qué se traga | Consecuencia |
|-----------|-----------|--------------|--------------|
| **Crítico** | `SyncService.GetQueueInfoAsync` `catch {}` (~212) | cualquier error al contar pendientes | los contadores posteriores quedan en 0; si `TotalPending` da 0 el push responde "Nothing to sync" y **no sube nada**, sin error visible |
| **Crítico** | `TryAutoCompleteSaleAsync` `catch (Exception ex)` (1550) | muestra `MessageBox` pero **no escribe al log** | una venta que falla al guardar no deja rastro en archivo; imposible investigar después |
| **Crítico** | `ShowSaleCompletedDialogAsync` → `Task.Run(PrintTicketAsync)` `catch { }` (4595) | cualquier fallo al imprimir el ticket | venta cobrada y confirmada, ticket no impreso, sin log |
| Alto | `ConfigManager.Load` `catch` (Config/ConfigManager.cs ~49) | error al leer/descifrar `config.json` | la app arranca con cadenas vacías como si no estuviera configurada, sin decir por qué |
| Alto | `GetCreditSnapshotAsync` `catch` sin log (7109) | fallo de conexión a MAIN | cae a LOCAL (saldos viejos) sin dejar rastro; el crédito se evalúa con datos de junio sin que nadie lo sepa |
| Medio | `KeypadCommit_Click` `catch` silencioso (1448) | error al agregar un pago desde el teclado | el operador cree que agregó un pago y no se agregó |
| Medio | `DatabaseHelper` `catch {}` en `UpdateQueueStatusAsync` (103), `LogConnectionErrorAsync` (134), `GetQueueStatusAsync` (163, asume FREE), (193) | fallos de la cola | hoy poco impacto (el push ya no depende de DIRTY/FREE) |
| Medio | `UpdateDriverInfoWithFallbackAsync` (2217) | no es un `catch`: trata 0 filas actualizadas como éxito (Bug L) | cambios de cuenta del chofer perdidos sin error |
| Bajo | firma digital `catch` (1536), `sync.push_timer_minutes` (2908), `try{...}catch{}` de UI en `ResetDriverContext` | UI/opcionales | aceptables |

Propuesta: los tres críticos de guardado/sync se corrigen en su fase (1550 y 4595 en Fase 1, `GetQueueInfoAsync` en Fase 3); el resto, en un fix aparte cuando se decida.

---

## Qué conviene desplegar ya y qué requiere fix nuevo

**No desplegar master tal cual.** Corrige S1 y S2, pero **elimina el pull**: LOCAL quedaría congelado permanentemente y el riesgo de crédito offline empeoraría.

| Acción | Tipo | Resuelve |
|--------|------|----------|
| 1. Leer `[Sync] PULL ERROR` del log de hoy en la báscula y alinear el esquema LOCAL↔MAIN | operación, sin código | C, parte de D/E, riesgo de crédito offline |
| 2. Backup de LOCAL antes de que el pull vuelva a correr | operación | protege de S2 |
| 3. Build nuevo = producción + parches de sync de master (verificación de push S1, `ShouldSkipAutoId` S2, reconciliación del contador G) **conservando el pull** | cherry-pick parcial de `e024e8a`/`7c80501` | S1, S2, G (parcial) |
| 4. Fix nuevo en `SaveSaleAsync`/`TryAutoCompleteSaleAsync` | fix nuevo | B, V |
| 5. Fix nuevo de identidad de cliente y reweigh | fix nuevo | A, R |
| 6. Retención de logs, `is_reweigh`, `rows==0` en update de chofer | fixes pequeños | M, I, L |
| 7. Diseño de movimientos de crédito | fix nuevo | K, J |

## ¿Qué conclusiones dependen de `LocalDbStrCon`? (pendiente Ctrl+Alt+F8)

- **Independientes** (código puro): B, V, A, R, I, J, L, M, S1, S2 y la estructura del push/pull.
- **Dependientes:** C ("falla antes de 429" se basa en los saldos de la base auditada), D (ticket 16608 ausente), E (sync_logs local desde abril), H (estado en LOCAL). Si la base auditada **no** es la del POS, estas tres observaciones no describen la báscula real, y la explicación podría ser simplemente "es otra base".

---

## Escenarios de prueba en la báscula real

> Cliente de prueba POSTPAID con saldo y otro PREPAID con saldo ≥ $12. Anotar hora exacta y **copiar `C:\TruckScaleLogs\` el mismo día**.

### F0-1 — Retención de logs (build de `fix/logging-visibility`)
1. Antes de instalar, copiar `C:\TruckScaleLogs\` completo a otra carpeta (respaldo).
2. Crear en `C:\TruckScaleLogs\` un archivo `log_2026-09-01.txt` y cambiarle la fecha de modificación a hace 10 días (PowerShell: `(Get-Item C:\TruckScaleLogs\log_2026-09-01.txt).LastWriteTime = (Get-Date).AddDays(-10)`).
3. Abrir el POS y hacer login.
- **Esperado:** el archivo de hace 10 días **sigue existiendo** (antes se borraba). Con `"log_retention_days": 5` en `config.json` y reiniciando, se borra.
- Ninguna tabla cambia.

### F0-2 — Visibilidad del sync
1. Con el build instalado, dejar el POS abierto 2 ciclos del timer (`settings.sync.push_timer_minutes`).
2. Abrir `C:\TruckScaleLogs\log_<hoy>.txt` y buscar `[SyncLog]`.
- **Esperado A (pull sigue fallando):** `[SyncLog] PULL_ERROR (ERROR) … | step=<tabla> | last_pull_ts=… | done: sites:0 … | [MySqlException #<número>] <mensaje>`. **Anotar `step` y el mensaje: es la causa de C.**
- **Esperado B:** si aparece `[SyncLog] FAILED to write sync_logs …`, el mensaje explica por qué `sync_logs` local no se escribe desde abril (E).
- En BD: si el INSERT funciona, filas nuevas en LOCAL `sync_logs` con `log_type` `PULL_ERROR`, `message` completo, `error_details` JSON (`SELECT JSON_VALUE(error_details,'$.step') FROM sync_logs ORDER BY created_at DESC LIMIT 5;`) y, si el error fue en una fila de venta, `related_entity`/`related_uid` llenos. **No se modifica ninguna otra tabla más de lo que ya hacía el sync.**

### F0-4 — Los logs ya no disparan el push
1. Sin hacer ventas, dejar el POS abierto 3 ciclos del timer.
- **Esperado en el log:** cada ciclo `[Sync] Nothing to PUSH (queue FREE or 0 pending).` aunque haya filas nuevas en `sync_logs` con `synced=0`. Antes, esas filas provocaban `Pending to PUSH: N records` en cada ciclo.
2. Hacer una venta offline y reconectar.
- **Esperado:** `PUSH_SUCCESS … sync_logs:<n> …` — los logs pendientes suben junto con la venta.

### F0-3 — Push con detalle
1. Desconectar MAIN, hacer una venta de contado, reconectar y esperar un ciclo.
- **Esperado en el log:** `[SyncLog] PUSH_SUCCESS … | scale_session_axles:1 sales:1 sale_lines:1 sale_driver_info:1 tickets:1 … payments:1`.
- **En BD:** mismas filas que suben hoy (sin cambio de comportamiento).

### S-0 — Diagnóstico del pull (hacer primero, sin tocar nada)
1. Abrir `C:\TruckScaleLogs\log_<hoy>.txt` y buscar `PULL`.
- **Esperado:** `[Sync] PULL ERROR: Pull failed: <mensaje>` repetido cada tick → el mensaje identifica tabla/columna. Si aparece `PULL OK`, la hipótesis C es incorrecta y la base auditada no es la del POS.

### B-1 — Carrera online
1. Pesar, OK, chofer sin cuenta, WEIGH.
2. Elegir cliente POSTPAID, **inmediatamente** *Business Account* y *Done* (<1 s). Repetir 10×. Variante: tras *Done*, pulsar *Cash* mientras aparece la firma.
- **Si hay bug:** `sales.amount_paid=12`, sin `payments`, `customer_credit.updated_at` sin cambio, sin `[CustomerCredit] Updating credit` en el log.
- **Si no:** fila `payments` (method_id=3; status 5 POSTPAID / 6 PREPAID) y `customer_credit.updated_at` = hora de la venta.

### B-2 — Carrera offline (reproduce 16608)
1. Desconectar la red de MAIN con el POS abierto.
2. Pesar, chofer, WEIGH, cliente, *Business Account*, *Done*.
3. Durante los ~8 s de espera pulsar *Cash* o cambiar el combo.
- **Esperado:** venta en LOCAL con `amount_paid=12` y sin `payments`; `customer_credit` LOCAL sin tocar. Al reconectar, el push sube venta, líneas, chofer y ticket sin pago.

### V-1 — Salto de la validación
1. Como B-1, pero pulsar *Cash* **justo después** de *Done* (cuando ya empezó a escribir).
- **Si hay bug:** fila `payments` Business **presente** pero `customer_credit` sin cambio (la validación y el consumo se saltaron).

### A-1 — Cruzado por cambio de combo tras guardar chofer
1. Chofer con cliente X; cerrar modal; cambiar combo a Y; Cash; *Done*.
- **Esperado:** `sales.customer_id=Y`, `sale_driver_info.account_number=X`.

### A-2 — Reweigh de venta de contado con combo sucio
1. WEIGH de contado con nombre libre "PRUEBA LIBRE". Completar.
2. Pesar, OK, *Reweigh* con ese ticket; dejar combo en cliente Y; pagar; *Done*.
- **Esperado:** `customer_id=Y`, `reweigh_of_sale_id` = venta 1, `sale_driver_info` clonado (`account_number NULL`, `account_name 'PRUEBA LIBRE'`), `is_reweigh=0`.

### A-3 — Match por nombre (ACC-4)
1. Dos clientes activos con el mismo `account_name`. Venta WEIGH con el segundo; reweigh; abrir modal de chofer, *Update driver*.
- **Si la hipótesis es correcta:** combo salta al otro cliente; `account_number` nuevo = el del otro.

### R-1 — Clon fantasma
1. Hacer un reweigh completo. Sin cerrar la app: pesar, OK, guardar chofer con MAIN arriba; desconectar MAIN; WEIGH, Cash, *Done*.
- **Esperado:** venta en LOCAL con `sale_driver_info` clonado de la venta origen del reweigh anterior (log `REWEIGH: cloning driver…`) en vez de error.

### S2-1 — Pérdida de ticket offline (solo con respaldo de LOCAL)
1. Anotar `MAX(ticket_id)` en MAIN y `AUTO_INCREMENT` de `tickets` en LOCAL. Si LOCAL ≤ MAIN, hacer una venta offline y reconectar.
- **Esperado en prod:** el ticket queda `synced=1` en LOCAL pero **no existe** en MAIN.

### G-1 — Numeración offline
1. Comparar `number_sequences.current_value` (`TICKETS.NUMBER`) en MAIN y LOCAL; si LOCAL < MAIN, venta offline.
- **Esperado:** número repetido en MAIN tras el push.

---

## Historial de sesiones

- **2026-09-29 — sesión 1.** Registro creado. Análisis sobre master (asumido en producción por error).
- **2026-09-29 — sesión 2.** Confirmado que producción es `1e04a54f` (31-mar). Reanálisis con worktree temporal (eliminado al terminar). Hallazgos: `MainWindow.xaml.cs` es prácticamente idéntico (B, A, A2, R, I, J, L, M existen en producción); la validación Business **sí existe** en producción y sin interrupciones desde dic-2025; nuevo Bug V (la validación usa `_pagos` en vivo); **el pull existe en producción y falla**, probablemente antes de `customer_credit` (línea 429); nuevos S1 (push marca synced sin verificar), S2 (autoincrementales copiados) y S3 (incremental omite `updated_at` NULL), que master corrige salvo S3; master elimina el pull, así que **no debe desplegarse tal cual**.
- **2026-09-29 — sesión 3 (corrección).** Creada rama `stable` desde `1e04a54f` (worktree `C:\Repos\POS-stable`). Fase 0 en `fix/logging-visibility` (`df97361`): retención de logs 30 días configurable, sync con paso/tabla/filas y error completo, `LogSyncEventAsync` sin tragar errores. Inventario 0.3 de `catch` críticos. En revisión; no se sigue a Fase 1 hasta aprobación.
- **2026-09-29 — sesión 4.** Ajuste de Fase 0 con el esquema real de `sync_logs` (`4f7aadf`): `error_details` JSON válido o NULL, `message` completo, límites de columnas, `related_uid` = sale_uid en errores de fila, logs ya no disparan push. Descartado que el CHECK `json_valid` explique E en producción. Propuesta de entorno de pruebas documentada en `docs/entorno-pruebas.md` (rama `tools/test-harness`), sin código hasta aprobación.
