param([switch]$Test)

$ErrorActionPreference = 'Stop'
$pilotCompiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
if (-not (Test-Path -LiteralPath $pilotCompiler)) {
    $pilotCompiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework\v4.0.30319\csc.exe'
}
if (-not (Test-Path -LiteralPath $pilotCompiler)) {
    throw 'The .NET Framework 4.x C# compiler is required on Windows.'
}

Push-Location $PSScriptRoot
try {
    New-Item -ItemType Directory -Path 'dist' -Force | Out-Null
    $pilotReferences = @('/reference:System.Windows.Forms.dll', '/reference:System.Drawing.dll', '/reference:System.Web.Extensions.dll')
    $pilotSource = Join-Path $PSScriptRoot 'source\FlClashPilot.cs'
    $pilotManifest = Join-Path $PSScriptRoot 'source\app.manifest'
    $pilotOutput = Join-Path $PSScriptRoot 'dist\FlClashPilot.exe'
    & $pilotCompiler /nologo /codepage:65001 /optimize+ /target:winexe "/win32manifest:$pilotManifest" "/out:$pilotOutput" @pilotReferences $pilotSource
    if ($LASTEXITCODE -ne 0) { throw 'Application compilation failed.' }

    $pilotHash = (Get-FileHash -LiteralPath 'dist/FlClashPilot.exe' -Algorithm SHA256).Hash.ToLowerInvariant()
    [System.IO.File]::WriteAllText((Join-Path $PSScriptRoot 'SHA256SUMS.txt'), "$pilotHash  dist/FlClashPilot.exe`n", [System.Text.Encoding]::ASCII)
    Write-Host 'Built dist/FlClashPilot.exe; SHA256SUMS.txt updated.'

    if ($Test) {
        if (-not (Get-Command node -ErrorAction SilentlyContinue)) { throw 'Node.js is required to run the mock integration tests.' }
        New-Item -ItemType Directory -Path 'build' -Force | Out-Null
        $pilotTestOutput = Join-Path $PSScriptRoot 'build\PilotTests.exe'
        $pilotTestSource = Join-Path $PSScriptRoot 'tests\PilotTests.cs'
        & $pilotCompiler /nologo /codepage:65001 /optimize+ /target:exe /main:PilotTests "/out:$pilotTestOutput" @pilotReferences $pilotSource $pilotTestSource
        if ($LASTEXITCODE -ne 0) { throw 'Test compilation failed.' }
        & node tests/verify.mjs
        if ($LASTEXITCODE -ne 0) { throw 'Tests failed.' }
    }
}
finally { Pop-Location }

