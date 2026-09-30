<#
.SYNOPSIS
  Corre check.sql (invariantes de ventas) contra MAIN, LOCAL o ambas.
.EXAMPLE
  .\check.ps1 local
  .\check.ps1 both
#>
param([Parameter(Position = 0)] [ValidateSet('main','local','both')] [string]$Target = 'both')
. "$PSScriptRoot\ts-common.ps1"
Assert-Docker
$sql = Join-Path $PSScriptRoot 'check.sql'
foreach ($t in $(if ($Target -eq 'both') { 'main','local' } else { $Target })) {
    Write-Host ''
    Write-Host "================ $($t.ToUpper()) ================" -ForegroundColor Cyan
    Invoke-TsSqlFile -Target $t -Path $sql -Table
}
