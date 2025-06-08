# This script handles the complex task of replacing multiple tokens in the config file.
param(
    [string]$DeployPath,
    [string]$ConnectionString,
    [string]$TelegramBotToken,
    [string]$TelegramApiId,
    [string]$TelegramApiHash,
    [string]$TelegramPhoneNumber,
    [string]$CryptoPayApiToken
)

$appSettingsPath = "$DeployPath\\appsettings.Production.json"
Write-Host "Injecting secrets into $appSettingsPath..."

(Get-Content $appSettingsPath -Raw) -replace '#{ConnectionString}#', $ConnectionString `
    -replace '#{TelegramBotToken}#', $TelegramBotToken `
    -replace '#{TelegramApiId}#', $TelegramApiId `
    -replace '#{TelegramApiHash}#', $TelegramApiHash `
    -replace '#{TelegramPhoneNumber}#', $TelegramPhoneNumber `
    -replace '#{CryptoPayApiToken}#', $CryptoPayApiToken | Set-Content -Path $appSettingsPath

Write-Host "Secret injection complete."