param(
    [Parameter(Mandatory)][string]$Models,
    [Parameter(Mandatory)][string]$Nodes,
    [Parameter(Mandatory)][string]$Pairs,
    [Parameter(Mandatory)][string]$Output,
    [ValidateRange(1, 65536)][int]$ViewerAnimationLimit = 1024
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$modelsBySource = @{}
foreach ($model in Import-Csv -LiteralPath $Models -Delimiter "`t") {
    if ($modelsBySource.ContainsKey($model.source)) { throw "Duplicate model source: $($model.source)" }
    $modelsBySource[$model.source] = $model
}
$nodeRows = @(Import-Csv -LiteralPath $Nodes -Delimiter "`t")
$pairRows = @(Import-Csv -LiteralPath $Pairs -Delimiter "`t")
if ($pairRows.Count -eq 0) { throw 'No model/motion pairs were supplied.' }
if ([IO.Path]::GetFullPath($Output) -in @([IO.Path]::GetFullPath($Models), [IO.Path]::GetFullPath($Nodes))) {
    throw 'Output must not overwrite the model or node evidence.'
}

$results = foreach ($pair in $pairRows) {
    foreach ($source in @($pair.parent_source, $pair.motion_source)) {
        if (-not $modelsBySource.ContainsKey($source) -or $modelsBySource[$source].status -ne 'structure-complete') {
            throw "A complete model record is required: $source"
        }
        $rows = @($nodeRows | Where-Object source -EQ $source)
        if ($rows.Count -ne [int]$modelsBySource[$source].parsed_nodes -or
            @($rows.node_index | Sort-Object -Unique).Count -ne $rows.Count) {
            throw "Incomplete or duplicate node records: $source"
        }
    }
    $parent = $modelsBySource[$pair.parent_source]
    $motion = $modelsBySource[$pair.motion_source]
    $parentNodes = @($nodeRows | Where-Object source -EQ $pair.parent_source)
    $motionNodes = @($nodeRows | Where-Object source -EQ $pair.motion_source)
    $parentNames = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
    $motionNames = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
    foreach ($node in $parentNodes) { [void]$parentNames.Add($node.name) }
    foreach ($node in $motionNodes) { [void]$motionNames.Add($node.name) }
    $matched = @($parentNodes | Where-Object { $motionNames.Contains($_.name) })
    $matchedNonRoot = @($matched | Where-Object { [int]$_.parent_index -ge 0 })
    $hierarchyEqual = $parent.hierarchy_sha256 -eq $motion.hierarchy_sha256
    [pscustomobject][ordered]@{
        parent_source = $pair.parent_source
        motion_source = $pair.motion_source
        parent_nodes = $parentNodes.Count
        motion_nodes = $motionNodes.Count
        node_hierarchy_equal = $hierarchyEqual.ToString().ToLowerInvariant()
        serialized_bind_matrices_equal = ($parent.bind_pose_sha256 -eq $motion.bind_pose_sha256).ToString().ToLowerInvariant()
        motion_animation_count = [int]$motion.parsed_animations
        status = if ($hierarchyEqual) { 'hierarchy-only; playback-unverified' } else { 'different-hierarchy; row-reference-only' }
        parent_nodes_with_matching_name = $matched.Count
        parent_nonroot_nodes_with_matching_name = $matchedNonRoot.Count
        parent_duplicate_names = $parentNodes.Count - $parentNames.Count
        motion_duplicate_names = $motionNodes.Count - $motionNames.Count
        any_name_gate_passes = ($matched.Count -gt 0).ToString().ToLowerInvariant()
        root_only_name_overlap = ($matched.Count -gt 0 -and $matchedNonRoot.Count -eq 0).ToString().ToLowerInvariant()
        viewer_animation_limit = $ViewerAnimationLimit
        full_motion_within_viewer_animation_limit = ([int]$motion.parsed_animations -le $ViewerAnimationLimit).ToString().ToLowerInvariant()
    }
}
$results | Export-Csv -LiteralPath $Output -Delimiter "`t" -NoTypeInformation -Encoding utf8
Write-Output "Compared $($pairRows.Count) model/motion pairs. Playback remains unverified."
