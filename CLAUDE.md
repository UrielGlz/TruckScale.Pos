# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Build & Run

```bash
# Build the solution
dotnet build TruckScale.Pos.sln

# Build the project directly
dotnet build TruckScale.Pos/TruckScale.Pos.csproj

# Run the application (Windows only)
dotnet run --project TruckScale.Pos/TruckScale.Pos.csproj
```

This is a **Windows-only** WPF application (target: `net8.0-windows`). It will not build or run on Linux/macOS. There is no test project.

## Architecture Overview

**TruckScale.Pos** is a Point-of-Sale desktop application for a truck weigh scale facility. It manages truck weigh sessions, ticket printing, payments, and syncs data between a local MySQL database and a central remote MySQL database.

### Startup Flow

1. `LoginWindow` loads first — calls `ConfigManager.Load()` to read encrypted DB credentials
2. User authenticates against MySQL `users` table (supports SHA256 and BCrypt password hashing)
3. On success, `PosSession` (static) stores the logged-in user's `UserId`, `Username`, `FullName`, `RoleCode`
4. `MainWindow` opens — this is where all POS operations live

### Key Configuration

| File | Purpose |
|------|---------|
| `C:\ProgramData\TruckScale\config.json` | DB connection strings (DPAPI-encrypted), ticket printer settings |
| `C:\TruckScale\serial.txt` | Serial port config for the scale hardware (COM port, baud, brand, units) |

Connection strings are encrypted with Windows DPAPI (`CryptoHelper` uses `DataProtectionScope.CurrentUser`). The `ConfigManager` is the canonical way to access them; `PosConfigService` is an older parallel implementation that should not be expanded.

DB config screen: `Ctrl+Alt+F8` at the login window (prompts for admin password).

### Dual-Database Pattern

The app runs with two MySQL connections:
- **`MainDbStrCon`** – remote/central database (primary source of truth)
- **`LocalDbStrCon`** – local MySQL instance (offline-capable fallback)

All transactions are written **to LOCAL first**, then synced to MAIN. Tables that participate in sync have a `synced` column: `synced=0` means pending upload.

#### Sync Service (`TruckScale.Pos.Sync`)

`SyncService` handles bidirectional sync:
- **Push** (`PushTransactionsAsync`): LOCAL → MAIN, in FK-dependency order:
  `scale_session_axles` → `sales` → `sale_lines` → `sale_driver_info` → `tickets` → `sync_logs` → `customer_credit` → `number_sequences` → `payments`
- **Pull** (`PullUpdatesAsync`): MAIN → LOCAL, incremental by `updated_at` (stored in `settings` table as `sync.last_pull_ts`)
- **Catalog sync** (`SyncCatalogsAsync`): full-refresh pull of lookup tables
- Uses `INSERT IGNORE` for push, `INSERT ... ON DUPLICATE KEY UPDATE` for pull
- Auto-increment PK columns (`sale_id`, `payment_id`, `line_id`, `id_driver_info`) are skipped during sync — only UID/GUID keys are used across DBs

### Serial Scale Reader (`SerialScaleReader`)

Reads weight data from a physical truck scale via serial port. Configured via `C:\TruckScale\serial.txt`. Parses lines with regex, emits `WeightUpdated(double value, string raw)` events. Supports Cardinal brand scales in Continuous or Demand (ENQ polling) modes. Performs port validation before accepting a connection.

### Scale Stability Logic & Weight Acceptance (all in `MainWindow.xaml.cs`)

The app parses Cardinal protocol frames and applies a two-layer stability check:

**1. Cardinal status flag (GG / GR)**
- `HandleCardinalRawGG(line)` — parses frames like `%0 1234lb GG` (channels 0–3: axle1, axle2, axle3, total). `GG` = scale reports stable; `GR` = in motion / unstable.
- On `GR`: immediately sets `_canAccept = false`, `_autoStable = false`.

