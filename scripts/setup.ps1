param([ValidateSet('Core','Full')][string]$Mode = 'Full', [string]$Root = (Split-Path $PSScriptRoot -Parent))
$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'
[Console]::OutputEncoding = [System.Text.UTF8Encoding]::new($false)
$env:PYTHONUTF8 = '1'
$Root = [System.IO.Path]::GetFullPath($Root)
$toolsPath = Join-Path $Root 'tools'
$cachePath = Join-Path $Root '.runtime\downloads'
New-Item -ItemType Directory -Force -Path $toolsPath,$cachePath | Out-Null

function Fetch([string]$Url, [string]$Destination) {
    if (Test-Path -LiteralPath $Destination) { return }
    Write-Output "下载 $Url"
    $partial = $Destination + '.partial'
    & curl.exe --fail --location --retry 3 --connect-timeout 30 --max-time 7200 --output $partial $Url
    if ($LASTEXITCODE -ne 0) { throw "下载失败：$Url。检查网络后再次安装即可。" }
    Move-Item -LiteralPath $partial -Destination $Destination -Force
}
function Native([string]$Exe, [string[]]$Arguments) {
    & $Exe @Arguments
    if ($LASTEXITCODE -ne 0) { throw "命令失败（$LASTEXITCODE）：$Exe" }
}

if (-not (Test-Path (Join-Path $toolsPath 'uv.exe'))) {
    $zip = Join-Path $cachePath 'uv.zip'
    Fetch 'https://github.com/astral-sh/uv/releases/latest/download/uv-x86_64-pc-windows-msvc.zip' $zip
    Expand-Archive -LiteralPath $zip -DestinationPath (Join-Path $toolsPath 'uv-package') -Force
    $uvFile = Get-ChildItem (Join-Path $toolsPath 'uv-package') -Filter uv.exe -Recurse | Select-Object -First 1
    Copy-Item -LiteralPath $uvFile.FullName -Destination (Join-Path $toolsPath 'uv.exe') -Force
}
$uv = Join-Path $toolsPath 'uv.exe'
$env:UV_PYTHON_INSTALL_DIR = Join-Path $Root '.runtime\python'
$env:UV_CACHE_DIR = Join-Path $Root '.runtime\uv-cache'
$python = Join-Path $Root '.runtime\ai\Scripts\python.exe'
if (-not (Test-Path $python)) {
    Native $uv @('python','install','3.12')
    Native $uv @('venv','--python','3.12', (Join-Path $Root '.runtime\ai'))
}

if (-not (Test-Path (Join-Path $toolsPath 'ffmpeg.exe'))) {
    $zip = Join-Path $cachePath 'ffmpeg.zip'
    Fetch 'https://www.gyan.dev/ffmpeg/builds/ffmpeg-release-essentials.zip' $zip
    Expand-Archive -LiteralPath $zip -DestinationPath (Join-Path $toolsPath 'ffmpeg-package') -Force
    foreach ($name in @('ffmpeg.exe','ffprobe.exe')) {
        $binary = Get-ChildItem (Join-Path $toolsPath 'ffmpeg-package') -Filter $name -Recurse | Select-Object -First 1
        if (-not $binary) { throw "FFmpeg 压缩包缺少 $name" }
        Copy-Item -LiteralPath $binary.FullName -Destination (Join-Path $toolsPath $name) -Force
    }
}
Fetch 'https://github.com/yt-dlp/yt-dlp/releases/latest/download/yt-dlp.exe' (Join-Path $toolsPath 'yt-dlp.exe')
if (-not (Test-Path (Join-Path $toolsPath 'deno.exe'))) {
    $zip = Join-Path $cachePath 'deno.zip'
    Fetch 'https://github.com/denoland/deno/releases/latest/download/deno-x86_64-pc-windows-msvc.zip' $zip
    Expand-Archive -LiteralPath $zip -DestinationPath $toolsPath -Force
}
Native $uv @('pip','install','--python',$python,'requests>=2.32,<3','soundfile>=0.12,<0.14','numpy>=1.26,<2')
if ($Mode -eq 'Full') {
    if (-not (Test-Path (Join-Path $toolsPath 'ollama\ollama.exe'))) {
        $zip = Join-Path $cachePath 'ollama.zip'
        Fetch 'https://github.com/ollama/ollama/releases/latest/download/ollama-windows-amd64.zip' $zip
        Expand-Archive -LiteralPath $zip -DestinationPath (Join-Path $toolsPath 'ollama') -Force
    }
    Write-Output '安装 PyTorch CUDA 12.4 与 AI 依赖（首次需要下载数 GB）…'
    Native $uv @('pip','install','--python',$python,'torch==2.5.1','torchaudio==2.5.1','--index-url','https://download.pytorch.org/whl/cu124')
    Native $uv @('pip','install','--python',$python,'-r',(Join-Path $Root 'engine\requirements.txt'),'--constraint',(Join-Path $Root 'engine\constraints.txt'))
}
Native (Join-Path $toolsPath 'ffmpeg.exe') @('-version')
Native (Join-Path $toolsPath 'yt-dlp.exe') @('--version')
Write-Output "环境安装完成：$Mode。AI 模型将在首次处理时自动下载，也可以在界面中提前下载。"
