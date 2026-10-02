param($Context)
# Real-client check that reinforcement abilities fire from the shared keys with the saved bindings:
# a Nu-7 operator presses Ability 1 (Z), Primary Use (Mouse0), Ability 2 (X) and Secondary Use (Mouse1).
# Run with --bindings tests/client/ability-keys-bindings.json.
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
Invoke-LabServer '/reinforcements spawn nu7' | Out-File "$E\spawn.txt" -Encoding utf8

# Wait through the arrival cinematic until the operator holds its kit.
$deadline = (Get-Date).AddSeconds(90)
do {
    Start-Sleep -Seconds 2
    $me = @(Observe -ClientsOnly)[0]
} while ((Get-Date) -lt $deadline -and (-not $me.items -or @($me.items).Count -lt 2 -or "$($me.role)" -eq 'Spectator'))
Start-Sleep -Seconds 6
Save 'observe-landed' (Observe -ClientsOnly)
Invoke-LabScreenshot -Name 'nu7-landed' | Out-Null

foreach ($press in @(
        @{ name = 'ability1'; code = 122; id = 1090004; key = 'Ability1' },
        @{ name = 'primary'; code = 323; id = 1090001; key = 'PrimaryUse' },
        @{ name = 'ability2'; code = 120; id = 1090005; key = 'Ability2' },
        @{ name = 'secondary'; code = 324; id = 1090002; key = 'SecondaryUse' })) {
    Invoke-LabInput @{ id = "nu7-$($press.name)"; frames = 150; inputFrames = 3; keys = @($press.code); capture = $true } |
        ConvertTo-Json -Depth 6 | Set-Content "$E\press-$($press.name).json" -Encoding utf8
    Save "observe-after-$($press.name)" (Observe -ClientsOnly)
    Invoke-LabScreenshot -Name "nu7-after-$($press.name)" | Out-Null
    $sent = Find-Evidence "SSS actor=$actor id=$($press.id) pressed=True" 3
    $routed = Find-Evidence "ability key $($press.key) (player" 3
    Note "$($press.name): client press $(if ($sent.Count) { 'reached the server' } else { 'not observed' }); $(@($routed | ForEach-Object { ($_.Line -split 'Trace: ')[-1] }) -join ' || ')"
    Start-Sleep -Seconds 2
}
Save 'ability-keys-nu7-result' @{ actor = $actor; notes = $notes }
