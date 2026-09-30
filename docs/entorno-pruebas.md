# Entorno de pruebas local del POS

> Objetivo: reproducir en una PC de desarrollo los bugs del guardado de venta (B y V) y del sync,
> **antes** de corregirlos, para poder comprobar después que el fix funciona.
> Rama: `tools/test-harness` (desde `stable`). Estado: **propuesta en revisión** — este documento
> describe lo que se puede hacer hoy sin código y lo que se propone construir.
> Fecha: 2026-09-29. Código de referencia: `stable` = `1e04a54f` (producción).

---

## 1. Respuestas previas (antes de construir nada)

### ¿Qué tan acoplado está el guardado a la UI? ¿Se puede llamar `SaveSaleAsync` desde una prueba?

**Muy acoplado. Hoy no se puede probar sin la UI.**

- `SaveSaleAsync` (`MainWindow.xaml.cs:4111`) es un método **privado de la ventana** `MainWindow`.
- Lee directamente controles y estado de la ventana: `ClienteRegCombo.SelectedItem` (4181), `_pagos`
  (una `ObservableCollection` enlazada a la lista de pagos de la pantalla, 4298 y 4364),
  `_driverLinked`, `_selectedProductId/Price/Code`, `_currentWeightUuid`, `_currentCashSessionUid`,
  `_reweighSourceSaleUid`, `PaymentMethods` (vía `FindPaymentMethodIdByCode`), y llama a
  `UpdateSyncStatusUI` y `AppendLog`.
- Para instanciar `MainWindow` hace falta un hilo STA de WPF, los recursos XAML (MaterialDesign), el
  `config.json` con DPAPI y conexión a BD en el constructor/Loaded (cash session, catálogos, puerto serie).
- Conclusión: **para el código actual, la única forma de ejercitar el guardado es la UI**.
- **Esto cambia con la Fase 1**: el fix propuesto (snapshot inmutable) separa naturalmente
  "capturar el estado de la pantalla" de "escribir la venta". La parte que escribe quedará en una clase
  sin dependencias de WPF que sí se puede probar automáticamente contra una MariaDB local.

### ¿Hay tests hoy en la solución?

**No.** `TruckScale.Pos.sln` tiene un solo proyecto (`TruckScale.Pos`). No hay proyecto de pruebas ni
framework referenciado. Propuesta: **xUnit** (estándar en .NET 8, funciona con `dotnet test`) en un
proyecto `TruckScale.Pos.Tests`, creado en la Fase 1 cuando exista algo testeable.

### ¿Algo impide una segunda instancia del POS en la misma máquina?

Sí, varias cosas — **pero no hace falta una segunda instancia** para reproducir B, V ni el sync.

| Impedimento | Dónde | Efecto |
|-------------|-------|--------|
| Mutex global `Global\ScaleSoftware` | `App.xaml.cs:17-34` | la segunda instancia muestra "The application is already running" y se cierra. **Bloquea por diseño.** |
| `config.json` en ruta fija | `C:\ProgramData\TruckScale\config.json` (`Config/ConfigManager.cs`) | todas las instancias de la máquina comparten conexiones |
| `serial.txt` en ruta fija | `C:\TruckScale\serial.txt` (`SerialScaleReader.cs:127`) | idem |
| Logs en ruta fija | `C:\TruckScaleLogs` (`AppLogger.cs`) | idem |
| Puerto serie | exclusivo por naturaleza | en la PC de desarrollo no aplica: se usa el simulador |
| Licencias | ninguna encontrada. El pad de firma Topaz (`Libs/SigPlusNET.dll`) es opcional; sin hardware la ventana de firma se cierra/omite (`catch` silencioso en 1536) | ninguno |

### ¿Basta una prueba automatizada que dispare la condición de carrera para B y V?

**Para demostrar el bug en el código actual: no de forma confiable con una prueba automatizada pura**,
porque el código actual solo se ejecuta por la UI y la carrera depende de tiempos.
Hay tres niveles, de menor a mayor esfuerzo:

1. **Manual + ventana ensanchada (sin código).** Con MAIN "colgado" (8 s de espera) o latencia artificial,
   la ventana de la carrera pasa de milisegundos a varios segundos: el operador tiene tiempo de sobra
   para pulsar *Cash* o cambiar el combo. **Reproducible de forma confiable a mano.** Suficiente para B.
   Para V también, con latencia en LOCAL (ver §4).
