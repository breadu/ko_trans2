param([Parameter(Mandatory)][string]$Destination, [string]$CrtDirectory = '')
$ErrorActionPreference = 'Stop'
# App-local redistribution from the licensed Visual Studio Redist tree only.
# Never borrow DLLs from System32, another application, or a user's PATH.
if (!$CrtDirectory) {
    $candidates = foreach ($programRoot in @($env:ProgramFiles, ${env:ProgramFiles(x86)}) | Select-Object -Unique) {
        Get-ChildItem -Path "$programRoot/Microsoft Visual Studio/*/*/VC/Redist/MSVC/*/x64/Microsoft.VC*.CRT" -Directory -ErrorAction SilentlyContinue
    }
    $CrtDirectory = ($candidates | Sort-Object { [version]$_.Parent.Parent.Name } -Descending | Select-Object -First 1).FullName
}
if (!$CrtDirectory -or !(Test-Path -LiteralPath $CrtDirectory -PathType Container)) {
    throw 'Build dependency missing: install the Visual Studio C++ Redist build component or supply LocalWorkerCrtDirectory. End users must receive these DLLs in the package.'
}
$crt = Get-Item -LiteralPath $CrtDirectory
$openMp = Get-ChildItem -LiteralPath $crt.Parent.FullName -Directory -Filter 'Microsoft.VC*.OPENMP' |
    Where-Object { Test-Path -LiteralPath (Join-Path $_.FullName 'vcomp140.dll') } | Select-Object -First 1
if (!$openMp) { throw 'The matching Visual Studio OpenMP Redist directory is missing.' }
$files = @((Get-ChildItem -LiteralPath $crt.FullName -File -Filter '*.dll')) + @(Get-Item -LiteralPath (Join-Path $openMp.FullName 'vcomp140.dll'))
foreach ($required in @('msvcp140.dll','vcruntime140.dll','vcruntime140_1.dll','vcomp140.dll')) {
    if ($files.Name -notcontains $required) { throw "Missing app-local dependency: $required" }
}
foreach ($file in $files) {
    $signature = Get-AuthenticodeSignature -LiteralPath $file.FullName
    if ($signature.Status -ne 'Valid' -or $signature.SignerCertificate.Subject -notmatch 'O=Microsoft Corporation') {
        throw "The official runtime signature could not be verified: $($file.Name)"
    }
}
$target = [IO.Path]::GetFullPath($Destination)
[IO.Directory]::CreateDirectory($target) | Out-Null
$manifest = foreach ($file in $files) {
    $output = Join-Path $target $file.Name
    if (!(Test-Path -LiteralPath $output) -or (Get-FileHash -LiteralPath $output -Algorithm SHA256).Hash -ne (Get-FileHash -LiteralPath $file.FullName -Algorithm SHA256).Hash) {
        Copy-Item -LiteralPath $file.FullName -Destination $output
    }
    [ordered]@{ name = $file.Name; version = $file.VersionInfo.FileVersion; sha256 = (Get-FileHash -LiteralPath $output -Algorithm SHA256).Hash }
}
$manifest | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $target 'local-worker-runtime.json') -Encoding utf8
