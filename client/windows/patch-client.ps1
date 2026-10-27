<#
.SYNOPSIS
    Apply HearthDAoC's client patch set to an OfflineDAoC client folder (Windows).

.DESCRIPTION
    patch-client.ps1 [-Client DIR] [-PatchSet FILE] [-Bundle DIR] [-Restore | -Check]

    connect-hearthdaoc.bat runs this script before every start of the game; players can also
    double-click patch-client.bat, which runs it too. Put both files and the patches folder next
    to connect-hearthdaoc.bat, in runtime\client-opendaoc\app of the OfflineDAoC install.

    -Client    the client folder, the one with game.dll (default: this script's folder)
    -PatchSet  the patch set (default: patches\classic-creation.json next to this script; each of
               the others, patches\default-layout.json, si-portals.json and si-portal-art.json,
               is applied by its own run)
    -Bundle    the folder with the bundled files, such as splash.mpk (default: the patch set's folder)
    -Check     only report each file's state
    -Restore   put the original files back (only over files that are still the patched ones)

    A retired set ("retired": true: a release took it back) is restored by a run without -Check
    too, so connect-hearthdaoc.bat needs no change to take it back.

    The rules, messages and exit codes are the ones of the Linux applier,
    client/patches/apply_patches.py with client/patches/patchset.py:
    - A file whose SHA-256 is its "after" hash is already patched and is skipped. A file whose
      "after" is "absent" (a remove operation) is already patched when it is missing.
    - A file that is missing, or that is neither the expected original nor the patched file,
      is refused. Then nothing at all is changed: every file is checked, and every new content
      built and verified, before anything is written.
    - The original is backed up once as <file>.hearthdaoc-orig. A backup that isn't the
      original is replaced by the verified original. A removed file is renamed over its backup.
    - An mpk-text-replace operation rebuilds the MPK archive byte for byte as patchset.py does.
    - A restore puts a backup back only over the patched file (for "absent", only where the
      file is missing). A file that has changed since it was patched (for example a newer
      client) or is missing is refused, and then nothing is restored.
    - Every write goes through a temporary file in the same folder and then replaces the file.
    - Only the patch set's own relative paths inside the client folder are touched.
    - A patch set's "refused" text, when it has one, follows "is not the file this HearthDAoC
      release supports" for a refused file.

    Exit codes:
      0  patched, already patched or restored (-Check: every file is known)
      1  a file couldn't be read or written
      2  bad usage, or an invalid patch set or bundle
      3  refused: a client file is unknown or missing, or a backup isn't the original, or
         (-Restore) a file has changed since it was patched; nothing was changed

    Players run Windows PowerShell 5.1: no PowerShell 7-only syntax or .NET Core-only APIs,
    and keep this file ASCII with CRLF line ends.
#>
$ErrorActionPreference = 'Stop'

$BackupSuffix = '.hearthdaoc-orig'
$Absent = 'absent'
$OpNames = @('replace', 'append', 'text-replace', 'mpk-text-replace', 'file', 'remove')
$OpFields = @{
    'replace'          = @('offset', 'from', 'to')
    'append'           = @('data')
    'text-replace'     = @('find', 'replace')
    'mpk-text-replace' = @('entry', 'find', 'replace')
    'file'             = @('source')
    'remove'           = @()
}
$Latin1 = [Text.Encoding]::GetEncoding(28591)
$MpkMagic = [byte[]](0x4D, 0x50, 0x41, 0x4B, 2)  # "MPAK", then 2
$MpkRecord = 284  # one directory record, as in patchset.py
$Usage = 'Usage: patch-client.ps1 [-Client DIR] [-PatchSet FILE] [-Bundle DIR] [-Restore | -Check]'
$UnknownMessage = 'Not patched: {0} is not the file this HearthDAoC release supports {1}'
# What follows for a patch set without its own "refused" text (classic-creation.json).
$DefaultRefused = '(for example the b edition, or an older or newer upstream client). The client still works ' +
    'with the standard creation screen.'
$MissingMessage = 'Not patched: {0} is missing from the client folder.'
$ChangedMessage = 'Not restored: {0} has changed since it was patched (for example a newer client was installed); ' +
    'the saved original is kept as {0}{1}.'
$GoneMessage = 'Not restored: {0} is missing from the client folder; the saved original is kept as {0}{1}.'

function New-Crc32Table {
    # zlib's CRC-32 table. Every 32-bit value is a [long]: Windows PowerShell reads a hex literal
    # above 0x7FFFFFFF as a negative [int], hence the decimals.
    $table = New-Object 'long[]' 256
    for ($n = 0; $n -lt 256; $n++) {
        [long]$c = $n
        for ($k = 0; $k -lt 8; $k++) {
            if ($c -band 1) { $c = 3988292384 -bxor ($c -shr 1) } else { $c = $c -shr 1 }
        }
        $table[$n] = $c
    }
    return , $table
}
$Crc32Table = New-Crc32Table

function Write-Out([string]$Text) { [Console]::Out.WriteLine($Text) }

function Write-Err([string]$Text) { [Console]::Error.WriteLine($Text) }

function New-PatchError([string]$Message) {
    # Patch-set and check failures (PatchError in patchset.py) are ApplicationExceptions;
    # anything else is a file system error.
    New-Object System.ApplicationException $Message
}

function Get-Cause($ErrorRecord) {
    $e = $ErrorRecord.Exception
    while ($e -is [System.Management.Automation.MethodInvocationException] -and $null -ne $e.InnerException) {
        $e = $e.InnerException
    }
    $e
}

function Format-Value($Value) {
    # Python's repr() for the values a patch set can hold, as patchset.py prints them.
    if ($null -eq $Value) { return 'None' }
    if ($Value -is [string]) { return "'" + $Value + "'" }
    return [string]$Value
}

function Test-Object($Value) { $Value -is [System.Management.Automation.PSCustomObject] }

function Test-List($Value) { $Value -is [System.Collections.IList] }

function Test-Integer($Value) { ($Value -is [int]) -or ($Value -is [long]) }

function Test-Sha256($Value) { ($Value -is [string]) -and ($Value -cmatch '^[0-9a-f]{64}\z') }

function Test-Field($Object, [string]$Name) {
    # JSON keys are case-sensitive, PowerShell property names are not.
    $p = $Object.PSObject.Properties[$Name]
    ($null -ne $p) -and ($p.Name -ceq $Name)
}

function Get-Field($Object, [string]$Name) {
    if (Test-Field $Object $Name) { return , $Object.PSObject.Properties[$Name].Value }
    return $null
}

function Get-FullPath([string]$Path) {
    # Relative to PowerShell's current location, which .NET's current directory may not follow.
    $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($Path)
}

function Join-Rel([string]$Base, [string]$Rel) {
    $path = $Base
    foreach ($part in $Rel.Split('/')) { $path = [IO.Path]::Combine($path, $part) }
    $path
}

function Assert-SafePath($Rel) {
    # Only a relative path with forward slashes that stays inside its folder.
    $ok = ($Rel -is [string]) -and $Rel.Length -gt 0 -and -not $Rel.StartsWith('/', [StringComparison]::Ordinal) -and
        -not $Rel.Contains('\') -and -not $Rel.Contains(':')
    if ($ok) {
        foreach ($part in $Rel.Split('/')) {
            if ($part -eq '' -or $part -eq '.' -or $part -eq '..') { $ok = $false }
        }
    }
    if (-not $ok) { throw (New-PatchError ('unsafe path: ' + (Format-Value $Rel))) }
}

function ConvertFrom-Hex($Value, [string]$Where) {
    if (-not ($Value -is [string]) -or -not ($Value -cmatch '^(?:[0-9a-f]{2})+\z')) {
        throw (New-PatchError ('{0}: not lower-case hex bytes' -f $Where))
    }
    $bytes = New-Object byte[] ($Value.Length / 2)
    for ($i = 0; $i -lt $bytes.Length; $i++) { $bytes[$i] = [Convert]::ToByte($Value.Substring(2 * $i, 2), 16) }
    return , $bytes
}

function Assert-Op($Op, [string]$Where) {
    $kind = $null
    if (Test-Object $Op) { $kind = Get-Field $Op 'op' }
    if (-not ($kind -is [string]) -or -not ($kind -cin $OpNames)) {
        throw (New-PatchError ('{0}: unknown operation' -f $Where))
    }
    foreach ($key in $OpFields[$kind]) {
        if (-not (Test-Field $Op $key)) { throw (New-PatchError ("{0}: {1} needs '{2}'" -f $Where, $kind, $key)) }
    }
    if ($kind -ceq 'replace') {
        $offset = Get-Field $Op 'offset'
        if (-not (Test-Integer $offset) -or $offset -lt 0) {
            throw (New-PatchError ('{0}: offset must be a whole number, 0 or more' -f $Where))
        }
        $from = ConvertFrom-Hex (Get-Field $Op 'from') $Where
        $to = ConvertFrom-Hex (Get-Field $Op 'to') $Where
        if ($from.Length -ne $to.Length) {
            throw (New-PatchError ("{0}: 'from' and 'to' must have the same length" -f $Where))
        }
    } elseif ($kind -ceq 'append') {
        $null = ConvertFrom-Hex (Get-Field $Op 'data') $Where
    } elseif ($kind -ceq 'text-replace' -or $kind -ceq 'mpk-text-replace') {
        if ($kind -ceq 'mpk-text-replace') {
            $name = Get-Field $Op 'entry'
            $ok = ($name -is [string]) -and $name.Length -gt 0 -and $name.Length -lt 256 -and
                -not $name.Contains([string][char]0) -and -not ($name -cmatch '[^\x00-\xFF]')
            if (-not $ok) {
                $message = "{0}: 'entry' must be an entry name (1 to 255 Latin-1 characters, no NUL)" -f $Where
                throw (New-PatchError $message)
            }
        }
        foreach ($key in @('find', 'replace')) {
            $text = Get-Field $Op $key
            if (-not ($text -is [string])) { throw (New-PatchError ("{0}: '{1}' must be text" -f $Where, $key)) }
            if ($text -cmatch '[^\x00-\xFF]') {
                throw (New-PatchError ("{0}: '{1}' has characters outside Latin-1" -f $Where, $key))
            }
        }
        if ((Get-Field $Op 'find').Length -eq 0) { throw (New-PatchError ("{0}: 'find' is empty" -f $Where)) }
    } elseif ($kind -ceq 'file') {
        Assert-SafePath (Get-Field $Op 'source')
    }
}

function Read-PatchSet([string]$Path, [string]$Shown) {
    # Read and validate a patch set like patchset.load; $Shown is the path as given, for messages.
    try {
        $data = ConvertFrom-Json -InputObject ([IO.File]::ReadAllText($Path, [Text.Encoding]::UTF8))
    } catch {
        throw (New-PatchError ('cannot read patch set {0}: {1}' -f $Shown, (Get-Cause $_).Message))
    }
    $format = $null
    if (Test-Object $data) { $format = Get-Field $data 'format' }
    if (-not (Test-Integer $format) -or $format -ne 1) {
        throw (New-PatchError ('{0}: not a format 1 patch set' -f $Shown))
    }
    if ((Test-Field $data 'refused') -and -not ((Get-Field $data 'refused') -is [string])) {
        throw (New-PatchError ("{0}: 'refused' must be text" -f $Shown))
    }
    if ((Test-Field $data 'retired') -and -not ((Get-Field $data 'retired') -is [bool])) {
        throw (New-PatchError ("{0}: 'retired' must be true or false" -f $Shown))
    }
    $files = Get-Field $data 'files'
    if (-not (Test-List $files) -or $files.Count -eq 0) {
        throw (New-PatchError ('{0}: the patch set lists no files' -f $Shown))
    }
    $seen = @{}
    $n = 0
    foreach ($entry in $files) {
        $n++
        if (-not (Test-Object $entry)) { throw (New-PatchError ('file {0}: not an object' -f $n)) }
        $rel = Get-Field $entry 'path'
        Assert-SafePath $rel
        $key = $rel.ToLowerInvariant()
        if ($seen.ContainsKey($key)) { throw (New-PatchError ('{0}: listed twice' -f $rel)) }
        $seen[$key] = $true
        $before = Get-Field $entry 'before'
        $after = Get-Field $entry 'after'
        $ops = Get-Field $entry 'ops'
        if (-not (Test-Sha256 $before)) {
            throw (New-PatchError ("{0}: 'before' must be a lower-case SHA-256" -f $rel))
        }
        if (-not ((Test-Sha256 $after) -or ($after -ceq 'source') -or ($after -ceq $Absent))) {
            $message = "{0}: 'after' must be a lower-case SHA-256, `"source`" or `"absent`"" -f $rel
            throw (New-PatchError $message)
        }
        if ($before -ceq $after) { throw (New-PatchError ("{0}: 'before' and 'after' are the same" -f $rel)) }
        if (-not (Test-List $ops) -or $ops.Count -eq 0) { throw (New-PatchError ('{0}: no operations' -f $rel)) }
        $i = 0
        $removes = 0
        foreach ($op in $ops) {
            $i++
            Assert-Op $op ('{0} op {1}' -f $rel, $i)
            if ((Get-Field $op 'op') -ceq 'remove') { $removes++ }
        }
        if ($after -ceq 'source' -and ($ops.Count -ne 1 -or (Get-Field $ops[0] 'op') -cne 'file')) {
            throw (New-PatchError ('{0}: "after": "source" needs exactly one file operation' -f $rel))
        }
        if ($after -ceq $Absent -and ($ops.Count -ne 1 -or $removes -ne 1)) {
            throw (New-PatchError ('{0}: "after": "absent" needs exactly one remove operation' -f $rel))
        }
        if ($after -cne $Absent -and $removes -gt 0) {
            throw (New-PatchError ('{0}: a remove operation needs "after": "absent"' -f $rel))
        }
    }
    return $data
}

function Get-Sha256([byte[]]$Data) {
    $sha = [Security.Cryptography.SHA256]::Create()
    try { $hash = $sha.ComputeHash($Data) } finally { $sha.Dispose() }
    ([BitConverter]::ToString($hash) -replace '-', '').ToLowerInvariant()
}

function Get-FileSha256([string]$Path) {
    (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash.ToLowerInvariant()
}

function Get-Bundled([string]$BundleDir, [string]$Rel) {
    $path = Join-Rel $BundleDir $Rel
    if (-not $BundleDir -or -not [IO.File]::Exists($path)) {
        throw (New-PatchError ('bundled file missing: {0}' -f $path))
    }
    $path
}

function Get-AfterHash($Entry, [string]$BundleDir) {
    $after = Get-Field $Entry 'after'
    if ($after -ceq 'source') {
        $ops = Get-Field $Entry 'ops'
        return Get-FileSha256 (Get-Bundled $BundleDir (Get-Field $ops[0] 'source'))
    }
    $after
}

function Get-Crc32([byte[]]$Data) {
    # zlib.crc32, from 0 to 4294967295.
    $table = $Crc32Table
    [long]$c = 4294967295
    foreach ($b in $Data) { $c = $table[($c -bxor $b) -band 255] -bxor ($c -shr 8) }
    $c -bxor 4294967295
}

function Get-Adler32([byte[]]$Data) {
    # zlib.adler32: the sums are taken modulo 65521 every 5552 bytes, as zlib does.
    [long]$a = 1
    [long]$s = 0
    $k = 0
    while ($k -lt $Data.Length) {
        $end = [Math]::Min($k + 5552, $Data.Length)
        for (; $k -lt $end; $k++) {
            $a += $Data[$k]
            $s += $a
        }
        $a %= 65521
        $s %= 65521
    }
    $s * 65536 + $a
}

function Get-Slice([byte[]]$Data, [long]$Start, [long]$Count) {
    $slice = New-Object byte[] $Count
    [Array]::Copy($Data, $Start, $slice, 0, $Count)
    return , $slice
}

function Add-UInt32([IO.MemoryStream]$Stream, [long]$Value) {
    # Little-endian, like struct.pack("<I").
    $Stream.Write([BitConverter]::GetBytes([uint32]$Value), 0, 4)
}

function ConvertTo-ZlibStored([byte[]]$Data) {
    # patchset.py's _zlib_stored: 78 01, stored blocks of at most 65535 bytes (one empty block for no
    # data), then the Adler-32, big-endian. .NET Framework's DeflateStream packs differently from zlib,
    # so both appliers write this.
    $out = New-Object IO.MemoryStream
    $out.WriteByte(0x78)
    $out.WriteByte(0x01)
    $start = 0
    do {
        $length = [Math]::Min(65535, $Data.Length - $start)
        if ($start + 65535 -ge $Data.Length) { $out.WriteByte(1) } else { $out.WriteByte(0) }
        $out.Write([BitConverter]::GetBytes([uint16]$length), 0, 2)
        $out.Write([BitConverter]::GetBytes([uint16](65535 -bxor $length)), 0, 2)
        $out.Write($Data, $start, $length)
        $start += 65535
    } while ($start -lt $Data.Length)
    $adler = [BitConverter]::GetBytes([uint32](Get-Adler32 $Data))
    [Array]::Reverse($adler)
    $out.Write($adler, 0, 4)
    return , $out.ToArray()
}

function Expand-Zlib([byte[]]$Packed, [long]$Size) {
    # patchset.py's _inflate: the $Size bytes the zlib stream holds, or $null when it holds anything
    # else. DeflateStream reads the deflate data after the 2-byte header; at most $Size + 1 bytes are
    # inflated.
    $skip = [Math]::Min(2, $Packed.Length)
    $source = New-Object IO.MemoryStream -ArgumentList $Packed, $skip, ($Packed.Length - $skip), $false
    $mode = [IO.Compression.CompressionMode]::Decompress
    $inflater = New-Object IO.Compression.DeflateStream -ArgumentList $source, $mode
    $out = New-Object IO.MemoryStream
    $buffer = New-Object byte[] 65536
    try {
        while ($out.Length -le $Size) {
            $got = $inflater.Read($buffer, 0, [int][Math]::Min(65536, $Size + 1 - $out.Length))
            if ($got -eq 0) { break }
            $out.Write($buffer, 0, $got)
        }
    } catch {
        return $null
    } finally {
        $inflater.Dispose()
    }
    if ($out.Length -ne $Size) { return $null }
    return , $out.ToArray()
}

function Invoke-MpkTextReplace([byte[]]$Data, $Op, [int]$Index) {
    # patchset.py's _mpk_text_replace, which says how the archive is rebuilt: the same bytes, every
    # time, from both appliers.
    $refused = 'op {0} (mpk-text-replace): ' -f $Index
    $magic = $Data.Length -ge 21
    for ($k = 0; $magic -and $k -lt 5; $k++) { if ($Data[$k] -ne $MpkMagic[$k]) { $magic = $false } }
    if (-not $magic) { throw (New-PatchError ($refused + 'not an MPK archive')) }
    $header = New-Object byte[] 16
    for ($k = 0; $k -lt 16; $k++) { $header[$k] = $Data[5 + $k] -bxor $k }
    $crc = [long][BitConverter]::ToUInt32($header, 0)
    $sizeDir = [long][BitConverter]::ToUInt32($header, 4)
    $sizeName = [long][BitConverter]::ToUInt32($header, 8)
    $count = [long][BitConverter]::ToUInt32($header, 12)
    $area = 21 + $sizeName + $sizeDir
    $damaged = $refused + 'the MPK archive is damaged'
    if ($area -gt $Data.Length) { throw (New-PatchError $damaged) }
    $packedDir = Get-Slice $Data (21 + $sizeName) $sizeDir
    if ((Get-Crc32 $packedDir) -ne $crc) { throw (New-PatchError $damaged) }
    $directory = Expand-Zlib $packedDir ($MpkRecord * $count)
    if ($null -eq $directory) { throw (New-PatchError $damaged) }
    # Per file: Head (name field, timestamp and flags: 264 bytes), Size, Packed (the compressed bytes),
    # Crc (4 bytes) and Name (the name field up to its first NUL).
    $files = @()
    for ($k = 0; $k -lt $count; $k++) {
        $base = $MpkRecord * $k
        $size = [long][BitConverter]::ToUInt32($directory, $base + 268)
        $offset = [long][BitConverter]::ToUInt32($directory, $base + 272)
        $packedSize = [long][BitConverter]::ToUInt32($directory, $base + 276)
        if ($area + $offset + $packedSize -gt $Data.Length) { throw (New-PatchError $damaged) }
        $nameEnd = [Array]::IndexOf($directory, [byte]0, $base, 256)
        if ($nameEnd -lt 0) { $nameEnd = $base + 256 }
        $files += [pscustomobject]@{
            Head = Get-Slice $directory $base 264; Size = $size; Packed = Get-Slice $Data ($area + $offset) $packedSize
            Crc = Get-Slice $directory ($base + 280) 4; Name = $Latin1.GetString($directory, $base, $nameEnd - $base)
        }
    }
    $name = Get-Field $Op 'entry'
    $found = @($files | Where-Object { [string]::Equals($_.Name, $name, [StringComparison]::Ordinal) })
    if ($found.Count -ne 1) {
        $message = 'the archive has {0} entries named {1}, not exactly one' -f $found.Count, $name
        throw (New-PatchError ($refused + $message))
    }
    $entry = $found[0]
    $content = $null
    if ((Get-Crc32 $entry.Packed) -eq [long][BitConverter]::ToUInt32($entry.Crc, 0)) {
        $content = Expand-Zlib $entry.Packed $entry.Size
    }
    if ($null -eq $content) { throw (New-PatchError ($refused + ('{0} in the archive is damaged' -f $name))) }
    $find = Get-Field $Op 'find'
    $text = $Latin1.GetString($content)
    $times = 0
    $at = $text.IndexOf($find, [StringComparison]::Ordinal)
    while ($at -ge 0) {
        $times++
        $at = $text.IndexOf($find, $at + $find.Length, [StringComparison]::Ordinal)
    }
    if ($times -ne 1) {
        throw (New-PatchError ($refused + ('found the text {0} times in {1}, not exactly once' -f $times, $name)))
    }
    $new = $Latin1.GetBytes($text.Replace($find, (Get-Field $Op 'replace')))
    $entry.Size = $new.Length
    $entry.Packed = ConvertTo-ZlibStored $new
    $entry.Crc = [BitConverter]::GetBytes([uint32](Get-Crc32 $entry.Packed))
    $dir = New-Object IO.MemoryStream
    $payload = New-Object IO.MemoryStream
    [long]$mem = 0
    foreach ($f in $files) {
        $dir.Write($f.Head, 0, 264)
        Add-UInt32 $dir $mem
        Add-UInt32 $dir $f.Size
        Add-UInt32 $dir $payload.Length
        Add-UInt32 $dir $f.Packed.Length
        $dir.Write($f.Crc, 0, 4)
        $mem = ($mem + $f.Size) % 4294967296
        $payload.Write($f.Packed, 0, $f.Packed.Length)
    }
    $packedDir = ConvertTo-ZlibStored $dir.ToArray()
    $out = New-Object IO.MemoryStream
    $out.Write($MpkMagic, 0, 5)
    $head = New-Object IO.MemoryStream
    Add-UInt32 $head (Get-Crc32 $packedDir)
    Add-UInt32 $head $packedDir.Length
    Add-UInt32 $head $sizeName
    Add-UInt32 $head $count
    $header = $head.ToArray()
    for ($k = 0; $k -lt 16; $k++) { $out.WriteByte($header[$k] -bxor $k) }
    $out.Write($Data, 21, [int]$sizeName)
    $out.Write($packedDir, 0, $packedDir.Length)
    $out.Write($payload.ToArray(), 0, [int]$payload.Length)
    return , $out.ToArray()
}

function Invoke-Ops([byte[]]$Data, $Ops, [string]$BundleDir) {
    # Apply the ops in order to a copy of $Data and return the new bytes, like patchset.transform.
    $out = [byte[]]$Data.Clone()
    $i = 0
    foreach ($op in $Ops) {
        $i++
        $kind = Get-Field $op 'op'
        if ($kind -ceq 'replace') {
            $start = [long](Get-Field $op 'offset')
            $old = ConvertFrom-Hex (Get-Field $op 'from') 'from'
            $new = ConvertFrom-Hex (Get-Field $op 'to') 'to'
            $same = ($start + $old.Length) -le $out.Length
            for ($k = 0; $same -and $k -lt $old.Length; $k++) {
                if ($out[[int]$start + $k] -ne $old[$k]) { $same = $false }
            }
            if (-not $same) {
                $message = 'op {0} (replace at 0x{1:x}): the bytes there are not the expected ones' -f $i, $start
                throw (New-PatchError $message)
            }
            [Array]::Copy($new, 0, $out, [int]$start, $new.Length)
        } elseif ($kind -ceq 'append') {
            $tail = ConvertFrom-Hex (Get-Field $op 'data') 'data'
            $grown = New-Object byte[] ($out.Length + $tail.Length)
            [Array]::Copy($out, $grown, $out.Length)
            [Array]::Copy($tail, 0, $grown, $out.Length, $tail.Length)
            $out = $grown
        } elseif ($kind -ceq 'text-replace') {
            # Latin-1 maps every byte to one character and back, so CRLF and other bytes are kept.
            $find = Get-Field $op 'find'
            $text = $Latin1.GetString($out)
            $count = 0
            $at = $text.IndexOf($find, [StringComparison]::Ordinal)
            while ($at -ge 0) {
                $count++
                $at = $text.IndexOf($find, $at + $find.Length, [StringComparison]::Ordinal)
            }
            if ($count -ne 1) {
                $message = 'op {0} (text-replace): found the text {1} times, not exactly once' -f $i, $count
                throw (New-PatchError $message)
            }
            $out = $Latin1.GetBytes($text.Replace($find, (Get-Field $op 'replace')))
        } elseif ($kind -ceq 'mpk-text-replace') {
            $out = Invoke-MpkTextReplace $out $op $i
        } elseif ($kind -ceq 'file') {
            $out = [IO.File]::ReadAllBytes((Get-Bundled $BundleDir (Get-Field $op 'source')))
        } elseif ($kind -ceq 'remove') {
            throw (New-PatchError ('op {0} (remove): a removed file has no new content' -f $i))
        } else {
            throw (New-PatchError ("op {0}: unknown operation '{1}'" -f $i, $kind))
        }
    }
    return , $out
}

function Get-States([string]$ClientDir, $Files, [string]$BundleDir) {
    # One object per file, in patch-set order: Path, State (unpatched, patched, unknown or missing;
    # a missing file whose "after" is "absent" is patched), Target and Entry.
    foreach ($entry in $Files) {
        $rel = Get-Field $entry 'path'
        $target = Join-Rel $ClientDir $rel
        if (-not [IO.File]::Exists($target)) {
            if ((Get-Field $entry 'after') -ceq $Absent) { $state = 'patched' } else { $state = 'missing' }
        } else {
            $digest = Get-FileSha256 $target
            if ($digest -ceq (Get-AfterHash $entry $BundleDir)) {
                $state = 'patched'
            } elseif ($digest -ceq (Get-Field $entry 'before')) {
                $state = 'unpatched'
            } else {
                $state = 'unknown'
            }
        }
        [pscustomobject]@{ Path = $rel; State = $state; Target = $target; Entry = $entry }
    }
}

function Get-RestoreStates([string]$ClientDir, $Files, [string]$BundleDir) {
    # One object per file, in patch-set order, like patchset.restore_status: Path, State (patched,
    # no-backup, original, wrong-backup, missing or changed; for "absent", a good backup and no file
    # is patched), Target and Backup.
    foreach ($entry in $Files) {
        $rel = Get-Field $entry 'path'
        $target = Join-Rel $ClientDir $rel
        $backup = $target + $BackupSuffix
        $before = Get-Field $entry 'before'
        if (-not [IO.File]::Exists($backup)) {
            $state = 'no-backup'
        } elseif ((Get-FileSha256 $backup) -cne $before) {
            $state = 'wrong-backup'
        } elseif (-not [IO.File]::Exists($target)) {
            if ((Get-Field $entry 'after') -ceq $Absent) { $state = 'patched' } else { $state = 'missing' }
        } else {
            $digest = Get-FileSha256 $target
            if ($digest -ceq $before) {
                $state = 'original'
            } elseif ($digest -ceq (Get-AfterHash $entry $BundleDir)) {
                $state = 'patched'
            } else {
                $state = 'changed'
            }
        }
        [pscustomobject]@{ Path = $rel; State = $state; Target = $target; Backup = $backup }
    }
}

function Move-Over([string]$Source, [string]$Destination) {
    # Like Python's os.replace. File.Replace is ReplaceFile on Windows and rename on Unix; a
    # $null backup name would reach .NET as '', hence NullString.
    if ([IO.File]::Exists($Destination)) {
        [IO.File]::Replace($Source, $Destination, [System.Management.Automation.Language.NullString]::Value)
    } else {
        [IO.File]::Move($Source, $Destination)
    }
}

function Write-Atomic([string]$Path, [byte[]]$Data) {
    # Write through a temporary file in the same folder, flushed to disk, then replace $Path.
    $name = '.hearthdaoc-' + [Guid]::NewGuid().ToString('N') + '.tmp'
    $tmp = [IO.Path]::Combine([IO.Path]::GetDirectoryName($Path), $name)
    try {
        $stream = New-Object IO.FileStream($tmp, [IO.FileMode]::CreateNew, [IO.FileAccess]::Write)
        try {
            $stream.Write($Data, 0, $Data.Length)
            $stream.Flush($true)
        } finally {
            $stream.Dispose()
        }
        Move-Over $tmp $Path
    } catch {
        if ([IO.File]::Exists($tmp)) { [IO.File]::Delete($tmp) }
        throw
    }
}

# Command line, parsed by hand so that every usage error exits with 2, as in apply_patches.py.
$Options = @{ Client = ''; PatchSet = ''; Bundle = ''; Restore = $false; Check = $false; Help = $false }
$UsageError = ''
$i = 0
while ($i -lt $args.Count -and -not $UsageError) {
    $arg = [string]$args[$i]
    $name = ''
    if ($arg -match '^--?([A-Za-z]+)$') { $name = $Matches[1] }
    if ($name -in @('Client', 'PatchSet', 'Bundle')) {
        if ($i + 1 -lt $args.Count) {
            $Options[$name] = [string]$args[$i + 1]
            $i++
        } else {
            $UsageError = $arg + ' needs a value'
        }
    } elseif ($name -in @('Restore', 'Check', 'Help')) {
        $Options[$name] = $true
    } else {
        $UsageError = 'unknown argument: ' + $arg
    }
    $i++
}
if ($Options.Help -and -not $UsageError) {
    Write-Out $Usage
    exit 0
}
if (-not $UsageError -and $Options.Restore -and $Options.Check) {
    $UsageError = '-Restore and -Check cannot be used together'
}
if ($UsageError) {
    Write-Err $Usage
    Write-Err ('patch-client.ps1: error: ' + $UsageError)
    exit 2
}
if (-not $Options.Client) { $Options.Client = $PSScriptRoot }
if (-not $Options.PatchSet) {
    $Options.PatchSet = [IO.Path]::Combine($PSScriptRoot, 'patches', 'classic-creation.json')
}

try {
    $ClientDir = Get-FullPath $Options.Client
    if (-not [IO.Directory]::Exists($ClientDir)) {
        Write-Err ('Error: client folder not found: {0}' -f $Options.Client)
        exit 2
    }
    $PatchSetPath = Get-FullPath $Options.PatchSet
    try {
        $Set = Read-PatchSet $PatchSetPath $Options.PatchSet
    } catch {
        Write-Err ('Error: invalid patch set: {0}' -f (Get-Cause $_).Message)
        exit 2
    }
    $Files = Get-Field $Set 'files'
    if ($Options.Bundle) {
        $BundleDir = Get-FullPath $Options.Bundle
    } else {
        $BundleDir = [IO.Path]::GetDirectoryName($PatchSetPath)
    }

    $Retired = (Test-Field $Set 'retired') -and (Get-Field $Set 'retired') -eq $true
    if ($Options.Restore -or ($Retired -and -not $Options.Check)) {
        # Check every file first: a backup goes back only over the patched file, and any problem
        # means nothing is restored.
        $Restore = @(Get-RestoreStates $ClientDir $Files $BundleDir)
        $wrong = @($Restore | Where-Object { $_.State -eq 'wrong-backup' } | ForEach-Object { $_.Path + $BackupSuffix })
        if ($wrong.Count -gt 0) {
            Write-Out ('Not restored: not the original file: {0}. Nothing was changed.' -f ($wrong -join ', '))
        }
        $refused = $wrong.Count
        foreach ($s in $Restore) {
            if ($s.State -eq 'changed') {
                Write-Out ($ChangedMessage -f $s.Path, $BackupSuffix)
                $refused++
            } elseif ($s.State -eq 'missing') {
                Write-Out ($GoneMessage -f $s.Path, $BackupSuffix)
                $refused++
            }
        }
        if ($refused -gt 0) { exit 3 }
        foreach ($s in $Restore) {
            if ($s.State -eq 'patched') { Move-Over $s.Backup $s.Target }
        }
        foreach ($s in $Restore) {
            if ($s.State -eq 'patched') {
                Write-Out ('Restored: {0}' -f $s.Path)
            } else {
                Write-Out ('Nothing to restore: {0}' -f $s.Path)
            }
        }
        exit 0
    }

    $States = @(Get-States $ClientDir $Files $BundleDir)
    $Refused = @($States | Where-Object { $_.State -eq 'unknown' -or $_.State -eq 'missing' })
    if ($Options.Check) {
        foreach ($s in $States) { Write-Out ('{0}: {1}' -f $s.Path, $s.State) }
        if ($Refused.Count -gt 0) { exit 3 }
        exit 0
    }
    if ($Refused.Count -gt 0) {
        $why = $DefaultRefused
        if (Test-Field $Set 'refused') { $why = Get-Field $Set 'refused' }
        foreach ($s in $Refused) {
            if ($s.State -eq 'unknown') {
                Write-Out ($UnknownMessage -f $s.Path, $why)
            } else {
                Write-Out ($MissingMessage -f $s.Path)
            }
        }
        exit 3
    }

    # Build and check every new content before the first write.
    $Work = @()
    foreach ($s in $States) {
        if ($s.State -eq 'patched') { continue }
        $before = Get-Field $s.Entry 'before'
        if ((Get-Field $s.Entry 'after') -ceq $Absent) {
            if ((Get-FileSha256 $s.Target) -cne $before) {
                throw (New-PatchError ('{0} changed while it was being checked; nothing changed' -f $s.Path))
            }
            $Work += [pscustomobject]@{ Target = $s.Target; Original = $null; New = $null; Before = $before }
            continue
        }
        $original = [IO.File]::ReadAllBytes($s.Target)
        if ((Get-Sha256 $original) -cne $before) {
            throw (New-PatchError ('{0} changed while it was being checked; nothing changed' -f $s.Path))
        }
        $new = Invoke-Ops $original (Get-Field $s.Entry 'ops') $BundleDir
        if ((Get-Sha256 $new) -cne (Get-AfterHash $s.Entry $BundleDir)) {
            $message = "{0}: the patched file doesn't have the expected SHA-256; nothing changed" -f $s.Path
            throw (New-PatchError $message)
        }
        $Work += [pscustomobject]@{ Target = $s.Target; Original = $original; New = $new; Before = $before }
    }
    foreach ($w in $Work) {
        $backup = $w.Target + $BackupSuffix
        if ($null -eq $w.New) {
            # "absent": the original itself becomes the backup, by a rename.
            Move-Over $w.Target $backup
            continue
        }
        if (-not ([IO.File]::Exists($backup) -and (Get-FileSha256 $backup) -ceq $w.Before)) {
            Write-Atomic $backup $w.Original
        }
        Write-Atomic $w.Target $w.New
    }
    foreach ($s in $States) {
        if ($s.State -eq 'patched') {
            Write-Out ('Already patched: {0}' -f $s.Path)
        } else {
            Write-Out ('Patched: {0} (original saved as {0}{1})' -f $s.Path, $BackupSuffix)
        }
    }
    exit 0
} catch {
    $cause = Get-Cause $_
    Write-Err ('Error: {0}' -f $cause.Message)
    if ($cause -is [System.ApplicationException]) { exit 2 }
    exit 1
}
