$ErrorActionPreference = 'Stop'

$compilerPaths = @(
    (Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'),
    (Join-Path $env:WINDIR 'Microsoft.NET\Framework\v4.0.30319\csc.exe')
)
$compiler = $compilerPaths | Where-Object { Test-Path -LiteralPath $_ } | Select-Object -First 1
if (-not $compiler) {
    throw 'The .NET Framework C# compiler was not found on this PC.'
}

$sourcePath = Join-Path $PSScriptRoot 'CallNotesLauncher.cs'
$outputPath = Join-Path $PSScriptRoot 'CallNotes.exe'
if (-not (Test-Path -LiteralPath $sourcePath)) {
    throw "Required build input was not found: $sourcePath"
}

& $compiler /nologo /target:exe /platform:anycpu "/out:$outputPath" $sourcePath
if ($LASTEXITCODE -ne 0) {
    throw "CallNotes compilation failed with exit code $LASTEXITCODE."
}

Write-Output "Built standalone C# application $outputPath"
