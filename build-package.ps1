param(
    [string]$Configuration = "Release"
)

$ErrorActionPreference = "Stop"
$root = [System.IO.Path]::GetFullPath((Split-Path -Parent $MyInvocation.MyCommand.Path))
$artifacts = [System.IO.Path]::GetFullPath((Join-Path $root "artifacts"))
$publish = [System.IO.Path]::GetFullPath((Join-Path $artifacts "publish"))
$zip = [System.IO.Path]::GetFullPath((Join-Path $artifacts "LanFileTransfer-win-x64.zip"))

if (-not $publish.StartsWith($artifacts + [System.IO.Path]::DirectorySeparatorChar, [System.StringComparison]::OrdinalIgnoreCase)) {
    throw "Publish path must stay inside the workspace artifacts directory."
}

if (Test-Path $publish) {
    Remove-Item -LiteralPath $publish -Recurse -Force
}
if (Test-Path $zip) {
    Remove-Item -LiteralPath $zip -Force
}

dotnet publish (Join-Path $root "src\LanFileTransfer.App\LanFileTransfer.App.csproj") `
    -c $Configuration `
    -r win-x64 `
    --self-contained true `
    -o $publish

New-Item -ItemType Directory -Force -Path (Join-Path $publish "Data") | Out-Null
New-Item -ItemType Directory -Force -Path (Join-Path $publish "Config") | Out-Null
New-Item -ItemType Directory -Force -Path (Join-Path $publish "Logs") | Out-Null
Copy-Item -LiteralPath (Join-Path $root "README.md") -Destination (Join-Path $publish "README.md")
Copy-Item -LiteralPath (Join-Path $root "docs\USER-GUIDE.zh-CN.md") -Destination (Join-Path $publish "USER-GUIDE.zh-CN.md")
Compress-Archive -Path (Join-Path $publish "*") -DestinationPath $zip -CompressionLevel Optimal
Write-Host "Package created: $zip"
