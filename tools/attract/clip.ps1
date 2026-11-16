# Renders the README's waiting-screen clip: four seconds across a scene cut, with the
# theme's music at the same moment, as docs/media/waiting-screen.mp4 (with sound) and
# docs/media/waiting-screen.webp (the silent animated preview the README shows; GitHub
# only plays video inline when it is uploaded through its web editor).
param(
    [double]$Start = 6.0,
    [double]$Seconds = 4.0,
    [int]$Fps = 30,
    [string]$Ffmpeg = "D:\Apps\msys64\mingw64\bin\ffmpeg.exe"
)
$ErrorActionPreference = "Stop"
$root = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
$work = Join-Path $env:TEMP "lounge-waiting-clip"
if (Test-Path $work) { Remove-Item -Recurse -Force $work }
New-Item -ItemType Directory -Force $work | Out-Null
$media = Join-Path $root "docs\media"
New-Item -ItemType Directory -Force $media | Out-Null

dotnet run --project (Join-Path $root "tools\Preview") -- $work --attract-frames $Start $Seconds $Fps
if ($LASTEXITCODE -ne 0) { throw "rendering the frames failed" }

$inv = [Globalization.CultureInfo]::InvariantCulture
$ss = $Start.ToString($inv); $t = $Seconds.ToString($inv)
$fadeOut = ($Seconds - 0.5).ToString($inv)
$mp4 = Join-Path $media "waiting-screen.mp4"
& $Ffmpeg -y -loglevel error -framerate $Fps -i (Join-Path $work "frame-%04d.png") -ss $ss -t $t -i (Join-Path $work "attract-theme.wav") `
    -vf "scale=1280:720:flags=lanczos,format=yuv420p" -c:v libx264 -crf 22 -preset slow -movflags +faststart `
    -af "afade=t=in:d=0.3,afade=t=out:st=${fadeOut}:d=0.5" -c:a aac -b:a 160k -shortest $mp4
if ($LASTEXITCODE -ne 0) { throw "encoding the mp4 failed" }

$webp = Join-Path $media "waiting-screen.webp"
& $Ffmpeg -y -loglevel error -framerate $Fps -i (Join-Path $work "frame-%04d.png") `
    -vf "fps=15,scale=960:-1:flags=lanczos" -c:v libwebp_anim -quality 70 -loop 0 $webp
if ($LASTEXITCODE -ne 0) { throw "encoding the webp failed" }

Get-Item $mp4, $webp | Select-Object Name, Length
