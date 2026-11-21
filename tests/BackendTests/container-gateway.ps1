# Test-only HTTP fixture. No credentials or provider connections.
$ErrorActionPreference = 'Stop'
$listener = [Net.HttpListener]::new()
$listener.Prefixes.Add('http://+:4000/')
$listener.Start()
try {
    while ($listener.IsListening) {
        $context = $listener.GetContext()
        $reader = [IO.StreamReader]::new($context.Request.InputStream)
        try { $null = $reader.ReadToEnd() } finally { $reader.Dispose() }
        $body = switch ($context.Request.Url.AbsolutePath) {
            '/models' { '{"data":[{"id":"synthetic-chat"}]}' }
            '/utils/model_info' { '{"model_info":{"max_input_tokens":30000}}' }
            '/utils/token_counter' { '{"total_tokens":100}' }
            '/v1/responses' { '{"status":"completed","usage":{"input_tokens":100,"output_tokens":20,"total_tokens":120},"output":[{"type":"message","role":"assistant","content":[{"type":"output_text","text":"synthetic container answer"}]}]}' }
            default { $context.Response.StatusCode = 404; '{}' }
        }
        $bytes = [Text.Encoding]::UTF8.GetBytes($body)
        $context.Response.ContentType = 'application/json'
        $context.Response.ContentLength64 = $bytes.Length
        try { $context.Response.OutputStream.Write($bytes, 0, $bytes.Length) } finally { $context.Response.Close() }
    }
} finally { $listener.Close() }
