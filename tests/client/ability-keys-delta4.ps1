param($Context)
# Real-client check that Delta-4 equipment fires from the shared keys. Delta-4 abilities answer only members of
# a real deployment, so this spawns a wave rather than applying a sub-role kit. The actor presses Ability 1 (Z)
# and Ability 2 (X), which select the command radio and snapshot recorder in grant order, then Primary and
# Secondary Use with the last card drawn. Run with --bindings tests/client/ability-regression-bindings.json.
$ErrorActionPreference = 'Stop'
$E = $Context.Evidence
$actor = $Context.Actor.id
$notes = [Collections.Generic.List[string]]::new()
function Save($Name, $Value) { $Value | ConvertTo-Json -Depth 10 | Set-Content "$E\$Name.json" -Encoding utf8 }
function Note($Message) { $notes.Add($Message); Write-Host $Message }
function Find-Evidence([string]$Pattern, [int]$Seconds = 6) {
    $deadline = (Get-Date).AddSeconds($Seconds)
    do {
        $hits = @(Get-ChildItem -LiteralPath $E -Recurse -File |
            Where-Object { $_.Extension -in @('.log', '.txt') } |
            Select-String -SimpleMatch $Pattern)
        if ($hits.Count) { return $hits }
        Start-Sleep -Milliseconds 250
    } while ((Get-Date) -lt $deadline)
    return @()
}

Invoke-LabServer '/keybinds trace on' | Out-File "$E\sk-trace.txt" -Encoding utf8
Invoke-LabServer '/forcestart' | Out-File "$E\forcestart.txt" -Encoding utf8
$deadline = (Get-Date).AddSeconds(30)
do { Start-Sleep -Milliseconds 500; $time = Invoke-LabServer '/roundtime' } while (($time -notmatch 'Round time') -and (Get-Date) -lt $deadline)
Invoke-LabServer "/forcerole $actor Spectator" | Out-File "$E\forcerole.txt" -Encoding utf8
Start-Sleep -Seconds 2
Invoke-LabServer '/reinforcements spawn delta4' | Out-File "$E\spawn.txt" -Encoding utf8

# Wait through the arrival until the operator holds its kit and the arrival lock has released.
$deadline = (Get-Date).AddSeconds(150)
do {
    Start-Sleep -Seconds 2
    $me = @(Observe -ClientsOnly)[0]
} while ((Get-Date) -lt $deadline -and (-not $me.items -or @($me.items).Count -lt 2 -or "$($me.role)" -eq 'Spectator'))
Start-Sleep -Seconds 10
Save 'observe-landed' (Observe -ClientsOnly)
Invoke-LabScreenshot -Name 'delta4-landed' | Out-Null

foreach ($press in @(
        @{ name = 'ability1'; code = 122; id = 1090004; key = 'Ability1'; hold = 3 },
        @{ name = 'ability2'; code = 120; id = 1090005; key = 'Ability2'; hold = 3 },
        @{ name = 'primary'; code = 323; id = 1090001; key = 'PrimaryUse'; hold = 90 },
        @{ name = 'secondary'; code = 324; id = 1090002; key = 'SecondaryUse'; hold = 3 })) {
    Invoke-LabInput @{ id = "delta4-$($press.name)"; frames = 150; inputFrames = $press.hold; keys = @($press.code); capture = $true } |
        ConvertTo-Json -Depth 6 | Set-Content "$E\press-$($press.name).json" -Encoding utf8
    Save "observe-after-$($press.name)" (Observe -ClientsOnly)
    Invoke-LabScreenshot -Name "delta4-after-$($press.name)" | Out-Null
    $sent = Find-Evidence "SSS actor=$actor id=$($press.id) pressed=True" 3
    $routed = Find-Evidence "ability key $($press.key) (player" 3
    Note "$($press.name): client press $(if ($sent.Count) { 'reached the server' } else { 'not observed' }); $(@($routed | ForEach-Object { ($_.Line -split 'Trace: ')[-1] }) -join ' || ')"
    Start-Sleep -Seconds 2
}
Save 'ability-keys-delta4-result' @{ actor = $actor; notes = $notes }
