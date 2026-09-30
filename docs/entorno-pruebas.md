# Entorno de pruebas local del POS

> Objetivo: reproducir en una PC de desarrollo los bugs del guardado de venta (B y V) y del sync,
> **antes** de corregirlos, y comprobar después que el fix funciona.
> Rama: `tools/test-harness` (desde `stable` = `1e04a54f`, producción).
> Scripts: `tools/test-env/`. Última actualización: 2026-09-29.

---

## 0. Resumen rápido (cuando ya está instalado)

```powershell
cd <repo>\tools\test-env
.\up.ps1                      # MariaDB MAIN + LOCAL + Toxiproxy
.\seed-dev.ps1                # restablece usuario dev, settings y saldos de clientes de prueba
.\net.ps1 main ok             # red normal
.\run-pos.ps1 -Fault B        # POS Debug con el gancho del bug B activo
#   ... hacer una venta Business con el cliente "TS TEST POSTPAID" ...
.\check.ps1 main              # ver la venta sin pago
```

---

## 1. Respuestas de diseño

| Pregunta | Respuesta |
|----------|-----------|
| ¿Se puede llamar `SaveSaleAsync` desde una prueba? | **No hoy.** Es privado de `MainWindow` y lee controles y estado de la ventana (`ClienteRegCombo`, `_pagos`, `_driverLinked`, `_selectedProduct*`, `_currentWeightUuid`, `_currentCashSessionUid`…). Solo se ejercita por la UI. La Fase 1 lo separa y lo hace testeable. |
| ¿Hay tests? | No. Un solo proyecto en la solución. Se propone xUnit (`TruckScale.Pos.Tests`) en la Fase 1. |
| ¿Segunda instancia del POS? | Imposible por diseño: mutex global `Global\ScaleSoftware` (`App.xaml.cs:17`) y rutas fijas (`C:\ProgramData\TruckScale\config.json`, `C:\TruckScale\serial.txt`, `C:\TruckScaleLogs`). No hace falta. |
| ¿Basta una prueba automatizada de la carrera? | Para el código actual, lo confiable es el **gancho de fallas Debug** (§5): reproduce B y V el 100 % de las veces. La reproducción manual con Toxiproxy (§6) confirma que la carrera ocurre también sin gancho. |
| Mínimo para B | Una MariaDB con la copia de LOCAL + `config.json` con MAIN colgada. El entorno completo lo automatiza. |

---

## 2. Qué incluye

| Archivo | Qué hace |
|---------|----------|
| `docker-compose.yml` | `ts-main` (3316), `ts-local` (3326) con **MariaDB 10.11.19** (misma versión que producción), `ts-toxiproxy` (API 8474, proxies 3317→MAIN y 3327→LOCAL). Datos en volúmenes Docker. |
| `ts-common.ps1` | Funciones compartidas (SQL vía `docker exec`, API de Toxiproxy por HTTP; no hace falta instalar cliente MariaDB ni toxiproxy-cli). |
| `up.ps1` / `down.ps1 [-Purge]` | Levantar / detener (y borrar datos) |
| `import.ps1` | Importa esquema de MAIN, esquema de LOCAL y catálogos (quita `DEFINER`) |
| `seed-dev.sql` / `seed-dev.ps1` | Usuario `dev`/`dev123`, `scale.com=COM99` (fuerza simulador), sync cada 1 min, impresora `TS-NULL`, clientes de prueba 990001 (POSTPAID, $1000) y 990002 (PREPAID, $500). Re-ejecutable: **restablece los saldos** entre corridas. |
| `set-config.ps1 [-Restore]` | Escribe `C:\ProgramData\TruckScale\config.json` apuntando al entorno, cifrado con DPAPI como el POS. **Respalda siempre** el existente y muestra a qué servidor apuntaba. |
| `net.ps1 <main\|local> <ok\|down\|hang\|slow\|status> [-Ms n]` | Controla la red entre el POS y cada base |
| `run-pos.ps1 [-Fault none\|B\|V]` | Compila en Debug y abre el POS; `TS_FAULT` solo para ese proceso |
| `check.sql` / `check.ps1 [main\|local\|both]` | Invariantes: ventas sin pago, `amount_paid` ≠ suma de pagos, Business sin cliente, identidad cruzada, saldos de prueba, últimas ventas |

