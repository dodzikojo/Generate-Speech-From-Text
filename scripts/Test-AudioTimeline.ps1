param(
    [string]$ServerExecutable = "$env:LOCALAPPDATA\Microsoft\WindowsApps\voicebench-mcp.exe",
    [Parameter(Mandatory=$true)][string]$TestProjectRoot,
    [switch]$KeepFixtureOpen,
    [switch]$Performance,
    [switch]$PendingPreviewOnly,
    [switch]$Trace,
    [switch]$RestoreOnly,
    [switch]$InspectOnly
)
$ErrorActionPreference='Stop'
$attachTimer=[Diagnostics.Stopwatch]::StartNew()
$start=New-Object Diagnostics.ProcessStartInfo
$start.FileName=$ServerExecutable
$start.UseShellExecute=$false
$start.CreateNoWindow=$true
$start.RedirectStandardInput=$true
$start.RedirectStandardOutput=$true
$start.RedirectStandardError=$true
$start.StandardOutputEncoding=New-Object Text.UTF8Encoding($false)
$start.StandardErrorEncoding=New-Object Text.UTF8Encoding($false)
$server=[Diagnostics.Process]::Start($start)
$inputWriter=New-Object IO.StreamWriter($server.StandardInput.BaseStream,(New-Object Text.UTF8Encoding($false)))
$inputWriter.AutoFlush=$true
$stderr=$server.StandardError.ReadToEndAsync()
$script:rpcId=0
$script:passed=0
$previous=$null
$success=$false
function Rpc([string]$method,$parameters) {
    $script:rpcId++
    $inputWriter.WriteLine((@{jsonrpc='2.0';id=$script:rpcId;method=$method;params=$parameters}|ConvertTo-Json -Depth 90 -Compress))
    while($true) {
        $line=$server.StandardOutput.ReadLineAsync()
        if(-not $line.Wait(45000)){throw "MCP timeout: $method"}
        if($null -eq $line.Result){throw "MCP exited: $method"}
        $reply=$line.Result|ConvertFrom-Json
        if($reply.id -eq $script:rpcId){if($reply.error){throw ($reply.error|ConvertTo-Json -Compress)};return $reply.result}
    }
}
function Tool([string]$command,$arguments=@{},[switch]$AllowError) {
    if($Trace){Write-Host "VoiceBench verification: $command"}
    $arguments.requestId='audio-runtime-'+[Guid]::NewGuid().ToString('N')
    $r=(Rpc 'tools/call' @{name='voicebench_'+$command.Replace('.','_');arguments=$arguments}).structuredContent
    if($r.isError -and -not $AllowError){throw "$command : $($r.code) : $($r.message)"}
    return $r
}
function Job($data) {
    for($n=0;$n -lt 180;$n++) {
        $j=(Tool 'jobs.wait' @{jobId=[string]$data.jobId;timeoutMilliseconds=1000}).data
        if($j.status -eq 'Completed'){return $j.result}
        if($j.status -in @('Failed','Interrupted','Cancelled')){throw "Job $($data.jobId): $($j.status) $($j.failureCode) $($j.failureMessage)"}
    }
    throw 'Job exceeded three minutes'
}
function Check([bool]$condition,[string]$message){if(-not $condition){throw $message};$script:passed++}
function Tone([string]$path,[int]$seconds,[double]$frequency) {
    $stream=[IO.File]::Open($path,[IO.FileMode]::CreateNew,[IO.FileAccess]::Write)
    $writer=New-Object IO.BinaryWriter($stream)
    try {
        $bytes=48000*$seconds*2
        $writer.Write([Text.Encoding]::ASCII.GetBytes('RIFF'));$writer.Write([int](36+$bytes));$writer.Write([Text.Encoding]::ASCII.GetBytes('WAVEfmt '));$writer.Write([int]16)
        $writer.Write([int16]1);$writer.Write([int16]1);$writer.Write([int]48000);$writer.Write([int]96000);$writer.Write([int16]2);$writer.Write([int16]16)
        $writer.Write([Text.Encoding]::ASCII.GetBytes('data'));$writer.Write([int]$bytes)
        for($i=0;$i -lt 48000*$seconds;$i++){$writer.Write([int16](1000*[Math]::Sin(2*[Math]::PI*$frequency*$i/48000)))}
    }finally{$writer.Dispose();$stream.Dispose()}
}
function Speech([string]$path,[string]$text) {
    $voice=New-Object -ComObject SAPI.SpVoice
    $file=New-Object -ComObject SAPI.SpFileStream
    try{$file.Open($path,3,$false);$voice.AudioOutputStream=$file;[void]$voice.Speak($text);$file.Close()}
    finally{[void][Runtime.InteropServices.Marshal]::FinalReleaseComObject($file);[void][Runtime.InteropServices.Marshal]::FinalReleaseComObject($voice)}
}
function Invoke-TimelineVerification {
try {
    $null=Rpc 'initialize' @{protocolVersion='2025-11-25';capabilities=@{};clientInfo=@{name='VoiceBench timeline verification';version='1.1'}}
    $inputWriter.WriteLine('{"jsonrpc":"2.0","method":"notifications/initialized"}')
    if($InspectOnly){
        $workspace=(Tool 'workspace.get').data
        $jobs=(Tool 'jobs.list').data
        $diagnostics=(Tool 'diagnostics.get').data
        [pscustomobject]@{ProjectRoot=$workspace.projectRoot;Dirty=$workspace.editor.isDirty;ActiveJobs=@($jobs|Where-Object {$_.status -in @('Queued','InProgress')}).Count;ProcessId=$diagnostics.processId}|ConvertTo-Json -Compress
        return
    }
    if($PendingPreviewOnly){
        $workspace=(Tool 'workspace.get').data
        if($workspace.projectRoot -ne [IO.Path]::GetFullPath($TestProjectRoot) -or $workspace.editor.isDirty){throw 'Open the saved verification fixture before previewing pending narration.'}
        $chapter=(Tool 'chapter.create' @{title='Pending narration verification'}).data
        $pendingId=($chapter.segments|Where-Object {$_.chapterId -eq $chapter.chapterOrder[-1]}|Select-Object -First 1).segmentId
        $null=Tool 'selection.set' @{segmentId=$pendingId}
        $draft=(Tool 'editor.get').data
        if($draft.segmentId -ne $pendingId){throw 'The verification editor did not select the newly created segment.'}
        $null=Tool 'editor.replace' @{expectedRevision=$draft.revision;text="@visual: Show the pending narration card.`n`nThis draft remains visible as unplaced narration until a reviewed recording is available. Its spoken estimate excludes this production guidance."}
        $draft=(Tool 'editor.get').data
        $null=Tool 'editor.save' @{expectedRevision=$draft.revision}
        $null=Tool 'mix.playback.play' @{scope='Compilation'} -AllowError
        $null=Tool 'mix.playback.stop'
        $null=Tool 'navigation.show' @{page='audio'}
        return
    }
    if($RestoreOnly){
        # ReadAllText avoids Windows PowerShell 5.1 serializing Get-Content's
        # provider metadata recursively when this string becomes a JSON argument.
        $restoreRoot=[IO.File]::ReadAllText((Resolve-Path -LiteralPath (Join-Path $TestProjectRoot 'previous-project.txt')).Path)
        $null=Tool 'mix.playback.stop'
        $null=Tool 'project.open' @{projectRoot=$restoreRoot}
        $null=Tool 'project.recent.remove' @{projectRoot=$TestProjectRoot}
        return
    }
    if(Test-Path -LiteralPath $TestProjectRoot){throw 'Use a new, empty test-project path.'}
    $workspace=(Tool 'workspace.get').data
    $attachMilliseconds=$attachTimer.ElapsedMilliseconds
    Check (-not $workspace.editor.isDirty) 'Save the operator draft before running verification.'
    $previous=$workspace.projectRoot
    $tools=Rpc 'tools/list' @{}
    Check (@($tools.tools|Where-Object name -eq 'voicebench_mix_clip_split').Count -eq 1) 'Timeline tools missing'
    $null=Tool 'project.create' @{projectRoot=$TestProjectRoot;name='Audio Timeline Verification'}
    Set-Content -LiteralPath (Join-Path $TestProjectRoot 'previous-project.txt') -Value $previous -NoNewline
    $chapter=(Tool 'chapter.create' @{title='Timeline chapter'}).data
    $segment=$chapter.segments[0].segmentId
    $chapterId=$chapter.segments[0].chapterId
    $null=Tool 'selection.set' @{segmentId=$segment}
    $editor=(Tool 'editor.get').data
    Check ($editor.segmentId -eq $segment) 'Live editor selection differs from requested segment'
    $narration='This recording verifies the shared audio timeline. The original source stays intact.'
    $null=Tool 'editor.replace' @{expectedRevision=$editor.revision;text="@visual: Show the overview.`n`n$narration"}
    $editor=(Tool 'editor.get').data
    $null=Tool 'editor.save' @{expectedRevision=$editor.revision}
    $recording=Join-Path $TestProjectRoot 'recording-fixture.wav'
    $music=Join-Path $TestProjectRoot 'music-fixture.wav'
    Speech $recording $narration
    Tone $music 16 220
    $speech=Job (Tool 'assets.import' @{sourcePath=$recording;segmentId=$segment}).data
    Check ($speech.origin -eq 'ImportedRecording') 'Recorded narration provenance missing'
    $reject=Tool 'render.approve' @{segmentId=$segment;renderId=[string]$speech.renderId} -AllowError
    Check $reject.isError 'Recorded narration approval lacked transcript confirmation'
    $null=Tool 'render.approve' @{segmentId=$segment;renderId=[string]$speech.renderId;confirmedTranscript=$true}
    $bed=Job (Tool 'assets.import' @{sourcePath=$music}).data
    $null=Job (Tool 'assets.prepare' @{assetId=[string]$bed.assetId}).data
    $wave=(Tool 'assets.waveform' @{assetId=[string]$bed.assetId;offset=0;count=64}).data
    Check ($wave.peaks.Count -eq 64) 'Waveform page was not populated'
    $mix=(Tool 'mix.get' @{scope='Chapter';chapterId=$chapterId}).data
    $trackId=[Guid]::NewGuid().ToString()
    $track=@{trackId=$trackId;name='Continuous music';kind='Music';gainDb=-20;duckUnderNarration=$true;order=3}
    $mix=(Tool 'mix.track.create' @{scope='Chapter';chapterId=$chapterId;expectedRevision=$mix.revision;track=$track}).data
    $clipId=[Guid]::NewGuid().ToString()
    $clip=@{clipId=$clipId;trackId=$trackId;assetId=[string]$bed.assetId;assetSha256=$bed.sha256;name='Music bed';startFrame=0;sourceStartFrame=0;durationFrames=768000;fadeInSeconds=.5;fadeOutSeconds=1}
    $stale=$mix.revision
    $mix=(Tool 'mix.clip.add' @{scope='Chapter';chapterId=$chapterId;expectedRevision=$mix.revision;clip=$clip}).data
    $conflict=Tool 'mix.clip.move' @{scope='Chapter';chapterId=$chapterId;expectedRevision=$stale;clipId=$clipId;startSeconds=1} -AllowError
    Check ($conflict.code -eq 'mix.revision-conflict') 'Stale timeline revision was accepted'
    $mix=(Tool 'mix.clip.split' @{scope='Chapter';chapterId=$chapterId;expectedRevision=$mix.revision;clipId=$clipId;splitSeconds=8}).data
    Check (@($mix.clips|Where-Object assetId -eq $bed.assetId).Count -eq 2) 'Audio split lost a half'
    $draftBefore=(Tool 'editor.get').data
    $dirtyText=$draftBefore.text+"`n`n@note: Keep this unsaved cue through mix history."
    $null=Tool 'editor.replace' @{expectedRevision=$draftBefore.revision;text=$dirtyText}
    $null=Tool 'mix.history.undo'
    $mix=(Tool 'mix.get' @{scope='Chapter';chapterId=$chapterId}).data
    Check (@($mix.clips|Where-Object assetId -eq $bed.assetId).Count -eq 1) 'Mix undo failed'
    $dirtyAfter=(Tool 'editor.get').data
    Check ($dirtyAfter.isDirty -and $dirtyAfter.text -eq $dirtyText) 'Mix undo changed the unsaved editor draft'
    $null=Tool 'mix.history.redo'
    $dirtyAfter=(Tool 'editor.get').data
    Check ($dirtyAfter.isDirty -and $dirtyAfter.text -eq $dirtyText) 'Mix redo changed the unsaved editor draft'
    $null=Tool 'editor.save' @{expectedRevision=$dirtyAfter.revision}
    $null=Tool 'mix.playback.play' @{scope='Chapter';chapterId=$chapterId}
    $null=Tool 'mix.playback.pause'
    $null=Tool 'mix.playback.seek' @{seconds=2}
    $playback=(Tool 'mix.playback.get').data
    Check ([Math]::Abs($playback.positionSeconds-2) -lt .1) 'Device-clock seek failed'
    $null=Tool 'mix.playback.loop' @{enabled=$true;seconds=1;endSeconds=4}
    $null=Tool 'mix.playback.loop' @{enabled=$false}
    $null=Tool 'mix.playback.stop'
    $eligibility=(Tool 'mix.validate' @{scope='Chapter';chapterId=$chapterId}).data
    Check $eligibility.canExport 'Approved-source chapter mix is blocked'
    $export=Job (Tool 'mix.export' @{scope='Chapter';chapterId=$chapterId;name='Timeline runtime';mp3=$true;stems=$true}).data
    Check (Test-Path -LiteralPath (Join-Path $TestProjectRoot $export.masterRelativePath)) 'Mixed WAV missing'
    Check (Test-Path -LiteralPath (Join-Path $TestProjectRoot $export.completionRelativePath)) 'Export completion record missing'
    Check ((Get-Content -LiteralPath (Join-Path $TestProjectRoot $export.transcriptRelativePath) -Raw).Contains('original source stays intact')) 'Aligned transcript missing'
    Check ([IO.File]::ReadAllText((Join-Path $TestProjectRoot $export.cueSheetRelativePath)).Contains('Keep this unsaved cue through mix history.')) 'Latest cue-only guidance was not exported'
    $mix=(Tool 'mix.get' @{scope='Chapter';chapterId=$chapterId}).data
    $null=Tool 'mix.edit' @{scope='Chapter';chapterId=$chapterId;expectedRevision=$mix.revision;edits=@(@{operation='SetEndMarker';durationSeconds=22})}
    $compilation=(Tool 'mix.get' @{scope='Compilation'}).data
    $overlayTrack=[Guid]::NewGuid().ToString()
    $compilation=(Tool 'mix.track.create' @{scope='Compilation';expectedRevision=$compilation.revision;track=@{trackId=$overlayTrack;name='Compilation music';kind='Music';gainDb=-30;duckUnderNarration=$true}}).data
    $overlay=@{clipId=[Guid]::NewGuid().ToString();trackId=$overlayTrack;assetId=[string]$bed.assetId;assetSha256=$bed.sha256;name='Chapter anchored loop';startFrame=0;sourceStartFrame=0;durationFrames=2112000;loop=$true;anchor=@{kind='ChapterStart';chapterId=$chapterId;offsetSeconds=0}}
    $null=Tool 'mix.clip.add' @{scope='Compilation';expectedRevision=$compilation.revision;clip=$overlay}
    $compilationExport=Job (Tool 'mix.export' @{scope='Compilation';name='Compilation runtime'}).data
    Check ([Math]::Abs($compilationExport.durationSeconds-22) -lt .02) 'Looped compilation overlay extended the chapter end marker'
    Check ([IO.File]::ReadAllText((Join-Path $TestProjectRoot $compilationExport.cueSheetRelativePath)).Contains('Keep this unsaved cue through mix history.')) 'Compilation guidance missing'
    $compilationPlan=(Tool 'mix.plan' @{scope='Compilation';count=1}).data
    Check ($compilationPlan.totalChapterRegions -eq 1 -and $compilationPlan.chapterRegions[0].chapterId -eq $chapterId -and $compilationPlan.chapterRegions[0].durationFrames -eq 1056000) 'Compilation chapter-region timing missing'
    $performanceResult=$null
    if($Performance) {
        $mix=(Tool 'mix.get' @{scope='Chapter';chapterId=$chapterId}).data
        $edits=New-Object 'Collections.Generic.List[object]'
        $trackIds=New-Object 'Collections.Generic.List[string]'
        $trackIds.Add($trackId)
        for($n=0;$n -lt 30;$n++) {
            $id=[Guid]::NewGuid().ToString();$trackIds.Add($id)
            $edits.Add(@{operation='AddTrack';track=@{trackId=$id;name="Performance $n";kind='Music';gainDb=-32;order=4+$n}})
        }
        for($n=0;$n -lt 1997;$n++) {
            $edits.Add(@{operation='AddClip';clip=@{clipId=[Guid]::NewGuid().ToString();trackId=$trackIds[$n%31];assetId=[string]$bed.assetId;assetSha256=$bed.sha256;name="Cached clip $n";startFrame=[long]([Math]::Round($n*7190.0/1997*48000));sourceStartFrame=0;durationFrames=240000}})
        }
        $edits.Add(@{operation='SetEndMarker';durationSeconds=7200})
        $timer=[Diagnostics.Stopwatch]::StartNew()
        for($offset=0;$offset -lt $edits.Count;$offset+=1000) {
            $count=[Math]::Min(1000,$edits.Count-$offset)
            $mix=(Tool 'mix.edit' @{scope='Chapter';chapterId=$chapterId;expectedRevision=$mix.revision;edits=$edits.GetRange($offset,$count).ToArray()}).data
        }
        $commitMs=$timer.ElapsedMilliseconds
        Check ($mix.tracks.Count -eq 32 -and $mix.clips.Count -eq 2000) 'Large fixture dimensions differ'
        $timer.Restart();$null=Tool 'mix.playback.play' @{scope='Chapter';chapterId=$chapterId};$loadMs=$timer.ElapsedMilliseconds
        $timer.Restart();$null=Tool 'mix.playback.seek' @{seconds=3600};$seekMs=$timer.ElapsedMilliseconds
        Start-Sleep -Milliseconds 1200
        $status=(Tool 'mix.playback.get').data
        Check ($status.positionSeconds -ge 3600) 'Large-project source seek failed'
        Check ($status.underruns -eq 0) 'Large-project playback underrun'
        $null=Tool 'mix.playback.stop'
        $diagnostics=(Tool 'diagnostics.get').data
        $memory=(Get-Process -Id $diagnostics.processId).WorkingSet64
        $performanceResult=@{tracks=32;clips=2000;logicalDurationSeconds=7200;commitMilliseconds=$commitMs;playbackLoadMilliseconds=$loadMs;seekMilliseconds=$seekMs;sampledPlaybackSeconds=1.2;underruns=$status.underruns;appWorkingSetMiB=[Math]::Round($memory/1MB,1)}
    }
    $before=(Tool 'mix.get' @{scope='Chapter';chapterId=$chapterId}).data
    $null=Tool 'project.close'
    $null=Tool 'project.open' @{projectRoot=$TestProjectRoot}
    $after=(Tool 'mix.get' @{scope='Chapter';chapterId=$chapterId}).data
    Check ($after.revision -eq $before.revision) 'Timeline revision did not persist'
    $null=Tool 'navigation.show' @{page='audio'}
    $success=$true
    [pscustomobject]@{Passed=$script:passed;Tools=$tools.tools.Count;AttachMilliseconds=$attachMilliseconds;Project=$TestProjectRoot;Master=$export.masterRelativePath;Compilation=$compilationExport.masterRelativePath;Performance=$performanceResult}|ConvertTo-Json -Depth 8 -Compress
}finally {
    if($previous -and (-not $KeepFixtureOpen -or $RestoreOnly -or -not $success)) {
        try{$null=Tool 'mix.playback.stop';$null=Tool 'project.open' @{projectRoot=$previous};$null=Tool 'project.recent.remove' @{projectRoot=$TestProjectRoot}}catch{Write-Warning $_.Exception.Message}
    }
    $inputWriter.Close()
    if(-not $server.WaitForExit(5000)){$server.Kill()}
    $server.Dispose()
}
}
Invoke-TimelineVerification
