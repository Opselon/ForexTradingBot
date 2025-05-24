# Create secrets directory if it doesn't exist
New-Item -ItemType Directory -Force -Path ./secrets

# Function to create secret file
function New-SecretFile {
    param (
        [string]$SecretName,
        [string]$EnvVarName
    )
    
    $secretValue = [Environment]::GetEnvironmentVariable($EnvVarName)
    if (-not $secretValue) {
        Write-Error "Environment variable $EnvVarName is not set"
        exit 1
    }
    
    $secretValue | Out-File -FilePath "./secrets/$SecretName.txt" -NoNewline -Encoding utf8
    Write-Host "Created secret file for $SecretName"
}

# Create secret files
New-SecretFile -SecretName "db_password" -EnvVarName "DB_PASSWORD"
New-SecretFile -SecretName "telegram_bot_token" -EnvVarName "TELEGRAM_BOT_TOKEN"
New-SecretFile -SecretName "telegram_api_id" -EnvVarName "TELEGRAM_API_ID"
New-SecretFile -SecretName "telegram_api_hash" -EnvVarName "TELEGRAM_API_HASH"
New-SecretFile -SecretName "cryptopay_api_token" -EnvVarName "CRYPTOPAY_API_TOKEN"
New-SecretFile -SecretName "cryptopay_api_key" -EnvVarName "CRYPTOPAY_API_KEY"
New-SecretFile -SecretName "cryptopay_webhook_secret" -EnvVarName "CRYPTOPAY_WEBHOOK_SECRET"

Write-Host "All secrets have been created successfully" 