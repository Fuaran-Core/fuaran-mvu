#Requires -Version 7.0
<#
.SYNOPSIS
    Check the Fuaran.MVU repository: tools, formatting, build, tests, the proof leg and the Fable leg.

.DESCRIPTION
    Drop into this directory and run one command: `pwsh ./run.ps1`. Needs PowerShell 7, the .NET SDK that
    global.json pins, and Node (for the two legs that run under it). Stages, in order; the first failure
    stops the run and exits non-zero, naming the stage:

      1. tools    dotnet tool restore (Fantomas, Fable).
      2. format   fantomas --check over src/ and tests/ (.fantomasignore keeps the generated oracle
                  and the derived Fable stand-in out of it).
      3. build    dotnet build Fuaran.MVU.slnx: the kernel, the three test projects, and the oracle project
                  the prover generated.
      4. tests    the Expecto suite on .NET (the laws, the differential against the extracted models, the
                  claims ladder held to the tree); then the React adapter's
                  laws on .NET (tests/Fuaran.MVU.React.Tests), and its negative build, which must FAIL: a
                  governed-surface admission of a component whose view is an element view; then the browser
                  package's laws on .NET (tests/Fuaran.MVU.Browser.Tests).
      5. proofs   the proof leg, pwsh ./proofs/check.ps1: the pinned kit verifies the three models from a cold
                  cache, holds the committed oracle to a fresh extraction, builds it, runs the Fable oracle
                  under Node and the .NET host's Proofs.Oracle and Proofs.Ladder families. One run per model by
                  default; -ProofRuns 3 -Strict is the recorded form.
      6. fable    the kernel under Fable: tests/Fuaran.MVU.Tests/Node transpiled with the repository's `fable`
                  tool and run under Node, the laws and the differential both; then the React adapter's laws
                  (tests/Fuaran.MVU.React.Tests) under Node, transpiled in Debug and again in Release (a
                  production build's configuration), and the check that a production (Release)
                  transpile of Fuaran.MVU.React carries no hot reload; then the browser package's laws
                  (tests/Fuaran.MVU.Browser.Tests) under Node, with its live binding against stubbed globals.

    Every stage seeds $global:LASTEXITCODE before it runs a native command, so a stage that is skipped, or
    that runs no native command, cannot inherit another stage's verdict. A skipped stage reports SKIPPED,
    never PASS.

.PARAMETER SkipFormat
    Skip stage 2 while iterating.

.PARAMETER SkipBuild
    Skip stage 3. The later stages then build what they need themselves.

.PARAMETER SkipTests
    Skip stage 4.

.PARAMETER SkipProofs
    Skip stage 5.

.PARAMETER SkipFable
    Skip stage 6.

.PARAMETER ProofRuns
    Cold-cache verifications of every model in stage 5. One by default; three for a recorded run.

.PARAMETER Strict
    Stage 5's recorded form: an incomplete budget declaration is red.

.PARAMETER KitAsset
    The proof kit's release asset as a local file, for a machine without the release (the sha256 is verified
    all the same). Also read from the FUARAN_PROOFS_KIT_ASSET environment variable.
#>
[CmdletBinding()]
param(
    [switch] $SkipFormat,
    [switch] $SkipBuild,
    [switch] $SkipTests,
    [switch] $SkipProofs,
    [switch] $SkipFable,
    [int] $ProofRuns = 1,
    [switch] $Strict,
    [string] $KitAsset
)

$ErrorActionPreference = "Stop"
Set-Location $PSScriptRoot
[Console]::OutputEncoding = [System.Text.UTF8Encoding]::new($false)

function Write-Stage([string] $Name) { Write-Host "`n=== $Name ===" -ForegroundColor Cyan }
function Write-Skip([string] $Name, [string] $Why) { Write-Host "`n=== ${Name}: SKIPPED ($Why) ===" -ForegroundColor Yellow }

function Stop-OnFailure([string] $Name) {
    if ($global:LASTEXITCODE -ne 0) {
        Write-Host "FAIL - $Name (exit $global:LASTEXITCODE)" -ForegroundColor Red
        exit $global:LASTEXITCODE
    }
    Write-Host "PASS - $Name" -ForegroundColor Green
}

# 1. tools
Write-Stage "tools"
$global:LASTEXITCODE = 0
dotnet tool restore
Stop-OnFailure "tools"

# 2. format
if ($SkipFormat) {
    Write-Skip "format" "-SkipFormat"
}
else {
    Write-Stage "format"
    $global:LASTEXITCODE = 0
    dotnet fantomas --check src tests
    Stop-OnFailure "format"
}

