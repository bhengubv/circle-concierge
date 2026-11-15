param([Parameter(Mandatory)][string]$Package)
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.IO.Compression
$archive = [System.IO.Compression.ZipFile]::OpenRead((Resolve-Path -LiteralPath $Package))
try {
    $manifestEntry = $archive.GetEntry('manifest.json')
    if ($null -eq $manifestEntry) { throw 'Missing manifest.json' }
    $reader = [System.IO.StreamReader]::new($manifestEntry.Open())
    try { $manifest = $reader.ReadToEnd() | ConvertFrom-Json } finally { $reader.Dispose() }
    if ($manifest.schemaVersion -ne 1 -or $manifest.kind -ne 'spatial-game-draft' -or $manifest.status -ne 'draft' -or $manifest.published) {
        throw 'Invalid spatial game draft manifest'
    }
    if ($manifest.entry -ne 'index.html') { throw 'Missing browser entry point' }
    if ($archive.Entries.Count -ne $manifest.files.Count + 1) { throw 'File count differs from manifest' }
    $seen = [System.Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
    foreach ($file in $manifest.files) {
        $path = [string]$file.path
        if ($path.StartsWith('/') -or $path.Contains('..') -or $path.Contains('\') -or -not $seen.Add($path)) {
            throw "Unsafe or duplicate path: $path"
        }
        $entry = $archive.GetEntry($path)
        if ($null -eq $entry -or $entry.Length -ne [long]$file.bytes) { throw "Missing or wrong size: $path" }
        $stream = $entry.Open()
        try { $hash = [Convert]::ToHexStringLower([Security.Cryptography.SHA256]::HashData($stream)) }
        finally { $stream.Dispose() }
        if ($hash -cne [string]$file.sha256) { throw "Hash mismatch: $path" }
    }
    if ((Get-Item -LiteralPath $Package).Length -gt 50MB) { throw 'Package exceeds current draft size limit' }
    "Verified $($manifest.files.Count) files in spatial game draft: $Package"
}
finally { $archive.Dispose() }
