<#
.SYNOPSIS
  Importa esquema y catálogos en MAIN y LOCAL de prueba.
.DESCRIPTION
  Espera los archivos generados con mariadb-dump (ver docs/entorno-pruebas.md §3.3):
    -MainSchema   esquema de MAIN  (--no-data --routines --triggers)
    -LocalSchema  esquema de LOCAL (--no-data --routines --triggers)
    -Catalogs     datos de catálogos (--no-create-info), se cargan en ambas
  Quita las cláusulas DEFINER antes de importar.
  NUNCA apunta a producción: solo escribe en los contenedores ts-main / ts-local.
.EXAMPLE
  .\import.ps1 -MainSchema C:\dumps\main_schema.sql -LocalSchema C:\dumps\local_schema.sql -Catalogs C:\dumps\catalogs.sql
#>
param(
    [Parameter(Mandatory)] [string]$MainSchema,
    [Parameter(Mandatory)] [string]$LocalSchema,
    [Parameter(Mandatory)] [string]$Catalogs
)
. "$PSScriptRoot\ts-common.ps1"
Assert-Docker

function Get-CleanCopy([string]$Path) {
    if (-not (Test-Path $Path)) { throw "No existe: $Path" }
    $text = [IO.File]::ReadAllText((Resolve-Path $Path))
    $text = [regex]::Replace($text, 'DEFINER=`[^`]+`@`[^`]+`\s*', '')
    $out = Join-Path $env:TEMP ('ts_clean_' + [IO.Path]::GetFileName($Path))
    [IO.File]::WriteAllText($out, $text, (New-Object Text.UTF8Encoding($false)))
    return $out
}

$main  = Get-CleanCopy $MainSchema
$local = Get-CleanCopy $LocalSchema
$cat   = Get-CleanCopy $Catalogs

Write-Host 'MAIN : esquema...';   Invoke-TsSqlFile -Target main  -Path $main
Write-Host 'LOCAL: esquema...';   Invoke-TsSqlFile -Target local -Path $local
Write-Host 'MAIN : catálogos...'; Invoke-TsSqlFile -Target main  -Path $cat
Write-Host 'LOCAL: catálogos...'; Invoke-TsSqlFile -Target local -Path $cat

Remove-Item $main, $local, $cat -ErrorAction SilentlyContinue
Write-Host 'Importación terminada. Siguiente paso: .\seed-dev.ps1'