---

## 3. Instalación paso a paso (una sola vez)

### 3.0 Proteger producción
- `set-config.ps1` respalda tu `config.json` actual y te dice a qué servidor apuntaba. Si ves la IP de
  producción, **ese respaldo es tu config real**: guárdalo. Para volver: `.\set-config.ps1 -Restore`.
- Mientras pruebes, ninguna cadena debe apuntar a producción. Los scripts solo escriben en
  `127.0.0.1:3316/3317/3326/3327`.

### 3.1 Requisitos
- Windows 10/11 con **Docker Desktop** (backend WSL2) **en ejecución**.
- **.NET 8 SDK** (`dotnet --version`).
- Nada más: el cliente MariaDB y Toxiproxy corren dentro de los contenedores.

### 3.2 Exportar esquema y catálogos (una persona con acceso, **solo lectura**)
Solo esquema + catálogos; **sin ventas** (tienen datos personales de choferes). Base: `<db>`.
```powershell
mariadb-dump -h <host_main>  -u <lector> -p --single-transaction --no-data --routines --triggers <db> > main_schema.sql
mariadb-dump -h <host_local> -u <lector> -p --single-transaction --no-data --routines --triggers <db> > local_schema.sql
mariadb-dump -h <host_main>  -u <lector> -p --single-transaction --no-create-info --skip-triggers <db> `
  sites status_catalogo tax_rates products customers customer_credit vehicle_types driver_products `
  license_states payment_methods number_sequences users settings pos_terminals void_reasons > catalogs.sql
```
MAIN y LOCAL se exportan por separado porque LOCAL tiene columnas de sync (`synced`, …) y MAIN tiene el
trigger `trg_ticket_apply_prepaid_credit`.

### 3.3 Levantar e importar
```powershell
cd <repo>\tools\test-env
$env:TS_DB = '<db>'            # nombre real de la base (default 'truckscale'); en la misma consola
.\up.ps1
.\import.ps1 -MainSchema C:\dumps\main_schema.sql -LocalSchema C:\dumps\local_schema.sql -Catalogs C:\dumps\catalogs.sql
.\seed-dev.ps1
```
Si `seed-dev.ps1` falla por una columna `NOT NULL` que no conozco, agrégala en `seed-dev.sql` y repite.
Revisa también que `sql_mode` y `time_zone` coincidan con producción:
`SELECT @@sql_mode, @@time_zone;` en producción y en `127.0.0.1:3316`.

### 3.4 Impresora nula (evita papel y diálogos)
Sin una impresora válida el POS usa "Microsoft Print to PDF" (abre diálogo) y luego la predeterminada
(**imprime papel**) — `MainWindow.xaml.cs:4668-4705`. Crear una que descarta todo:
Configuración → Impresoras → Agregar manualmente → impresora local → **Crear puerto nuevo: Local Port**,
nombre `NUL` → controlador **Generic / Text Only** → nombre de impresora **`TS-NULL`**.

### 3.5 Config del POS
```powershell
.\set-config.ps1
```
Escribe MAIN → `127.0.0.1:3317` y LOCAL → `127.0.0.1:3327` (ambos por Toxiproxy), cifradas con DPAPI
(`CurrentUser`, igual que `Security/CryptoHelper.cs`). **Ejecútalo con el mismo usuario de Windows que abrirá
el POS**; un `config.json` cifrado por otro usuario o en otra PC no se puede descifrar.
Alternativa manual: `Ctrl+Alt+F8` en el login (contraseña admin en `LoginWindow.xaml.cs:25`).

