param($Context)
# Real-client before/after for the shared ability keys on the OA1 mirror. Run with
# --bindings tests/client/ability-keys-bindings.json: it seeds saved keys under the production ids,
# so a key that is still assigned on arrival proves the client-side binding survived.
# Records what the client received as a lobby Tutorial and as SCP-079 (tier 5), then presses the
# legacy SCP-079 repair key (R, 9100107) and the shared Ability 1 key (Z, 1090004) as SCP-079.
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

# The client's copy of every keybind, as received: id, label, assigned and suggested key, prefs key.
function Save-Keybinds([string]$Tag, [int[]]$WaitFor) {
    $deadline = (Get-Date).AddSeconds(25)
    do {
        $settings = Get-LabSettings
        $ids = @($settings.settings | ForEach-Object { [int]$_.id })
        if (@($WaitFor | Where-Object { $_ -notin $ids }).Count -eq 0) { break }
        Start-Sleep -Milliseconds 500
    } while ((Get-Date) -lt $deadline)
    Save "sss-$Tag" $settings
    $rows = @($settings.settings | ForEach-Object {
        [pscustomobject]@{ id = $_.id; type = $_.type; label = $_.label; assigned = $_.assignedKeyCode; suggested = $_.suggestedKey; prefsKey = $_.prefsKey }
    })
    $rows | Export-Csv "$E\sss-$Tag.csv" -NoTypeInformation -Encoding utf8
    return $rows
}

Invoke-LabServer '/keybinds trace on' | Out-File "$E\sk-trace.txt" -Encoding utf8

# 1. Lobby Tutorial view.
$human = Save-Keybinds 'tutorial' @(1090001, 1090002)
foreach ($seed in @(
        @{ id = 1090001; key = 'Mouse0' }, @{ id = 1090002; key = 'Mouse1' }, @{ id = 1090003; key = 'B' },
        @{ id = 1090004; key = 'Z' }, @{ id = 1090005; key = 'X' }, @{ id = 1090006; key = 'T' },
        @{ id = 1090007; key = 'LeftControl' }, @{ id = 9100107; key = 'R' })) {
    $entry = @($human | Where-Object { [int]$_.id -eq $seed.id })
    if ($entry.Count -ne 1) { Note "Tutorial view: $($seed.id) not received"; continue }
    Note "Tutorial view: $($seed.id) '$($entry[0].label)' assigned=$($entry[0].assigned) (seeded $($seed.key))"
}
Invoke-LabScreenshot -Name 'tutorial-hud' | Out-Null

# 2. SCP-079 at tier 5.
Invoke-LabServer '/forcestart' | Out-File "$E\forcestart.txt" -Encoding utf8
$deadline = (Get-Date).AddSeconds(30)
do { Start-Sleep -Milliseconds 500; $time = Invoke-LabServer '/roundtime' } while (($time -notmatch 'Round time') -and (Get-Date) -lt $deadline)
Invoke-LabServer "/forcerole $actor Scp079" | Out-File "$E\forcerole.txt" -Encoding utf8
Start-Sleep -Seconds 4
Invoke-LabServer "/scptiers force $actor 5" | Out-File "$E\scptiers-force.txt" -Encoding utf8
Start-Sleep -Seconds 4
$scp = Save-Keybinds 'scp079' @(1090001)
Save 'observe-scp079' (Observe -ClientsOnly)

# 3. Press the legacy repair key, then the shared Ability 1 key, inside the SCP-079 interface.
foreach ($press in @(@{ name = 'R'; code = 114 }, @{ name = 'Z'; code = 122 })) {
    Invoke-LabInput @{ id = "scp079-$($press.name.ToLower())"; frames = 120; inputFrames = 3; keys = @($press.code); capture = $true } |
        ConvertTo-Json -Depth 6 | Set-Content "$E\press-$($press.name).json" -Encoding utf8
    Invoke-LabScreenshot -Name "scp079-after-$($press.name)" | Out-Null
    Start-Sleep -Seconds 3
}
foreach ($id in @(9100107, 1090004)) {
    $sent = Find-Evidence "SSS actor=$actor id=$id pressed=True"
    Note "SCP-079: client press of $id $(if ($sent.Count) { 'reached the server' } else { 'not observed' })"
}
foreach ($pattern in @('handled by ''ScpTiers''', 'not handled', '当前房间没有可修复的损坏房门')) {
    $hits = Find-Evidence $pattern 2
    Note "Server log '$pattern': $($hits.Count) hit(s)"
}

Save 'ability-keys-result' @{ actor = $actor; notes = $notes }
