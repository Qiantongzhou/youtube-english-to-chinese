param([switch]$SelfContained)
$ErrorActionPreference = 'Stop'
$rootPath = Split-Path $PSScriptRoot -Parent
$destination = Join-Path $rootPath 'dist\VideoCN'
$sc = if ($SelfContained) { 'true' } else { 'false' }
dotnet publish (Join-Path $rootPath 'src\VideoCN\VideoCN.csproj') -c Release -r win-x64 --self-contained $sc -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o $destination
if ($LASTEXITCODE -ne 0) { throw '桌面程序发布失败' }
foreach ($name in @('engine','scripts')) {
    Copy-Item -LiteralPath (Join-Path $rootPath $name) -Destination $destination -Recurse -Force
}
Copy-Item -LiteralPath (Join-Path $rootPath 'README.md') -Destination $destination -Force
Write-Output "发布成功：$destination\VideoCN.exe"