**2. Software stability window (`EvaluateStabilityAndUpdateUi`)**
- Tracks a rolling window of readings per channel (`StableWindowMs` ms, default 500 ms from `serial.txt`).
- If the spread within the window ≤ `StableDeltaLb` (default 80 lb) for at least `HOLD_MS` (600 ms, hardcoded), sets `_autoStable = true`.
- Once stable: snapshots `_snapAx1 / _snapAx2 / _snapAx3 / _snapTotal` and raises `_canAccept = true`.

**3. Simulation mode**
- `TryAcceptStableSet()` — used when no real serial port is present; validates the 4 simulated channels are coherent before setting the snapshot.

**Key flag: `_canAccept` (bool)**
- `true` = there is a stable weight snapshot ready. The OK button is only operative when `_canAccept = true`.
- Reset to `false` after the operator accepts the weight (presses OK) or when an unstable frame arrives.

### Scale Session Persistence (MySQL)

| Location | What it does |
|----------|-------------|
| `MainWindow.xaml.cs` `SaveScaleSnapshotAsync()` | Inserts the accepted snapshot into `scale_session_axles` (`synced=0`); tries PRIMARY DB first, falls back to LOCAL DB |
| `Domain/WeightLogger.cs` `LogEventAsync()` | **Only active method** — writes debug rows to `scale_debug` table |
| `Domain/WeightLogger.cs` `InsertSessionAsync/InsertAxleAsync` | Dead code — never called from anywhere in the app |
| `Domain/WeightTelemetryInput.cs` | Immutable DTO carrying one weight reading (port, brand, unit, raw line, value in lb) |
| `Domain/AxleSessionTracker.cs` | In-memory tracker for the current axle session (no direct DB writes) |

> **Dead Code**: `sp_scale_session_insert` and `sp_scale_axle_insert` are never called. `WeightLogger` is instantiated in MainWindow but only `LogEventAsync` is invoked (lines 1588, 2856, 4566). The active insert path for scale data is `SaveScaleSnapshotAsync()` in `MainWindow.xaml.cs` (direct SQL). Also, `sp_scale_axle_insert` references column `session_uuid10` which doesn't exist in the current `scale_session_axles` table (`uuid_weight` is the actual column name).

### Namespace Structure

| Namespace | Contents |
|-----------|----------|
| `TruckScale.Pos` | `MainWindow`, `LoginWindow`, `DbConfigWindow`, `SerialScaleReader`, `SerialOptions`, `PosSession`, `PosConfigService` |
| `TruckScale.Pos.Config` | `AppConfig`, `ConfigManager` (authoritative config), `PosSession` |
| `TruckScale.Pos.Data` | `IDbConnectionFactory`, `MySqlConnectionFactory`, `DatabaseConfig`, `DatabaseHelper` |
| `TruckScale.Pos.Domain` | `WeightLogger` (calls MySQL stored procedures `sp_scale_session_insert`, `sp_scale_axle_insert`), `AxleModels`, `AxleSessionTracker` |
| `TruckScale.Pos.Security` | `CryptoHelper` (DPAPI protect/unprotect) |
| `TruckScale.Pos.Sync` | `SyncService`, `SyncModels` |
| `TruckScale.Pos.Tickets` | `TicketData`, `TicketView` (WPF print view) |
| `TruckScale.Pos.Services` | `ThemeService` |

### Key Models in MainWindow.xaml.cs

Most business logic and UI models live in `MainWindow.xaml.cs` (the file is very large). Notable types defined there:
- `PaymentMethod` / `PaymentEntry` — multi-method payment support
- `TodayTxRow` — today's transaction list row with void/edit permissions
- `KeypadConfig` / `DenomButtonVm` — numeric keypad and denomination buttons
- `LicenseState`, `DriverProduct`, `VoidReasonItem` — lookup data

### UI Framework

- **MaterialDesignThemes 5.x** for all UI components and icons (`PackIconKind`)
- **CommunityToolkit.Mvvm 8.x** available but not heavily used — most code is code-behind rather than MVVM ViewModels
- Theme resources in `Theme/Colors.xaml`, `Theme/Components.xaml`, `Theme/Typography.xaml`

