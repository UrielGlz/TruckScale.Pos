<#
.SYNOPSIS
  Detiene el entorno. Con -Purge borra también los datos (volúmenes).
#>
param([switch]$Purge)
. "$PSScriptRoot\ts-common.ps1"
Assert-Docker

if ($Purge) {
    docker compose -f $script:TsCompose down -v
    Write-Host 'Contenedores y datos eliminados.'
} else {
    docker compose -f $script:TsCompose down
    Write-Host 'Contenedores detenidos (los datos se conservan; usa -Purge para borrarlos).'
}
