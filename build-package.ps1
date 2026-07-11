param(
    [string]$Configuration = "Release"
)

$ErrorActionPreference = "Stop"
$root = [System.IO.Path]::GetFullPath((Split-Path -Parent $MyInvocation.MyCommand.Path))
$artifacts = [System.IO.Path]::GetFullPath((Join-Path $root "artifacts"))
$publish = [System.IO.Path]::GetFullPath((Join-Path $artifacts "publish"))
$zip = [System.IO.Path]::GetFullPath((Join-Path $artifacts "LFT-win-x64.zip"))
$zipHashFile = "$zip.sha256"

if (-not $publish.StartsWith($artifacts + [System.IO.Path]::DirectorySeparatorChar, [System.StringComparison]::OrdinalIgnoreCase)) {
    throw "Publish path must stay inside the workspace artifacts directory."
}

if (Test-Path $publish) {
    Remove-Item -LiteralPath $publish -Recurse -Force
}
if (Test-Path $zip) {
    Remove-Item -LiteralPath $zip -Force
}
if (Test-Path $zipHashFile) { Remove-Item -LiteralPath $zipHashFile -Force }

dotnet test (Join-Path $root "LanFileTransfer.sln") -c $Configuration
if ($LASTEXITCODE -ne 0) { throw "自动测试失败，已停止构建。" }

dotnet publish (Join-Path $root "src\LanFileTransfer.App\LanFileTransfer.App.csproj") `
    -c $Configuration `
    -r win-x64 `
    --self-contained true `
    -p:PublishSingleFile=true `
    -p:UseAppHost=true `
    -p:AssemblyName=内网文件传输工具 `
    -o $publish
if ($LASTEXITCODE -ne 0) { throw "发布失败，已停止打包。" }

Get-ChildItem -LiteralPath $publish -Recurse -File | Where-Object { $_.Extension -eq '.pdb' } | Remove-Item -Force

$releaseReadme = Get-Content -LiteralPath (Join-Path $root "README.md") -Raw -Encoding utf8
$releaseReadme = $releaseReadme.Replace("docs/USER-GUIDE.zh-CN.md", "USER-GUIDE.zh-CN.md")
Set-Content -LiteralPath (Join-Path $publish "README.md") -Value $releaseReadme -Encoding utf8
Copy-Item -LiteralPath (Join-Path $root "docs\USER-GUIDE.zh-CN.md") -Destination (Join-Path $publish "USER-GUIDE.zh-CN.md")
Copy-Item -LiteralPath (Join-Path $root "CHANGELOG.md") -Destination (Join-Path $publish "CHANGELOG.md")
$buildTime = Get-Date -Format "yyyy-MM-dd HH:mm:ss K"
@("软件名称：内网文件传输工具", "版本：1.1.1", "构建时间：$buildTime", "平台：Windows x64", "发布类型：自包含完整绿色版", "默认端口：28080") | Set-Content -LiteralPath (Join-Path $publish "VERSION.txt") -Encoding utf8
$checksums = Get-ChildItem -LiteralPath $publish -File | Get-FileHash -Algorithm SHA256 | ForEach-Object { "$($_.Hash.ToLowerInvariant())  $($_.Name)" }
$checksums | Set-Content -LiteralPath (Join-Path $publish "SHA256SUMS.txt") -Encoding ascii
if ((Get-ChildItem -LiteralPath $publish -Filter *.exe).Name -ne "内网文件传输工具.exe") { throw "发布目录只能包含中文 EXE：内网文件传输工具.exe" }
if ((Get-ChildItem -LiteralPath $publish -Recurse -File | Where-Object { $_.Extension -eq '.pdb' -or $_.Name -like '*Tests*.dll' }).Count -gt 0) { throw "发布目录不允许包含 PDB 或测试 DLL。" }
if (-not (Test-Path (Join-Path $publish "USER-GUIDE.zh-CN.md"))) { throw "发布包缺少使用说明。" }
Compress-Archive -Path (Join-Path $publish "*") -DestinationPath $zip -CompressionLevel Optimal
$zipHash = (Get-FileHash -LiteralPath $zip -Algorithm SHA256).Hash.ToLowerInvariant()
"$zipHash  $(Split-Path -Leaf $zip)" | Set-Content -LiteralPath $zipHashFile -Encoding ascii
Write-Host "Package created: $zip"

