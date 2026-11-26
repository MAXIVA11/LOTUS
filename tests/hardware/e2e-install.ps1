# End-to-end test of the real product on this PC. Needs an elevated prompt.
#
# It installs LOTUS with the real setup, arms a real policy, checks that the
# service blocks one chosen USB device and leaves the others alone, releases it
# again, checks read-only mode and a service restart, and then uninstalls and
# checks that nothing is left behind.
#
# -Target is the instance ID of a USB device that may be switched off for a few
# seconds. Every other connected device is put on the trusted list first.

param(
    [string]$Target = '',
    [string]$Setup = '',
    [string]$Log = '',
    [switch]$KeepInstalled
)

$ErrorActionPreference = 'Continue'
if (-not $Target) { Write-Host 'Pass -Target with the instance ID of a USB device that may be switched off briefly.'; exit 1 }
if (-not $Setup) { $Setup = Join-Path $PSScriptRoot '..\..\dist\LOTUS-Setup.exe' }
if (-not $Log) { $Log = Join-Path $env:TEMP 'lotus-e2e.log' }
$Setup = [System.IO.Path]::GetFullPath($Setup)
$installDir = Join-Path $env:ProgramFiles 'LOTUS'
$dataDir = Join-Path $env:ProgramData 'LOTUS'
$guard = Join-Path $installDir 'LotusGuard.exe'
$policyFile = Join-Path $dataDir 'policy.json'
$eventsFile = Join-Path $dataDir 'events.jsonl'
$roKey = 'HKLM:\SOFTWARE\Policies\Microsoft\Windows\RemovableStorageDevices'
$script:failed = 0
$script:passed = 0

Set-Content -Path $Log -Value "LOTUS end-to-end test $(Get-Date -Format s)" -Encoding utf8
function Say($text) { Add-Content -Path $Log -Value $text -Encoding utf8 }
function Check([string]$name, [bool]$ok, [string]$detail = '') {
    if ($ok) { $script:passed++; Say "PASS  $name" }
    else { $script:failed++; Say "FAIL  $name  $detail" }
}
function Wait-Until([scriptblock]$condition, [int]$seconds = 15) {
    $deadline = (Get-Date).AddSeconds($seconds)
    while ((Get-Date) -lt $deadline) {
        if (& $condition) { return $true }
        Start-Sleep -Milliseconds 400
    }
    return [bool](& $condition)
}
function Get-Scan {
    $text = (& $guard --scan) -join "`n"
    return $text | ConvertFrom-Json
}
function Get-Device($id) {
    return (Get-Scan).devices | Where-Object { $_.id -eq $id.ToUpperInvariant() }
}
function Write-Policy([bool]$armed, [string]$storage, [string]$pnp, [bool]$allowInput, $trusted) {
    $policy = [ordered]@{
        version = 1; armed = $armed; storage = $storage; pnp = $pnp; allowInput = $allowInput
        trusted = @($trusted)
    }
    $json = $policy | ConvertTo-Json -Depth 5
    [System.IO.File]::WriteAllText($policyFile, $json, (New-Object System.Text.UTF8Encoding $false))
}
function Test-ReadOnly {
    # True while any of the Deny_Write values LOTUS manages is set.
    foreach ($cls in '{53f5630d-b6bf-11d0-94f2-00a0c91efb8b}', '{53f56308-b6bf-11d0-94f2-00a0c91efb8b}', '{53f56311-b6bf-11d0-94f2-00a0c91efb8b}', '{6AC27878-A6FA-4155-BA85-F98F491D4F33}', '{F33FDC04-D1AC-4E8E-9A30-19BBD4B108AE}') {
        if ((Get-ItemProperty "$roKey\$cls" -ErrorAction SilentlyContinue).Deny_Write -eq 1) { return $true }
    }
    return $false
}
function Get-Events {
    if (-not (Test-Path $eventsFile)) { return @() }
    return Get-Content $eventsFile | Where-Object { $_ } | ForEach-Object { $_ | ConvertFrom-Json }
}

$admin = ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole(
    [Security.Principal.WindowsBuiltInRole]::Administrator)
Say "elevated=$admin target=$Target"
if (-not $admin) { Say 'RESULT: not elevated, nothing was tested'; exit 1 }
if (-not (Test-Path $Setup)) { Say "RESULT: setup not found at $Setup"; exit 1 }

