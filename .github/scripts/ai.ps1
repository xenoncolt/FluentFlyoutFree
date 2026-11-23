# Shared AI helper for the workflows. Dot-source it, then call Invoke-AiChat.
#
# Works with any OpenAI-compatible chat API. Settings (see installer/README.md):
#   AI_API_KEY   repository secret: the API key (by default a free Google Gemini key)
#   AI_ENDPOINT  optional repository variable: another API's ".../chat/completions" URL
#   AI_MODEL     optional repository variable: the model to use

function Test-AiConfigured {
    return [bool]$env:AI_API_KEY
}

# Sends one request and returns @{ Text; Model }. Tries the next model if one fails, and tries everything a
# second time a minute later if the failures look temporary (busy, rate-limited, network). Throws if nothing worked.
function Invoke-AiChat {
    param(
        [Parameter(Mandatory = $true)][string] $System,
        [Parameter(Mandatory = $true)][string] $Prompt,
        [int] $TimeoutSec = 300
    )

    if (-not $env:AI_API_KEY) { throw 'The AI_API_KEY secret is not set (see installer/README.md).' }

    # Default: Google Gemini (free tier) through its OpenAI-compatible endpoint. "gemini-flash-latest"
    # always points to Google's current Flash model; the others are fallbacks.
    $endpoint = if ($env:AI_ENDPOINT) { $env:AI_ENDPOINT } else { 'https://generativelanguage.googleapis.com/v1beta/openai/chat/completions' }
    $models = if ($env:AI_MODEL) { @($env:AI_MODEL) } else { @('gemini-flash-latest', 'gemini-3.8-flash', 'gemini-3.5-flash-lite') }
    $headers = @{ Authorization = "Bearer $env:AI_API_KEY" }
    $failures = @()

    for ($pass = 1; $pass -le 2; $pass++) {
        $temporary = $false
        foreach ($model in $models) {
            $body = @{
                model    = $model
                messages = @(
                    @{ role = 'system'; content = $System },
                    @{ role = 'user'; content = $Prompt }
                )
            } | ConvertTo-Json -Depth 5

            # read the raw answer and parse it here, so an unexpected answer is shown in the log
            # instead of failing with an unclear error
            $status = 0; $raw = ''
            try {
                $response = Invoke-WebRequest -Method Post -Uri $endpoint -Headers $headers -UseBasicParsing `
                    -ContentType 'application/json; charset=utf-8' -Body ([Text.Encoding]::UTF8.GetBytes($body)) -TimeoutSec $TimeoutSec
                $status = [int]$response.StatusCode
                $raw = "$($response.Content)"
            } catch {
                if ($_.Exception.Response) { $status = [int]$_.Exception.Response.StatusCode }
                $raw = if ($_.ErrorDetails.Message) { $_.ErrorDetails.Message } else { $_.Exception.Message }
            }

            $text = ''
            try {
                $json = $raw | ConvertFrom-Json
                if ($json.choices) { $text = "$($json.choices[0].message.content)".Trim() }
            } catch { }

            if ($status -lt 400 -and $text) {
                Write-Host "AI answer from $model"
                return [pscustomobject]@{ Text = $text; Model = $model }
            }

            $snippet = if ($raw.Length -gt 600) { $raw.Substring(0, 600) + '...' } else { $raw }
            Write-Host "$model failed (HTTP $status): $snippet"
            $failures += "$model (HTTP $status)"
            if ($status -eq 0 -or $status -eq 429 -or $status -ge 500) { $temporary = $true }
        }

        if ($pass -eq 1 -and $temporary) {
            Write-Host 'Trying the AI again in 60 seconds...'
            Start-Sleep -Seconds 60
        } else {
            break
        }
    }

    throw "The AI didn't answer: $($failures -join ', ')."
}
