<#
.SYNOPSIS
  Genera C:\ProgramData\TruckScale\config.json apuntando al entorno de pruebas
  (MAIN 127.0.0.1:3317 y LOCAL 127.0.0.1:3327, ambos por Toxiproxy).
.DESCRIPTION
  - Cifra las cadenas con DPAPI (CurrentUser), igual que Security/CryptoHelper.cs.
    Por eso debe ejecutarse con el MISMO usuario de Windows que abrirá el POS.
  - SIEMPRE respalda el config.json existente como config.json.bak-<fecha> antes de escribir.
  - -Restore vuelve a poner el respaldo más reciente.
.EXAMPLE
  .\set-config.ps1
  .\set-config.ps1 -Restore
#>
param([switch]$Restore)
. "$PSScriptRoot\ts-common.ps1"
Add-Type -AssemblyName System.Security

$dir  = Join-Path $env:ProgramData 'TruckScale'
$path = Join-Path $dir 'config.json'

if ($Restore) {
    $bak = Get-ChildItem $dir -Filter 'config.json.bak-*' -ErrorAction SilentlyContinue |
           Sort-Object LastWriteTime -Descending | Select-Object -First 1
    if (-not $bak) { throw "No hay respaldos en $dir" }
    Copy-Item $bak.FullName $path -Force
    Write-Host "Restaurado: $($bak.Name) -> config.json"
    return
}

function Protect([string]$plain) {
    $bytes = [Text.Encoding]::UTF8.GetBytes($plain)
    $enc = [Security.Cryptography.ProtectedData]::Protect($bytes, $null, [Security.Cryptography.DataProtectionScope]::CurrentUser)
    return 'dpapi:' + [Convert]::ToBase64String($enc)
}
function Unprotect([string]$stored) {
    if (-not $stored) { return '' }
    if (-not $stored.StartsWith('dpapi:')) { return $stored }
    $bytes = [Convert]::FromBase64String($stored.Substring(6))
    $dec = [Security.Cryptography.ProtectedData]::Unprotect($bytes, $null, [Security.Cryptography.DataProtectionScope]::CurrentUser)
    return [Text.Encoding]::UTF8.GetString($dec)
}

New-Item -ItemType Directory -Force $dir | Out-Null

if (Test-Path $path) {
    # Mostrar a qué servidores apuntaba (sin contraseñas) para que sea evidente si era producción
    try {
        $old = Get-Content $path -Raw | ConvertFrom-Json
        foreach ($k in 'main_db_str_con','local_db_str_con') {
            $cs = Unprotect $old.$k
            $srv = ([regex]::Match($cs, '(?i)(server|host|data source)\s*=\s*([^;]+)')).Groups[2].Value
            $prt = ([regex]::Match($cs, '(?i)port\s*=\s*([^;]+)')).Groups[1].Value
            Write-Host ("  config actual {0}: {1}:{2}" -f $k, $srv, $prt)
        }
    } catch { Write-Host "  (no se pudo leer el config actual: $($_.Exception.Message))" }

    $bakPath = "$path.bak-{0}" -f (Get-Date -Format 'yyyyMMdd-HHmmss')
    Copy-Item $path $bakPath
    Write-Host "Respaldo: $bakPath"
}

$db = $script:TsDb
$main  = "Server=127.0.0.1;Port=3317;Database=$db;Uid=root;Pwd=$script:TsRootPwd;SslMode=None;"
$local = "Server=127.0.0.1;Port=3327;Database=$db;Uid=root;Pwd=$script:TsRootPwd;SslMode=None;Connection Timeout=5;"

$cfg = [ordered]@{
    main_db_str_con      = Protect $main
    local_db_str_con     = Protect $local
    ticket_printer_name  = 'TS-NULL'
    ticket_landscape     = $true
    ticket_margin_inches = 0.25
    log_retention_days   = 30
}
[IO.File]::WriteAllText($path, ($cfg | ConvertTo-Json), (New-Object Text.UTF8Encoding($false)))
Write-Host "Escrito $path"
Write-Host "  MAIN  -> 127.0.0.1:3317 (proxy)   LOCAL -> 127.0.0.1:3327 (proxy)   base '$db'"
