<#
.SYNOPSIS
  Aplica seed-dev.sql (usuario dev, settings de desarrollo, clientes de prueba) en MAIN y LOCAL.
  También sirve para RESTABLECER los saldos de los clientes de prueba entre corridas.
#>
. "$PSScriptRoot\ts-common.ps1"
Assert-Docker
$sql = Join-Path $PSScriptRoot 'seed-dev.sql'
Write-Host 'MAIN :'; Invoke-TsSqlFile -Target main  -Path $sql -Table
Write-Host 'LOCAL:'; Invoke-TsSqlFile -Target local -Path $sql -Table