### Ticket Printing

`TicketView.xaml` is a WPF `UserControl` used as a print document. `TicketData` carries all fields. The printer name, landscape orientation, and margin are stored in `config.json` and accessed via `ConfigManager.Current`.

### QR Codes

`QrCodeConverter` (in `Tickets/`) generates QR codes for tickets. Payload is serialized via `QrPayload`.

## Database Schema

Same schema on MAIN_DB and LOCAL_DB. LOCAL_DB adds `synced`, `synced_at`, `sync_attempts`, `last_sync_error` columns to sync-participating tables — these columns do NOT exist in MAIN_DB.

Full column-level detail: see memory file `database-schema.md`.

### Core Transaction Flow
```
sales ──< sale_lines         (sale_uid)
sales ──  sale_driver_info   (sale_uid, 1:1)
sales ──< payments           (sale_uid)
sales ──  tickets            (sale_uid, 1:1)
sales >── customers          (customer_id)
sales >── cash_sessions      (cash_session_uid)
sales >── status_catalogo    (sale_status_id, module='sales')
payments >── payment_methods (method_id)
payments >── status_catalogo (payment_status_id, module='payments')
tickets  >── status_catalogo (ticket_status_id, module='tickets')
customers── customer_credit  (customer_id, 1:1)
scale_sessions ──< scale_session_axles (session_id / uuid10)
ar_payments ──< ar_payment_allocations ──> tickets (ticket_uid)
```

### Key Design Decisions
- **Cross-DB identity**: All transactional tables use `*_uid` char(36) (UUID) as the cross-DB key. Auto-increment PKs (`sale_id`, `payment_id`, `line_id`, `id_driver_info`) are LOCAL-only and excluded from sync.
- **`status_catalogo`**: Universal status catalog filtered by `module` ('sales', 'payments', 'tickets'). `status_id = 9` = TICKETS.PRINTED (hardcoded in app).
- **`number_sequences`**: Ticket/receipt number counters, keyed by `(site_id, scope)` with prefix/suffix.
- **`settings`**: Key-value store; also holds sync timestamps: `sync.last_pull_ts`, `sync.last_push_success`, `sync.last_catalog_sync`.
- **`scale_session_axles`**: Has both `weight_lb decimal(12,3)` (primary) and `eje1/eje2/eje3/peso_total double` (snapshot of all channels at capture time).
- **`cash_sessions`**: `is_open=1` means the register is open. Sales reference the session via `cash_session_uid`.
- **AR module** (`ar_payments`, `ar_payment_allocations`): Separate from POS `payments`. Handles post-sale accounts receivable collection with FIFO/MANUAL allocation to individual tickets.
- **`sale_driver_info.identify_by` + `match_key`**: Flexible driver-lookup strategy stored per transaction.
- **Views**: `vw_pending_ar_transactions` — AR aging; `v_sales_fin` — financial totals (total, amount_paid, balance_due, change_given, lines_sum) per sale.

### Active DB Trigger (MAIN_DB only)
**`trg_ticket_apply_prepaid_credit`** — AFTER INSERT ON `tickets`:
Fires when a ticket is printed. If the sale's customer has `credit_type = 'PREPAID'` and the payment used `method_id = 3` (business/account, **hardcoded**), automatically allocates that amount against the customer's oldest available `ar_payments` (CREDIT_BALANCE, FIFO). Inserts into `ar_payment_allocations`; `amount_unapplied` in `ar_payments` is a STORED GENERATED column so only `amount_applied` needs updating. Does NOT update `customer_credit` balances directly. Only runs on MAIN_DB.

### Tables NOT in Sync (MAIN_DB only / admin)
`roles`, `permissions`, `role_permissions`, `navigation_items`, `report_definitions`, `report_columns`, `report_filters`, `refresh_tokens`, `pos_terminals`, `scale_debug`, `ar_payments`, `ar_payment_allocations`.