2. **Inyección de fallas solo en Debug (propuesto, requiere tu aprobación).** Un gancho compilado
   **únicamente en builds Debug** (`#if DEBUG`) que, si existe la variable de entorno
   `TS_FAULT=clear-pagos-after-open` o `clear-pagos-during-payment-insert`, ejecuta en el punto exacto de
   `SaveSaleAsync` lo mismo que hace la UI (`UpdatePaymentMethodsVisibility(false)`, que vacía `_pagos`).
   Resultado: **B y V se reproducen el 100 % de las veces sin depender de la velocidad del operador.**
   El build Release (el de la báscula) queda idéntico.
3. **Pruebas automatizadas de invariantes (después de la Fase 1).** Sobre la clase extraída:
   "ninguna venta cierra sin filas de pago", "`amount_paid` = suma de pagos insertados",
   "`customer_id` y `sale_driver_info.account_number` coinciden". Estas pruebas no necesitan provocar la
   carrera: el snapshot la hace imposible por construcción, y la prueba verifica el contrato.

**Automatizar la UI (FlaUI/WinAppDriver) no se recomienda**: mucho esfuerzo y frágil para este caso.

### ¿Cuál es el mínimo para reproducir B y V?

| Bug | Mínimo indispensable |
|-----|----------------------|
| **B** | Una sola MariaDB local (copia de esquema + catálogos de LOCAL) + `config.json` en la PC de desarrollo con **MAIN apuntando a una IP que no responde** (p. ej. `10.255.255.1`). Cada venta espera 8 s antes de caer a LOCAL; durante esa espera se pulsa *Cash*. No hace falta Docker ni Toxiproxy. |
| **V** | Lo mismo + **latencia en LOCAL** (Toxiproxy) para que cada `INSERT` tarde ~2 s, o el gancho Debug `clear-pagos-during-payment-insert`. La ventana de V (entre el `INSERT` del pago y el cálculo de `businessAmount`) es un solo round-trip: a mano sin latencia es casi imposible. |
| Sync (F0-2, F0-3, S1, S2) | Dos bases (MAIN y LOCAL) + Toxiproxy para cortar/reconectar MAIN. |

---

## 2. Qué ya existe y se reutiliza

- **Simulador de báscula: ya existe, no hay que construir nada.** `MainWindow.StartSimulatedReader()`
  (`MainWindow.xaml.cs:2602`) genera tramas Cardinal `%0 … GG` para los 3 ejes y el total cada ~1.25 s.
  Se activa **solo** cuando falla la apertura del puerto serie al arrancar (`TryAutoConnectAtBoot`, 497-510).
  El puerto se lee del setting de BD `scale.com` (default `COM2`, 6905). En una PC sin puertos COM el
  modo simulado arranca solo. Si tu PC tiene un COM (Bluetooth, módem), pon `scale.com = COM99` en la copia
  de `settings`.
  En git no hay otro simulador (búsqueda por "simul/serial/emulat" en todo el historial).
- **Sync manual:** `Ctrl+Alt+F9` en `MainWindow` abre el diálogo de sync (`ShowSyncDebugDialog`, 6437) y
  ejecuta push + pull. El timer automático usa el setting `sync.push_timer_minutes` (mínimo 1).
- **Config de BD:** `Ctrl+Alt+F8` en la ventana de login, con la contraseña de administrador definida en
  `LoginWindow.xaml.cs:25` (`AdminConfigPassword`; no se reproduce aquí).

---

## 3. Instalación paso a paso (PC de desarrollo, Windows)

### 3.0 Antes de empezar — **proteger producción**
1. Si tu PC ya tiene `C:\ProgramData\TruckScale\config.json`, **cópialo a un lugar seguro**: puede apuntar
   a la MAIN de producción. Mientras pruebas, **ninguna** cadena de conexión debe apuntar a producción.
