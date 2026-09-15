$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path $PSScriptRoot -Parent
Push-Location $projectRoot
try {
    # LabChecks starts the MCP server as a child process, so build it first.
    dotnet build src/McpServer -c Release
    if ($LASTEXITCODE -ne 0) { throw 'MCP server build failed.' }
    dotnet build src/Simulator -c Release
    if ($LASTEXITCODE -ne 0) { throw 'Simulator build failed.' }
    dotnet run --project tools/LabChecks -c Release
    if ($LASTEXITCODE -ne 0) { throw 'Balance lab checks failed.' }
    git diff --check
    if ($LASTEXITCODE -ne 0) { throw 'Whitespace check failed.' }
} finally {
    Pop-Location
}
