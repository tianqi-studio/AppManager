# Trims WindowsAppSDK extra language resource folders after build/publish.
# Keeps zh-CN / en only. Handles both output layouts:
#   net8.0 top-level (CLI build) and win-x64 subfolder (VS build).
param(
    [Parameter(Mandatory = $true)]
    [string]$OutDir
)

$OutDir = $OutDir.Trim('"', '\')
$keep = @('zh-CN', 'en-us', 'en-US', 'zh-Hans')
$roots = @($OutDir, (Join-Path $OutDir 'win-x64')) | Where-Object { Test-Path -LiteralPath $_ }
$removed = 0

foreach ($root in $roots) {
    Get-ChildItem -LiteralPath $root -Directory -ErrorAction SilentlyContinue | Where-Object {
        # Match en-us, zh-CN, and script/region codes such as az-Latn-AZ or ca-Es-VALENCIA.
        $_.Name -match '^[a-zA-Z]{2,3}(-[a-zA-Z0-9]{2,8}){0,2}$' -and $_.Name -notin $keep
    } | ForEach-Object {
        Remove-Item -LiteralPath $_.FullName -Recurse -Force -Confirm:$false -ErrorAction SilentlyContinue
        $removed++
    }
}

Write-Host "trim-languages: removed $removed language folder(s) under $OutDir"
