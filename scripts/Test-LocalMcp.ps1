param(
    [Parameter(Mandatory = $true)][string]$ServerExecutable,
    [Parameter(Mandatory = $true)][string]$TestProjectRoot,
    [switch]$EnsureToolchain
)
$ErrorActionPreference = 'Stop'
$start = New-Object System.Diagnostics.ProcessStartInfo
$start.FileName = $ServerExecutable
$start.UseShellExecute = $false
$start.CreateNoWindow = $true
$start.RedirectStandardInput = $true
$start.RedirectStandardOutput = $true
$start.RedirectStandardError = $true
$start.StandardInputEncoding = New-Object System.Text.UTF8Encoding($false)
$start.StandardOutputEncoding = New-Object System.Text.UTF8Encoding($false)
$start.StandardErrorEncoding = New-Object System.Text.UTF8Encoding($false)
$server = [System.Diagnostics.Process]::Start($start)
$errors = $server.StandardError.ReadToEndAsync()
$script:messageId = 0
$script:assertions = 0
$previousRoot = $null
function Invoke-Mcp([string]$method, $parameters) {
    $script:messageId++
    $request = @{ jsonrpc = '2.0'; id = $script:messageId; method = $method; params = $parameters }
    $server.StandardInput.WriteLine(($request | ConvertTo-Json -Depth 80 -Compress))
    while ($true) {
        $read = $server.StandardOutput.ReadLineAsync()
        if (-not $read.Wait(45000)) { throw "Timed out waiting for MCP method $method" }
        if ($null -eq $read.Result) { throw "MCP exited during $method" }
        $response = $read.Result | ConvertFrom-Json
        if ($response.id -eq $script:messageId) {
            if ($null -ne $response.error) { throw ($response.error | ConvertTo-Json -Compress) }
            return $response.result
        }
    }
}
function Invoke-VoiceBench([string]$name, $arguments = @{}, [switch]$AllowError) {
    $arguments.requestId = 'runtime-' + [Guid]::NewGuid().ToString('N')
    $response = Invoke-Mcp 'tools/call' @{ name = 'voicebench_' + $name.Replace('.', '_'); arguments = $arguments }
    $result = $response.structuredContent
    if ($result.isError -and -not $AllowError) { throw "$name failed: $($result.code): $($result.message)" }
    return $result
}
function Assert-True([bool]$condition, [string]$message) {
    if (-not $condition) { throw $message }
    $script:assertions++
}
function Await-VoiceBenchJob($data) {
    if (-not $data.jobId) { return $data }
    for ($attempt = 0; $attempt -lt 180; $attempt++) {
        $job = (Invoke-VoiceBench 'jobs.wait' @{ jobId = [string]$data.jobId; timeoutMilliseconds = 1000 }).data
        if ($job.status -eq 'Completed') { return $job.result }
        if ($job.status -in @('Failed', 'Cancelled', 'Interrupted')) { throw "Runtime job failed: $($job.failureCode)" }
    }
    throw 'The runtime job did not finish within three minutes.'
}
function Write-TestTone([string]$path) {
    $stream = [IO.File]::Open($path, [IO.FileMode]::CreateNew, [IO.FileAccess]::Write)
    $writer = New-Object IO.BinaryWriter($stream)
    try {
        $writer.Write([Text.Encoding]::ASCII.GetBytes('RIFF')); $writer.Write([int]48036)
        $writer.Write([Text.Encoding]::ASCII.GetBytes('WAVEfmt ')); $writer.Write([int]16)
        $writer.Write([int16]1); $writer.Write([int16]1); $writer.Write([int]24000)
        $writer.Write([int]48000); $writer.Write([int16]2); $writer.Write([int16]16)
        $writer.Write([Text.Encoding]::ASCII.GetBytes('data')); $writer.Write([int]48000)
        for ($sample = 0; $sample -lt 24000; $sample++) {
            $writer.Write([int16](3000 * [Math]::Sin(2 * [Math]::PI * 440 * $sample / 24000)))
        }
    } finally { $writer.Dispose(); $stream.Dispose() }
}
try {
    $initialized = Invoke-Mcp 'initialize' @{ protocolVersion = '2025-11-25'; capabilities = @{}; clientInfo = @{ name = 'VoiceBench installed runtime verification'; version = '1.0' } }
    $server.StandardInput.WriteLine('{"jsonrpc":"2.0","method":"notifications/initialized"}')
    $tools = Invoke-Mcp 'tools/list' @{}
    Assert-True ($tools.tools.Count -ge 90) 'The complete tool catalog was not exposed.'
    $resources = Invoke-Mcp 'resources/list' @{}
    Assert-True ($resources.resources.Count -ge 7) 'The expected workspace resources were not exposed.'
    $active = Invoke-VoiceBench 'project.get'
    $previousRoot = $active.data.projectRoot
    if ($active.data.project.name -eq 'MCP Runtime Verification') {
        $oldDraft = (Invoke-VoiceBench 'editor.get').data
        Write-Host ('Previous fixture text: ' + ($oldDraft.text | ConvertTo-Json -Compress))
        if ($oldDraft.isDirty) { Invoke-VoiceBench 'editor.reload' @{ expectedRevision = $oldDraft.revision } | Out-Null }
        $recent = (Invoke-VoiceBench 'project.recent').data
        $previousRoot = ($recent | Where-Object { $_.name -ne 'MCP Runtime Verification' } | Select-Object -First 1).projectRoot
    }
    $created = Invoke-VoiceBench 'project.create' @{ projectRoot = $TestProjectRoot; name = 'MCP Runtime Verification' }
    Assert-True ($created.data.project.name -eq 'MCP Runtime Verification') 'Project creation failed.'
    $chapter = Invoke-VoiceBench 'chapter.create' @{ title = 'MCP chapter' }
    $segmentId = $chapter.data.segments[0].segmentId
    Invoke-VoiceBench 'selection.set' @{ segmentId = $segmentId } | Out-Null
    $draft = (Invoke-VoiceBench 'editor.get').data
    $text = "@visual: Show the overview.`n`nThis narration was written through local MCP. Unicode: café, coordination, and teamwork."
    $edited = (Invoke-VoiceBench 'editor.replace' @{ expectedRevision = $draft.revision; text = $text }).data
    Assert-True ($edited.text -eq $text) ("The live editor did not receive the complete text. Actual=" + ($edited.text | ConvertTo-Json -Compress) + "; Expected=" + ($text | ConvertTo-Json -Compress))
    $conflict = Invoke-VoiceBench 'editor.replace' @{ expectedRevision = $draft.revision; text = 'Must never overwrite newer text.' } -AllowError
    Assert-True ([bool]$conflict.isError) 'A stale editor revision overwrote a newer draft.'
    $direction = (Invoke-VoiceBench 'directions.set' @{ expectedRevision = $edited.revision; directions = 'Calm and clear, with a brief pause between ideas.' }).data
    Assert-True ($direction.directions -match 'Calm and clear') 'Direction editing failed.'
    $saved = (Invoke-VoiceBench 'editor.save' @{ expectedRevision = $direction.revision }).data
    Assert-True (-not $saved.isDirty) 'Saving left a dirty draft.'
    $disk = (Invoke-VoiceBench 'script.get' @{ segmentId = $segmentId }).data
    Assert-True ($disk.authoringSha256 -ne $null) 'The saved script hash was not returned.'
    $providers = (Invoke-VoiceBench 'providers.list').data
    Assert-True ($null -ne $providers) 'Provider catalog was empty.'
    $toolchain = (Invoke-VoiceBench 'toolchain.status').data
    if (-not $toolchain.installed -and $EnsureToolchain) {
        Await-VoiceBenchJob (Invoke-VoiceBench 'toolchain.ensure').data | Out-Null
        $toolchain = (Invoke-VoiceBench 'toolchain.status').data
    }
    if ($toolchain.installed) {
        $tone = Join-Path $TestProjectRoot '04-generated-audio/runtime-tone.wav'
        Write-TestTone $tone
        $audio = (Invoke-VoiceBench 'artifacts.audio-preview' @{ relativePath = '04-generated-audio/runtime-tone.wav'; startSeconds = 0; durationSeconds = 1 }).data
        Assert-True ($audio.mimeType -eq 'audio/mpeg') 'The bounded audio preview was not returned.'
        $playback = (Invoke-VoiceBench 'playback.play' @{ relativePath = '04-generated-audio/runtime-tone.wav' }).data
        Assert-True ([bool]$playback.hasAudio) 'The transport did not load the audio fixture.'
        Invoke-VoiceBench 'playback.pause' | Out-Null
        Invoke-VoiceBench 'playback.seek' @{ seconds = 0.2 } | Out-Null
        Invoke-VoiceBench 'playback.stop' | Out-Null
    }
    Invoke-VoiceBench 'presets.save' @{ preset = @{ presetId = 'mcp-runtime'; name = 'Runtime voice'; providerId = 'openai'; modelId = 'gpt-4o-mini-tts'; voice = 'marin' } } | Out-Null
    Invoke-VoiceBench 'presets.default' @{ presetId = 'mcp-runtime' } | Out-Null
    $presets = (Invoke-VoiceBench 'presets.list').data
    Assert-True ($presets.defaultVoicePresetId -eq 'mcp-runtime') 'The project voice did not persist.'
    $assembly = (Invoke-VoiceBench 'assembly.get').data
    Assert-True (-not $assembly.canBuild) 'Assembly accepted a project without approved takes.'
    $pack = Await-VoiceBenchJob (Invoke-VoiceBench 'exports.create' @{ name = 'runtime-production'; scope = 'Compilation' }).data
    Assert-True ($pack.productionScriptRelativePath -ne $null) 'Production exports were not created.'
    $history = (Invoke-VoiceBench 'history.get').data
    Assert-True ($history.transactions.Count -ge 2) 'Durable chapter and script history was not recorded.'
    Invoke-VoiceBench 'history.undo' | Out-Null
    Invoke-VoiceBench 'history.redo' | Out-Null
    Invoke-VoiceBench 'navigation.show' @{ page = 'settings' } | Out-Null
    $control = (Invoke-VoiceBench 'control.get').data
    Assert-True ($null -ne $control) 'Local control status is unavailable.'
    [pscustomobject]@{ Passed = $script:assertions; ToolCount = $tools.tools.Count; ResourceCount = $resources.resources.Count; TestProjectRoot = $TestProjectRoot } | ConvertTo-Json
}
finally {
    try {
        $current = (Invoke-VoiceBench 'project.get').data
        if ($current.projectRoot -eq $TestProjectRoot) {
            $currentEditor = (Invoke-VoiceBench 'editor.get').data
            if ($currentEditor.isDirty) { Invoke-VoiceBench 'editor.reload' @{ expectedRevision = $currentEditor.revision } | Out-Null }
        }
        if ($previousRoot) { Invoke-VoiceBench 'project.open' @{ projectRoot = $previousRoot } | Out-Null }
        else { Invoke-VoiceBench 'project.close' | Out-Null }
        $fixtureParent = [IO.Path]::GetFullPath((Split-Path -Parent $TestProjectRoot)).TrimEnd('\') + '\'
        foreach ($entry in (Invoke-VoiceBench 'project.recent').data) {
            if ($entry.name -eq 'MCP Runtime Verification' -and
                [IO.Path]::GetFullPath($entry.projectRoot).StartsWith($fixtureParent, [StringComparison]::OrdinalIgnoreCase)) {
                Invoke-VoiceBench 'project.recent.remove' @{ projectRoot = $entry.projectRoot } | Out-Null
            }
        }
    } catch { Write-Warning 'The previous project could not be restored automatically.' }
    $server.StandardInput.Close()
    if (-not $server.WaitForExit(5000)) { $server.Kill() }
    $server.Dispose()
}