2. Respalda `C:\TruckScaleLogs\` si existe.

### 3.1 Software
| Herramienta | Uso | Nota |
|-------------|-----|------|
| .NET 8 SDK | compilar/ejecutar | `dotnet --version` ≥ 8 |
| Docker Desktop (WSL2) | dos MariaDB aisladas | alternativa: MariaDB instalada con dos puertos/instancias |
| Cliente MariaDB (`mariadb`, `mariadb-dump`) | importar/exportar | viene con MariaDB Server o HeidiSQL |
| Toxiproxy (server + cli, binarios Windows de GitHub `Shopify/toxiproxy`) | cortar MAIN, colgarla 8 s, latencia | no se instala: son dos `.exe` |

### 3.2 Versión de base de datos
El `CHECK (json_valid(...))` de `sync_logs.error_details` indica **MariaDB**. Confirmar en ambas bases:
`SELECT VERSION();` y usar la **misma versión** en Docker (`mariadb:<major.minor>`).

### 3.3 Obtener esquema y catálogos (lo ejecuta quien tenga acceso; **solo lectura**)
> Contiene datos personales de choferes y clientes si se exportan ventas. Recomendado: **solo esquema +
> catálogos**, sin ventas. Las ventas de prueba se generan en tu PC.

```powershell
# ESQUEMA completo (tablas, vistas, triggers, rutinas) de cada base
mariadb-dump -h <host_main>  -u <usuario_lectura> -p --single-transaction --no-data --routines --triggers <db> > main_schema.sql
mariadb-dump -h <host_local> -u <usuario_lectura> -p --single-transaction --no-data --routines --triggers <db> > local_schema.sql

# CATÁLOGOS (datos) — mismos en ambas bases
$cat = "sites status_catalogo tax_rates products customers customer_credit vehicle_types driver_products license_states payment_methods number_sequences users settings pos_terminals void_reasons"
mariadb-dump -h <host_main> -u <usuario_lectura> -p --single-transaction --no-create-info --skip-triggers <db> $cat.Split(' ') > catalogs.sql

# Quitar DEFINER (si no, falla la importación de triggers/vistas con otro usuario)
foreach ($f in 'main_schema.sql','local_schema.sql') {
  (Get-Content $f) -replace 'DEFINER=`[^`]+`@`[^`]+`','' | Set-Content $f -Encoding utf8
}
```
Notas: MAIN y LOCAL **no tienen el mismo esquema** (LOCAL agrega `synced`, `synced_at`, `sync_attempts`,
`last_sync_error`); por eso se exportan por separado. El trigger `trg_ticket_apply_prepaid_credit` solo
existe en MAIN y viene en `main_schema.sql`.

### 3.4 Levantar las dos bases
```powershell
docker run -d --name ts-main  -p 3316:3306 -e MARIADB_ROOT_PASSWORD=dev -e MARIADB_DATABASE=<db> mariadb:<version>
docker run -d --name ts-local -p 3326:3306 -e MARIADB_ROOT_PASSWORD=dev -e MARIADB_DATABASE=<db> mariadb:<version>

Get-Content main_schema.sql  | mariadb -h 127.0.0.1 -P 3316 -u root -pdev <db>
Get-Content local_schema.sql | mariadb -h 127.0.0.1 -P 3326 -u root -pdev <db>
Get-Content catalogs.sql     | mariadb -h 127.0.0.1 -P 3316 -u root -pdev <db>
Get-Content catalogs.sql     | mariadb -h 127.0.0.1 -P 3326 -u root -pdev <db>
```

### 3.5 Ajustes de datos para desarrollo (en **ambas** copias)
```sql
-- Usuario de pruebas (login: dev / dev123). El POS compara SHA-256 en hexadecimal.
INSERT INTO users (username, full_name, role_code, password_hash, password_algo, is_active)
VALUES ('dev', 'Dev Tester', '<role_code de un cajero>', SHA2('dev123', 256), 'SHA256', 1);
-- (ajusta columnas NOT NULL adicionales según tu esquema)

-- Forzar simulador de báscula y sync cada minuto
UPDATE settings SET value = 'COM99' WHERE `key` = 'scale.com';
UPDATE settings SET value = '1'     WHERE `key` = 'sync.push_timer_minutes';

-- Impresora que no imprime (ver 3.7)
UPDATE settings SET value = 'TS-NULL' WHERE `key` = 'tickets.printer_name';

