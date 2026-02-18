param(
    [Parameter(Mandatory = $true)]
    [string]$PublishDir,
    [Parameter(Mandatory = $true)]
    [string]$FixturePath
)

$ErrorActionPreference = "Stop"

$exePath = Join-Path $PublishDir "PioneerConverter.exe"
if (!(Test-Path $exePath)) {
    throw "Expected executable not found: $exePath"
}

Write-Host "Running startup check"
& $exePath | Out-Null
if ($LASTEXITCODE -ne 0) {
    throw "Startup check failed with exit code $LASTEXITCODE"
}

if (!(Test-Path $FixturePath)) {
    throw "Fixture missing: $FixturePath"
}

$fixtureInfo = Get-Item $FixturePath
if ($fixtureInfo.Length -eq 0) {
    Write-Host "Fixture is empty, skipping conversion smoke test: $FixturePath"
    exit 0
}

$tmpDir = Join-Path ([System.IO.Path]::GetTempPath()) ("pioneerconverter-smoke-" + [System.Guid]::NewGuid().ToString())
New-Item -ItemType Directory -Path $tmpDir | Out-Null

try {
    $tmpFixture = Join-Path $tmpDir "smoke.raw"
    Copy-Item -Path $FixturePath -Destination $tmpFixture

    Write-Host "Running conversion smoke test"
    $outputDir = Join-Path $tmpDir "custom_out"
    & $exePath $tmpFixture -b 50 -n 1 -o $outputDir
    if ($LASTEXITCODE -ne 0) {
        throw "Conversion smoke test failed with exit code $LASTEXITCODE"
    }

    $outputFile = Join-Path $outputDir "smoke.arrow"
    if (!(Test-Path $outputFile)) {
        throw "Expected output file missing: $outputFile"
    }

    $outputInfo = Get-Item $outputFile
    if ($outputInfo.Length -eq 0) {
        throw "Expected output file is empty: $outputFile"
    }

    $completeHash = (Get-FileHash -Path $outputFile -Algorithm SHA256).Hash

    Write-Host "Running skip-existing smoke check for complete output"
    & $exePath $tmpFixture -b 50 -n 1 -o $outputDir --skip-existing
    if ($LASTEXITCODE -ne 0) {
        throw "Skip-existing smoke check failed with exit code $LASTEXITCODE"
    }

    $afterCompleteSkipHash = (Get-FileHash -Path $outputFile -Algorithm SHA256).Hash
    if ($afterCompleteSkipHash -ne $completeHash) {
        throw "Expected output to remain unchanged with --skip-existing: $outputFile"
    }

    [System.IO.File]::WriteAllText($outputFile, "skip-existing-sentinel")
    $sentinelHash = (Get-FileHash -Path $outputFile -Algorithm SHA256).Hash

    Write-Host "Running skip-existing smoke check for incomplete output"
    & $exePath $tmpFixture -b 50 -n 1 -o $outputDir --skip-existing
    if ($LASTEXITCODE -ne 0) {
        throw "Skip-existing smoke check failed with exit code $LASTEXITCODE"
    }

    $afterHash = (Get-FileHash -Path $outputFile -Algorithm SHA256).Hash
    if ($afterHash -eq $sentinelHash) {
        throw "Expected incomplete output to be reconverted with --skip-existing: $outputFile"
    }
}
finally {
    Remove-Item -Path $tmpDir -Recurse -Force -ErrorAction SilentlyContinue
}

Write-Host "Smoke test passed"
