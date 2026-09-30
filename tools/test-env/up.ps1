<#
.SYNOPSIS
  Levanta MAIN, LOCAL y Toxiproxy en Docker y crea los proxies que usa el POS.
.EXAMPLE
  .\up.ps1
#>
. "$PSScriptRoot\ts-common.ps1"
Assert-Docker

$tag = if ($env:TS_MARIADB_TAG) { $env:TS_MARIADB_TAG } else { '10.11.19' }
Write-Host "Levantando contenedores (MariaDB $tag, base '$script:TsDb')..."
docker compose -f $script:TsCompose up -d --wait
if ($LASTEXITCODE -ne 0) { throw 'docker compose up falló.' }

# La API de Toxiproxy tarda un instante en responder
$ok = $false
for ($i = 0; $i -lt 20 -and -not $ok; $i++) {
    try { Invoke-Tox GET '/version' | Out-Null; $ok = $true } catch { Start-Sleep -Milliseconds 500 }
}
if (-not $ok) { throw 'Toxiproxy no responde en 127.0.0.1:8474.' }

Initialize-TsProxies
Clear-TsToxics main
Clear-TsToxics local

Write-Host ''
Write-Host 'Listo. El POS debe conectarse a:'
Write-Host '  MAIN : 127.0.0.1:3317  (proxy)   | directo para verificar: 3316'
Write-Host '  LOCAL: 127.0.0.1:3327  (proxy)   | directo para verificar: 3326'
Get-TsNetState | Format-Table -AutoSize