-- Dos clientes de prueba con crédito: uno POSTPAID con saldo y uno PREPAID con ≥ $12
-- (usar clientes existentes del catálogo o insertar en customers + customer_credit)
```

### 3.6 Toxiproxy (cortar y ralentizar)
```powershell
.\toxiproxy-server.exe                       # dejar corriendo en otra consola
.\toxiproxy-cli.exe create -l 127.0.0.1:3317 -u 127.0.0.1:3316 main
.\toxiproxy-cli.exe create -l 127.0.0.1:3327 -u 127.0.0.1:3326 local
```
El POS se conecta a **3317 (MAIN)** y **3327 (LOCAL)**, nunca directo a 3316/3326.

### 3.7 Impresora nula (evita papel y diálogos)
Si no hay impresora configurada válida, el POS usa "Microsoft Print to PDF" (abre un diálogo de guardar)
y luego la predeterminada del sistema (**imprime papel**) — `MainWindow.xaml.cs:4668-4705`.
Crear una impresora que descarta todo:
1. Configuración → Impresoras → Agregar manualmente → "Agregar impresora local" → **Crear un puerto nuevo:
   Local Port** → nombre del puerto `NUL`.
2. Controlador: **Generic / Text Only**. Nombre de la impresora: `TS-NULL`.

### 3.8 `config.json` en tu PC (DPAPI)
`config.json` guarda las cadenas **cifradas con DPAPI, alcance CurrentUser** (`Security/CryptoHelper.cs`):
solo las puede descifrar **el mismo usuario de Windows en la misma máquina**. **No se puede copiar el
de la báscula.** Se genera desde el propio POS:
1. Compilar y ejecutar `stable` (o la rama a probar): `dotnet run --project TruckScale.Pos/TruckScale.Pos.csproj`.
2. En el login: `Ctrl+Alt+F8` → contraseña admin → capturar:
   - MAIN: `Server=127.0.0.1;Port=3317;Database=<db>;Uid=root;Pwd=dev;SslMode=None;`
   - LOCAL: `Server=127.0.0.1;Port=3327;Database=<db>;Uid=root;Pwd=dev;SslMode=None;Connection Timeout=5;`
3. Guardar. (El POS fuerza `ConnectionTimeout=8` en MAIN — `MainWindow.xaml.cs:865` — sin importar lo que
   pongas; esa es la ventana del bug B.)
4. Opcional, en texto plano dentro del mismo `config.json`: `"log_retention_days": 30` (con la Fase 0).
5. Login `dev` / `dev123`, abrir caja.

---

## 4. Provocar las condiciones

| Condición | Comando | Qué pasa en el POS |
|-----------|---------|--------------------|
| MAIN **caída rápida** (rechaza) | `toxiproxy-cli toggle main` | `OpenAsync` falla al instante → cae a LOCAL sin espera. **No** reproduce la ventana de B. |
| MAIN **colgada 8 s** (ventana de B) | `toxiproxy-cli toxic add -t timeout -a timeout=0 -n hang main` | la conexión se abre pero nunca responde → el POS espera el `ConnectionTimeout=8` y cae a LOCAL. **Esta es la condición de 16608.** |
| Alternativa sin Toxiproxy | MAIN = `Server=10.255.255.1;...` en `config.json` | IP no enrutable → 8 s por conexión. Para volver hay que reconfigurar. |
| Reconectar MAIN | `toxiproxy-cli toxic remove -n hang main` (o `toggle main` de nuevo) | siguiente operación va a MAIN |
| Disparar sync | `Ctrl+Alt+F9` o esperar 1 min (`sync.push_timer_minutes=1`) | push + pull; resultado en `C:\TruckScaleLogs\log_<hoy>.txt` |
| Latencia en LOCAL (ensancha V) | `toxiproxy-cli toxic add -t latency -a latency=2000 -n slow local` | cada comando a LOCAL tarda +2 s; los `INSERT` de pago dejan ventanas de segundos |
| Latencia en MAIN (ensancha B online) | `toxiproxy-cli toxic add -t latency -a latency=1500 -n slow main` | ventas online con varios segundos entre el inicio y el `INSERT` de pagos |
| Quitar latencia | `toxiproxy-cli toxic remove -n slow local` | — |

---

## 5. Escenarios con mínimos pasos manuales

Consultas de verificación (correr en la base donde se guardó la venta: 3316 MAIN o 3326 LOCAL):
```sql
-- Ventas cerradas sin pago (bug B)
SELECT s.sale_uid, s.total, s.amount_paid FROM sales s
LEFT JOIN payments p ON p.sale_uid = s.sale_uid
WHERE s.sale_status_id = 2 AND p.payment_uid IS NULL;

-- amount_paid distinto de la suma de pagos (bug B/V)
SELECT s.sale_uid, s.amount_paid, COALESCE(SUM(p.amount),0) AS pagos
FROM sales s LEFT JOIN payments p ON p.sale_uid = s.sale_uid
GROUP BY s.sale_uid HAVING s.amount_paid <> pagos;