$Target = $Target.ToUpperInvariant()

# ---------- 0. leftovers from an earlier, interrupted run ----------
# An early version of the uninstaller asked Windows to delete its last file at the
# next restart. Take those requests back, or they would hit a fresh install.
Say ''
Say '=== 0. clean start ==='
try {
    $hklm = [Microsoft.Win32.RegistryKey]::OpenBaseKey('LocalMachine', 'Registry64')
    $sm = $hklm.OpenSubKey('SYSTEM\CurrentControlSet\Control\Session Manager', $true)
    $old = $sm.GetValue('PendingFileRenameOperations', $null)
    if ($null -ne $old -and ($old.Length % 2) -eq 0) {
        $new = New-Object 'System.Collections.Generic.List[string]'
        $removed = 0
        for ($i = 0; $i -lt $old.Length; $i += 2) {
            $source = $old[$i]
            if ($source -like '*\C:\Program Files\LOTUS' -or $source -like '*\C:\Program Files\LOTUS\*') {
                $removed++
                Say "   taking back: $source"
                continue
            }
            $new.Add($old[$i]); $new.Add($old[$i + 1])
        }
        if ($removed -gt 0) {
            if ($new.Count -gt 0) { $sm.SetValue('PendingFileRenameOperations', $new.ToArray(), [Microsoft.Win32.RegistryValueKind]::MultiString) }
            else { $sm.DeleteValue('PendingFileRenameOperations', $false) }
            $after = $sm.GetValue('PendingFileRenameOperations', @())
            Check 'pending deletes: only the LOTUS entries were removed' ($after.Length -eq ($old.Length - 2 * $removed)) "before=$($old.Length) after=$($after.Length) removed=$removed"
        }
        else { Say '   no pending deletes for LOTUS' }
    }
    $sm.Close(); $hklm.Close()
}
catch { Say "   could not inspect pending deletes: $($_.Exception.Message)" }
if ((Test-Path $installDir) -and -not (Get-Service LotusGuard -ErrorAction SilentlyContinue)) {
    Remove-Item $installDir -Recurse -Force -ErrorAction SilentlyContinue
    Say "   removed leftover folder: $(-not (Test-Path $installDir))"
}
Check 'nothing is installed before the test' (-not (Test-Path $installDir) -and -not (Get-Service LotusGuard -ErrorAction SilentlyContinue))

