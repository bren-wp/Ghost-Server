param(
    [string]$SourceRoot = "src/GhostServer"
)

$ErrorActionPreference = "Stop"

$csFiles = @(Get-ChildItem $SourceRoot -Recurse -File -Filter *.cs)
$xamlFiles = @(Get-ChildItem $SourceRoot -Recurse -File -Filter *.xaml)

$allCs = ($csFiles | ForEach-Object { Get-Content $_.FullName -Raw }) -join "`n"
$allXaml = ($xamlFiles | ForEach-Object { Get-Content $_.FullName -Raw }) -join "`n"
$allSource = $allCs + "`n" + $allXaml

$failures = [System.Collections.Generic.List[string]]::new()

function Get-ReferenceCount {
    param(
        [Parameter(Mandatory)]
        [string]$Text,
        [Parameter(Mandatory)]
        [string]$Name
    )

    return [regex]::Matches(
        $Text,
        "\b$([regex]::Escape($Name))\b",
        [System.Text.RegularExpressions.RegexOptions]::CultureInvariant
    ).Count
}

# Private class methods should either be called by C# or be wired from XAML.
$privateMethodPattern = '(?m)^\s*private\s+(?:static\s+)?(?:async\s+)?(?:[\w<>,?.\[\]\s]+?)\s+([A-Za-z_][A-Za-z0-9_]*)\s*\('
foreach ($file in $csFiles) {
    $content = Get-Content $file.FullName -Raw
    foreach ($match in [regex]::Matches($content, $privateMethodPattern)) {
        $name = $match.Groups[1].Value
        $csReferences = Get-ReferenceCount -Text $allCs -Name $name
        $xamlReferences = Get-ReferenceCount -Text $allXaml -Name $name

        if ($csReferences -eq 1 -and $xamlReferences -eq 0) {
            $failures.Add("Orphan private method: $($file.FullName): $name")
        }
    }
}

# A private underscore field that occurs only in its declaration is dead.
$privateFieldPattern = '(?m)^\s*private\s+(?:readonly\s+)?(?:static\s+)?[\w<>,?.\[\]\s]+\s+(_[A-Za-z_][A-Za-z0-9_]*)\s*(?:=|;)'
foreach ($file in $csFiles) {
    $content = Get-Content $file.FullName -Raw
    foreach ($match in [regex]::Matches($content, $privateFieldPattern)) {
        $name = $match.Groups[1].Value
        $referenceCount = Get-ReferenceCount -Text $allCs -Name $name
        if ($referenceCount -eq 1) {
            $failures.Add("Unused private field: $($file.FullName): $name")
            continue
        }

        if ($referenceCount -eq 2) {
            $assignmentPattern = '(?m)^\s*' + [regex]::Escape($name) + '\s*='
            $assignmentCount = [regex]::Matches($content, $assignmentPattern).Count
            if ($assignmentCount -eq 1) {
                $failures.Add("Write-only private field: $($file.FullName): $name")
            }
        }
    }
}

# Public/internal service methods are application-internal API. A declaration-only
# method is therefore unused unless another source reference exists.
$serviceFiles = @($csFiles | Where-Object {
    $_.FullName -match '[\\/]Services[\\/]'
})
$serviceMethodPattern = '(?m)^\s*(?:public|internal)\s+(?:static\s+)?(?:async\s+)?(?:[\w<>,?.\[\]\s]+?)\s+([A-Za-z_][A-Za-z0-9_]*)\s*\('
foreach ($file in $serviceFiles) {
    $content = Get-Content $file.FullName -Raw
    foreach ($match in [regex]::Matches($content, $serviceMethodPattern)) {
        $name = $match.Groups[1].Value
        if ((Get-ReferenceCount -Text $allCs -Name $name) -eq 1) {
            $failures.Add("Unused service method: $($file.FullName): $name")
        }
    }
}

# x:Name generates a field. Keep it only when code or another XAML reference uses it.
$xNamePattern = 'x:Name="([^"]+)"'
foreach ($file in $xamlFiles) {
    $content = Get-Content $file.FullName -Raw
    foreach ($match in [regex]::Matches($content, $xNamePattern)) {
        $name = $match.Groups[1].Value

        # PART_* names are framework-defined template parts. Their consumer is WPF's
        # control-template contract, not an ordinary source-text reference.
        if ($name.StartsWith("PART_", [System.StringComparison]::Ordinal)) {
            continue
        }

        if ((Get-ReferenceCount -Text $allSource -Name $name) -eq 1) {
            $failures.Add("Unused x:Name generated field: $($file.FullName): $name")
        }
    }
}

# Keyed resources must be consumed by StaticResource/DynamicResource/BasedOn or code.
$keyPattern = 'x:Key="([^"]+)"'
foreach ($file in $xamlFiles) {
    $content = Get-Content $file.FullName -Raw
    foreach ($match in [regex]::Matches($content, $keyPattern)) {
        $name = $match.Groups[1].Value
        if ((Get-ReferenceCount -Text $allSource -Name $name) -eq 1) {
            $failures.Add("Unused keyed XAML resource: $($file.FullName): $name")
        }
    }
}

if ($failures.Count -gt 0) {
    $failures | Sort-Object -Unique | ForEach-Object { Write-Host $_ }
    throw "Dead code audit failed with $($failures.Count) candidate(s)."
}

Write-Host "Dead code audit passed."
Write-Host "Scanned $($csFiles.Count) C# file(s) and $($xamlFiles.Count) XAML file(s)."