### 3.6 Primer arranque
```powershell
.\run-pos.ps1
```
Login `dev` / `dev123`, abrir caja (cualquier monto). Debe verse "Scale: Simulated": el puerto `COM99` no
existe y el POS arranca su simulador de báscula (`StartSimulatedReader`, ya existente en el código).

---

## 4. Condiciones de red (`net.ps1`)

| Comando | Efecto en el POS |
|---------|------------------|
| `.\net.ps1 main ok` | normal |
| `.\net.ps1 main down` | conexión **rechazada**: el POS falla **al instante** y cae a LOCAL. No abre la ventana de B. |
| `.\net.ps1 main hang` | conexión aceptada pero sin respuesta: el POS espera **8 s** (`ConnectionTimeout=8` forzado en `MainWindow.xaml.cs:865`) y cae a LOCAL. **Es la condición del ticket 16608.** |
| `.\net.ps1 main slow -Ms 1500` | +1.5 s por respuesta: ventas online lentas (ensancha B online) |
| `.\net.ps1 local slow -Ms 2000` | +2 s por respuesta en LOCAL (ensancha V) |
| `.\net.ps1 main status` | estado actual |
| Disparar sync | `Ctrl+Alt+F9` en el POS, o esperar 1 min |

Notas:
- Con `hang`, **toda** operación contra MAIN tarda 8 s (cargar clientes, re-evaluar crédito). Prepara la
  venta con `main ok` y activa `hang` justo antes de pulsar *Done*.
- El POS reutiliza conexiones del pool. Si tras activar `hang` la primera operación no tarda ~8 s,
  repítela: la segunda ya abre conexión nueva.

---

## 5. Gancho de fallas (solo Debug) — reproducción determinista de B y V

### 5.1 Doble candado
1. **Compilación:** el código está dentro de `#if DEBUG` (`MainWindow.xaml.cs`, métodos `AnnounceFaultMode` y
   `FaultPoint`, y dos llamadas en `SaveSaleAsync`). En **Release no existe**: verificado que la cadena
   `TS_FAULT` no aparece en `bin\Release\...\TruckScale.Pos.dll`. El build de la báscula es Release.
2. **Variable de entorno `TS_FAULT` del proceso.** No hay bandera en `config.json`, en `settings` ni en
   ningún archivo: sin la variable, el build Debug se comporta normal. `run-pos.ps1` la pasa **solo al
   proceso del POS** (`ProcessStartInfo`), no a la consola ni al sistema.
- Cuando está activo, el título de la ventana dice `[TS_FAULT=B — BUILD DE PRUEBAS]` y el log registra
  `[FAULT] … ACTIVE` al abrir y `[FAULT] … _pagos 1 -> 0` al disparar.
- **Nunca** definas `TS_FAULT` a nivel de usuario o sistema (`setx`).

### 5.2 Qué hace
En el punto indicado ejecuta lo mismo que la UI cuando una re-evaluación de crédito en vuelo devuelve
"no permitido": `UpdatePaymentMethodsVisibility(false)`, que **vacía `_pagos`** si está seleccionado Business
(`MainWindow.xaml.cs` 6722-6729 en producción). No inventa un camino: fuerza el orden de eventos real.

| `TS_FAULT` | Punto | Reproduce |
|------------|-------|-----------|
| `B` | después de abrir la conexión, antes de la transacción (entre la lectura de `recibido` y la relectura de `_pagos`) | venta COMPLETED, `amount_paid = total`, **sin filas en `payments`**, crédito sin tocar (16608, 10270, 13875) |
| `V` | después de insertar los pagos, antes de calcular `businessAmount` | pago Business **insertado**, validación de cliente y consumo de crédito **saltados** |

Si la venta no es Business, el gancho no hace nada y lo registra (`not applicable`).

---

## 6. Escenarios

Antes de cada corrida: `.\seed-dev.ps1` (restablece saldos) y `.\net.ps1 main ok`.
Pasos comunes de venta (**V0**): OK en el peso simulado estable → *Search driver* → capturar nombre y placas
→ **en el mismo modal elegir el cliente TS TEST POSTPAID** → guardar chofer → *WEIGH* → método
**Business Account**.
(Elegir el cliente **antes** de guardar el chofer evita mezclar el bug A: si se elige después, el chequeo 4
de `check.ps1` marcará la venta por identidad cruzada, que es justamente el bug A.)

