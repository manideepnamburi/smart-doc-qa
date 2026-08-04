# Key-Health-Check.ps1
#
# Tests each configured API key with a minimal, cheap real API call and
# emails an alert if any key has actually been revoked/rotated (an auth
# failure), as opposed to a transient network error. Every run is logged
# regardless of outcome, so you have a history even on quiet days.
#
# Run manually:
#   .\Key-Health-Check.ps1
#
# Run on a schedule: see README.md for Windows Task Scheduler setup.

param(
    [string]$KeysConfigPath = "$PSScriptRoot\keys-to-check.json",
    [string]$EmailConfigPath = "$PSScriptRoot\email-config.json",
    [string]$LogPath = "$PSScriptRoot\key-health-log.txt"
)

$ErrorActionPreference = "Stop"
$timestamp = Get-Date -Format "yyyy-MM-dd HH:mm:ss"

function Write-Log {
    param([string]$Message)
    $line = "[$timestamp] $Message"
    Write-Host $line
    Add-Content -Path $LogPath -Value $line
}

# ── Provider-specific test calls ────────────────────────────────────────────
# Each returns @{ Success = bool; ErrorType = 'Auth' | 'Other' | $null; Message = string }
# ErrorType 'Auth' is what triggers an email alert; 'Other' (network blips,
# rate limits, etc.) is logged but does NOT alert, since that's noise, not
# a revoked key.

function Test-AnthropicKey {
    param([string]$ApiKey, [string]$Model)
    try {
        $null = Invoke-RestMethod -Uri "https://api.anthropic.com/v1/messages" `
            -Method Post `
            -Headers @{ "x-api-key" = $ApiKey; "anthropic-version" = "2023-06-01" } `
            -ContentType "application/json" `
            -Body (@{ model = $Model; max_tokens = 1; messages = @(@{ role = "user"; content = "hi" }) } | ConvertTo-Json)
        return @{ Success = $true; ErrorType = $null; Message = "OK" }
    }
    catch {
        $statusCode = $_.Exception.Response.StatusCode.value__
        if ($statusCode -eq 401) {
            return @{ Success = $false; ErrorType = "Auth"; Message = "401 Unauthorized - key is invalid or revoked" }
        }
        return @{ Success = $false; ErrorType = "Other"; Message = "HTTP $statusCode - $($_.Exception.Message)" }
    }
}

function Test-OpenAIKey {
    param([string]$ApiKey, [string]$Model)
    try {
        $null = Invoke-RestMethod -Uri "https://api.openai.com/v1/chat/completions" `
            -Method Post `
            -Headers @{ "Authorization" = "Bearer $ApiKey" } `
            -ContentType "application/json" `
            -Body (@{ model = $Model; max_tokens = 1; messages = @(@{ role = "user"; content = "hi" }) } | ConvertTo-Json)
        return @{ Success = $true; ErrorType = $null; Message = "OK" }
    }
    catch {
        $statusCode = $_.Exception.Response.StatusCode.value__
        if ($statusCode -eq 401) {
            return @{ Success = $false; ErrorType = "Auth"; Message = "401 Unauthorized - key is invalid or revoked" }
        }
        return @{ Success = $false; ErrorType = "Other"; Message = "HTTP $statusCode - $($_.Exception.Message)" }
    }
}

function Test-AzureOpenAIKey {
    param([string]$ApiKey, [string]$Endpoint, [string]$Deployment)
    try {
        $uri = "$Endpoint/openai/deployments/$Deployment/embeddings?api-version=2023-05-15"
        $null = Invoke-RestMethod -Uri $uri `
            -Method Post `
            -Headers @{ "api-key" = $ApiKey } `
            -ContentType "application/json" `
            -Body (@{ input = "test" } | ConvertTo-Json)
        return @{ Success = $true; ErrorType = $null; Message = "OK" }
    }
    catch {
        $statusCode = $_.Exception.Response.StatusCode.value__
        if ($statusCode -eq 401 -or $statusCode -eq 403) {
            return @{ Success = $false; ErrorType = "Auth"; Message = "$statusCode - key is invalid, revoked, or lacks access" }
        }
        return @{ Success = $false; ErrorType = "Other"; Message = "HTTP $statusCode - $($_.Exception.Message)" }
    }
}

