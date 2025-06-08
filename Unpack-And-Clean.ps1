# This script unpacks the new release and cleans up the temporary file.
param(
    [string]$TempPath,
    [string]$DeployPath
)

Write-Host "Unpacking from $TempPath\\release.zip to $DeployPath..."
Expand-Archive -Path "$TempPath\\release.zip" -DestinationPath "$DeployPath" -Force
Remove-Item -Path "$TempPath\\release.zip" -Force
Write-Host "Unpack and cleanup complete."