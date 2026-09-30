# Funciones comunes del entorno de pruebas. Se carga con: . "$PSScriptRoot\ts-common.ps1"
# Compatible con Windows PowerShell 5.1 y PowerShell 7.

$ErrorActionPreference = 'Stop'

$script:TsDb        = if ($env:TS_DB) { $env:TS_DB } else { 'truckscale' }
$script:TsRootPwd   = 'dev'
$script:TsToxApi    = 'http://127.0.0.1:8474'
$script:TsCompose   = Join-Path $PSScriptRoot 'docker-compose.yml'
$script:TsContainer = @{ main = 'ts-main'; local = 'ts-local' }

# Puertos que usa el POS (pasan por Toxiproxy) y puertos directos (verificación)
$script:TsProxy = @(
    @{ name = 'main';  listen = '0.0.0.0:3317'; upstream = 'ts-main:3306'  },
    @{ name = 'local'; listen = '0.0.0.0:3327'; upstream = 'ts-local:3306' }
)

function Assert-Docker {
    if (-not (Get-Command docker -ErrorAction SilentlyContinue)) {
        throw 'Docker no está instalado o no está en el PATH (se requiere Docker Desktop).'
    }
}

function Get-TsContainer([ValidateSet('main','local')] [string]$Target) {
    return $script:TsContainer[$Target]
}

# Ejecuta un archivo .sql dentro del contenedor (evita problemas de codificación de PowerShell 5.1)
function Invoke-TsSqlFile {
    param(
        [Parameter(Mandatory)] [ValidateSet('main','local')] [string]$Target,
        [Parameter(Mandatory)] [string]$Path,
        [switch]$Table
    )
    $c = Get-TsContainer $Target
    $name = 'ts_' + [IO.Path]::GetFileName($Path)
    docker cp $Path "${c}:/tmp/$name" | Out-Null
    if ($LASTEXITCODE -ne 0) { throw "docker cp falló para $Path" }
    $fmt = if ($Table) { '-t' } else { '' }
    docker exec $c sh -c "mariadb $fmt -uroot -p$script:TsRootPwd --default-character-set=utf8mb4 $script:TsDb < /tmp/$name"
    if ($LASTEXITCODE -ne 0) { throw "Falló SQL '$Path' en $Target" }
}

function Invoke-TsSql {
    param(
        [Parameter(Mandatory)] [ValidateSet('main','local')] [string]$Target,
        [Parameter(Mandatory)] [string]$Sql,
        [switch]$Table
    )
    $tmp = Join-Path $env:TEMP ("ts_inline_{0}.sql" -f ([guid]::NewGuid().ToString('N')))
    [IO.File]::WriteAllText($tmp, $Sql, (New-Object Text.UTF8Encoding($false)))
    try { Invoke-TsSqlFile -Target $Target -Path $tmp -Table:$Table }
    finally { Remove-Item $tmp -ErrorAction SilentlyContinue }
}

# ---- Toxiproxy (API HTTP; no requiere toxiproxy-cli) ----

function Invoke-Tox([string]$Method, [string]$Path, $Body = $null) {
    $req = @{ Method = $Method; Uri = "$script:TsToxApi$Path"; ContentType = 'application/json' }
    if ($null -ne $Body) { $req.Body = (ConvertTo-Json -InputObject $Body -Depth 5 -Compress) }
    return Invoke-RestMethod @req
}

function Initialize-TsProxies {
    # /populate crea o actualiza (idempotente)
    Invoke-Tox POST '/populate' $script:TsProxy | Out-Null
}

function Clear-TsToxics([ValidateSet('main','local')] [string]$Target) {
    $toxics = Invoke-Tox GET "/proxies/$Target/toxics"
    foreach ($t in @($toxics)) {
        if ($t -and $t.name) { Invoke-Tox DELETE "/proxies/$Target/toxics/$($t.name)" | Out-Null }
    }
    Invoke-Tox POST "/proxies/$Target" @{ enabled = $true } | Out-Null
}

function Get-TsNetState {
    $proxies = Invoke-Tox GET '/proxies'
    foreach ($p in $proxies.PSObject.Properties.Value) {
        $tox = @($p.toxics | ForEach-Object { if ($_) { "$($_.name)($($_.type))" } }) -join ', '
        [pscustomobject]@{
            proxy   = $p.name
            listen  = $p.listen
            enabled = $p.enabled
            toxics  = if ($tox) { $tox } else { '-' }
        }
    }
}
