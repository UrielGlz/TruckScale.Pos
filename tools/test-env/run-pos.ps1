<#
.SYNOPSIS
  Compila el POS en Debug y lo abre, opcionalmente con el gancho de fallas activo.
.PARAMETER Fault
  none : POS normal (build Debug, sin fallas)
  B    : vacía _pagos justo después de abrir la conexión -> reproduce el bug B (venta sin pago)
  V    : vacía _pagos justo después de insertar los pagos -> reproduce el bug V
.DESCRIPTION
  TS_FAULT se pasa SOLO al proceso del POS (ProcessStartInfo), nunca a la sesión de PowerShell
  ni al sistema, así que no queda activo por accidente. El gancho además solo existe en Debug.
.EXAMPLE
  .\run-pos.ps1 -Fault B
#>
param([ValidateSet('none','B','V')] [string]$Fault = 'none')
$ErrorActionPreference = 'Stop'

$repo = Resolve-Path (Join-Path $PSScriptRoot '..\..')
$proj = Join-Path $repo 'TruckScale.Pos\TruckScale.Pos.csproj'

Write-Host "Compilando (Debug)..."
dotnet build $proj -c Debug -nologo -v q
if ($LASTEXITCODE -ne 0) { throw 'La compilación falló.' }

$exe = Get-ChildItem (Join-Path $repo 'TruckScale.Pos\bin\Debug') -Recurse -Filter 'TruckScale.Pos.exe' |
       Sort-Object LastWriteTime -Descending | Select-Object -First 1
if (-not $exe) { throw 'No se encontró TruckScale.Pos.exe en bin\Debug.' }

$psi = New-Object System.Diagnostics.ProcessStartInfo
$psi.FileName = $exe.FullName
$psi.WorkingDirectory = $exe.DirectoryName
$psi.UseShellExecute = $false
[void]$psi.EnvironmentVariables.Remove('TS_FAULT')
if ($Fault -ne 'none') { $psi.EnvironmentVariables['TS_FAULT'] = $Fault }

$p = [System.Diagnostics.Process]::Start($psi)
Write-Host ("POS iniciado (PID {0}) con TS_FAULT={1}. El título de la ventana lo indica." -f $p.Id, $Fault)
Write-Host ("Log: C:\TruckScaleLogs\log_{0}.txt  (buscar [FAULT] y [SaveSale])" -f (Get-Date -Format 'yyyy-MM-dd'))
