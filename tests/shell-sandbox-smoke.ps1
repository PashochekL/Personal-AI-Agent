$ErrorActionPreference = 'Stop'
function Invoke-Sandbox([string] $command, [int] $timeout = 5) {
    $socket = [System.Net.Sockets.Socket]::new([System.Net.Sockets.AddressFamily]::Unix, [System.Net.Sockets.SocketType]::Stream, [System.Net.Sockets.ProtocolType]::Unspecified)
    try {
        $socket.Connect([System.Net.Sockets.UnixDomainSocketEndPoint]::new('/ipc/sandbox.sock'))
        $stream = [System.Net.Sockets.NetworkStream]::new($socket, $false)
        $writer = [System.IO.StreamWriter]::new($stream, [System.Text.UTF8Encoding]::new($false), 1024, $true)
        $writer.AutoFlush = $true
        $reader = [System.IO.StreamReader]::new($stream)
        $writer.WriteLine((@{ command = $command; timeout = $timeout } | ConvertTo-Json -Compress))
        return ($reader.ReadLine() | ConvertFrom-Json)
    } finally { $socket.Dispose() }
}
$first = Invoke-Sandbox 'printf first > /workspace/test; printf ok'
$second = Invoke-Sandbox 'cat /workspace/test'
$third = Invoke-Sandbox 'printf partial; sleep 5' 1
$fourth = Invoke-Sandbox 'printf alive'
$fifth = Invoke-Sandbox 'pwsh -Version'
if ($first.stdout -ne 'ok' -or $second.stdout -ne 'first' -or !$third.timedOut -or $third.stdout -ne 'partial' -or $fourth.stdout -ne 'alive' -or $fifth.stdout -notmatch '^PowerShell 7\.') { throw 'Persistent sandbox check failed' }
Write-Output 'Persistent sandbox: sequential commands and timeout passed'
