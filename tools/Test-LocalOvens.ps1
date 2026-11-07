param([string]$LogicPath = (Join-Path $PSScriptRoot '../ProjectFiles/NetSolution/ProjectLogic.cs'))
$ErrorActionPreference = 'Stop'
$source = [IO.File]::ReadAllText([IO.Path]::GetFullPath($LogicPath))
$start = $source.IndexOf('public sealed class LocalOvenPhase', [StringComparison]::Ordinal)
if ($start -lt 0) { throw 'Local oven simulation not found.' }
$namespace = 'LocalOvenTest_' + [Guid]::NewGuid().ToString('N')
$types = Add-Type -TypeDefinition ("namespace $namespace { " + $source.Substring($start) + ' }') -PassThru
$phaseType = $types | Where-Object Name -eq 'LocalOvenPhase'
$engineType = $types | Where-Object Name -eq 'LocalOvenSimulation'
$first = [Activator]::CreateInstance($engineType)
$second = [Activator]::CreateInstance($engineType)
$phases = [Array]::CreateInstance($phaseType, 3)
foreach ($index in 0..2) {
    $phases[$index] = [Activator]::CreateInstance($phaseType, @([double]100, [double]1, [double]20, [double]20, ($index -ne 2)))
}
if ($first.Start($false)) { throw 'An oven without a recipe must not start.' }
$first.Apply($phases)
$second.Apply($phases)
if (-not $first.Start($false) -or $first.Start($false)) { throw 'Start must be idempotent while running.' }
$blocked = $false
try { $first.Apply($phases) } catch { $blocked = $true }
if (-not $blocked) { throw 'Apply must not change a running cycle.' }
foreach ($tick in 1..12) { $first.Tick(0.25) }
if ($first.Running -or $first.Cycles -ne 1 -or $first.GoodCycles -ne 1 -or $first.RunningSeconds -ne 3 -or $first.IdealSeconds -ne 3) { throw 'Completed-cycle counters are incorrect.' }
if ($second.Cycles -ne 0 -or $second.RunningSeconds -ne 0 -or $second.Temperature -ne 20) { throw 'Oven state leaked between instances.' }
if ($first.EnergyKwh -le 0) { throw 'A completed cycle must consume energy.' }
$null = $second.Start($true)
foreach ($tick in 1..12) { $second.Tick(0.25) }
if ($second.RejectedCycles -ne 1 -or $second.GoodCycles -ne 0) { throw 'RejectNextCycle was not honoured.' }
$null = $first.Start($false)
$first.Tick(0.25)
$first.Stop()
if ($first.Cycles -ne 1 -or $first.PowerKw -ne 0 -or $first.Running) { throw 'Aborted cycles must not count as completed.' }
$invalid = $false
try { $null = [Activator]::CreateInstance($phaseType, @([double]100, [double]0, [double]20, [double]20, $true)) } catch { $invalid = $true }
if (-not $invalid) { throw 'Invalid phase duration was accepted.' }

$storeType = $types | Where-Object Name -eq 'OvenCycleTraceStore'
$snapshotType = $types | Where-Object Name -eq 'OvenCycleSnapshot'
$snapshotPhaseType = $types | Where-Object Name -eq 'OvenCycleSnapshotPhase'
$store = [Activator]::CreateInstance($storeType)
$store.Record('A', $false, 0, 20)
if ($store.Get('A').Length -ne 0) { throw 'An idle oven must not accumulate samples.' }
$store.Record('A', $true, 0.04, 21.04)
$store.Record('A', $true, 1.0, 40)
$store.Record('B', $true, 1.0, 99)
if ($store.Get('A').Length -ne 2 -or $store.Get('B').Length -ne 1) { throw 'Traces must be kept per oven.' }
if ($store.Get('A')[0][0] -ne 0 -or $store.Get('A')[0][1] -ne 21) { throw 'Samples must be rounded to one decimal.' }
$store.Record('A', $false, 0, 30)
if ($store.Get('A').Length -ne 2) { throw 'The last cycle trace must survive the end of the cycle.' }
$store.Record('A', $true, 0, 25)
if ($store.Get('A').Length -ne 1) { throw 'A new cycle must restart the trace.' }
foreach ($second in 1..7300) { $store.Record('C', $true, $second, 50) }
if ($store.Get('C').Length -ne 7200) { throw 'The trace must be capped.' }

$profile = [Array]::CreateInstance($snapshotPhaseType, 2)
$profile[0] = [Activator]::CreateInstance($snapshotPhaseType, @('PreHeat', [double]0, [double]60, [double]180))
$profile[1] = [Activator]::CreateInstance($snapshotPhaseType, @('Bake', [double]60, [double]150, [double]220))
if ($snapshotType::Elapsed($profile, 2, 30, 7) -ne 90) { throw 'Elapsed time must add the phase start to the phase time.' }
if ($snapshotType::Elapsed($profile, 0, 30, 7) -ne 7 -or $snapshotType::Elapsed($null, 1, 30, 7) -ne 7) { throw 'Elapsed time must fall back without a profile.' }
$json = $snapshotType::Build('OVEN-1', $true, $true, 2, 201.26, 220, 90, 'Margherita v1', $profile, $store.Get('A')) | ConvertFrom-Json
if ($json.state -ne 1 -or $json.phase -ne 2 -or $json.total -ne 150 -or $json.temperature -ne 201.3 -or $json.phases.Count -ne 2 -or $json.recipe -ne 'Margherita v1') { throw 'Snapshot content is incorrect.' }
$idle = $snapshotType::Build('OVEN-1', $true, $false, 2, 25, 220, 90, $null, $null, $null) | ConvertFrom-Json
if ($idle.state -ne 0 -or $idle.phase -ne 0 -or $idle.cycleElapsed -ne 0 -or $idle.total -ne 0 -or $idle.phases.Count -ne 0) { throw 'An idle snapshot must not report a running cycle.' }
[pscustomobject]@{ Result = 'PASS'; Scenarios = 17; Source = [IO.Path]::GetFullPath($LogicPath) }