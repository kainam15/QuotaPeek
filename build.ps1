param([switch]$Publish, [switch]$Check, [string]$DotnetPath)
$ErrorActionPreference = 'Stop'
Set-Location -LiteralPath $PSScriptRoot
$candidates = @($DotnetPath, $env:QUOTAPEEK_DOTNET, (Join-Path $PSScriptRoot '.tools\dotnet\dotnet.exe'), 'D:\my_project\AltTabLock\.tools\dotnet\dotnet.exe')
$dotnet = $candidates | Where-Object { $_ -and (Test-Path -LiteralPath $_) } | Select-Object -First 1
if (-not $dotnet) { $dotnet = (Get-Command dotnet -ErrorAction Stop).Source }
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
if ($Check) {
    & $dotnet run --project tests\QuotaPeek.Checks -c Release
    if ($LASTEXITCODE -ne 0) { throw 'Checks failed.' }
} elseif (-not $Publish) {
    & $dotnet build src\QuotaPeek\QuotaPeek.csproj -c Release
    if ($LASTEXITCODE -ne 0) { throw 'Build failed.' }
}
if ($Publish) {
    $publishTarget = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot 'dist\QuotaPeek.exe'))
    $running = Get-Process -Name QuotaPeek -ErrorAction SilentlyContinue | Where-Object { $_.Path -eq $publishTarget }
    if ($running) { throw 'QuotaPeek is running from dist. Exit it from the tray before publishing; no process was stopped.' }
    & $dotnet publish src\QuotaPeek\QuotaPeek.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:IncludeAllContentForSelfExtract=true -p:EnableCompressionInSingleFile=true -o dist
    if ($LASTEXITCODE -ne 0) { throw 'Publish failed.' }
    Get-Item -LiteralPath 'dist\QuotaPeek.exe' | Select-Object FullName,Length
}
