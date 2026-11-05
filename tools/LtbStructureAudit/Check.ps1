param(
    [string]$ToolDll,
    [string]$Output = 'artifacts/ltb-structure-check/results.tsv'
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$repo = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
$run = Join-Path $repo ('artifacts/ltb-structure-check/' + [Guid]::NewGuid().ToString('N'))
$fixtures = Join-Path $run 'fixtures'
$reports = Join-Path $run 'reports'
[void][IO.Directory]::CreateDirectory($fixtures)
if (-not $ToolDll) {
    $build = Join-Path $run 'build'
    & dotnet build (Join-Path $PSScriptRoot 'LtbStructureAudit.csproj') -c Release -o $build --nologo
    if ($LASTEXITCODE -ne 0) { throw 'Structure tool build failed.' }
    $ToolDll = Join-Path $build 'LtbStructureAudit.dll'
}
$ToolDll = [IO.Path]::GetFullPath($ToolDll)

function Write-String([IO.BinaryWriter]$Writer, [string]$Text) {
    $bytes = [Text.Encoding]::ASCII.GetBytes($Text)
    $Writer.Write([uint16]$bytes.Length)
    $Writer.Write($bytes)
}

function New-Fixture([string[]]$Animations = @('Idle', 'Walk'), [string[]]$Bindings = @('Idle', 'Walk'), [switch]$EmptyChildBindings) {
    $buffer = [IO.MemoryStream]::new()
    $w = [IO.BinaryWriter]::new($buffer)
    $w.Write([uint16]1); $w.Write([uint16]9); $w.Write([byte[]]::new(16)); $w.Write([uint32]25)
    $alloc = [uint32[]]::new(15)
    $alloc[1] = 2; $alloc[2] = 2; $alloc[4] = 2; $alloc[9] = 1; $alloc[10] = 1
    foreach ($value in $alloc) { $w.Write($value) }
    Write-String $w 'structure-check'; $w.Write([single]1); $w.Write([uint32]0); $w.Write([uint32]0)
    foreach ($index in 0..1) {
        Write-String $w @('Root', 'Bone')[$index]
        $w.Write([uint16]$index); $w.Write([byte]0)
        foreach ($cell in 0..15) { $w.Write([single][int]($cell -in @(0, 5, 10, 15))) }
        $w.Write([uint32](1 - $index))
    }
    $w.Write([uint32]1); Write-String $w 'Upper'
    $weightCount = [int]$buffer.Position
    $w.Write([uint32]2)
    $weightStart = [int]$buffer.Position
    $w.Write([single]0.25); $w.Write([single]0.75)
    $childCount = [int]$buffer.Position
    $w.Write([uint32]2)
    $childPath = [int]$buffer.Position
    Write-String $w 'Motion/"shared".ltb'
    $w.Write([uint32]2)
    foreach ($name in $Animations) {
        foreach ($v in 1..3) { $w.Write([single]$v) }
        Write-String $w $name
        $w.Write([uint32]1); $w.Write([uint32]200); $w.Write([uint32]1)
        $w.Write([uint32]0); Write-String $w 'key'
        foreach ($node in 0..1) {
            $w.Write([uint32]1)
            foreach ($v in 0..2) { $w.Write([single]$v) }
            $w.Write([uint32]1)
            foreach ($v in @(0, 0, 0, 1)) { $w.Write([single]$v) }
        }
    }
    $w.Write([uint32]1); $w.Write([uint32]1); Write-String $w 'Effect'
    foreach ($v in @(0, 0, 0, 1, 1, 2, 3, 1, 1, 1)) { $w.Write([single]$v) }
    $bindingCount = [int]$buffer.Position
    $w.Write([uint32]2)
    foreach ($name in $Bindings) {
        Write-String $w $name
        foreach ($v in 1..6) { $w.Write([single]$v) }
    }
    $w.Write([uint32][int](-not $EmptyChildBindings))
    if (-not $EmptyChildBindings) {
        Write-String $w 'External'
        foreach ($v in @(7, 8, 9, -1, -2, -3)) { $w.Write([single]$v) }
    }
    $data = $buffer.ToArray()
    $w.Dispose(); $buffer.Dispose()
    return [pscustomobject]@{ Data = $data; WeightCount = $weightCount; WeightStart = $weightStart;
        ChildCount = $childCount; ChildPath = $childPath; BindingCount = $bindingCount }
}

function Save-Fixture([string]$Name, [byte[]]$Data) {
    [IO.File]::WriteAllBytes((Join-Path $fixtures ($Name + '.ltb')), $Data)
}
function Save-Changed([string]$Name, $Fixture, [int]$Offset, [uint32]$Value) {
    $data = [byte[]]$Fixture.Data.Clone()
    [BitConverter]::GetBytes($Value).CopyTo($data, $Offset)
    Save-Fixture $Name $data
}

$valid = @('valid', 'case-insensitive', 'duplicate-names', 'reordered-names', 'missing-name', 'empty-child-bindings')
$fixture = New-Fixture
Save-Fixture 'valid' $fixture.Data
Save-Fixture 'case-insensitive' (New-Fixture -Bindings @('idle', 'WALK')).Data
Save-Fixture 'duplicate-names' (New-Fixture -Animations @('Idle', 'Idle') -Bindings @('Idle', 'Idle')).Data
Save-Fixture 'reordered-names' (New-Fixture -Bindings @('Walk', 'Idle')).Data
Save-Fixture 'missing-name' (New-Fixture -Bindings @('Idle', 'Missing')).Data
Save-Fixture 'empty-child-bindings' (New-Fixture -EmptyChildBindings).Data
Save-Changed 'invalid-weight-count' $fixture $fixture.WeightCount 3
Save-Changed 'nonfinite-weight' $fixture $fixture.WeightStart 2143289344
Save-Changed 'invalid-child-count' $fixture $fixture.ChildCount 3
Save-Changed 'invalid-binding-count' $fixture $fixture.BindingCount 65537
Save-Changed 'nonfinite-binding' $fixture ($fixture.Data.Length - 4) 2143289344
Save-Fixture 'truncated-child' $fixture.Data[0..($fixture.ChildPath + 4)]
Save-Fixture 'truncated-binding' $fixture.Data[0..($fixture.Data.Length - 2)]
Save-Fixture 'trailing-data' ($fixture.Data + [byte[]]@(1, 2, 3, 4))
& dotnet $ToolDll $fixtures $reports > (Join-Path $run 'audit.log')
if ($LASTEXITCODE -ne 1) { throw 'Expected exit code 1 for the rejected fixtures.' }

$models = @(Import-Csv (Join-Path $reports 'models.tsv') -Delimiter "`t")
$tables = @{}
foreach ($file in @('nodes', 'animations', 'sockets', 'weight-sets', 'child-models', 'animation-bindings')) {
    $tables[$file] = @(Import-Csv (Join-Path $reports ($file + '.tsv')) -Delimiter "`t")
}
$checks = [Collections.Generic.List[object]]::new()
function Check([string]$Name, [bool]$Passed) {
    if (-not $Passed) { throw "FAILED: $Name" }
    $checks.Add([pscustomobject]@{ check = $Name; status = 'passed' })
}
Check 'all fixture model rows retained' ($models.Count -eq 14)
foreach ($model in $models) {
    $isValid = [IO.Path]::GetFileNameWithoutExtension($model.source) -in $valid
    Check ('parse status: ' + $model.source) (($model.status -eq 'structure-complete') -eq $isValid)
    if (-not $isValid) {
        Check ('no partial details: ' + $model.source) (@($tables.Values | ForEach-Object { $_ } | Where-Object source -EQ $model.source).Count -eq 0)
    }
}
foreach ($pair in @(@('nodes', 12), @('animations', 12), @('sockets', 6), @('weight-sets', 6), @('child-models', 12), @('animation-bindings', 17))) {
    Check ('complete row count: ' + $pair[0]) ($tables[$pair[0]].Count -eq $pair[1])
}
$children = @($tables['child-models'] | Where-Object source -EQ 'valid.ltb')
Check 'implicit self has no serialized path' ($children[0].child_index -eq '0' -and $children[0].is_self -eq 'true' -and $children[0].declared_path -eq '')
Check 'external child retains quoted path and binding count' ($children[1].child_index -eq '1' -and $children[1].declared_path -eq 'Motion/"shared".ltb' -and $children[1].animation_bindings -eq '1')
Check 'empty child bindings do not become invented entries' (@($tables['child-models'] | Where-Object { $_.source -eq 'empty-child-bindings.ltb' -and $_.child_index -eq '1' -and $_.animation_bindings -eq '0' }).Count -eq 1 -and @($tables['animation-bindings'] | Where-Object { $_.source -eq 'empty-child-bindings.ltb' -and $_.child_index -eq '1' }).Count -eq 0)
$model = @($models | Where-Object source -EQ 'valid.ltb')[0]
Check 'summary retains section counts' ($model.weight_sets -eq '1' -and $model.child_models -eq '2' -and $model.animation_bindings -eq '3')
$weights = @($tables['weight-sets'] | Where-Object source -EQ 'valid.ltb')
Check 'node mask weights retained as float bytes' ($weights[0].node_count -eq '2' -and $weights[0].node_weights_f32_hex -eq '0000803E0000403F')
$bindings = @($tables['animation-bindings'] | Where-Object source -EQ 'valid.ltb')
Check 'self binding dimensions and translation retained' ($bindings[0].dimensions_f32_hex -eq '0000803F0000004000004040' -and $bindings[0].translation_f32_hex -eq '000080400000A0400000C040')
Check 'external binding is not matched to parent animation' ($bindings[2].child_index -eq '1' -and $bindings[2].self_name_match_count -eq '' -and $bindings[2].self_same_index_name_matches -eq '' -and $bindings[2].translation_f32_hex -eq '000080BF000000C0000040C0')
Check 'case-insensitive self names match' (@($tables['animation-bindings'] | Where-Object { $_.source -eq 'case-insensitive.ltb' -and $_.child_index -eq '0' -and $_.self_name_match_count -eq '1' -and $_.self_same_index_name_matches -eq 'true' }).Count -eq 2)
Check 'duplicate self names remain ambiguous' (@($tables['animation-bindings'] | Where-Object { $_.source -eq 'duplicate-names.ltb' -and $_.child_index -eq '0' -and $_.self_name_match_count -eq '2' }).Count -eq 2)
Check 'reordered names do not claim index equality' (@($tables['animation-bindings'] | Where-Object { $_.source -eq 'reordered-names.ltb' -and $_.child_index -eq '0' -and $_.self_same_index_name_matches -eq 'false' }).Count -eq 2)
Check 'missing self name is recorded' (@($tables['animation-bindings'] | Where-Object { $_.source -eq 'missing-name.ltb' -and $_.animation_name -eq 'Missing' -and $_.self_name_match_count -eq '0' -and $_.self_same_index_name_matches -eq 'false' }).Count -eq 1)
$outputPath = if ([IO.Path]::IsPathRooted($Output)) { $Output } else { Join-Path $repo $Output }
[void][IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($outputPath))
$checks | Export-Csv -LiteralPath $outputPath -Delimiter "`t" -NoTypeInformation -Encoding utf8
Write-Output "$($checks.Count) structure checks passed. Results: $outputPath"
