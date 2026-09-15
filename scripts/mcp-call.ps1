# Sends tool calls to the MCP server over stdio and prints what came back.
# For manual checks and demos. The automated equivalent lives in tools/LabChecks.
#
#   ./scripts/mcp-call.ps1 get_lab_status
#   ./scripts/mcp-call.ps1 run_simulation '{"runs":5,"seed":42,"label":"baseline"}'
#
# Pass -Raw to print the tool's JSON exactly as it was returned.
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true, Position = 0)][string]$Tool,
    [Parameter(Position = 1)][string]$Arguments = '{}',
    [switch]$Raw
)

$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$exe = Join-Path $root 'src/McpServer/bin/Release/net10.0/PetThem.BalanceLab.Mcp.exe'
if (-not (Test-Path $exe)) { throw "Build first: dotnet build src/McpServer -c Release. Missing $exe" }

# Fail early on a malformed argument object rather than inside the server.
$null = $Arguments | ConvertFrom-Json

$requests = @(
    '{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2025-06-18","capabilities":{},"clientInfo":{"name":"mcp-call","version":"0.1.0"}}}'
    '{"jsonrpc":"2.0","method":"notifications/initialized"}'
    ('{"jsonrpc":"2.0","id":2,"method":"tools/call","params":{"name":"' + $Tool + '","arguments":' + $Arguments + '}}')
)

$info = New-Object System.Diagnostics.ProcessStartInfo
$info.FileName = $exe
$info.RedirectStandardInput = $true
$info.RedirectStandardOutput = $true
$info.RedirectStandardError = $true
$info.UseShellExecute = $false
$process = [System.Diagnostics.Process]::Start($info)

try {
    foreach ($request in $requests) { $process.StandardInput.WriteLine($request) }
    $process.StandardInput.Flush()

    # A real client keeps stdin open while it waits. Closing it early shuts the transport
    # down before the server has written its replies.
    $answer = $null
    $deadline = (Get-Date).AddSeconds(300)
    while ($null -eq $answer -and (Get-Date) -lt $deadline) {
        $line = $process.StandardOutput.ReadLine()
        if ($null -eq $line) { break }
        $message = $line | ConvertFrom-Json
        if ($message.id -eq 2) { $answer = $message }
    }
} finally {
    $process.StandardInput.Close()
    $null = $process.WaitForExit(30000)
    if (-not $process.HasExited) { $process.Kill() }
}

if ($null -eq $answer) { throw "No response for $Tool. stderr: $($process.StandardError.ReadToEnd())" }
if ($answer.error) { throw "MCP error for ${Tool}: $($answer.error.message)" }

$text = $answer.result.content[0].text
if ($Raw) { Write-Output $text } else { $text | ConvertFrom-Json | ConvertTo-Json -Depth 8 }
