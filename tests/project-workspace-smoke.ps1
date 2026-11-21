$ErrorActionPreference = 'Stop'
$socket = [System.Net.Sockets.Socket]::new([System.Net.Sockets.AddressFamily]::Unix, [System.Net.Sockets.SocketType]::Stream, [System.Net.Sockets.ProtocolType]::Unspecified)
try {
    $socket.Connect([System.Net.Sockets.UnixDomainSocketEndPoint]::new('/ipc/sandbox.sock'))
    $stream = [System.Net.Sockets.NetworkStream]::new($socket, $false)
    $writer = [System.IO.StreamWriter]::new($stream, [System.Text.UTF8Encoding]::new($false), 1024, $true)
    $writer.AutoFlush = $true
    $reader = [System.IO.StreamReader]::new($stream)
    $writer.WriteLine((@{ command = 'cat /workspace/marker; printf updated > /workspace/result'; timeout = 5 } | ConvertTo-Json -Compress))
    $result = $reader.ReadLine() | ConvertFrom-Json
    if ($result.exitCode -ne 0 -or $result.stdout -ne $env:EXPECTED_MARKER) { throw 'Wrong workspace' }
} finally { $socket.Dispose() }
