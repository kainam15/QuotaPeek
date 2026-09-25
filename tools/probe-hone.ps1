param(
    [string]$BaseUrl = 'https://hone.vvvv.ee',
    [switch]$WithoutKey,
    [switch]$SaveResponse
)
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Net.Http
$uri = [Uri]$BaseUrl
if ($uri.Scheme -ne 'https' -or $uri.UserInfo) { throw 'Use a HTTPS URL without embedded credentials.' }
$root = $BaseUrl.TrimEnd('/') -replace '/v1$', ''
$apiSecret = $null
if (-not $WithoutKey) {
    $apiSecret = $env:HONE_API_KEY
    if (-not $apiSecret) {
        $secure = Read-Host 'Hone API key (input hidden; not written to disk)' -AsSecureString
        $pointer = [Runtime.InteropServices.Marshal]::SecureStringToBSTR($secure)
        try { $apiSecret = [Runtime.InteropServices.Marshal]::PtrToStringBSTR($pointer) }
        finally { [Runtime.InteropServices.Marshal]::ZeroFreeBSTR($pointer) }
    }
}
$handler = [System.Net.Http.HttpClientHandler]::new()
$handler.AllowAutoRedirect = $false
$handler.UseCookies = $false
$client = [System.Net.Http.HttpClient]::new($handler)
$client.Timeout = [TimeSpan]::FromSeconds(25)
$output = @{}
try {
    foreach ($endpoint in @('/api/status', '/v1/dashboard/billing/subscription', '/v1/dashboard/billing/usage')) {
        $request = [System.Net.Http.HttpRequestMessage]::new([System.Net.Http.HttpMethod]::Get, $root + $endpoint)
        if ($apiSecret -and $endpoint -ne '/api/status') { $request.Headers.Authorization = [System.Net.Http.Headers.AuthenticationHeaderValue]::new('Bearer', $apiSecret) }
        $response = $client.SendAsync($request).GetAwaiter().GetResult()
        try {
            $body = $response.Content.ReadAsStringAsync().GetAwaiter().GetResult()
            if ($apiSecret) { $body = $body.Replace($apiSecret, '[REDACTED]') }
            $json = $body | ConvertFrom-Json
            if ($endpoint -eq '/api/status') {
                $output.status = @{http_status=[int]$response.StatusCode; system_name=$json.data.system_name; version=$json.data.version; quota_display_type=$json.data.quota_display_type; quota_per_unit=$json.data.quota_per_unit}
            } elseif ($endpoint.EndsWith('subscription')) {
                $output.subscription = @{http_status=[int]$response.StatusCode; response=$json}
            } else {
                $output.usage = @{http_status=[int]$response.StatusCode; response=$json}
            }
        } finally { $response.Dispose(); $request.Dispose() }
    }
    if ($output.subscription.http_status -eq 200 -and $output.usage.http_status -eq 200 -and $null -ne $output.subscription.response.hard_limit_usd -and $null -ne $output.usage.response.total_usage) {
        $total = [decimal]$output.subscription.response.hard_limit_usd
        $used = [decimal]$output.usage.response.total_usage / 100
        $remaining = if ($total -ge 100000000) { $null } else { $total - $used }
        $output.normalized = @{remaining=$remaining; used=$used; total=$total; currency=$output.status.quota_display_type; usage_period='cumulative'; unit_check='Verify these values against the Hone console.'}
    }
    $text = $output | ConvertTo-Json -Depth 12
    if ($SaveResponse) {
        $directory = Join-Path (Split-Path $PSScriptRoot) '.artifacts\provider-probes'
        New-Item -ItemType Directory -Path $directory -Force | Out-Null
        $path = Join-Path $directory ('hone-' + (Get-Date -Format 'yyyyMMdd-HHmmss') + '.json')
        [IO.File]::WriteAllText($path, $text)
        Write-Output ('Saved: ' + $path)
    }
    Write-Output $text
} finally { $apiSecret = $null; $client.Dispose() }
