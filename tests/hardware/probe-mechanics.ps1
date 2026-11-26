# Checks, on real hardware, the Windows calls LOTUS depends on. Needs an elevated prompt.
#
#   A. A virtual disk is attached, disabled, detached, re-enabled while detached and
#      attached again. This proves the lasting disable and the cleanup of a device
#      that is no longer connected.
#   B. One real USB device that Windows allows to be disabled is switched off and on.
#   C. One USB keyboard, which Windows does not allow to be disabled, is removed
#      from the running system and brought back.
#
# Every step is undone before the script ends. Results go to the log file.

param(
    [string]$Probe = '',
    [string]$Log = '',
    [string]$DisableTarget = '',
    [string]$KeyboardTarget = '',
    [switch]$SkipKeyboard
)

$ErrorActionPreference = 'Continue'
if (-not $Probe) { $Probe = Join-Path $PSScriptRoot '..\Lotus.Probe\bin\Release\Lotus.Probe.exe' }
if (-not $Log) { $Log = Join-Path $env:TEMP 'lotus-probe.log' }
$Probe = [System.IO.Path]::GetFullPath($Probe)
Set-Content -Path $Log -Value "LOTUS hardware probe $(Get-Date -Format s)" -Encoding utf8

function Say($text) { Add-Content -Path $Log -Value $text -Encoding utf8 }
function Run([string[]]$probeArgs) {
    Say ">> probe $($probeArgs -join ' ')"
    $out = & $Probe @probeArgs 2>&1
    foreach ($line in $out) { Say "   $line" }
    Say "   exit=$LASTEXITCODE"
    return ($out -join "`n")
}
function DiskPart([string[]]$lines) {
    $script = Join-Path $env:TEMP 'lotus-probe-diskpart.txt'
    Set-Content -Path $script -Value $lines -Encoding ascii
    $out = & diskpart.exe /s $script 2>&1
    Say ">> diskpart: $($lines -join ' ; ')"
    Say "   $(($out | Where-Object { $_ -match '\S' } | Select-Object -Last 2) -join ' | ')"
    Remove-Item $script -ErrorAction SilentlyContinue
}

$admin = ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole(
    [Security.Principal.WindowsBuiltInRole]::Administrator)
Say "elevated=$admin"
if (-not $admin) { Say 'RESULT: not elevated, nothing was tested'; exit 1 }

# ---------- A. virtual disk ----------
Say ''
Say '=== A. virtual disk: lasting disable, cleanup while detached ==='
$vhd = Join-Path $env:TEMP 'lotus-probe.vhd'
if (Test-Path $vhd) { DiskPart @("select vdisk file=`"$vhd`"", 'detach vdisk'); Remove-Item $vhd -Force -ErrorAction SilentlyContinue }
try {
    $before = (& $Probe find 'PROD_VIRTUAL_DISK') -split "`n" | Where-Object { $_ }
    $watch = Start-Job -ScriptBlock { param($p) & $p watch 9000 } -ArgumentList $Probe
    Start-Sleep -Milliseconds 1500
    DiskPart @("create vdisk file=`"$vhd`" maximum=16 type=expandable", 'attach vdisk')
    Start-Sleep -Seconds 3
    $after = (& $Probe find 'PROD_VIRTUAL_DISK') -split "`n" | Where-Object { $_ }
    $disk = $after | Where-Object { $before -notcontains $_ } | Select-Object -First 1
    Say "virtual disk instance: $disk"
    $watchOut = Receive-Job $watch -Wait -AutoRemoveJob
    Say "watcher: $($watchOut -join ' | ')"

    if ($disk) {
        Run @('status', $disk) | Out-Null
        Run @('disable', $disk) | Out-Null
        DiskPart @("select vdisk file=`"$vhd`"", 'detach vdisk')
        Start-Sleep -Seconds 2
        Say '-- detached while disabled: the mark should still be there (configFlags bit 0)'
        Run @('status', $disk) | Out-Null
        DiskPart @("select vdisk file=`"$vhd`"", 'attach vdisk')
        Start-Sleep -Seconds 3
        Say '-- attached again while disabled: should come back disabled (problem 22)'
        Run @('status', $disk) | Out-Null
        DiskPart @("select vdisk file=`"$vhd`"", 'detach vdisk')
        Start-Sleep -Seconds 2
        Say '-- enable while detached: should clear the mark'
        Run @('enable', $disk) | Out-Null
        DiskPart @("select vdisk file=`"$vhd`"", 'attach vdisk')
        Start-Sleep -Seconds 3
        Say '-- attached after cleanup: should be started with no problem'
        Run @('status', $disk) | Out-Null
    } else {
        Say 'RESULT A: could not identify the virtual disk'
    }
}
finally {
    DiskPart @("select vdisk file=`"$vhd`"", 'detach vdisk')
    Remove-Item $vhd -Force -ErrorAction SilentlyContinue
    Say "vhd removed=$(-not (Test-Path $vhd))"
}

# ---------- B. a real USB device that can be disabled ----------
Say ''
Say '=== B. real USB device: disable and enable ==='
if ($DisableTarget) {
    try {
        Run @('status', $DisableTarget) | Out-Null
        Run @('disable', $DisableTarget) | Out-Null
        Run @('present', $DisableTarget) | Out-Null
    }
    finally {
        Run @('enable', $DisableTarget) | Out-Null
        Start-Sleep -Seconds 2
        Run @('status', $DisableTarget) | Out-Null
    }
} else { Say 'skipped: no target given' }

# ---------- C. keyboard: Windows refuses to disable it ----------
Say ''
Say '=== C. keyboard: disable is refused, removal is used instead ==='
if ($KeyboardTarget -and -not $SkipKeyboard) {
    try {
        Run @('status', $KeyboardTarget) | Out-Null
        Say '-- disable should be refused and leave no mark behind'
        Run @('disable', $KeyboardTarget) | Out-Null
        Say '-- remove'
        Run @('remove', $KeyboardTarget) | Out-Null
        Start-Sleep -Seconds 1
        Run @('present', $KeyboardTarget) | Out-Null
        Say '-- a hardware rescan must not bring it back while it is blocked'
        & pnputil.exe /scan-devices | Out-Null
        Start-Sleep -Seconds 2
        Run @('status', $KeyboardTarget) | Out-Null
        Run @('present', $KeyboardTarget) | Out-Null
    }
    finally {
        Say '-- restore'
        Run @('restore', $KeyboardTarget) | Out-Null
        Start-Sleep -Seconds 2
        $final = Run @('status', $KeyboardTarget)
        if ($final -notmatch 'started=True') {
            Say '-- still not started, asking Windows to rescan'
            & pnputil.exe /scan-devices | Out-Null
            Start-Sleep -Seconds 3
            Run @('status', $KeyboardTarget) | Out-Null
        }
    }
} else { Say 'skipped' }

Say ''
Say 'RESULT: finished'
