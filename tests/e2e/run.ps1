[CmdletBinding()]
param(
    [switch]$WithSqlServer
)

$ErrorActionPreference = 'Stop'
$env:FOREXBOT_E2E_WITH_SQLSERVER = if ($WithSqlServer) { '1' } else { '0' }

dotnet test .\tests\e2e\ForexTradingBot.EndToEnd.csproj -c Release --logger "console;verbosity=normal"
exit $LASTEXITCODE
