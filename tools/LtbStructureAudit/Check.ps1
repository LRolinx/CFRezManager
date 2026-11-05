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
function To-Hex([byte[]]$Bytes) { return [BitConverter]::ToString($Bytes).Replace('-', '') }

function New-Fixture([string[]]$Animations = @('Idle', 'Walk'), [string[]]$Bindings = @('Idle', 'Walk'), [switch]$EmptyChildBindings,
    [uint32]$Compression = 1, [uint32]$Frames = 1, [switch]$DefaultChannels, [switch]$SwapNodes, [switch]$VertexChannel) {
    $buffer = [IO.MemoryStream]::new()
    $w = [IO.BinaryWriter]::new($buffer)
    $w.Write([uint16]1); $w.Write([uint16]9); $w.Write([byte[]]::new(16)); $w.Write([uint32]25)
    $alloc = [uint32[]]::new(15)
    $alloc[1] = 2; $alloc[2] = 2; $alloc[4] = 2; $alloc[9] = 1; $alloc[10] = 1
    foreach ($value in $alloc) { $w.Write($value) }
    Write-String $w 'structure-check'; $w.Write([single]1); $w.Write([uint32]0); $w.Write([uint32]0)
    foreach ($index in 0..1) {
        Write-String $w @('Root', 'Bone')[$index]
        $w.Write([uint16]$(if ($SwapNodes) { 1 - $index } else { $index })); $w.Write([byte]0)
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
    $positionCount = -1; $positionStart = -1; $rotationStart = -1
    foreach ($name in $Animations) {
        foreach ($v in 1..3) { $w.Write([single]$v) }
        Write-String $w $name
        $w.Write($Compression); $w.Write([uint32]200); $w.Write($Frames)
        for ($frame = 0; $frame -lt $Frames; $frame++) {
            $w.Write([uint32]($frame * 33)); Write-String $w $(if ($frame -eq 0) { 'key' } else { "key`"event`tline" })
        }
        foreach ($node in 0..1) {
            if ($Compression -eq 0) { $w.Write([byte][int][bool]$VertexChannel) }
            if ($Compression -eq 0 -and $VertexChannel) {
                for ($frame = 0; $frame -lt $Frames; $frame++) {
                    $w.Write([uint32]2)
                    if ($positionStart -lt 0) { $positionStart = [int]$buffer.Position }
                    foreach ($v in 0..5) { $w.Write([single]($v + $frame)) }
                }
                continue
            }
            $samples = if ($DefaultChannels) { 0 } else { $Frames }
            if ($Compression -ne 0) {
                if ($positionCount -lt 0) { $positionCount = [int]$buffer.Position }
                $w.Write([uint32]$samples)
            }
            if ($positionStart -lt 0) { $positionStart = [int]$buffer.Position }
            for ($sample = 0; $sample -lt $samples; $sample++) {
                if ($Compression -eq 2) {
                    foreach ($v in $(if ($sample -eq 0) { @(-32768, 0, 32767) } else { @(16, -32, 48) })) { $w.Write([int16]$v) }
                }
                else { foreach ($v in 0..2) { $w.Write([single]($v + $sample * 3)) } }
            }
            if ($Compression -ne 0) { $w.Write([uint32]$samples) }
            if ($rotationStart -lt 0) { $rotationStart = [int]$buffer.Position }
            for ($sample = 0; $sample -lt $samples; $sample++) {
                if ($Compression -in @(2, 3)) {
                    foreach ($v in $(if ($sample -eq 0) { @(0, 0, 0, 32767) } else { @(0, 16384, 0, 28377) })) { $w.Write([int16]$v) }
                }
                else { foreach ($v in $(if ($sample -eq 0) { @(0, 0, 0, 1) } else { @(0, 0.5, 0, 0.8660254) })) { $w.Write([single]$v) } }
            }
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
        ChildCount = $childCount; ChildPath = $childPath; BindingCount = $bindingCount;
        PositionCount = $positionCount; PositionStart = $positionStart; RotationStart = $rotationStart }
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
Check 'default inspection leaves channel tables empty' (@(Import-Csv (Join-Path $reports 'channels.tsv') -Delimiter "`t").Count -eq 0 -and @(Import-Csv (Join-Path $reports 'keyframes.tsv') -Delimiter "`t").Count -eq 0 -and (Import-Csv (Join-Path $reports 'inspection.tsv') -Delimiter "`t").requested -eq 'false')
$channelFixtures = Join-Path $run 'channel-fixtures'
$channelReports = Join-Path $run 'channel-reports'
[void][IO.Directory]::CreateDirectory($channelFixtures)
$fixtures = $channelFixtures
$channelControls = [ordered]@{
    'plain' = (New-Fixture -Compression 0 -Frames 2)
    'float' = (New-Fixture -Compression 1 -Frames 2)
    'packed' = (New-Fixture -Compression 2 -Frames 2)
    'rotation-packed' = (New-Fixture -Compression 3 -Frames 2)
    'defaults' = (New-Fixture -Frames 2 -DefaultChannels)
    'swapped-nodes' = (New-Fixture -Frames 2 -SwapNodes)
    'vertex' = (New-Fixture -Compression 0 -Frames 2 -VertexChannel)
}
foreach ($name in $channelControls.Keys) { Save-Fixture $name $channelControls[$name].Data }
$partial = New-Fixture -Frames 3
Save-Changed 'partial-channel' $partial $partial.PositionCount 2
Save-Changed 'nonfinite-position' $channelControls['float'] $channelControls['float'].PositionStart 2143289344
Save-Changed 'nonfinite-rotation' $channelControls['float'] $channelControls['float'].RotationStart 2143289344
Save-Changed 'nonfinite-vertex' $channelControls['vertex'] $channelControls['vertex'].PositionStart 2143289344
Save-Fixture 'truncated-channel-file' $channelControls['packed'].Data[0..($channelControls['packed'].Data.Length - 4)]
& dotnet $ToolDll $channelFixtures $channelReports --channels > (Join-Path $run 'channel-audit.log')
Check 'channel audit rejects invalid fixtures' ($LASTEXITCODE -eq 1)
$channelModels = @(Import-Csv (Join-Path $channelReports 'models.tsv') -Delimiter "`t")
$channels = @(Import-Csv (Join-Path $channelReports 'channels.tsv') -Delimiter "`t")
$keys = @(Import-Csv (Join-Path $channelReports 'keyframes.tsv') -Delimiter "`t")
Check 'all channel fixture model rows retained' ($channelModels.Count -eq 12)
foreach ($model in $channelModels) {
    $isControl = $channelControls.Contains([IO.Path]::GetFileNameWithoutExtension($model.source))
    Check ('channel parse status: ' + $model.source) (($model.status -eq 'structure-complete') -eq $isControl)
    if (-not $isControl) {
        Check ('no partial channels or keys: ' + $model.source) (@($channels | Where-Object source -EQ $model.source).Count -eq 0 -and @($keys | Where-Object source -EQ $model.source).Count -eq 0)
    }
}
Check 'complete channel row count' ($channels.Count -eq 52)
Check 'complete keyframe row count' ($keys.Count -eq 28)
Check 'channel inspection is explicitly recorded' ((Import-Csv (Join-Path $channelReports 'inspection.tsv') -Delimiter "`t").requested -eq 'true')
foreach ($channel in $channels) {
    $data = $channelControls[[IO.Path]::GetFileNameWithoutExtension($channel.source)].Data
    $payload = [byte[]]::new([int]$channel.payload_bytes)
    [Array]::Copy($data, [int]$channel.decoded_byte_offset, $payload, 0, $payload.Length)
    $digest = [Security.Cryptography.SHA256]::Create()
    try { $payloadHash = To-Hex ($digest.ComputeHash($payload)) } finally { $digest.Dispose() }
    Check ("exact payload hash: $($channel.source)/$($channel.animation_index)/$($channel.node_index)/$($channel.kind)") ($payloadHash -eq $channel.payload_sha256)
}
function Channel([string]$Source, [string]$Kind, [int]$Node = 0) {
    return @($channels | Where-Object { $_.source -eq ($Source + '.ltb') -and $_.animation_index -eq '0' -and $_.node_index -eq [string]$Node -and $_.kind -eq $Kind })[0]
}
Check 'uncompressed positions are a separate block' ((Channel 'plain' 'position').first_value_f32_hex -eq '000000000000803F00000040' -and (Channel 'plain' 'position').last_value_f32_hex -eq '00004040000080400000A040')
Check 'uncompressed quaternion block starts after every position' ((Channel 'plain' 'rotation').first_value_f32_hex -eq '0000000000000000000000000000803F')
Check 'full-float compressed channels preserve both samples' ((Channel 'float' 'position').first_value_f32_hex -eq (Channel 'plain' 'position').first_value_f32_hex -and (Channel 'float' 'position').last_value_f32_hex -eq (Channel 'plain' 'position').last_value_f32_hex)
Check 'quantized positions use signed int16 divided by 16' ((Channel 'packed' 'position').first_value_f32_hex -eq '000000C50000000000FEFF44' -and (Channel 'packed' 'position').last_value_f32_hex -eq '0000803F000000C000004040')
$rotation = ([single[]]@(0, ([single]16384 / [single]32767), 0, ([single]28377 / [single]32767)) | ForEach-Object { To-Hex ([BitConverter]::GetBytes($_)) }) -join ''
Check 'quantized rotations divide components by 32767' ((Channel 'packed' 'rotation').last_value_f32_hex -eq $rotation)
Check 'rotation-only compression keeps full-float positions' ((Channel 'rotation-packed' 'position').last_value_f32_hex -eq (Channel 'float' 'position').last_value_f32_hex -and (Channel 'rotation-packed' 'rotation').last_value_f32_hex -eq $rotation)
Check 'zero position samples retain the default zero vector' ((Channel 'defaults' 'position').samples -eq '0' -and (Channel 'defaults' 'position').frame_binding -eq 'default' -and (Channel 'defaults' 'position').first_value_f32_hex -eq '000000000000000000000000')
Check 'zero rotation samples retain the default identity quaternion' ((Channel 'defaults' 'rotation').samples -eq '0' -and (Channel 'defaults' 'rotation').first_value_f32_hex -eq '0000000000000000000000000000803F')
Check 'channel order follows the hierarchy rather than sorted node indices' ((Channel 'swapped-nodes' 'position' 1).decoded_byte_offset -eq [string]$channelControls['swapped-nodes'].PositionStart -and (Channel 'swapped-nodes' 'position' 1).node_name -eq 'Root')
Check 'vertex channels remain distinct from bone transforms' (@($channels | Where-Object { $_.source -eq 'vertex.ltb' -and $_.kind -eq 'vertex' -and $_.samples -eq '2' -and $_.frame_binding -eq 'per-frame-vertex-lists' }).Count -eq 4)
Check 'raw event bytes retain quotes and tabs' (@($keys | Where-Object { $_.source -eq 'plain.ltb' -and $_.keyframe_index -eq '1' -and $_.time_ms -eq '33' -and $_.event_cp949_hex -eq '6B6579226576656E74096C696E65' }).Count -eq 2)
& dotnet $ToolDll $channelFixtures $channelReports > (Join-Path $run 'plain-rerun.log')
Check 'plain rerun still reports invalid files' ($LASTEXITCODE -eq 1)
Check 'plain rerun clears stale channel records' (@(Import-Csv (Join-Path $channelReports 'channels.tsv') -Delimiter "`t").Count -eq 0 -and @(Import-Csv (Join-Path $channelReports 'keyframes.tsv') -Delimiter "`t").Count -eq 0 -and (Import-Csv (Join-Path $channelReports 'inspection.tsv') -Delimiter "`t").requested -eq 'false')
$outputPath = if ([IO.Path]::IsPathRooted($Output)) { $Output } else { Join-Path $repo $Output }
[void][IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($outputPath))
$checks | Export-Csv -LiteralPath $outputPath -Delimiter "`t" -NoTypeInformation -Encoding utf8
Write-Output "$($checks.Count) structure checks passed. Results: $outputPath"
exit 0
