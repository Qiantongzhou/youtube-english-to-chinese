$ErrorActionPreference = 'Stop'
$rootPath = Split-Path $PSScriptRoot -Parent
$folder = Join-Path $rootPath 'artifacts\ai-smoke'
New-Item -ItemType Directory -Force -Path $folder | Out-Null
Add-Type -AssemblyName System.Speech
$synth = [System.Speech.Synthesis.SpeechSynthesizer]::new()
$synth.SelectVoice('Microsoft Zira Desktop')
$synth.Rate = -1
$synth.SetOutputToWaveFile((Join-Path $folder 'english.wav'))
$prompt = [System.Speech.Synthesis.PromptBuilder]::new()
$prompt.AppendText('Welcome to this video. Today, we are going to learn something new.')
$prompt.AppendBreak([TimeSpan]::FromSeconds(2))
$prompt.AppendText('You can change the words before creating the Chinese voice.')
$synth.Speak($prompt)
$synth.Dispose()
& (Join-Path $rootPath 'tools\ffmpeg.exe') -nostdin -v error -y -f lavfi -i 'color=c=0x173c35:s=1280x720:r=25:d=20' -i (Join-Path $folder 'english.wav') -c:v libx264 -pix_fmt yuv420p -c:a aac -af apad -t 20 (Join-Path $folder 'english-test.mp4')
if ($LASTEXITCODE -ne 0) { throw '测试视频生成失败' }
$request = @{ source=(Join-Path $folder 'english-test.mp4'); output_dir=$folder; mode='quality'; voice='Serena'; resolution='1080'; device='cuda'; dub=$true; background=$true; burn_subtitles=$true; review_first=$true; glossary='' }
$request | ConvertTo-Json | Set-Content (Join-Path $folder 'request.json') -Encoding UTF8
Write-Output '英文语音测试视频已生成。'
