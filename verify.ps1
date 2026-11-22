#Requires -Version 7.0
# The gate: a pass-through to run.ps1, so the gate and the one-command entry point cannot diverge.
# The roadmap engine runs `verify.ps1` in an owner repository as its gate; this repository's checks
# live in run.ps1, which is what a contributor runs, so this file only forwards and carries the exit.
$ErrorActionPreference = "Stop"
$global:LASTEXITCODE = 0
& (Join-Path $PSScriptRoot "run.ps1") @args
exit $LASTEXITCODE