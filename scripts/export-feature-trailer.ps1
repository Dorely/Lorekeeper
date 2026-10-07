[CmdletBinding()]
param(
    [ValidatePattern('^v[0-9]+$')]
    [string]$Version = 'v1',
    [string]$CaptureDirectory = ".artifacts/trailer/$Version/raw",
    [string]$OutputDirectory = ".artifacts/trailer/$Version/export"
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$repositoryRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$artifactRoot = Join-Path $repositoryRoot '.artifacts/trailer'

function Resolve-TrailerDirectory([string]$Path)
{
    $resolved = [System.IO.Path]::GetFullPath([System.IO.Path]::Combine($repositoryRoot, $Path))
    $prefix = $artifactRoot.TrimEnd([System.IO.Path]::DirectorySeparatorChar) + [System.IO.Path]::DirectorySeparatorChar
    if (-not $resolved.StartsWith($prefix, [System.StringComparison]::OrdinalIgnoreCase))
    {
        throw 'Trailer inputs and generated outputs must be below .artifacts/trailer.'
    }
    return $resolved
}

function Invoke-TrailerCommand([string]$Command, [string[]]$Arguments)
{
    & $Command @Arguments
    if ($LASTEXITCODE -ne 0) { throw "$Command failed with exit code $LASTEXITCODE." }
}

function Get-TrailerMediaMetadata([string]$Path)
{
    $probeText = & ffprobe -v error -show_entries 'stream=codec_type,codec_name,codec_tag_string,profile,width,height,pix_fmt,field_order,r_frame_rate,avg_frame_rate,nb_frames,sample_rate,channels:format=duration' -of json $Path
    if ($LASTEXITCODE -ne 0) { throw "Cannot inspect exported media: $Path" }
    return ($probeText -join "`n") | ConvertFrom-Json
}

function Assert-TrailerVideo([string]$Path, [int]$Width, [int]$Height, [long]$MaximumBytes, [int]$Seconds)
{
    $metadata = Get-TrailerMediaMetadata $Path
    $video = @($metadata.streams | Where-Object { $_.codec_type -ceq 'video' })
    $audio = @($metadata.streams | Where-Object { $_.codec_type -ceq 'audio' })
    $duration = [double]::Parse($metadata.format.duration, [Globalization.CultureInfo]::InvariantCulture)
    if ($video.Count -ne 1 -or $audio.Count -ne 1 -or @($metadata.streams).Count -ne 2 -or
        $video[0].codec_name -cne 'h264' -or $video[0].codec_tag_string -cne 'avc1' -or
        $video[0].profile -cne 'High' -or $video[0].pix_fmt -cne 'yuv420p' -or
        $video[0].width -ne $Width -or $video[0].height -ne $Height -or
        $video[0].field_order -cne 'progressive' -or $video[0].r_frame_rate -cne '30/1' -or
        $video[0].avg_frame_rate -cne '30/1' -or [int]$video[0].nb_frames -ne ($Seconds * 30) -or
        $audio[0].codec_name -cne 'aac' -or $audio[0].profile -cne 'LC' -or
        [int]$audio[0].sample_rate -ne 48000 -or $audio[0].channels -ne 2 -or
        $duration -lt $Seconds -or $duration -gt ($Seconds + 0.1) -or
        (Get-Item -LiteralPath $Path).Length -gt $MaximumBytes)
    {
        throw "Exported trailer metadata or size does not match its delivery contract: $Path"
    }
}

$captureRoot = Resolve-TrailerDirectory $CaptureDirectory
$exportRoot = Resolve-TrailerDirectory $OutputDirectory
if ($captureRoot -eq $exportRoot) { throw 'Capture and export directories must differ.' }
foreach ($command in @('ffmpeg', 'ffprobe', 'git'))
{
    if (-not (Get-Command $command -ErrorAction SilentlyContinue)) { throw "$command is required." }
}
$manifestPath = Join-Path $captureRoot 'captures.json'
if (-not (Test-Path -LiteralPath $manifestPath -PathType Leaf)) { throw 'Reviewed captures.json is required before exporting real footage.' }
$manifest = [System.IO.File]::ReadAllText($manifestPath) | ConvertFrom-Json
if ($manifest.sourceCommit -cnotmatch '^[0-9a-f]{40}$' -or
    $manifest.qaAccepted -isnot [bool] -or $manifest.qaAccepted -ne $true -or
    $manifest.reviewed -isnot [bool] -or $manifest.reviewed -ne $true)
{
    throw 'Footage must identify an accepted commit and completed QA/privacy review.'
}
if ([string]::IsNullOrWhiteSpace($manifest.syntheticProject) -or $manifest.qaEvidence -cnotmatch '^docs/evidence/[A-Za-z0-9._-]+\.md$')
{
    throw 'The synthetic project and committed QA evidence must be identified.'
}
$sourceCommit = [string]$manifest.sourceCommit
& git -C $repositoryRoot merge-base --is-ancestor $sourceCommit HEAD
if ($LASTEXITCODE -ne 0)
{
    throw 'The accepted application source commit must exist and be an ancestor of HEAD.'
}
$applicationInputs = @('Lorekeeper', 'Lorekeeper.Press', 'eng', 'tools/semantic-editor', 'tools/distribution',
    'tools/msix/Build-WindowsMsix.ps1', 'tools/msix/Package.appxmanifest.xml',
    'scripts/build-windows-release.ps1', 'scripts/build-linux-release.ps1', 'scripts/build-linux-release-wsl.ps1',
    'scripts/build-macos-release.ps1',
    'global.json', 'Directory.Build.props', 'NuGet.Config', 'licenses', 'LICENSE', 'THIRD-PARTY-NOTICES.txt')
& git -C $repositoryRoot diff --quiet $sourceCommit HEAD -- @applicationInputs
if ($LASTEXITCODE -ne 0) { throw 'Runtime or packaging inputs changed after the accepted application source commit.' }
& git -C $repositoryRoot diff --quiet HEAD -- @applicationInputs
if ($LASTEXITCODE -ne 0) { throw 'Runtime or packaging inputs have uncommitted changes.' }
$untrackedInputs = @(& git -C $repositoryRoot ls-files --others --exclude-standard -- @applicationInputs)
if ($LASTEXITCODE -ne 0 -or $untrackedInputs.Count -gt 0) { throw 'Runtime or packaging inputs contain untracked files.' }
$qaPath = Join-Path $repositoryRoot $manifest.qaEvidence
$qaBlob = (& git -C $repositoryRoot rev-parse "HEAD:$($manifest.qaEvidence)").Trim()
if ($LASTEXITCODE -ne 0) { throw 'QA evidence must be committed before export.' }
$workingQaBlob = (& git -C $repositoryRoot hash-object -- $qaPath).Trim()
if ($LASTEXITCODE -ne 0 -or $qaBlob -cne $workingQaBlob) { throw 'QA evidence has uncommitted changes.' }
$qaText = [System.IO.File]::ReadAllText($qaPath)
$qaSource = [regex]::Match($qaText, '(?m)^Tested source commit: ([0-9a-f]{40})\s*$')
if (-not $qaSource.Success -or $qaSource.Groups[1].Value -cne $sourceCommit -or
    $manifest.qaEvidenceSha256 -cnotmatch '^[0-9a-f]{64}$' -or
    (Get-FileHash -LiteralPath $qaPath -Algorithm SHA256).Hash.ToLowerInvariant() -cne $manifest.qaEvidenceSha256)
{
    throw 'The reviewed QA evidence must name the accepted application source commit and match its manifest SHA-256.'
}
# v1 predates per-clip lengths in the manifest; later versions list each clip's whole seconds in edit order.
$sequence = [ordered]@{}
if ($Version -ceq 'v1')
{
    $sequence = [ordered]@{ 'intro.mp4'=5; 'outline.mp4'=8; 'writing.mp4'=12; 'sources.mp4'=9; 'design.mp4'=11; 'export.mp4'=10; 'outro.mp4'=5 }
}
else
{
    foreach ($clip in @($manifest.clips))
    {
        if ($clip.file -cnotmatch '^[a-z0-9-]+\.mp4$' -or $sequence.Contains($clip.file) -or -not ($clip.seconds -is [int] -or $clip.seconds -is [long]) -or $clip.seconds -lt 1)
        {
            throw "Each clip needs a unique file name and whole seconds: $($clip.file)"
        }
        $sequence[$clip.file] = [int]$clip.seconds
    }
}
$durationSeconds = [int](($sequence.Values | Measure-Object -Sum).Sum)
if ($sequence.Count -lt 2 -or $durationSeconds -gt 90) { throw 'A trailer needs at least two clips and at most 90 seconds.' }
if (@($manifest.clips).Count -ne $sequence.Count) { throw "Exactly $($sequence.Count) reviewed clips are required." }
foreach ($entry in $sequence.GetEnumerator())
{
    $records = @($manifest.clips | Where-Object { $_.file -ceq $entry.Key })
    if ($records.Count -ne 1) { throw "Missing or duplicate capture record: $($entry.Key)" }
    $clipPath = Join-Path $captureRoot $entry.Key
    if ($records[0].sha256 -cnotmatch '^[0-9a-f]{64}$' -or
        -not (Test-Path -LiteralPath $clipPath -PathType Leaf) -or
        (Get-FileHash -LiteralPath $clipPath -Algorithm SHA256).Hash.ToLowerInvariant() -cne $records[0].sha256)
    {
        throw "Capture hash mismatch: $($entry.Key)"
    }
    $probeText = & ffprobe -v error -select_streams v:0 -show_entries stream=width,height,r_frame_rate -show_entries format=duration -of json $clipPath
    if ($LASTEXITCODE -ne 0) { throw "Cannot inspect capture: $($entry.Key)" }
    $probe = ($probeText -join "`n") | ConvertFrom-Json
    if (@($probe.streams).Count -ne 1 -or $probe.streams[0].width -ne 1920 -or
        $probe.streams[0].height -ne 1080 -or $probe.streams[0].r_frame_rate -cne '30/1' -or
        [double]::Parse($probe.format.duration, [Globalization.CultureInfo]::InvariantCulture) -lt $entry.Value)
    {
        throw "Capture must be 1080p30 and at least $($entry.Value) seconds: $($entry.Key)"
    }
}

New-Item -ItemType Directory -Path $exportRoot -Force | Out-Null
$prefix = "lorekeeper-$Version"
$captionName = "$prefix.en.vtt"
$captionSource = Join-Path $repositoryRoot "media/trailer/$captionName"
if ((Get-Item -LiteralPath $captionSource).Length -ge 50000000 -or
    -not ([System.IO.File]::ReadAllText($captionSource)).StartsWith('WEBVTT', [StringComparison]::Ordinal))
{
    throw 'The caption source must be a WebVTT file below 50 MB.'
}
$titlePath = Join-Path $repositoryRoot "media/trailer/$prefix.title.txt"
if (-not (Test-Path -LiteralPath $titlePath -PathType Leaf)) { $titlePath = Join-Path $repositoryRoot 'media/trailer/title.txt' }
$title = [System.IO.File]::ReadAllText($titlePath).Trim()
if ([string]::IsNullOrWhiteSpace($title) -or $title.Length -gt 255 -or $title -match '[\r\n]')
{
    throw 'The Store trailer needs a single-line title of at most 255 characters.'
}
Copy-Item -LiteralPath $captionSource -Destination (Join-Path $exportRoot $captionName) -Force
$concatLines = [System.Collections.Generic.List[string]]::new()
$index = 0
foreach ($entry in $sequence.GetEnumerator())
{
    $segmentName = 'segment-{0:D2}.mp4' -f $index++
    Invoke-TrailerCommand ffmpeg @('-hide_banner','-loglevel','warning','-y','-i',(Join-Path $captureRoot $entry.Key),'-t',[string]$entry.Value,
        '-an','-vf','setsar=1,setfield=prog,fps=30','-c:v','libx264','-preset','veryfast','-profile:v','high','-pix_fmt','yuv420p','-b:v','50M','-maxrate','50M','-bufsize','100M',
        '-g','15','-keyint_min','15','-sc_threshold','0','-bf','2','-x264-params','open-gop=0:cabac=1:b-adapt=0:nal-hrd=vbr',(Join-Path $exportRoot $segmentName))
    $concatLines.Add("file '$segmentName'")
}
$concatPath = Join-Path $exportRoot 'segments.txt'
[System.IO.File]::WriteAllLines($concatPath, $concatLines, [System.Text.UTF8Encoding]::new($false))

# Original four-chord, slowly modulated sine score; no samples or external audio.
# A minor -> F major -> C major -> G major, one chord each five seconds.
$rootFrequency = 'if(lt(mod(t,20),5),220,if(lt(mod(t,20),10),174.614,if(lt(mod(t,20),15),130.813,195.998)))'
$thirdRatio = 'if(lt(mod(t,20),5),1.189207,1.259921)'
$score = "0.025*(sin(2*PI*($rootFrequency)*t)+0.65*sin(2*PI*($rootFrequency)*($thirdRatio)*t)+0.45*sin(2*PI*($rootFrequency)*1.498307*t))*(0.75+0.25*sin(2*PI*0.1*t))*min(1,min(mod(t,5)/0.05,(5-mod(t,5))/0.05))"
$audioSource = "aevalsrc=exprs='$score|$score':s=48000:d=$durationSeconds"
$masterPath = Join-Path $exportRoot "$prefix-store.mp4"
Push-Location $exportRoot
try
{
    Invoke-TrailerCommand ffmpeg @('-hide_banner','-loglevel','warning','-y','-f','concat','-safe','1','-i','segments.txt','-f','lavfi','-i',$audioSource,
        '-map','0:v:0','-map','1:a:0','-t',[string]$durationSeconds,'-vf',"setfield=prog,fps=30,subtitles=$($captionName):force_style='Fontname=Segoe UI,Fontsize=25,Outline=2,Shadow=0,Alignment=2,MarginV=40'",
        '-af',"afade=t=in:d=2,afade=t=out:st=$($durationSeconds - 3):d=3",'-c:v','libx264','-preset','veryfast','-profile:v','high','-pix_fmt','yuv420p','-tag:v','avc1',
        '-b:v','50M','-maxrate','50M','-bufsize','100M','-g','15','-keyint_min','15','-sc_threshold','0','-bf','2','-x264-params','open-gop=0:cabac=1:b-adapt=0:nal-hrd=vbr',
        '-c:a','aac','-profile:a','aac_low','-ar','48000','-ac','2','-b:a','384k','-movflags','+faststart','-use_editlist','0',$masterPath)
    $githubPath = Join-Path $exportRoot "$prefix-github.mp4"
    # Without edit lists the master's video starts after its B-frame delay, so a fixed-length cut drops its last
    # frames. Take every frame and restamp both streams from zero to keep a constant 30/1 rate.
    # The GitHub copy must stay under 9.5 MB, so a longer trailer gets a lower video bitrate.
    $githubVideoKbps = [Math]::Min(1000, [int][Math]::Floor(9000000 * 8 / 1000 / $durationSeconds) - 160)
    $videoArguments = @('-hide_banner','-loglevel','warning','-y','-i',$masterPath,'-t',[string]($durationSeconds + 0.1),'-frames:v',[string]($durationSeconds * 30),'-vf','setpts=N/30/TB,scale=1280:720','-af','asetpts=N/SR/TB','-c:v','libx264','-preset','slow',
        '-profile:v','high','-pix_fmt','yuv420p','-b:v',"$($githubVideoKbps)k",'-passlogfile','github-pass')
    $nullTarget = if ([Environment]::OSVersion.Platform -eq [PlatformID]::Win32NT) { 'NUL' } else { '/dev/null' }
    Invoke-TrailerCommand ffmpeg ($videoArguments + @('-pass','1','-an','-f','null',$nullTarget))
    Invoke-TrailerCommand ffmpeg ($videoArguments + @('-pass','2','-c:a','aac','-profile:a','aac_low','-b:a','128k','-ar','48000','-ac','2','-movflags','+faststart','-use_editlist','0',$githubPath))
    Assert-TrailerVideo -Path $masterPath -Width 1920 -Height 1080 -MaximumBytes 2000000000 -Seconds $durationSeconds
    Assert-TrailerVideo -Path $githubPath -Width 1280 -Height 720 -MaximumBytes 9500000 -Seconds $durationSeconds
    $posterPath = Join-Path $exportRoot "$prefix-poster.png"
    Invoke-TrailerCommand ffmpeg @('-hide_banner','-loglevel','warning','-y','-ss','2','-i',$masterPath,'-frames:v','1','-update','1',$posterPath)
    $poster = Get-TrailerMediaMetadata $posterPath
    if (@($poster.streams).Count -ne 1 -or $poster.streams[0].codec_name -cne 'png' -or
        $poster.streams[0].width -ne 1920 -or $poster.streams[0].height -ne 1080)
    {
        throw 'The Store thumbnail must be a 1920x1080 PNG.'
    }
    $outputs = @("$prefix-store.mp4","$prefix-github.mp4","$prefix-poster.png",$captionName) | ForEach-Object {
        $outputPath = Join-Path $exportRoot $_
        [ordered]@{ file=$_; bytes=(Get-Item -LiteralPath $outputPath).Length; sha256=(Get-FileHash -LiteralPath $outputPath -Algorithm SHA256).Hash.ToLowerInvariant() }
    }
    $provenance = [ordered]@{
        sourceCommit=$sourceCommit; exportCommit=(& git -C $repositoryRoot rev-parse HEAD).Trim(); syntheticProject=$manifest.syntheticProject
        qaEvidence=$manifest.qaEvidence; qaEvidenceSha256=$manifest.qaEvidenceSha256; title=$title
        captures=$manifest.clips; durationSeconds=$durationSeconds; audio='Original mathematical sine score; A minor/F major/C major/G major; no samples'
        captionsSha256=(Get-FileHash -LiteralPath $captionSource -Algorithm SHA256).Hash.ToLowerInvariant()
        ffmpeg=(& ffmpeg -version | Select-Object -First 1); recipe='scripts/export-feature-trailer.ps1'; outputs=$outputs
    }
    [System.IO.File]::WriteAllText((Join-Path $exportRoot "$prefix-provenance.json"), ($provenance | ConvertTo-Json -Depth 8) + "`n", [System.Text.UTF8Encoding]::new($false))
}
finally { Pop-Location }
Write-Host "Exported trailer materials to $exportRoot. Review both videos and the poster before copying compact deliverables into Git."