# ── Email alert (provider configurable — currently supports "smtp") ────────

function Send-AlertEmail {
    param(
        [PSCustomObject]$EmailConfig,
        [string]$Subject,
        [string]$Body
    )

    if ($EmailConfig.provider -ne "smtp") {
        Write-Log "ERROR: email provider '$($EmailConfig.provider)' is not supported. Only 'smtp' is implemented."
        return
    }

    $password = [Environment]::GetEnvironmentVariable($EmailConfig.credentialEnvVar)
    if ([string]::IsNullOrEmpty($password)) {
        Write-Log "ERROR: cannot send alert email - env var '$($EmailConfig.credentialEnvVar)' is not set."
        return
    }

    try {
        $smtpClient = New-Object System.Net.Mail.SmtpClient($EmailConfig.smtpHost, $EmailConfig.smtpPort)
        $smtpClient.EnableSsl = $EmailConfig.useSsl
        $smtpClient.UseDefaultCredentials = $false
        $smtpClient.Credentials = New-Object System.Net.NetworkCredential($EmailConfig.fromAddress, $password)

        $mail = New-Object System.Net.Mail.MailMessage
        $mail.From = $EmailConfig.fromAddress
        $mail.To.Add($EmailConfig.toAddress)
        $mail.Subject = $Subject
        $mail.Body = $Body

        $smtpClient.Send($mail)
        Write-Log "Alert email sent to $($EmailConfig.toAddress)."
    }
    catch {
        Write-Log "ERROR: failed to send alert email - $($_.Exception.Message)"
    }
}

# ── Main ─────────────────────────────────────────────────────────────────────

Write-Log "=== Key health check started ==="

$keysConfig = Get-Content $KeysConfigPath -Raw | ConvertFrom-Json
$emailConfig = Get-Content $EmailConfigPath -Raw | ConvertFrom-Json

$failures = @()

foreach ($key in $keysConfig.keysToCheck) {
    if (-not $key.enabled) {
        Write-Log "SKIP: $($key.name) (disabled in config)"
        continue
    }

    $apiKey = [Environment]::GetEnvironmentVariable($key.apiKeyEnvVar)
    if ([string]::IsNullOrEmpty($apiKey)) {
        Write-Log "SKIP: $($key.name) - env var '$($key.apiKeyEnvVar)' is not set"
        continue
    }

    $result = switch ($key.provider) {
        "anthropic" { Test-AnthropicKey -ApiKey $apiKey -Model $key.model }
        "openai" { Test-OpenAIKey -ApiKey $apiKey -Model $key.model }
        "azureopenai" {
            $endpoint = [Environment]::GetEnvironmentVariable($key.endpointEnvVar)
            $deployment = [Environment]::GetEnvironmentVariable($key.deploymentEnvVar)
            Test-AzureOpenAIKey -ApiKey $apiKey -Endpoint $endpoint -Deployment $deployment
        }
        default {
            @{ Success = $false; ErrorType = "Other"; Message = "Unknown provider '$($key.provider)'" }
        }
    }

    if ($result.Success) {
        Write-Log "OK: $($key.name) - key is valid"
    }
    elseif ($result.ErrorType -eq "Auth") {
        Write-Log "AUTH FAILURE: $($key.name) - $($result.Message)"
        $failures += "$($key.name): $($result.Message)"
    }
    else {
        # Network blip, rate limit, etc. — logged, but not treated as a
        # revoked-key alert, since that would create noisy false alarms.
        Write-Log "WARNING (non-auth): $($key.name) - $($result.Message)"
    }
}

if ($failures.Count -gt 0) {
    $subject = "[SmartDocQA] $($failures.Count) API key(s) failed authentication"
    $body = "The following keys failed authentication during today's health check:`n`n" +
            ($failures -join "`n") +
            "`n`nCheck the Anthropic/OpenAI/Azure console and rotate as needed, then update your local .local.ps1 files."
    Send-AlertEmail -EmailConfig $emailConfig -Subject $subject -Body $body
}
else {
    Write-Log "All checked keys are valid. No alert sent."
}

Write-Log "=== Key health check finished ==="
Write-Log ""
