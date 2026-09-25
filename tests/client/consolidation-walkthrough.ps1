param($Context)
# Real-client acceptance for the consolidated ServerKeybinds registry on the OA1 mirror.
# Run with --bindings tests/client/consolidation-bindings.json: it seeds saved keys under the
# production setting ids, so an assigned key on arrival proves the client-side key is unchanged.
$ErrorActionPreference = 'Stop'
$E = $Context.Evidence
$actor = $Context.Actor.id
$failures = [Collections.Generic.List[string]]::new()
function Save($Name, $Value) { $Value | ConvertTo-Json -Depth 8 | Set-Content "$E\$Name.json" -Encoding utf8 }
function Fail($Message) { $failures.Add($Message); Write-Host "FAIL: $Message" }
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

# 1. Server side: one registry, nothing foreign, trace on for press routing.
Invoke-LabServer '/keybinds foreign' | Out-File "$E\sk-foreign.txt" -Encoding utf8
if (-not (Select-String -LiteralPath "$E\sk-foreign.txt" -SimpleMatch 'none seen' -Quiet)) { Fail 'Foreign settings were seen' }
Invoke-LabServer '/keybinds trace on' | Out-File "$E\sk-trace.txt" -Encoding utf8
Invoke-LabScreenshot -Name 'hud-on-join' | Out-Null

# 2. Client side: every expected keybind arrived, and seeded saved keys are still assigned.
$expected = @(1090001, 1090002, 1090003, 1090004, 1090005, 1090006, 1090007, 1092003, 9100101, 9100102, 9100103, 9100107)
$received = $null
$deadline = (Get-Date).AddSeconds(25)
do {
    $raw = Invoke-LabSetup 'bindings'
    $received = $raw.setupResponse | ConvertFrom-Json
    $ids = @($received.entries | ForEach-Object { [int]$_.id })
    if (@($expected | Where-Object { $_ -notin $ids }).Count -eq 0) { break }
    Start-Sleep -Milliseconds 500
} while ((Get-Date) -lt $deadline)
Save 'sss-client-bindings' $received
$ids = @($received.entries | ForEach-Object { [int]$_.id })
$missing = @($expected | Where-Object { $_ -notin $ids })
if ($missing.Count) { Fail "Client did not receive keybinds: $($missing -join ', ')" }
foreach ($seed in @(@{id = 1090003; key = 'F7'}, @{id = 9100102; key = 'F8'})) {
    $entry = @($received.entries | Where-Object { [int]$_.id -eq $seed.id })
    if ($entry.Count -ne 1) { Fail "Seeded keybind $($seed.id) not delivered"; continue }
    if ($entry[0].assigned -ne $seed.key) { Fail "Saved key for $($seed.id) is '$($entry[0].assigned)', expected $($seed.key)" }
    if ($entry[0].prefsKey -notmatch "_1_$($seed.id)$") { Fail "Prefs key for $($seed.id) changed: $($entry[0].prefsKey)" }
}

# 3. A real press on the preserved reinforcements key reaches the registry's router.
$press = Invoke-LabInput @{id = 'rs-key'; frames = 90; inputFrames = 3; keys = @(288); capture = $true }
Save 'sss-press-input' $press
if (-not (Find-Evidence "SSS actor=$actor id=1090003 pressed=True")) { Fail 'Client press of 1090003 not observed' }
if (-not (Find-Evidence 'press latched for id 1090003')) { Fail 'Registry did not latch the 1090003 press' }
if (-not (Find-Evidence 'release for id 1090003')) { Fail 'Registry did not see the 1090003 release' }
Invoke-LabServer "/keybinds status $actor" | Out-File "$E\sk-status.txt" -Encoding utf8

# 4. Native settings menu, for visual review of the categories and every block.
try {
    Invoke-LabInput @{ frames = 30; inputFrames = 2; keys = @(27) } | Out-Null
    Invoke-LabScreenshot -Name 'menu-pause' | Out-Null
    Invoke-LabInput @{ frames = 30; inputFrames = 2; keys = @(323); cursorX = 0.12; cursorY = 0.21 } | Out-Null
    Invoke-LabScreenshot -Name 'menu-settings' | Out-Null
    Invoke-LabInput @{ frames = 30; inputFrames = 2; keys = @(323); cursorX = 0.81; cursorY = 0.75 } | Out-Null
    Invoke-LabScreenshot -Name 'menu-server-top' | Out-Null
    # No wheel input: click down the settings scrollbar (x 0.871) like the SCP-079 key-assignment suite.
    # Coordinates are Unity screen fractions, origin bottom-left.
    $step = 0
    foreach ($y in @(0.62, 0.5, 0.35, 0.14)) {
        $step++
        Invoke-LabInput @{ frames = 24; inputFrames = 2; keys = @(323); cursorX = 0.871; cursorY = $y } | Out-Null
        Invoke-LabScreenshot -Name "menu-server-$step" | Out-Null
    }
    Invoke-LabInput @{ frames = 20; inputFrames = 2; keys = @(27) } | Out-Null
} catch {
    "Menu navigation stopped: $($_.Exception.Message)" | Set-Content "$E\menu-navigation.txt" -Encoding utf8
}

Save 'consolidation-result' @{ passed = ($failures.Count -eq 0); failures = $failures; actor = $actor }
if ($failures.Count) { throw ('Consolidation walkthrough failed: ' + ($failures -join '; ')) }