### B-auto — bug B con gancho (el principal; online)
1. `.\run-pos.ps1 -Fault B`
2. V0 → *Done* (cerrar la ventana de firma si aparece).
3. `.\check.ps1 main`
- **Esperado con el código actual (falla visible):**
  - Chequeo 1 y 2: la venta aparece; `amount_paid = 12.00`, `suma_pagos = 0`.
  - Chequeo 5: `customer_credit.updated_at` del 990001 **no** cambia; `current_balance` sigue en 0.
  - Chequeo 6: `n_pagos = 0`.
  - Log: `[FAULT] TS_FAULT=B at after-connection-open: … _pagos 1 -> 0`, `[SaveSale] Sale … saved in MAIN_DB`,
    y **no** aparece `[CustomerCredit] Updating credit`.
- **Esperado después del fix de Fase 1:** misma corrida, pero con 1 pago Business y crédito actualizado
  (o la venta rechazada) — `check.ps1` sin filas en 1 y 2.

### B-auto-offline — igual que 16608
1. `.\run-pos.ps1 -Fault B`, V0.
2. `.\net.ps1 main hang` → *Done* (tarda ~8 s).
3. `.\check.ps1 local` — la venta está en **LOCAL** sin pagos.
4. `.\net.ps1 main ok`, `Ctrl+Alt+F9` → `.\check.ps1 main` — la venta subió a MAIN sin pago.

### B-manual — sin gancho (demuestra que la carrera es real)
1. `.\run-pos.ps1` (sin falla), V0.
2. `.\net.ps1 main hang` → *Done* → **durante los 8 s de espera** pulsar **Cash**.
3. `.\check.ps1 local` — mismo resultado que B-auto-offline.

### V-auto
1. `.\run-pos.ps1 -Fault V`; V0 con **TS TEST PREPAID** (Business queda RECEIVED) → *Done*.
2. `.\check.ps1 main`
- **Esperado (falla visible):** chequeo 6: `n_pagos = 1` (`3:6:12.00`), pero chequeo 5: `available_credit` del
  990002 sigue en **500.00** (no se descontó) y el log no tiene `[CustomerCredit] Updating credit`.

### V-manual
1. `.\run-pos.ps1`, V0 con PREPAID, `.\net.ps1 main hang`, `.\net.ps1 local slow -Ms 2000`.
2. *Done*; cuando terminen los 8 s de MAIN, en los ~2-4 s siguientes pulsar **Cash**. Requiere práctica; por
   eso existe V-auto.

### F0-1 … F0-4 (Fase 0, sobre la rama `fix/logging-visibility`)
Ver `docs/bugs-pos.md`. Equivalencias: "desconectar MAIN" = `.\net.ps1 main down`; "reconectar" =
`.\net.ps1 main ok`; "ciclo del timer" = 1 min o `Ctrl+Alt+F9`. Para provocar `PULL_ERROR` a propósito:
`ALTER TABLE users ADD COLUMN ts_extra INT NULL; UPDATE users SET ts_extra = 1 WHERE username = 'dev';`
**solo en MAIN (3316)** (el `UPDATE` hace que la fila entre en el pull incremental) → el pull intenta insertar
esa columna en LOCAL → `Unknown column` con `step=users`. Para deshacer: `ALTER TABLE users DROP COLUMN ts_extra;`.

---

## 7. Limitaciones conocidas
- No se ha corrido todavía de punta a punta: requiere los dumps reales y Docker en ejecución.
- `seed-dev.sql` asume columnas vistas en el código; puede requerir ajustes al esquema real.
- La latencia/cuelgue es a nivel TCP; no simula errores SQL específicos.
- El pad de firma Topaz no está: la ventana de firma se cierra/omite.