# 3. build
if ($SkipBuild) {
    Write-Skip "build" "-SkipBuild"
}
else {
    Write-Stage "build"
    $global:LASTEXITCODE = 0
    dotnet build Fuaran.MVU.slnx --nologo
    Stop-OnFailure "build"
}

# 4. tests
if ($SkipTests) {
    Write-Skip "tests" "-SkipTests"
}
else {
    Write-Stage "tests"
    $global:LASTEXITCODE = 0
    if ($SkipBuild) {
        dotnet run --project tests/Fuaran.MVU.Tests -- --summary
    }
    else {
        dotnet run --project tests/Fuaran.MVU.Tests --no-build -- --summary
    }
    Stop-OnFailure "tests (Fuaran.MVU.Tests)"



    # The React adapter (Phase 720, DECISIONS.md D29): its laws on .NET, then the build that must FAIL - a
    # governed-surface admission of a component whose view is an element view - and must fail for that reason.
    $global:LASTEXITCODE = 0
    if ($SkipBuild) {
        dotnet run --project tests/Fuaran.MVU.React.Tests
    }
    else {
        dotnet run --project tests/Fuaran.MVU.React.Tests --no-build
    }
    Stop-OnFailure "tests (Fuaran.MVU.React.Tests)"

    Write-Host "--- Fuaran.MVU.React.Tests -p:FuaranMvuNegative=governed (must fail to compile)"
    $global:LASTEXITCODE = 0
    $negative = dotnet build tests/Fuaran.MVU.React.Tests/Fuaran.MVU.React.Tests.fsproj --nologo -p:FuaranMvuNegative=governed 2>&1 | Out-String
    $negativeExit = $global:LASTEXITCODE
    # Build the ordinary project again, so the tree's outputs are the ordinary build's.
    $global:LASTEXITCODE = 0
    dotnet build tests/Fuaran.MVU.React.Tests/Fuaran.MVU.React.Tests.fsproj --nologo *> $null
    Stop-OnFailure "tests (Fuaran.MVU.React.Tests rebuilt after the negative build)"

    if ($negativeExit -eq 0 -or $negative -notmatch "'ElementView<Msg>' is not compatible with the type 'IGovernedView'") {
        Write-Host $negative
        Write-Host "FAIL - a governed admission of an element view compiled, or failed for another reason" -ForegroundColor Red
        $global:LASTEXITCODE = 1
        exit 1
    }

    Write-Host "PASS - a governed admission of an element view is refused by the compiler" -ForegroundColor Green
    $global:LASTEXITCODE = 0

    # The browser package (Phase 719, DECISIONS.md D30): its laws on .NET, against a stand-in for the DOM.
    $global:LASTEXITCODE = 0
    if ($SkipBuild) {
        dotnet run --project tests/Fuaran.MVU.Browser.Tests
    }
    else {
        dotnet run --project tests/Fuaran.MVU.Browser.Tests --no-build
    }
    Stop-OnFailure "tests (Fuaran.MVU.Browser.Tests)"
}

# 5. proofs
if ($SkipProofs) {
    Write-Skip "proofs" "-SkipProofs"
}
else {
    Write-Stage "proofs"
    $legArgs = @("-Runs", $ProofRuns)
    if ($Strict) { $legArgs += "-Strict" }
    if ($KitAsset) { $legArgs += @("-KitAsset", $KitAsset) }
    $global:LASTEXITCODE = 0
    pwsh -NoProfile -File (Join-Path "proofs" "check.ps1") @legArgs
    Stop-OnFailure "proofs"
}

