param([Parameter(Mandatory)][string]$Python, [Parameter(Mandatory)][string]$Source, [string]$Output, [switch]$V6Medium)
$ErrorActionPreference='Stop'
if (!$Output) { $Output=Join-Path (Split-Path $PSScriptRoot -Parent) 'models' }
if ([IO.Path]::GetFullPath($Source) -eq [IO.Path]::GetFullPath($Output)) { throw 'Model source and output must differ.' }
if ($V6Medium) {
    & $Python -B "$PSScriptRoot/prepare-v6-models.py" --source $Source --output $Output
} else {
    & $Python "$PSScriptRoot/convert-models.py" --source $Source --output $Output
}
if ($LASTEXITCODE -ne 0) { throw 'Model conversion failed. Do not treat the engine as ready.' }