try {
    # ---------- 1. install ----------
    Say ''
    Say '=== 1. install ==='
    $p = Start-Process -FilePath $Setup -ArgumentList '/quiet' -Wait -PassThru
    Check 'setup exits with 0' ($p.ExitCode -eq 0) "exit=$($p.ExitCode)"
    foreach ($f in 'LOTUS.exe', 'LotusGuard.exe', 'Lotus.Core.dll', 'Uninstall.exe', 'LICENSE.txt', 'LOTUS Notifier.lnk') {
        Check "installed file $f" (Test-Path (Join-Path $installDir $f))
    }
    $svc = Get-CimInstance Win32_Service -Filter "Name='LotusGuard'"
    Check 'service exists' ($null -ne $svc)
    Check 'service is running' ($svc.State -eq 'Running') "state=$($svc.State)"
    Check 'service starts automatically' ($svc.StartMode -eq 'Auto') "mode=$($svc.StartMode)"
    Check 'service runs as LocalSystem' ($svc.StartName -eq 'LocalSystem') "account=$($svc.StartName)"
    Check 'service path is quoted' ($svc.PathName -eq "`"$guard`"") "path=$($svc.PathName)"
    Check 'start menu shortcut' (Test-Path (Join-Path $env:ProgramData 'Microsoft\Windows\Start Menu\Programs\LOTUS.lnk'))
    Check 'desktop shortcut' (Test-Path (Join-Path ([Environment]::GetFolderPath('CommonDesktopDirectory')) 'LOTUS.lnk'))
    $run = (Get-ItemProperty 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Run' -ErrorAction SilentlyContinue).LOTUS
    Check 'notifier starts at sign-in' ($run -eq "`"$(Join-Path $installDir 'LOTUS.exe')`" --tray") "value=$run"
    $un = Get-ItemProperty 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\LOTUS' -ErrorAction SilentlyContinue
    Check 'listed in installed apps' ($un.DisplayName -eq 'LOTUS' -and $un.UninstallString -like '*Uninstall.exe" /uninstall')
    $acl = (& icacls.exe $dataDir) -join "`n"
    Say "   acl: $(($acl -split "`n" | Select-Object -First 4) -join ' | ')"
    Check 'policy folder: users can read' ($acl -match 'BUILTIN\\Users:\(OI\)\(CI\)\(RX\)')
    Check 'policy folder: users cannot write' (-not ($acl -match 'Users:[^\n]*\((F|M|W)\)'))
    Check 'policy folder: not inherited' (-not ($acl -match '\(I\)'))
    Check 'guard logged its start' (Wait-Until { @(Get-Events | Where-Object { $_.type -eq 'guard_started' }).Count -ge 1 } 10)
    Check 'notifier is running' (Wait-Until { @(Get-CimInstance Win32_Process -Filter "Name='LOTUS.exe'" | Where-Object { $_.CommandLine -like '*--tray*' }).Count -ge 1 } 10)

    # ---------- 2. nothing is blocked after install ----------
    Say ''
    Say '=== 2. disarmed after install ==='
    $scan = Get-Scan
    Say "   policy: $($scan.policy)"
    Check 'policy is disarmed' ($scan.policy -like 'disarmed*')
    Check 'target is present' ($null -ne ($scan.devices | Where-Object { $_.id -eq $Target }))
    Check 'no device is off' (@($scan.devices | Where-Object { $_.problem -ne 0 }).Count -eq 0)
    Check 'no ledger entries' (-not (Test-Path (Join-Path $dataDir 'ledger.json')) -or ((Get-Content (Join-Path $dataDir 'ledger.json') -Raw | ConvertFrom-Json).blocked.Count -eq 0))

    # ---------- 3. arm: target is blocked, everything else keeps working ----------
    Say ''
    Say '=== 3. armed, target not trusted ==='
    $others = @($scan.devices | Where-Object { $_.id -ne $Target } | ForEach-Object {
        [ordered]@{ kind = 'device'; id = $_.id; name = $_.name; added = (Get-Date).ToUniversalTime().ToString('s') + 'Z' }
    })
    Say "   trusting $($others.Count) other devices"
    Write-Policy $true 'block' 'block' $false $others
    $blocked = Wait-Until { (Get-Device $Target).state -eq 'Blocked' } 20
    $t = Get-Device $Target
    Say "   target: state=$($t.state) problem=$($t.problem) started=$($t.started) kinds=$($t.kinds)"
    Check 'target is blocked' $blocked "state=$($t.state)"
    Check 'target is switched off in Windows' ($t.problem -eq 22 -or $t.problem -eq 21) "problem=$($t.problem)"
    $scan = Get-Scan
    $bad = @($scan.devices | Where-Object { $_.id -ne $Target -and ($_.problem -ne 0 -or $_.state -ne 'Trusted') })
    Check 'every other device still works and shows as trusted' ($bad.Count -eq 0) (($bad | ForEach-Object { "$($_.name)=$($_.state)/$($_.problem)" }) -join ', ')
    Check 'block was logged' (@(Get-Events | Where-Object { $_.type -eq 'blocked' -and $_.id -eq $Target }).Count -ge 1)
    $ledger = Get-Content (Join-Path $dataDir 'ledger.json') -Raw | ConvertFrom-Json
    Check 'ledger records the target' (@($ledger.blocked | Where-Object { $_.id -eq $Target }).Count -eq 1)
    $pnp = Get-PnpDevice -InstanceId $Target -ErrorAction SilentlyContinue
    Say "   Windows reports: status=$($pnp.Status) problem=$($pnp.Problem)"
    Check 'Device Manager agrees it is off' ($pnp.Status -ne 'OK')

    # ---------- 4. the block survives a service restart ----------
    Say ''
    Say '=== 4. service restart ==='
    Restart-Service LotusGuard -Force
    Start-Sleep -Seconds 4
    $t = Get-Device $Target
    Check 'target is still blocked after a restart' ($t.state -eq 'Blocked') "state=$($t.state)"
    Check 'it was not blocked a second time' (@(Get-Events | Where-Object { $_.type -eq 'blocked' -and $_.id -eq $Target }).Count -eq 1)

    # ---------- 5. trust the target: it comes back ----------
    Say ''
    Say '=== 5. trust the target ==='
    $all = $others + @([ordered]@{ kind = 'device'; id = $Target; name = 'target'; added = (Get-Date).ToUniversalTime().ToString('s') + 'Z' })
    Write-Policy $true 'block' 'block' $false $all
    $released = Wait-Until { $d = Get-Device $Target; $d.state -eq 'Trusted' -and $d.problem -eq 0 } 20
    $t = Get-Device $Target
    Say "   target: state=$($t.state) problem=$($t.problem) started=$($t.started)"
    Check 'target works again and shows as trusted' $released "state=$($t.state) problem=$($t.problem)"
    Check 'release was logged' (@(Get-Events | Where-Object { $_.type -eq 'released' -and $_.id -eq $Target }).Count -ge 1)
    $ledger = Get-Content (Join-Path $dataDir 'ledger.json') -Raw | ConvertFrom-Json
    Check 'ledger is empty again' (@($ledger.blocked).Count -eq 0)
    $pnp = Get-PnpDevice -InstanceId $Target -ErrorAction SilentlyContinue
    Check 'Device Manager agrees it works' ($pnp.Status -eq 'OK') "status=$($pnp.Status)"

    # ---------- 6. read-only mode ----------
    Say ''
    Say '=== 6. read-only ==='
    Write-Policy $true 'readonly' 'allow' $true $all
    $on = Wait-Until { (Get-ItemProperty "$roKey\{53f5630d-b6bf-11d0-94f2-00a0c91efb8b}" -ErrorAction SilentlyContinue).Deny_Write -eq 1 } 15
    Check 'Windows read-only policy is set for removable disks' $on
    foreach ($cls in '{53f56308-b6bf-11d0-94f2-00a0c91efb8b}', '{53f56311-b6bf-11d0-94f2-00a0c91efb8b}', '{6AC27878-A6FA-4155-BA85-F98F491D4F33}', '{F33FDC04-D1AC-4E8E-9A30-19BBD4B108AE}') {
        Check "read-only policy for $cls" ((Get-ItemProperty "$roKey\$cls" -ErrorAction SilentlyContinue).Deny_Write -eq 1)
    }
    Check 'read-only was logged' (@(Get-Events | Where-Object { $_.type -eq 'readonly_on' }).Count -ge 1)
    Say '-- someone deletes the setting behind LOTUS: it must come back'
    Remove-ItemProperty "$roKey\{53f5630d-b6bf-11d0-94f2-00a0c91efb8b}" -Name Deny_Write -ErrorAction SilentlyContinue
    Check 'tampered setting is restored' (Wait-Until { (Get-ItemProperty "$roKey\{53f5630d-b6bf-11d0-94f2-00a0c91efb8b}" -ErrorAction SilentlyContinue).Deny_Write -eq 1 } 15)
    Write-Policy $true 'allow' 'allow' $true $all
    Check 'read-only policy is removed again' (Wait-Until { -not (Test-ReadOnly) } 15)

    # ---------- 7. disarm releases everything ----------
    Say ''
    Say '=== 7. block again, then disarm ==='
    Write-Policy $true 'allow' 'block' $false $others
    Check 'target is blocked again' (Wait-Until { (Get-Device $Target).state -eq 'Blocked' } 20)
    Write-Policy $false 'allow' 'block' $false $others
    Check 'disarming switches it back on' (Wait-Until { $d = Get-Device $Target; $d.problem -eq 0 -and $d.state -eq 'Allowed' } 20)

    # ---------- 8. a damaged policy file ----------
    Say ''
    Say '=== 8. damaged policy file ==='
    Write-Policy $true 'allow' 'allow' $true $all
    Start-Sleep -Seconds 3
    [System.IO.File]::WriteAllText($policyFile, '{ broken')
    Check 'damage is logged' (Wait-Until { @(Get-Events | Where-Object { $_.type -eq 'error' -and $_.msg -like 'The policy file could not be read*' }).Count -ge 1 } 15)
    Check 'service keeps running' ((Get-Service LotusGuard).Status -eq 'Running')
    Check 'nothing got blocked' (@((Get-Scan).devices | Where-Object { $_.problem -ne 0 }).Count -eq 0)
    Write-Policy $false 'allow' 'allow' $true $all

    # ---------- 9. the app starts against the real service ----------
    Say ''
    Say '=== 9. app ==='
    $app = Start-Process -FilePath (Join-Path $installDir 'LOTUS.exe') -PassThru
    $up = Wait-Until { $app.Refresh(); $app.MainWindowHandle -ne 0 } 15
    Check 'main window opens' $up
    Start-Sleep -Seconds 3
    $app.Refresh()
    Check 'app is still running and responding' (-not $app.HasExited -and $app.Responding)
    $second = Start-Process -FilePath (Join-Path $installDir 'LOTUS.exe') -PassThru
    Check 'a second copy steps aside' ($second.WaitForExit(8000))
    if (-not $app.HasExited) { $app.CloseMainWindow() | Out-Null; if (-not $app.WaitForExit(5000)) { $app.Kill() } }
}
catch {
    $script:failed++
    Say "EXCEPTION: $($_.Exception.Message)"
    Say $_.ScriptStackTrace
}
finally {
    # ---------- 10. uninstall ----------
    Say ''
    Say '=== 10. uninstall ==='
    if ($KeepInstalled) {
        Say 'skipped: -KeepInstalled'
        if (Test-Path $dataDir) { Write-Policy $false 'allow' 'allow' $true @() }
    }
    else {
        # Leave a blocked device behind on purpose, so the uninstaller has to clean it up.
        if (Test-Path $guard) {
            $scan = Get-Scan
            $others = @($scan.devices | Where-Object { $_.id -ne $Target } | ForEach-Object { [ordered]@{ kind = 'device'; id = $_.id; name = $_.name } })
            Write-Policy $true 'readonly' 'block' $false $others
            $prepared = Wait-Until { (Get-Device $Target).state -eq 'Blocked' -and (Test-ReadOnly) } 20
            Check 'state before uninstall: target blocked and read-only on' $prepared
        }
        $uninstaller = Join-Path $installDir 'Uninstall.exe'
        if (Test-Path $uninstaller) {
            $p = Start-Process -FilePath $uninstaller -ArgumentList '/uninstall', '/quiet' -Wait -PassThru
            Check 'uninstaller exits with 0' ($p.ExitCode -eq 0) "exit=$($p.ExitCode)"
        }
        else { Check 'uninstaller exists' $false }
        Check 'service is gone' ($null -eq (Get-Service LotusGuard -ErrorAction SilentlyContinue))
        # The uninstaller cannot delete itself while it runs. A helper removes the
        # folder a few seconds after it has exited.
        Check 'program folder is gone' (Wait-Until { -not (Test-Path $installDir) } 20)
        $pending = @((Get-ItemProperty 'HKLM:\SYSTEM\CurrentControlSet\Control\Session Manager' -Name PendingFileRenameOperations -ErrorAction SilentlyContinue).PendingFileRenameOperations | Where-Object { $_ -like '*Program Files\LOTUS*' })
        Check 'nothing is left for Windows to delete at restart' ($pending.Count -eq 0)
        Check 'policy folder is gone' (-not (Test-Path $dataDir))
        Check 'start menu shortcut is gone' (-not (Test-Path (Join-Path $env:ProgramData 'Microsoft\Windows\Start Menu\Programs\LOTUS.lnk')))
        Check 'desktop shortcut is gone' (-not (Test-Path (Join-Path ([Environment]::GetFolderPath('CommonDesktopDirectory')) 'LOTUS.lnk')))
        Check 'sign-in entry is gone' ($null -eq (Get-ItemProperty 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Run' -ErrorAction SilentlyContinue).LOTUS)
        Check 'installed-apps entry is gone' (-not (Test-Path 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\LOTUS'))
        Check 'read-only policy is gone' (-not (Test-ReadOnly))
        Check 'no LOTUS process is left' (@(Get-Process LOTUS, LotusGuard -ErrorAction SilentlyContinue).Count -eq 0)
        $pnp = Get-PnpDevice -InstanceId $Target -ErrorAction SilentlyContinue
        Say "   target after uninstall: status=$($pnp.Status) problem=$($pnp.Problem)"
        Check 'target works after uninstall' ($pnp.Status -eq 'OK') "status=$($pnp.Status)"
        $off = @(Get-PnpDevice -PresentOnly | Where-Object { $_.InstanceId -like 'USB\VID_*' -and $_.Status -ne 'OK' })
        Check 'no USB device is left switched off' ($off.Count -eq 0) (($off | ForEach-Object { $_.InstanceId }) -join ', ')
    }
    Say ''
    Say "RESULT: passed=$($script:passed) failed=$($script:failed)"
}
exit $script:failed