# 6. fable
if ($SkipFable) {
    Write-Skip "fable" "-SkipFable"
}
else {
    Write-Stage "fable"
    $nodeProject = Join-Path "tests" "Fuaran.MVU.Tests" "Node"
    $nodeOut = Join-Path $nodeProject "output"
    $global:LASTEXITCODE = 0
    dotnet fable (Join-Path $nodeProject "Fuaran.MVU.Tests.Node.fsproj") -o $nodeOut --noCache
    Stop-OnFailure "fable (transpile tests/Fuaran.MVU.Tests/Node)"

    $node = Get-Command node -CommandType Application -ErrorAction SilentlyContinue | Select-Object -First 1
    if ($null -eq $node) {
        Write-Host "FAIL - fable: the Node runner needs node, and there is no node on PATH" -ForegroundColor Red
        $global:LASTEXITCODE = 1
        exit 1
    }
    $global:LASTEXITCODE = 0
    & $node.Source (Join-Path $nodeOut "Main.js")
    Stop-OnFailure "fable (the kernel's laws and the differential under Node)"

    # The React adapter (Phase 720, DECISIONS.md D29) under Fable: its laws, transpiled in Debug (the configuration
    # Fable's watch mode uses, which carries hot reload) and run under Node with no React installed; then the package
    # alone transpiled in Release, the configuration of a production build, which must carry no hot reload, and in
    # Debug, which must (so the check can fail). The output goes under bin/, which git and Fantomas ignore.
    $reactProject = Join-Path "tests" "Fuaran.MVU.React.Tests"
    $reactOut = Join-Path $reactProject "bin" "fable"
    $global:LASTEXITCODE = 0
    dotnet fable (Join-Path $reactProject "Fuaran.MVU.React.Tests.fsproj") -o $reactOut -c Debug --noCache
    Stop-OnFailure "fable (transpile tests/Fuaran.MVU.React.Tests)"
    $global:LASTEXITCODE = 0
    & $node.Source (Join-Path $reactOut "ReactLaws.js")
    Stop-OnFailure "fable (the React adapter's laws under Node)"

    # The same laws transpiled in Release, the configuration of a production build, where Fable optimises more than
    # in Debug: what a function value's identity is in a consumer's bundle (the view's one dispatch) shows only here.
    $reactReleaseOut = Join-Path $reactProject "bin" "fable-release"
    $global:LASTEXITCODE = 0
    dotnet fable (Join-Path $reactProject "Fuaran.MVU.React.Tests.fsproj") -o $reactReleaseOut -c Release --noCache
    Stop-OnFailure "fable (transpile tests/Fuaran.MVU.React.Tests, Release)"
    $global:LASTEXITCODE = 0
    & $node.Source (Join-Path $reactReleaseOut "ReactLaws.js")
    Stop-OnFailure "fable (the React adapter's laws under Node, Release)"

    $hotReloadMarker = "fuaran-mvu-react:hot-reload"
    foreach ($configuration in @("Release", "Debug")) {
        $packageOut = Join-Path "src" "Fuaran.MVU.React" "bin" "fable-$($configuration.ToLowerInvariant())"
        $global:LASTEXITCODE = 0
        dotnet fable (Join-Path "src" "Fuaran.MVU.React" "Fuaran.MVU.React.fsproj") -o $packageOut -c $configuration --noCache
        Stop-OnFailure "fable (transpile src/Fuaran.MVU.React, $configuration)"
        $carriers = @(Get-ChildItem -Path $packageOut -Recurse -Filter *.js |
            Where-Object { $_.FullName -notmatch "fable_modules" } |
            Where-Object { (Get-Content -Raw $_.FullName).Contains($hotReloadMarker) })
        $carries = $carriers.Count -gt 0
        if ($configuration -eq "Release" -and $carries) {
            Write-Host "FAIL - a production (Release) transpile of Fuaran.MVU.React carries hot reload: $($carriers.Name -join ', ')" -ForegroundColor Red
            $global:LASTEXITCODE = 1
            exit 1
        }
        if ($configuration -eq "Debug" -and -not $carries) {
            Write-Host "FAIL - a Debug transpile of Fuaran.MVU.React carries no hot reload, so the Release check measures nothing" -ForegroundColor Red
            $global:LASTEXITCODE = 1
            exit 1
        }
    }
    Write-Host "PASS - hot reload is compiled out of a production transpile of Fuaran.MVU.React, and present in Debug" -ForegroundColor Green
    $global:LASTEXITCODE = 0

    # The browser package (Phase 719, DECISIONS.md D30) under Fable: its laws, transpiled and run under Node, where the
    # live binding also runs against stubbed globals (the one law compiled under Fable alone). Output under bin/.
    $browserProject = Join-Path "tests" "Fuaran.MVU.Browser.Tests"
    $browserOut = Join-Path $browserProject "bin" "fable"
    $global:LASTEXITCODE = 0
    dotnet fable (Join-Path $browserProject "Fuaran.MVU.Browser.Tests.fsproj") -o $browserOut --noCache
    Stop-OnFailure "fable (transpile tests/Fuaran.MVU.Browser.Tests)"
    $global:LASTEXITCODE = 0
    & $node.Source (Join-Path $browserOut "BrowserLaws.js")
    Stop-OnFailure "fable (the browser package's laws under Node)"
}

Write-Host "`nEvery stage that ran passed." -ForegroundColor Green