-- Pago Business sin consumo de crédito (bug V): comparar updated_at
SELECT c.customer_id, c.updated_at, c.current_balance, c.available_credit FROM customer_credit c WHERE c.customer_id IN (<ids de prueba>);

-- Identidad (bug A)
SELECT s.sale_uid, s.customer_id, c.account_number AS cuenta_de_customer_id, d.account_number AS cuenta_en_driver_info
FROM sales s LEFT JOIN customers c ON c.id_customer = s.customer_id
LEFT JOIN sale_driver_info d ON d.sale_uid = s.sale_uid
WHERE NOT (s.customer_id <=> (SELECT id_customer FROM customers WHERE account_number = d.account_number));
```

### B-2 local — venta offline sin pago (reproduce 16608)
1. `toxiproxy-cli toxic add -t timeout -a timeout=0 -n hang main`
2. En el POS: OK (peso simulado estable) → registrar chofer → WEIGH → cliente POSTPAID → *Business Account* → *Done*.
3. **Durante los 8 s** en que el POS espera a MAIN: pulsar *Cash* (o cambiar el combo a "Select Business Account").
4. Esperado con el código actual (`stable`): la venta queda en **LOCAL (3326)** con `amount_paid=12`, **sin
   filas en `payments`**, `customer_credit` del cliente sin cambios; aparece en la primera consulta.
5. `toxiproxy-cli toxic remove -n hang main`, `Ctrl+Alt+F9`: la venta sube a MAIN sin pago.

### B-1 local — online con latencia
1. `toxiproxy-cli toxic add -t latency -a latency=1500 -n slow main`
2. Mismos pasos; tras *Done* pulsar *Cash* dentro de los ~5-10 s siguientes.
3. Esperado: igual que B-2 pero en **MAIN (3316)**.

### V local — pago Business sin validar ni consumir crédito
1. `toxiproxy-cli toxic add -t latency -a latency=2000 -n slow local` y MAIN colgada (hang) para forzar LOCAL.
2. Cliente PREPAID con saldo → *Business Account* → *Done*.
3. Esperar a que termine la espera de MAIN (8 s) y, **en los ~2-4 s siguientes** (mientras se insertan
   venta, línea y pago), pulsar *Cash*.
4. Esperado: fila en `payments` (method 3) **presente** y `customer_credit` **sin cambios**. Con el gancho
   Debug (§6) no hace falta el paso 3.

### F0-1 / F0-2 / F0-3 / F0-4 (Fase 0)
Igual que en `docs/bugs-pos.md` (rama `fix/logging-visibility`), con estas equivalencias:
- "desconectar MAIN" = `toxiproxy-cli toggle main`; "reconectar" = `toggle main` otra vez.
- "esperar un ciclo del timer" = 1 min o `Ctrl+Alt+F9`.
- Para ver `PULL_ERROR` a propósito: renombrar una columna en la copia LOCAL de una tabla del pull (p. ej.
  `ALTER TABLE users RENAME COLUMN full_name TO full_name_x;` **solo en 3326**) y esperar un ciclo. El log
  debe decir `step=users` y `Unknown column`.

---

## 6. Lo que se propone construir (pendiente de aprobación)

| # | Qué | Dónde | Toca código de la app | Para qué |
|---|-----|-------|-----------------------|----------|
| 1 | Scripts PowerShell: `up.ps1` (contenedores + proxies), `import.ps1`, `main-hang.ps1`/`main-ok.ps1`, `latency.ps1 <ms> <main\|local>`, `check.sql` (consultas de §5) | `tools/test-env/` | no | reducir los pasos manuales a 1-2 comandos |
| 2 | Gancho de fallas **solo Debug** (`#if DEBUG` + `TS_FAULT`) en `SaveSaleAsync`: `clear-pagos-after-open`, `clear-pagos-during-payment-insert`, `change-customer-after-open` | `MainWindow.xaml.cs` | sí, pero excluido del build Release | reproducir B, V y A de forma determinista, antes y después del fix |
| 3 | Proyecto `TruckScale.Pos.Tests` (xUnit) con pruebas de invariantes contra la MariaDB de Docker | nuevo proyecto | no (prueba la clase que extraiga la Fase 1) | verificación automática del fix |

Decisión que necesito: si apruebas el #2, la Fase 1 debería salir de una rama que ya tenga el gancho
(para comparar el mismo escenario antes/después). Propuesta: `tools/test-harness` → se mergea a `stable`
(el gancho no existe en Release) y `fix/save-sale-snapshot` sale de ahí.
