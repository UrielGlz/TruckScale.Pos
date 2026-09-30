<#
.SYNOPSIS
  Controla la "red" entre el POS y cada base (vía Toxiproxy).
.PARAMETER Target
  main | local
.PARAMETER Mode
  ok     : quita toda falla (conexión normal)
  down   : rechaza conexiones -> el POS falla AL INSTANTE y cae a LOCAL (no abre la ventana de 8 s)
  hang   : acepta la conexión pero no responde -> el POS espera su ConnectionTimeout (8 s en MAIN)
           y luego cae a LOCAL. ESTA es la condición del ticket 16608 (ventana del bug B).
  slow   : agrega latencia de -Ms milisegundos a cada respuesta (ensancha las ventanas de B y V)
  status : muestra el estado actual
.EXAMPLE
  .\net.ps1 main hang
  .\net.ps1 local slow -Ms 2000
  .\net.ps1 main ok
#>
param(
    [Parameter(Position = 0)] [ValidateSet('main','local')] [string]$Target = 'main',
    [Parameter(Position = 1)] [ValidateSet('ok','down','hang','slow','status')] [string]$Mode = 'status',
    [int]$Ms = 2000
)
. "$PSScriptRoot\ts-common.ps1"

switch ($Mode) {
    'ok' {
        Clear-TsToxics $Target
    }
    'down' {
        Clear-TsToxics $Target
        Invoke-Tox POST "/proxies/$Target" @{ enabled = $false } | Out-Null
    }
    'hang' {
        Clear-TsToxics $Target
        # timeout=0: retiene los datos indefinidamente; el cliente agota su propio timeout
        Invoke-Tox POST "/proxies/$Target/toxics" @{
            name = 'hang'; type = 'timeout'; stream = 'downstream'; toxicity = 1.0
            attributes = @{ timeout = 0 }
        } | Out-Null
    }
    'slow' {
        Clear-TsToxics $Target
        Invoke-Tox POST "/proxies/$Target/toxics" @{
            name = 'slow'; type = 'latency'; stream = 'downstream'; toxicity = 1.0
            attributes = @{ latency = $Ms; jitter = 0 }
        } | Out-Null
    }
}

Get-TsNetState | Format-Table -AutoSize
