param($Context)
# Real-client check that SCP-999's abilities fire from the shared keys: X (技能 2) is Jelly bounce,
# Z (技能 1) is Tickle. Run with --bindings tests/client/ability-keys-bindings.json.
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
Invoke-LabServer "/scp999 $actor force" | Out-File "$E\scp999.txt" -Encoding utf8
Start-Sleep -Seconds 8
Save 'observe-before' (Observe -ClientsOnly)

$take = Invoke-LabInput @{ id = 'scp999-x'; frames = 150; inputFrames = 3; keys = @(120); capture = $true }
$take | ConvertTo-Json -Depth 6 | Set-Content "$E\press-x.json" -Encoding utf8
Save 'observe-after-x' (Observe -ClientsOnly)
Invoke-LabInput @{ id = 'scp999-z'; frames = 90; inputFrames = 3; keys = @(122); capture = $true } |
    ConvertTo-Json -Depth 6 | Set-Content "$E\press-z.json" -Encoding utf8
Invoke-LabScreenshot -Name 'scp999-after-z' | Out-Null

foreach ($pattern in @("SSS actor=$actor id=1090005 pressed=True", 'ability key Ability2', "SSS actor=$actor id=1090004 pressed=True", 'ability key Ability1')) {
    $hits = Find-Evidence $pattern 3
    Note "'$pattern': $(@($hits | ForEach-Object { $_.Line.Trim() }) -join ' || ')"
}
Save 'ability-keys-scp999-result' @{ actor = $actor; notes = $notes }
