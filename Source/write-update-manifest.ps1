[CmdletBinding()]
param([Parameter(Mandatory)][string] $Archive)

$ErrorActionPreference = 'Stop'
$archivePath = [IO.Path]::GetFullPath($Archive)
$output = Join-Path (Split-Path -Parent $archivePath) 'VPNDesktop-win-x64.files.json'
$stream = [IO.File]::OpenRead($archivePath)
try {
    $reader = [IO.BinaryReader]::new($stream, [Text.Encoding]::UTF8, $true)
    $zip = [IO.Compression.ZipArchive]::new($stream, [IO.Compression.ZipArchiveMode]::Read, $true)
    try {
        # Compress-Archive produces a single-disk ZIP with a standard footer and no ZIP comment.
        $stream.Position = $stream.Length - 22
        if ($reader.ReadUInt32() -ne 0x06054b50 -or $reader.ReadUInt32() -ne 0) {
            throw 'Unsupported ZIP footer for range updates.'
        }
        $diskEntries = $reader.ReadUInt16()
        $entryCount = $reader.ReadUInt16()
        $centralSize = $reader.ReadUInt32()
        $centralOffset = $reader.ReadUInt32()
        if ($reader.ReadUInt16() -ne 0 -or $diskEntries -ne $entryCount -or $zip.Entries.Count -ne $entryCount -or
            [long]$centralOffset + $centralSize -ne $stream.Length - 22) {
            throw 'Unsupported ZIP directory for range updates.'
        }
        $nextEntry = [long]$centralOffset
        $files = foreach ($entry in $zip.Entries) {
            $stream.Position = $nextEntry
            if ($reader.ReadUInt32() -ne 0x02014b50) { throw 'Invalid central ZIP header.' }
            $stream.Position = $nextEntry + 8
            $flags = $reader.ReadUInt16()
            $method = $reader.ReadUInt16()
            $stream.Position = $nextEntry + 20
            $compressed = $reader.ReadUInt32()
            $size = $reader.ReadUInt32()
            $nameSize = $reader.ReadUInt16()
            $extraSize = $reader.ReadUInt16()
            $commentSize = $reader.ReadUInt16()
            $stream.Position = $nextEntry + 42
            $localOffset = $reader.ReadUInt32()
            $name = [Text.Encoding]::UTF8.GetString($reader.ReadBytes($nameSize)).Replace('\', '/')
            $nextEntry += 46 + $nameSize + $extraSize + $commentSize
            if ($name -ne $entry.FullName.Replace('\', '/') -or $compressed -ne $entry.CompressedLength -or $size -ne $entry.Length) {
                throw 'ZIP entry does not match its directory.'
            }
            if ($name.EndsWith('/')) { continue }
            if (!$name.StartsWith('VPNDesktop/', [StringComparison]::Ordinal) -or ($flags -band 1) -or $method -notin @(0, 8)) {
                throw 'Unsupported ZIP entry for range updates.'
            }
            $stream.Position = $localOffset
            if ($reader.ReadUInt32() -ne 0x04034b50) { throw 'Invalid local ZIP header.' }
            $stream.Position = [long]$localOffset + 26
            $localNameSize = $reader.ReadUInt16()
            $localExtraSize = $reader.ReadUInt16()
            $offset = [long]$localOffset + 30 + $localNameSize + $localExtraSize
            if ($offset + $compressed -gt $centralOffset) { throw 'ZIP data overlaps its directory.' }
            $content = $entry.Open()
            try { $hash = (Get-FileHash -InputStream $content -Algorithm SHA256).Hash }
            finally { $content.Dispose() }
            [ordered]@{
                Path = $name.Substring('VPNDesktop/'.Length)
                Size = [long]$size
                Sha256 = $hash.ToLowerInvariant()
                Offset = $offset
                CompressedSize = [long]$compressed
                Compression = [int]$method
            }
        }
        if ($nextEntry -ne [long]$centralOffset + $centralSize) { throw 'Invalid ZIP directory size.' }
    }
    finally { $zip.Dispose(); $reader.Dispose() }
}
finally { $stream.Dispose() }

$manifest = [ordered]@{
    ArchiveSize = (Get-Item -LiteralPath $archivePath).Length
    ArchiveSha256 = (Get-FileHash -LiteralPath $archivePath -Algorithm SHA256).Hash.ToLowerInvariant()
    Files = @($files)
}
$manifest | ConvertTo-Json -Depth 4 -Compress | Set-Content -LiteralPath $output -Encoding utf8NoBOM
