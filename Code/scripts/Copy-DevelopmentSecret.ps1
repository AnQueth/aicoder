[CmdletBinding()]
param(
    [ValidateNotNullOrEmpty()]
    [string]$Name = 'Parameters:api-key'
)

$ErrorActionPreference = 'Stop'

Write-Output 'Phase 1/2: Reading the AppHost user-secrets configuration.'
$projectPath = Join-Path $PSScriptRoot '..\src\Collaborator.AppHost\Collaborator.AppHost.csproj'
[xml]$project = Get-Content -LiteralPath $projectPath -Raw
$secretsId = [string]$project.Project.PropertyGroup.UserSecretsId
if ([string]::IsNullOrWhiteSpace($secretsId)) {
    throw 'The AppHost project does not define a UserSecretsId.'
}
if ([string]::IsNullOrWhiteSpace($env:APPDATA)) {
    throw 'APPDATA is not available. Run this script in your Windows user session.'
}

$secretsPath = Join-Path $env:APPDATA "Microsoft\UserSecrets\$secretsId\secrets.json"
if (-not (Test-Path -LiteralPath $secretsPath -PathType Leaf)) {
    throw 'The AppHost user-secrets file does not exist. Configure user secrets first.'
}
$secrets = Get-Content -LiteralPath $secretsPath -Raw | ConvertFrom-Json
$property = $secrets.PSObject.Properties[$Name]
if ($null -eq $property -or [string]::IsNullOrWhiteSpace([string]$property.Value)) {
    throw "User secret '$Name' is missing or empty."
}

Write-Output 'Phase 2/2: Copying the selected value to the clipboard without printing it.'
Set-Clipboard -Value ([string]$property.Value)
Write-Output "Copied '$Name'. Paste it where needed, then clear your clipboard when finished."
Write-Output 'Summary: processed=1 succeeded=1 failed=0 skipped=0.'
