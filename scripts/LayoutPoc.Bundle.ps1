# .NET 10 apphost bundle manifest, version 6.0. Keep this reader scoped to the PoC.
# Format: dotnet/runtime, Microsoft.NET.HostModel/Bundle (MIT).
function Read-LayoutPocBundle([string]$Path) {
    $stream = [IO.File]::OpenRead($Path)
    $reader = [IO.BinaryReader]::new($stream)
    try {
        $signature = '8B1202B96A612038727B930214D7A03213F5B9E6EFAE3318EE3B2DCE24B36AAE'
        $hostPrefix = [Convert]::ToHexString($reader.ReadBytes(16MB))
        $marker = $hostPrefix.IndexOf($signature, [StringComparison]::Ordinal)
        if ($marker -lt 16 -or $marker % 2) { throw 'Missing .NET bundle marker.' }
        $stream.Position = $marker / 2 - 8
        $header = $reader.ReadInt64()
        if ($header -le $marker / 2 + 32 -or $header -gt $stream.Length - 64) { throw 'Invalid bundle offset.' }
        $stream.Position = $header
        if ($reader.ReadUInt32() -ne 6 -or $reader.ReadUInt32() -ne 0) { throw 'Unsupported bundle format.' }
        $count = $reader.ReadInt32()
        if ($count -lt 1 -or $count -gt 2048) { throw 'Invalid bundle file count.' }
        $null = $reader.ReadString()
        $null = $reader.ReadBytes(32)
        if ($reader.ReadUInt64() -ne 0) { throw 'Bundle-wide extraction is forbidden in this profile.' }
        $entries = @{}
        for ($i = 0; $i -lt $count; $i++) {
            $offset = $reader.ReadInt64()
            $size = $reader.ReadInt64()
            $compressed = $reader.ReadInt64()
            $type = $reader.ReadByte()
            $name = $reader.ReadString()
            if ($offset -lt $marker / 2 + 32 -or $size -lt 1 -or $offset -gt $header -or $size -gt $header - $offset -or $compressed -ne 0 -or $type -notin 1,3,4 -or
                $name -notmatch '^(?:[A-Za-z0-9_-]+/)*[A-Za-z0-9_-][A-Za-z0-9_.-]+$' -or $entries.ContainsKey($name)) {
                throw 'Invalid or unexpected bundle entry.'
            }
            $entries[$name] = @{ Offset = $offset; Size = $size; Type = $type }
        }
        foreach ($name in @('LibNMeCab.dll','LibNMeCab.IpaDicBin.dll','OpenCvSharpExtern.dll','onnxruntime.dll','DirectML.dll')) {
            if ($entries.ContainsKey($name)) { throw "Expected a separate library, not a bundled entry: $name" }
        }
        foreach ($name in @('KoTrans2.dll','System.Private.CoreLib.dll','PresentationFramework.dll','PresentationCore.dll','Microsoft.ML.OnnxRuntime.dll','OpenCvSharp.dll')) {
            if (!$entries.ContainsKey($name) -or $entries[$name].Type -ne 1) { throw "Missing managed bundle assembly: $name" }
        }
        $json = @{}
        foreach ($name in @('KoTrans2.runtimeconfig.json', 'KoTrans2.deps.json')) {
            $entry = $entries[$name]
            if (!$entry -or $entry.Size -gt 2MB -or $entry.Type -ne $(if ($name -like '*.deps.json') { 3 } else { 4 })) { throw "Missing bundle metadata: $name" }
            $stream.Position = $entry.Offset
            $json[$name] = [Text.Encoding]::UTF8.GetString($reader.ReadBytes([int]$entry.Size)).TrimStart([char]0xFEFF) | ConvertFrom-Json
        }
        return @{ Runtime = $json['KoTrans2.runtimeconfig.json']; Deps = $json['KoTrans2.deps.json']; Entries = $entries }
    } finally { $reader.Dispose() }
}
