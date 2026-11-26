# Runs the final LOTUS block and unblock code on real hardware. Needs an elevated prompt.
#
#   A. A USB device Windows allows to be disabled: block, check, unblock.
#   B. A USB keyboard, which Windows refuses to disable while it runs: block,
#      check that a rescan does not bring it back, unblock.
#   C. A device record for something that is not plugged in: mark it disabled,
#      then check that unblock clears the mark.
#
# Every step is undone before the script ends. Results go to the log file.

param(
    [string]$Probe = '',
    [string]$Log = '',
    [string]$DisableTarget = '',
    [string]$KeyboardTarget = '',
    [string]$AbsentTarget = ''
)

$ErrorActionPreference = 'Continue'
if (-not $Probe) { $Probe = Join-Path $PSScriptRoot '..\Lotus.Probe\bin\Release\Lotus.Probe.exe' }
if (-not $Log) { $Log = Join-Path $env:TEMP 'lotus-probe-block.log' }
$Probe = [System.IO.Path]::GetFullPath($Probe)
Set-Content -Path $Log -Value "LOTUS block probe $(Get-Date -Format s)" -Encoding utf8

function Say($text) { Add-Content -Path $Log -Value $text -Encoding utf8 }
function Run([string[]]$probeArgs) {
    Say ">> probe $($probeArgs -join ' ')"
    $out = & $Probe @probeArgs 2>&1
    foreach ($line in $out) { Say "   $line" }
    Say "   exit=$LASTEXITCODE"
    return ($out -join "`n")
}

$admin = ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole(
    [Security.Principal.WindowsBuiltInRole]::Administrator)
Say "elevated=$admin"
if (-not $admin) { Say 'RESULT: not elevated, nothing was tested'; exit 1 }

Say ''
Say '=== A. device that can be disabled ==='
if ($DisableTarget) {
    try {
        Run @('block', $DisableTarget) | Out-Null
        Run @('present', $DisableTarget) | Out-Null
    }
    finally {
        Run @('unblock', $DisableTarget) | Out-Null
        Start-Sleep -Seconds 1
        Run @('status', $DisableTarget) | Out-Null
    }
} else { Say 'skipped' }

Say ''
Say '=== B. keyboard ==='
if ($KeyboardTarget) {
    try {
        Run @('block', $KeyboardTarget) | Out-Null
        Run @('present', $KeyboardTarget) | Out-Null
        Say '-- hardware rescan while blocked'
        & pnputil.exe /scan-devices | Out-Null
        Start-Sleep -Seconds 2
        Run @('status', $KeyboardTarget) | Out-Null
    }
    finally {
        Run @('unblock', $KeyboardTarget) | Out-Null
        Start-Sleep -Seconds 2
        $final = Run @('status', $KeyboardTarget)
        if ($final -notmatch 'started=True') {
            Say '-- not started yet, running the manual restore'
            Run @('setflag', $KeyboardTarget, '0') | Out-Null
            Run @('restore', $KeyboardTarget) | Out-Null
            & pnputil.exe /scan-devices | Out-Null
            Start-Sleep -Seconds 3
            Run @('status', $KeyboardTarget) | Out-Null
        }
    }
} else { Say 'skipped' }

Say ''
Say '=== C. device that is not plugged in ==='
if ($AbsentTarget) {
    try {
        Run @('status', $AbsentTarget) | Out-Null
        Run @('setflag', $AbsentTarget, '1') | Out-Null
        Say '-- unblock should clear the mark'
        Run @('unblock', $AbsentTarget) | Out-Null
    }
    finally {
        Run @('setflag', $AbsentTarget, '0') | Out-Null
    }
} else { Say 'skipped' }

Say ''
Say 'RESULT: finished'
