param($Context)
# Real-client regression sweep for the shared ability keys: each case gives the actor a role or kit, presses
# the shared keys with real input and records which consumer handled each press (ServerKeybinds press trace)
# plus a screenshot of the ability line. Run with --bindings tests/client/ability-regression-bindings.json
# (every shared key bound). Results land in ability-regression.json; the scenario does not stop on a miss.
$ErrorActionPreference = 'Stop'
$E = $Context.Evidence
$actor = $Context.Actor.id
$results = [Collections.Generic.List[object]]::new()

function Read-Logs {
    $text = ''
    foreach ($file in @(Get-ChildItem -LiteralPath (Join-Path $E 'server-logs') -Recurse -File -ErrorAction SilentlyContinue)) {
        $stream = [IO.FileStream]::new($file.FullName, [IO.FileMode]::Open, [IO.FileAccess]::Read, [IO.FileShare]::ReadWrite)
        try { $text += [IO.StreamReader]::new($stream).ReadToEnd() } finally { $stream.Dispose() }
    }
    return $text
}

function Press([string]$Id, [int[]]$Keys, [int]$Hold = 3, [int]$Frames = 90) {
    Invoke-LabInput @{ id = $Id; frames = $Frames; inputFrames = $Hold; keys = $Keys; capture = $true } |
        ConvertTo-Json -Depth 6 | Set-Content "$E\press-$Id.json" -Encoding utf8
}

function Run-Case([string]$Name, [scriptblock]$Setup, [scriptblock]$Act, [string[]]$Expect) {
    $before = (Read-Logs).Length
    $setupReply = & $Setup
    Start-Sleep -Seconds 4
    Invoke-LabScreenshot -Name "$Name-ready" | Out-Null
    & $Act
    Start-Sleep -Seconds 2
    Invoke-LabScreenshot -Name "$Name-after" | Out-Null
    Start-Sleep -Seconds 1
    $log = (Read-Logs)
    $segment = if ($log.Length -gt $before) { $log.Substring($before) } else { '' }
    $lines = @($segment -split "`n" | Where-Object { $_ -match 'ability key|routing (press|release)|press latched|not handled' } |
        ForEach-Object { ($_ -split 'Trace: ')[-1].Trim() })
    $missing = @($Expect | Where-Object { $segment -notmatch [regex]::Escape($_) })
    $results.Add([pscustomobject]@{ case = $Name; setup = "$setupReply"; trace = $lines; expected = $Expect; missing = $missing; passed = ($missing.Count -eq 0) })
    Write-Host "$Name : $(if ($missing.Count) { 'MISSING ' + ($missing -join ' | ') } else { 'ok' })"
}

function Sub([string]$Force, [string[]]$Prefer) {
    $usage = "$(Invoke-LabServer "/reinforcements sub $Force")"
    $roles = @()
    if ($usage -match '<([^<>]+)>\s*\[playerId\]') { $roles = $Matches[1] -split '\|' }
    $role = @($Prefer | Where-Object { $_ -in $roles })[0]
    if (-not $role) { $role = $roles[0] }
    return "$role :: $(Invoke-LabServer "/reinforcements sub $Force $role $actor")"
}

Invoke-LabServer '/keybinds trace on' | Out-File "$E\sk-trace.txt" -Encoding utf8
Invoke-LabServer '/forcestart' | Out-File "$E\forcestart.txt" -Encoding utf8
$deadline = (Get-Date).AddSeconds(30)
do { Start-Sleep -Milliseconds 500; $time = Invoke-LabServer '/roundtime' } while (($time -notmatch 'Round time') -and (Get-Date) -lt $deadline)

$rs = "handled by 'ReinforcementsSystem'"
Run-Case 'serpent' { Sub 'serpents_hand' @() } { Press 'serpent-z' @(122); Press 'serpent-x' @(120) } @("ability key Ability1 (player $actor) $rs")
Run-Case 'goc-airborne' { Sub 'goc_airborne' @() } { Press 'goc-jet' @(32, 98) -Hold 60 -Frames 120; Press 'goc-z' @(122) } @("binding='喷射背包推进'", "ability key Ability1 (player $actor) $rs")
Run-Case 'chaos' { Sub 'chaos' @('hermetic', 'hermes') } { Press 'chaos-z' @(122); Start-Sleep -Seconds 3; Press 'chaos-ctrl-z' @(306, 122) } @("ability key Ability1 (player $actor) $rs")
Run-Case 'mtf-medic' { Sub 'mtf' @('medic', 'corpsman') } { Press 'medic-hold' @(122) -Hold 120 -Frames 150 } @("ability key Ability1 (player $actor) $rs", "routing release to owner='Ability Keys' binding='技能 1'")
# Delta-4's slots follow grant order (command radio or snapshot recorder first); the radio uses native controls.
Run-Case 'delta4' { Sub 'delta4' @() } { Press 'delta4-z' @(122) -Hold 150 -Frames 180; Press 'delta4-x' @(120) -Hold 150 -Frames 180 } @($rs)
Run-Case 'mcd' { Sub 'mcd' @() } { Press 'mcd-z' @(122); Press 'mcd-primary' @(323) } @("ability key")
Run-Case 'qrt-no-abilities' { Sub 'qrt' @() } { Press 'qrt-z' @(122); Press 'qrt-primary' @(323) } @("ability key Ability1 (player $actor) not handled")

Run-Case 'scp173-t5' { "$(Invoke-LabServer "/forcerole $actor Scp173")"; Start-Sleep -Seconds 4; "$(Invoke-LabServer "/scptiers force $actor 5")" } { Press 'scp173-z' @(122) } @("ability key Ability1 (player $actor) handled by 'ScpTiers'")
Run-Case 'scp106-t3' { "$(Invoke-LabServer "/forcerole $actor Scp106")"; Start-Sleep -Seconds 4; "$(Invoke-LabServer "/scptiers force $actor 3")" } { Press 'scp106-x' @(120); Start-Sleep -Seconds 4; Press 'scp106-x2' @(120) } @("ability key Ability2 (player $actor) handled by 'ScpTiers'")
Run-Case 'scp049' { "$(Invoke-LabServer "/forcerole $actor Scp049")" } { Press 'scp049-z' @(122) } @("ability key Ability1 (player $actor) handled by 'ScpTiers'")
Run-Case 'scp0492' { "$(Invoke-LabServer "/forcerole $actor Scp0492")" } { Press 'scp0492-z' @(122) } @("ability key Ability1 (player $actor) handled by 'ScpTiers'")
Run-Case 'scp999' { "$(Invoke-LabServer "/scp999 $actor force")"; Start-Sleep -Seconds 6 } { Press 'scp999-x' @(120) -Frames 150 } @("ability key Ability2 (player $actor) handled by 'SCP999'")

$results | ConvertTo-Json -Depth 6 | Set-Content "$E\ability-regression.json" -Encoding utf8
$failed = @($results | Where-Object { -not $_.passed })
Write-Host "Ability regression: $($results.Count - $failed.Count)/$($results.Count) cases saw every expected trace."
