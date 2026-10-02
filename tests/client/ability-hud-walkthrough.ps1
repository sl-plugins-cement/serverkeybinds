param($Context)
# Real-client walkthrough of the ability HUD: client-rendered key names on a Nu-7 operator, SCP-173 (tier 2),
# SCP-079, SCP-096 and SCP-999. Run with --bindings tests/client/ability-hud-bindings.json (技能1 = Z,
# 技能2 deliberately unbound) and the deploy-equivalent SCP Tiers / ReinforcementsSystem configs.
$ErrorActionPreference = 'Stop'
$E = $Context.Evidence
$actor = $Context.Actor.id

function Shot([string]$Name) {
    Invoke-LabScreenshot -Name $Name | Out-Null
    Save-Observe $Name
}
function Save-Observe([string]$Name) {
    Observe -ClientsOnly | ConvertTo-Json -Depth 8 | Set-Content "$E\observe-$Name.json" -Encoding utf8
}

Invoke-LabServer '/keybinds trace on' | Out-File "$E\sk-trace.txt" -Encoding utf8
Invoke-LabServer '/forcestart' | Out-File "$E\forcestart.txt" -Encoding utf8
$deadline = (Get-Date).AddSeconds(30)
do { Start-Sleep -Milliseconds 500; $time = Invoke-LabServer '/roundtime' } while (($time -notmatch 'Round time') -and (Get-Date) -lt $deadline)

# 1. Nu-7 operator: the reinforcement ability line under the identity strip.
Invoke-LabServer "/forcerole $actor Spectator" | Out-File "$E\forcerole.txt" -Encoding utf8
Start-Sleep -Seconds 2
Invoke-LabServer '/reinforcements spawn nu7' | Out-File "$E\spawn.txt" -Encoding utf8
$deadline = (Get-Date).AddSeconds(90)
do {
    Start-Sleep -Seconds 2
    $me = @(Observe -ClientsOnly)[0]
} while ((Get-Date) -lt $deadline -and (-not $me.items -or @($me.items).Count -lt 2 -or "$($me.role)" -eq 'Spectator'))
Start-Sleep -Seconds 4
Shot 'nu7-landed'
Start-Sleep -Seconds 20
Shot 'nu7-settled'
Invoke-LabInput @{ id = 'nu7-ability1'; frames = 150; inputFrames = 3; keys = @(122); capture = $true } |
    ConvertTo-Json -Depth 6 | Set-Content "$E\press-nu7-z.json" -Encoding utf8
Start-Sleep -Seconds 2
Shot 'nu7-after-z'

# 2. SCP-173 at tier 2: tier line, ability line and the native ability icon below them.
Invoke-LabServer "/forcerole $actor Scp173" | Out-File "$E\forcerole-173.txt" -Encoding utf8
Start-Sleep -Seconds 5
Invoke-LabServer "/scptiers force $actor 2" | Out-File "$E\scptiers-173.txt" -Encoding utf8
Start-Sleep -Seconds 6
Shot 'scp173'

# 3. SCP-079: four tier abilities including the modifier combination.
Invoke-LabServer "/forcerole $actor Scp079" | Out-File "$E\forcerole-079.txt" -Encoding utf8
Start-Sleep -Seconds 8
Shot 'scp079'

# 4. SCP-096: memory line plus ability line.
Invoke-LabServer "/forcerole $actor Scp096" | Out-File "$E\forcerole-096.txt" -Encoding utf8
Start-Sleep -Seconds 8
Shot 'scp096'

# 5. SCP-999: ability line above the stats bar.
Invoke-LabServer "/scp999 $actor force" | Out-File "$E\scp999.txt" -Encoding utf8
Start-Sleep -Seconds 18
Shot 'scp999'
