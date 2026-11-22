# Test-only HTTP fixture. No credentials or provider connections.
$ErrorActionPreference = 'Stop'
$listener = [Net.HttpListener]::new()
$listener.Prefixes.Add('http://+:4000/')
$listener.Start()
try {
    while ($listener.IsListening) {
        $context = $listener.GetContext()
        $reader = [IO.StreamReader]::new($context.Request.InputStream)
        try { $requestText = $reader.ReadToEnd() } finally { $reader.Dispose() }
        $body = switch ($context.Request.Url.AbsolutePath) {
            '/models' { '{"data":[{"id":"synthetic-chat"}]}' }
            '/utils/model_info' { '{"model_info":{"max_input_tokens":30000}}' }
            '/utils/token_counter' { '{"total_tokens":100}' }
            '/v1/responses' {
                $request = $requestText | ConvertFrom-Json
                $user = @($request.input | Where-Object { $_.role -eq 'user' })[-1]
                $prompt = if ($user.content -is [string]) { $user.content } else { $user.content[0].text }
                if ($prompt -match '^isolation-shell-([AB])$') {
                    $account = $Matches[1]; $peer = if ($account -eq 'A') { 'B' } else { 'A' }
                    if ($request.input[-1].type -eq 'function_call_output') {
                        @{ status = 'completed'; output = @(@{ type = 'message'; role = 'assistant'; content = @(@{ type = 'output_text'; text = $request.input[-1].output }) }) } | ConvertTo-Json -Depth 10 -Compress
                    } else {
                        $name = 'tool_' + [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData([Text.Encoding]::UTF8.GetBytes('shell.execute'))).ToLowerInvariant().Substring(0, 32)
                        $args = @{ command = "printf 'private-$account' > /workspace/secret-$account.txt; test ! -e /workspace/secret-$peer.txt && cat /workspace/secret-$account.txt"; timeout = 5 } | ConvertTo-Json -Compress
                        @{ status = 'completed'; output = @(@{ type = 'function_call'; call_id = [guid]::NewGuid().ToString('N'); name = $name; arguments = $args }) } | ConvertTo-Json -Depth 10 -Compress
                    }
                } else { '{"status":"completed","usage":{"input_tokens":100,"output_tokens":20,"total_tokens":120},"output":[{"type":"message","role":"assistant","content":[{"type":"output_text","text":"synthetic container answer"}]}]}' }
            }
            default { $context.Response.StatusCode = 404; '{}' }
        }
        $bytes = [Text.Encoding]::UTF8.GetBytes($body)
        $context.Response.ContentType = 'application/json'
        $context.Response.ContentLength64 = $bytes.Length
        try { $context.Response.OutputStream.Write($bytes, 0, $bytes.Length) } finally { $context.Response.Close() }
    }
} finally { $listener.Close() }
