# End-to-end test of the real product on this PC. Needs an elevated prompt.
#
# It takes whatever LOTUS is installed through an update, then installs fresh
# and checks, on real hardware:
#
#   - updates over 1.0 and over a password-protected 1.1 keep the policy and
#     keep a blocked device blocked
#   - a data folder planted by someone else before installation is not trusted
#   - permissions: who can read and write the policy store and its key, and
#     that nothing in the store can be read by someone who finds it
#   - the service cannot be stopped, reconfigured or deleted with Windows tools,
#     and comes back by itself when its process is killed
#   - an edited or replaced policy store is put back and the edit is logged
#   - strict mode: Windows refuses to install a blocked device it has never
#     seen, so it never starts, and a new device the policy allows is let in,
#     a device with several functions included
#   - with strict mode off, how long a never-seen device runs before it is off
#   - a device LOTUS blocked before does not start when it is plugged in again
#   - a device can be forced off the bus when Windows will not stop it
#   - read-only mode, trust, disarm
#   - what a new installation starts with, and trust entries added by hand
#     for a vendor, a model and an instance path
#   - an installation that puts the connected devices on the trusted list
#   - the rescue command, with the service running and without it
#   - a wrecked store locks LOTUS down, and running setup again resets it
#   - removal leaves nothing behind, the Windows restrictions included
#   - the setup window: install, update, remove
#
# -Target     a USB device that may be switched off for a few seconds at a time.
#             Every other connected device is put on the trusted list.
# -Composite  optional: a harmless USB device with several functions.
# -OldSetup   optional: the LOTUS 1.0 installer. With nothing installed, the
#             test installs 1.0, lets it block the target, and updates over it.
# -Old11Setup optional: the LOTUS 1.1 installer, for the same with a password set.
#
# At the end LOTUS is installed and disarmed, and the policy that was there
# before the test (if any) is put back, disarmed. That policy is also written
# to a file next to the log, and -RestorePolicy takes such a file when an
# earlier run could not put it back itself.

param(
    [string]$Target = '',
    [string]$Composite = '',
    [string]$Setup = '',
    [string]$Log = '',
    [string]$Core = '',
    [string]$RestorePolicy = '',
    [string]$OldSetup = '',
    [string]$Old11Setup = '',
    [switch]$LeaveRemoved
)

$ErrorActionPreference = 'Continue'
if (-not $Target) { Write-Host 'Pass -Target with the instance ID of a USB device that may be switched off briefly.'; exit 1 }
if (-not $Setup) { $Setup = Join-Path $PSScriptRoot '..\..\dist\LOTUS-Setup.exe' }
if (-not $Core) { $Core = Join-Path $PSScriptRoot '..\..\src\Lotus.Guard\bin\Release\Lotus.Core.dll' }
if (-not $Log) { $Log = Join-Path $env:TEMP 'lotus-e2e.log' }
$Setup = [System.IO.Path]::GetFullPath($Setup)
$Core = [System.IO.Path]::GetFullPath($Core)
$installDir = Join-Path $env:ProgramFiles 'LOTUS'
$dataDir = Join-Path $env:ProgramData 'LOTUS'
$secureDir = Join-Path $dataDir 'secure'
$storeFile = Join-Path $secureDir 'policy.dat'
$ledgerFile = Join-Path $secureDir 'ledger.dat'
$keyFile = Join-Path $secureDir 'seal.key'
$statusFile = Join-Path $dataDir 'status.json'
$legacyFile = Join-Path $dataDir 'policy.json'
$legacyLedger = Join-Path $dataDir 'ledger.json'
$guard = Join-Path $installDir 'LotusGuard.exe'
$eventsFile = Join-Path $dataDir 'events.jsonl'
$roKey = 'HKLM:\SOFTWARE\Policies\Microsoft\Windows\RemovableStorageDevices'
$gateKey = 'HKLM:\SOFTWARE\Policies\Microsoft\Windows\DeviceInstall\Restrictions'
$eventLogKey = 'HKLM:\SYSTEM\CurrentControlSet\Services\EventLog\Application\LotusGuard'
$defaultServiceRights = 'D:(A;;CCLCSWRPWPDTLOCRRC;;;SY)(A;;CCDCLCSWRPWPDTLOCRSDRCWDWO;;;BA)(A;;CCLCSWLOCRRC;;;IU)(A;;CCLCSWLOCRRC;;;SU)'
$oldPassword = 'e2e test password 1'
# The version the installer under test carries, as a pattern: 1.3.*
$newVersion = (((Get-Item $Setup).VersionInfo.FileVersion -split '\.')[0..1] -join '.') + '.*'
$script:failed = 0
$script:passed = 0

Set-Content -Path $Log -Value "LOTUS end-to-end test $(Get-Date -Format s)" -Encoding utf8
function Say($text) { Add-Content -Path $Log -Value $text -Encoding utf8 }
function Check([string]$name, [bool]$ok, [string]$detail = '') {
    if ($ok) { $script:passed++; Say "PASS  $name" }
    else { $script:failed++; Say "FAIL  $name  $detail" }
}
function Section([string]$title) { Say ''; Say "=== $title ===" }
function Wait-Until([scriptblock]$condition, [int]$seconds = 15) {
    $deadline = (Get-Date).AddSeconds($seconds)
    while ((Get-Date) -lt $deadline) {
        if (& $condition) { return $true }
        Start-Sleep -Milliseconds 300
    }
    return [bool](& $condition)
}
function Read-Text([string]$path) {
    if (-not (Test-Path $path)) { return '' }
    try { return [string][IO.File]::ReadAllText($path) } catch { return '' }
}
function Get-Scan {
    $text = (& $guard --scan) -join "`n"
    return $text | ConvertFrom-Json
}
function Get-Device($id) {
    return (Get-Scan).devices | Where-Object { $_.id -eq $id.ToUpperInvariant() }
}
function Get-Events {
    if (-not (Test-Path $eventsFile)) { return @() }
    return @(Get-Content $eventsFile | Where-Object { $_ } | ForEach-Object { $_ | ConvertFrom-Json })
}
function Count-Events([string]$type, [string]$id = '') {
    return @(Get-Events | Where-Object { $_.type -eq $type -and (-not $id -or $_.id -eq $id) }).Count
}
function Get-Errors { return @(Get-Events | Where-Object { $_.type -eq 'error' }) }
function Get-EventTime($e) {
    if ($e.t -is [DateTime]) { return $e.t.ToLocalTime() }
    return [DateTime]::Parse($e.t).ToLocalTime()
}
function Test-ReadOnly {
    foreach ($cls in '{53f5630d-b6bf-11d0-94f2-00a0c91efb8b}', '{53f56308-b6bf-11d0-94f2-00a0c91efb8b}', '{53f56311-b6bf-11d0-94f2-00a0c91efb8b}', '{6AC27878-A6FA-4155-BA85-F98F491D4F33}', '{F33FDC04-D1AC-4E8E-9A30-19BBD4B108AE}') {
        if ((Get-ItemProperty "$roKey\$cls" -ErrorAction SilentlyContinue).Deny_Write -eq 1) { return $true }
    }
    return $false
}
function Test-Gate { return ((Get-ItemProperty $gateKey -ErrorAction SilentlyContinue).ManagedByLOTUS -eq 1) }
function Get-GateList([string]$name) {
    $key = Get-Item "$gateKey\$name" -ErrorAction SilentlyContinue
    if ($null -eq $key) { return @() }
    return @($key.GetValueNames() | ForEach-Object { [string]$key.GetValue($_) })
}
function Test-Installed {
    return (Test-Path $installDir) -or [bool](Get-Service LotusGuard -ErrorAction SilentlyContinue) -or (Test-Path $dataDir)
}
function Get-ServiceInfo { return Get-CimInstance Win32_Service -Filter "Name='LotusGuard'" }
function Get-NotWorking {
    return @(Get-PnpDevice -PresentOnly | Where-Object { $_.InstanceId -like 'USB\VID_*' -and $_.Status -ne 'OK' })
}
function Get-PnpEvents([datetime]$since, [string]$id) {
    $model = ($id -split '\\')[1]
    return @(Get-WinEvent -FilterHashtable @{ LogName = 'Microsoft-Windows-Kernel-PnP/Configuration'; StartTime = $since } -ErrorAction SilentlyContinue | Where-Object { $_.Message -like "*$model*" } | Sort-Object TimeCreated)
}
function Say-PnpEvents($events) {
    foreach ($e in $events) { Say "   Windows: $($e.TimeCreated.ToString('HH:mm:ss.fff')) event $($e.Id) $(($e.Message -split "`n")[0])" }
}
function Forget-Device([string]$id) {
    & pnputil.exe /remove-device "$id" /subtree | Out-Null
    if ($LASTEXITCODE -ne 0) { & pnputil.exe /remove-device "$id" | Out-Null }
    return $LASTEXITCODE
}
function Get-Family([string]$id) {
    $model = ($id -split '\\')[1]
    return @(Get-PnpDevice -PresentOnly -ErrorAction SilentlyContinue | Where-Object { $_.InstanceId -like "USB\$model*" } | Sort-Object InstanceId)
}
function Repair-NotWorking {
    # Has Windows forget every USB device that is not working, together with
    # the device it is a function of, and find it again.
    $broken = Get-NotWorking
    if ($broken.Count -eq 0) { return }
    Say "   not working: $(($broken | ForEach-Object { "$($_.InstanceId)=$($_.Status)" }) -join ', '). Windows is made to forget them and find them again."
    $roots = @{}
    foreach ($d in $broken) {
        $parent = [string](Get-PnpDeviceProperty -InstanceId $d.InstanceId -KeyName DEVPKEY_Device_Parent -ErrorAction SilentlyContinue).Data
        if ($parent -like 'USB\VID_*') { $roots[$parent.ToUpperInvariant()] = $true } else { $roots[$d.InstanceId.ToUpperInvariant()] = $true }
    }
    foreach ($id in $roots.Keys) { $null = Forget-Device $id }
    Start-Sleep -Seconds 1
    Start-Process pnputil.exe -ArgumentList '/scan-devices' -WindowStyle Hidden -Wait
    $null = Wait-Until { (Get-NotWorking).Count -eq 0 } 20
}
function Test-Unreadable([string]$path) {
    # True when the file shows none of the words a policy or a device list is made of.
    try { $bytes = [IO.File]::ReadAllBytes($path) } catch { return $true }
    foreach ($encoding in [Text.Encoding]::GetEncoding(28591), [Text.Encoding]::Unicode) {
        $text = $encoding.GetString($bytes)
        if ($text -match 'armed|trusted|storage|VID_|blocked') { return $false }
    }
    return $true
}
function Test-Sealed([string]$path) {
    try { return [Lotus.Core.SealedBox]::LooksSealed([IO.File]::ReadAllBytes($path)) } catch { return $false }
}

# ---------- talking to the service ----------
function New-Guard { return New-Object Lotus.Core.GuardClient ([Lotus.Core.GuardProtocol]::PipeName), $true }
function New-Policy([bool]$armed, [string]$storage, [string]$pnp, [bool]$allowInput, [string[]]$exclude = @(), [bool]$strict = $true) {
    $policy = New-Object Lotus.Core.Policy
    $policy.Armed = $armed
    $policy.Storage = [Lotus.Core.StorageMode]$storage
    $policy.Pnp = [Lotus.Core.PnpMode]$pnp
    $policy.AllowInput = $allowInput
    $policy.Strict = $strict
    foreach ($device in [Lotus.Core.DeviceScanner]::Scan()) {
        if ($exclude -contains $device.InstanceId) { continue }
        [void]$policy.AddTrust([Lotus.Core.TrustEntry]::ForDevice($device))
    }
    return $policy
}
function Send-Policy($policy) {
    try { return (New-Guard).Apply($policy.ToJson()) }
    catch {
        $reply = New-Object Lotus.Core.GuardReply
        $reply.Code = 'unavailable'; $reply.Message = $_.Exception.Message
        return $reply
    }
}
function Get-Status {
    try { return (New-Guard).Status() }
    catch { return New-Object Lotus.Core.GuardReply }
}
function Get-Policy {
    # The policy as the service holds it, or $null when the service does not answer.
    try {
        $reply = (New-Guard).Read()
        if ($reply.Ok) { return [Lotus.Core.GuardReads]::ReadPolicy($reply) }
    }
    catch { }
    return $null
}
function Get-StrictState {
    try { return [string][Lotus.Core.Json]::GetString((New-Guard).Read().Data, 'strictState', '') } catch { return '' }
}
function Get-InstalledPolicyJson {
    # Works for every version: 1.2 answers on its pipe, 1.1 kept a readable
    # store, 1.0 a plain file.
    $policy = Get-Policy
    if ($policy) { return $policy.ToJson() }
    if (Test-Path $storeFile) {
        $text = Read-Text $storeFile
        if ($text.StartsWith('LOTUS-STORE-1')) {
            try {
                $tree = [Lotus.Core.Json]::AsObject([Lotus.Core.Json]::Parse(($text -split "`n", 3)[2]))
                return [Lotus.Core.Json]::Write($tree['policy'], $true)
            }
            catch { }
        }
    }
    if (Test-Path $legacyFile) { return Read-Text $legacyFile }
    return $null
}

# ---------- setup ----------
function Invoke-Setup([string]$exe, [string[]]$arguments) {
    $p = Start-Process -FilePath $exe -ArgumentList $arguments -Wait -PassThru
    return $p.ExitCode
}
function Remove-Quietly {
    $uninstaller = Join-Path $installDir 'Uninstall.exe'
    if (Test-Path $uninstaller) { return Invoke-Setup $uninstaller @('/uninstall', '/quiet') }
    return Invoke-Setup $Setup @('/uninstall', '/quiet')
}
function Say-SetupLog {
    foreach ($line in @(Get-Content (Join-Path $installDir 'setup.log') -ErrorAction SilentlyContinue | Select-Object -Last 14)) { Say "   setup.log: $line" }
}
function Stop-ServiceHard {
    # What setup does when the service will not listen: administrators get
    # their usual rights back, the service is kept from restarting, and its
    # process is ended.
    & sc.exe sdset LotusGuard $defaultServiceRights | Out-Null
    & sc.exe config LotusGuard start= disabled | Out-Null
    $servicePid = (Get-ServiceInfo).ProcessId
    if ($servicePid) { Stop-Process -Id $servicePid -Force -ErrorAction SilentlyContinue }
    return (Wait-Until { (Get-ServiceInfo).State -eq 'Stopped' } 20)
}
function Start-ServiceAgain {
    & sc.exe config LotusGuard start= auto | Out-Null
    & sc.exe start LotusGuard | Out-Null
    return (Wait-Until { (Get-ServiceInfo).State -eq 'Running' -and (Get-Status).Ok } 25)
}
function Test-NothingLeft([string]$label) {
    Check "$label service is gone" ($null -eq (Get-Service LotusGuard -ErrorAction SilentlyContinue))
    Check "$label program folder is gone" (Wait-Until { -not (Test-Path $installDir) } 20)
    Check "$label policy folder is gone" (-not (Test-Path $dataDir))
    $pending = @((Get-ItemProperty 'HKLM:\SYSTEM\CurrentControlSet\Control\Session Manager' -Name PendingFileRenameOperations -ErrorAction SilentlyContinue).PendingFileRenameOperations | Where-Object { $_ -like '*Program Files\LOTUS*' })
    Check "$label nothing is left for Windows to delete at restart" ($pending.Count -eq 0)
    Check "$label start menu shortcut is gone" (-not (Test-Path (Join-Path $env:ProgramData 'Microsoft\Windows\Start Menu\Programs\LOTUS.lnk')))
    Check "$label desktop shortcut is gone" (-not (Test-Path (Join-Path ([Environment]::GetFolderPath('CommonDesktopDirectory')) 'LOTUS.lnk')))
    Check "$label sign-in entry is gone" ($null -eq (Get-ItemProperty 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Run' -ErrorAction SilentlyContinue).LOTUS)
    Check "$label installed-apps entry is gone" (-not (Test-Path 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\LOTUS'))
    Check "$label event log registration is gone" (-not (Test-Path $eventLogKey))
    Check "$label no setup log is left in TEMP" (-not (Test-Path (Join-Path $env:TEMP 'LOTUS-Setup.log')))
    Check "$label read-only policy is gone" (-not (Test-ReadOnly))
    Check "$label the Windows installation restrictions are gone" (-not (Test-Path $gateKey))
    Check "$label the registry key of LOTUS is gone" (-not (Test-Path 'HKLM:\SOFTWARE\LOTUS'))
    Check "$label no LOTUS process is left" (Wait-Until { @(Get-Process LOTUS, LotusGuard -ErrorAction SilentlyContinue).Count -eq 0 } 10)
    $null = Wait-Until { (Get-NotWorking).Count -eq 0 } 15
    $off = Get-NotWorking
    Check "$label every connected USB device works" ($off.Count -eq 0) (($off | ForEach-Object { "$($_.InstanceId)=$($_.Status)" }) -join ', ')
}

# ---------- the setup window, driven through UI Automation ----------
Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes
function Get-SetupWindow($process) {
    # Windows PowerShell only reports the exit code later if the handle was opened early.
    $null = $process.Handle
    $shown = Wait-Until { $process.Refresh(); $process.HasExited -or $process.MainWindowHandle -ne 0 } 20
    if (-not $shown -or $process.HasExited) { return $null }
    return [System.Windows.Automation.AutomationElement]::FromHandle($process.MainWindowHandle)
}
function Find-Part($window, [string]$id) {
    $byId = New-Object System.Windows.Automation.PropertyCondition(
        [System.Windows.Automation.AutomationElement]::AutomationIdProperty, $id)
    return $window.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $byId)
}
function Get-PartText($window, [string]$id) {
    $part = Find-Part $window $id
    if ($null -eq $part) { return '' }
    return [string]$part.Current.Name
}
function Wait-Button($window, [string]$id, [string]$label, [int]$seconds = 60) {
    return Wait-Until {
        $b = Find-Part $window $id
        $null -ne $b -and $b.Current.Name -eq $label -and $b.Current.IsEnabled
    } $seconds
}
function Push-Button($window, [string]$id) {
    $button = Find-Part $window $id
    $button.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
}
function Wait-Exit($process, [int]$seconds = 15) {
    if (-not $process.WaitForExit($seconds * 1000)) { return $null }
    return $process.ExitCode
}

# =====================================================================
$admin = ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole(
    [Security.Principal.WindowsBuiltInRole]::Administrator)
Say "elevated=$admin target=$Target"
if (-not $admin) { Say 'RESULT: not elevated, nothing was tested'; exit 1 }
if (-not (Test-Path $Setup)) { Say "RESULT: setup not found at $Setup"; exit 1 }
if (-not (Test-Path $Core)) { Say "RESULT: Lotus.Core.dll not found at $Core"; exit 1 }

# Load the LOTUS library from a copy, so the installed file is never held open.
$coreCopy = Join-Path $env:TEMP ('lotus-e2e-core-' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Force $coreCopy | Out-Null
Copy-Item $Core $coreCopy
Add-Type -Path (Join-Path $coreCopy 'Lotus.Core.dll')

$Target = $Target.ToUpperInvariant()
$Composite = $Composite.ToUpperInvariant()
Say "setup=$Setup"
Say "setup sha256=$((Get-FileHash $Setup -Algorithm SHA256).Hash)"
Say "windows=$([Environment]::OSVersion.Version)"

$savedPolicy = $null
$savedPolicyFile = "$Log.policy.json"
if ($RestorePolicy) {
    if (-not (Test-Path $RestorePolicy)) { Say "RESULT: -RestorePolicy file not found at $RestorePolicy"; exit 1 }
    $savedPolicy = Get-Content $RestorePolicy -Raw
    Say "   policy to restore at the end: $RestorePolicy"
}
if ((Test-Path $gateKey) -and -not (Test-Gate)) {
    Say 'RESULT: this PC has device installation restrictions that LOTUS did not set. The test does not run here.'
    exit 1
}

try {
    # ---------- A. whatever is installed now ----------
    Section 'A. starting point'
    Repair-NotWorking
    Check 'every connected USB device works before the test' ((Get-NotWorking).Count -eq 0) ((Get-NotWorking | ForEach-Object { $_.InstanceId }) -join ', ')
    $existing = Get-ServiceInfo
    if ($existing) {
        $oldVersion = (Get-Item $guard -ErrorAction SilentlyContinue).VersionInfo.FileVersion
        Say "   LOTUS $oldVersion is installed, service state $($existing.State)"
        # This is the policy of the person who owns the PC. Keep a copy on disk.
        $found = Get-InstalledPolicyJson
        if ($found) {
            $savedPolicy = $found
            Set-Content -Path $savedPolicyFile -Value $savedPolicy -Encoding utf8
            $old = [Lotus.Core.Policy]::FromJson($savedPolicy)
            Say "   its policy: armed=$($old.Armed), $($old.Trusted.Count) trusted entries"

            Section 'A. update over the installed version'
            $code = Invoke-Setup $Setup @('/quiet')
            Check 'update exits with 0' ($code -eq 0) "exit=$code"
            Say-SetupLog
            Check 'service runs after the update' (Wait-Until { (Get-ServiceInfo).State -eq 'Running' -and (Get-Status).Ok } 20) "state=$((Get-ServiceInfo).State)"
            Check 'the new version is installed' ((Get-Item $guard).VersionInfo.FileVersion -like $newVersion) "version=$((Get-Item $guard).VersionInfo.FileVersion)"
            $now = Get-Policy
            Check 'update keeps the trusted list' ($null -ne $now -and $now.Trusted.Count -eq $old.Trusted.Count) "before=$($old.Trusted.Count) after=$($now.Trusted.Count)"
            Check 'update keeps the armed setting' ($null -ne $now -and $now.Armed -eq $old.Armed)
            Check 'the store is encrypted after the update' (Wait-Until { Test-Sealed $storeFile } 10)
            Check 'the service reported no error after the update' ((Get-Errors).Count -eq 0) ((Get-Errors | ForEach-Object { $_.msg }) -join ' / ')
        }
        else { Say '   its policy could not be read' }
        $code = Remove-Quietly
        Check 'removal of what was installed exits with 0' ($code -eq 0) "exit=$code"
        Test-NothingLeft 'after the first removal:'
    }
    elseif (Test-Installed) {
        Say '   parts of an earlier run are left and are removed first'
        $code = Invoke-Setup $Setup @('/uninstall', '/quiet')
        Say "   exit=$code"
        if (Test-Path $installDir) { Remove-Item $installDir -Recurse -Force -ErrorAction SilentlyContinue }
        Check 'the leftovers are gone' (Wait-Until { -not (Test-Installed) } 20)
    }
    else { Say '   nothing is installed' }

    if ($OldSetup) {
        Section 'A. LOTUS 1.0 with a blocked device, then the update'
        Say "   old setup sha256=$((Get-FileHash $OldSetup -Algorithm SHA256).Hash)"
        $code = Invoke-Setup $OldSetup @('/quiet')
        Check '1.0 installs' ($code -eq 0 -and (Wait-Until { (Get-ServiceInfo).State -eq 'Running' } 20)) "exit=$code"
        # A policy file as 1.0 wrote it: armed, everything but the target trusted.
        $entries = @([Lotus.Core.DeviceScanner]::Scan() | Where-Object { $_.InstanceId -ne $Target } | ForEach-Object {
                [ordered]@{ kind = 'device'; id = $_.InstanceId; name = $_.Name; added = '2026-11-26T21:39:27Z' } })
        $json = [ordered]@{ version = 1; armed = $true; storage = 'allow'; pnp = 'block'; allowInput = $false; trusted = $entries } | ConvertTo-Json -Depth 4
        [IO.File]::WriteAllText($legacyFile, $json)
        Check '1.0 blocks the target' (Wait-Until { (Get-PnpDevice -InstanceId $Target).Status -ne 'OK' } 20)
        $null = Wait-Until { Test-Path $legacyLedger } 10

        $code = Invoke-Setup $Setup @('/quiet')
        Check 'update over 1.0 exits with 0' ($code -eq 0) "exit=$code"
        Say-SetupLog
        Check 'service runs after the update over 1.0' (Wait-Until { (Get-ServiceInfo).State -eq 'Running' -and (Get-Status).Ok } 20)
        $now = Get-Policy
        Check 'the 1.0 policy was taken over: armed, same trusted list' ($null -ne $now -and $now.Armed -and $now.Trusted.Count -eq $entries.Count) "trusted=$($now.Trusted.Count)"
        Check 'the readable policy file is gone' (Wait-Until { -not (Test-Path $legacyFile) } 10)
        Check 'the readable list of blocked devices is gone' (Wait-Until { (Test-Path $ledgerFile) -and -not (Test-Path $legacyLedger) } 15)
        Check 'the old event log registration is gone' (-not (Test-Path $eventLogKey))
        Check 'the target stayed switched off through the update' ((Get-PnpDevice -InstanceId $Target).Status -ne 'OK')
        Check 'the new version shows the target as blocked' (Wait-Until { (Get-Device $Target).state -eq 'Blocked' } 15) "state=$((Get-Device $Target).state)"
        Check 'the target was not blocked a second time' ((Count-Events 'blocked' $Target) -eq 1) "blocked events=$(Count-Events 'blocked' $Target)"
        Check 'strict mode came on with the update' (Wait-Until { Test-Gate } 10)
        $reply = Send-Policy (New-Policy $false 'Allow' 'Block' $false @($Target))
        Check 'the policy from 1.0 can be disarmed' $reply.Ok "code=$($reply.Code) $($reply.Message)"
        Check 'disarming switches the target back on' (Wait-Until { (Get-PnpDevice -InstanceId $Target).Status -eq 'OK' } 20)
        Check 'the service reported no error' ((Get-Errors).Count -eq 0) ((Get-Errors | ForEach-Object { $_.msg }) -join ' / ')
        $code = Remove-Quietly
        Check 'removal after the update over 1.0 exits with 0' ($code -eq 0) "exit=$code"
        Test-NothingLeft 'after 1.0:'
    }

    if ($Old11Setup) {
        Section 'A. LOTUS 1.1 with a password and a blocked device, then the update'
        Say "   1.1 setup sha256=$((Get-FileHash $Old11Setup -Algorithm SHA256).Hash)"
        $code = Invoke-Setup $Old11Setup @('/quiet')
        Check '1.1 installs' ($code -eq 0 -and (Wait-Until { (Get-ServiceInfo).State -eq 'Running' -and (Get-Status).Ok } 20)) "exit=$code"
        $policy11 = New-Policy $true 'Allow' 'Block' $false @($Target)
        $request = New-Object 'System.Collections.Generic.Dictionary[string,object]'
        $request['op'] = 'apply'
        $request['password'] = ''
        $request['newPassword'] = $oldPassword
        $request['policy'] = [Lotus.Core.Json]::Parse($policy11.ToJson())
        $reply = (New-Guard).Send($request, 30000)
        Check '1.1 is armed with a password' $reply.Ok "code=$($reply.Code) $($reply.Message)"
        Check '1.1 blocks the target' (Wait-Until { (Get-PnpDevice -InstanceId $Target).Status -ne 'OK' } 20)
        Check '1.1 kept its store readable' ((Read-Text $storeFile).StartsWith('LOTUS-STORE-1'))
        & sc.exe stop LotusGuard | Out-Null
        Check '1.1 does not let itself be stopped' ($LASTEXITCODE -ne 0)

        $code = Invoke-Setup $Setup @('/quiet')
        Check 'update over a password-protected 1.1 exits with 0' ($code -eq 0) "exit=$code"
        Say-SetupLog
        Check 'service runs after the update over 1.1' (Wait-Until { (Get-ServiceInfo).State -eq 'Running' -and (Get-Status).Ok } 25) "state=$((Get-ServiceInfo).State)"
        Check 'the new version is installed over 1.1' ((Get-Item $guard).VersionInfo.FileVersion -like $newVersion)
        Check 'the service starts automatically again' ((Get-ServiceInfo).StartMode -eq 'Auto') "mode=$((Get-ServiceInfo).StartMode)"
        $now = Get-Policy
        Check 'the 1.1 policy was taken over: armed, same trusted list' ($null -ne $now -and $now.Armed -and $now.Trusted.Count -eq $policy11.Trusted.Count) "trusted=$($now.Trusted.Count)"
        Check 'the store is encrypted now' (Test-Sealed $storeFile)
        $readable = @(Get-ChildItem $secureDir -File | Where-Object { $_.Name -ne 'seal.key' -and -not (Test-Unreadable $_.FullName) })
        Check 'nothing readable is left in the store folder' ($readable.Count -eq 0) (($readable | ForEach-Object { $_.Name }) -join ', ')
        Check 'the readable list of blocked devices from 1.1 is gone' (-not (Test-Path (Join-Path $secureDir 'ledger.json')))
        Check 'the target stayed switched off through the update over 1.1' ((Get-PnpDevice -InstanceId $Target).Status -ne 'OK')
        Check 'the target shows as blocked' (Wait-Until { (Get-Device $Target).state -eq 'Blocked' } 15)
        $reply = Send-Policy (New-Policy $false 'Allow' 'Block' $false @($Target))
        Check 'the policy can be changed without any password' $reply.Ok "code=$($reply.Code) $($reply.Message)"
        Check 'disarming switches the target back on (after 1.1)' (Wait-Until { (Get-PnpDevice -InstanceId $Target).Status -eq 'OK' } 20)
        Check 'the service reported no error (after 1.1)' ((Get-Errors).Count -eq 0) ((Get-Errors | ForEach-Object { $_.msg }) -join ' / ')
        $code = Remove-Quietly
        Check 'removal after the update over 1.1 exits with 0' ($code -eq 0) "exit=$code"
        Test-NothingLeft 'after 1.1:'
    }
    Check 'nothing is installed before the fresh install' (-not (Test-Installed))

    # ---------- B. a data folder someone planted before installation ----------
    Section 'B. planted data folder'
    $outside = Join-Path $env:TEMP ('lotus-e2e-outside-' + [Guid]::NewGuid().ToString('N'))
    New-Item -ItemType Directory -Force $outside | Out-Null
    Set-Content (Join-Path $outside 'keep.txt') 'this file must survive'
    New-Item -ItemType Directory -Force $dataDir | Out-Null
    Set-Content $legacyFile '{"version":1,"armed":false,"storage":"allow","pnp":"allow","allowInput":true,"trusted":[{"kind":"model","id":"VID_DEAD&PID_BEEF","name":"planted"}]}'
    & icacls.exe $dataDir /setowner "$env:USERDOMAIN\$env:USERNAME" | Out-Null
    & icacls.exe $dataDir /grant '*S-1-5-32-545:(OI)(CI)F' | Out-Null
    & cmd.exe /c mklink /J "$dataDir\secure" "$outside" | Out-Null
    Say "   planted: owner=$((Get-Acl $dataDir).Owner), secure is a link=$(((Get-Item "$dataDir\secure" -Force).Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0)"

    $code = Invoke-Setup $Setup @('/quiet')
    Check 'setup finishes (0, or 2 for the warning about the folder)' ($code -eq 0 -or $code -eq 2) "exit=$code"
    Check 'the folder is owned by Administrators now' ((Get-Acl $dataDir).Owner -eq 'BUILTIN\Administrators') "owner=$((Get-Acl $dataDir).Owner)"
    $acl = (& icacls.exe $dataDir) -join "`n"
    Check 'users cannot write to the folder' (-not ($acl -match 'Users:[^\n]*\((F|M|W)\)')) $acl
    Check 'the secure folder is a real folder' (((Get-Item $secureDir -Force).Attributes -band [IO.FileAttributes]::ReparsePoint) -eq 0)
    Check 'the file behind the planted link was not touched' (Test-Path (Join-Path $outside 'keep.txt'))
    Check 'nothing of LOTUS was written behind the planted link' (@(Get-ChildItem $outside).Count -eq 1)
    Check 'the planted policy file is gone' (-not (Test-Path $legacyFile))
    $null = Wait-Until { (Get-Status).Ok } 15
    $now = Get-Policy
    Check 'the planted trust entry was not taken over' ($null -ne $now -and $now.Trusted.Count -eq 0) "trusted=$($now.Trusted.Count)"
    Check 'setup wrote down that it replaced the folder' ((Read-Text (Join-Path $installDir 'setup.log')) -like '*could not be trusted*')
    Remove-Item $outside -Recurse -Force -ErrorAction SilentlyContinue
    $code = Remove-Quietly
    Check 'removal exits with 0' ($code -eq 0) "exit=$code"
    Check 'nothing is installed again' (Wait-Until { -not (Test-Installed) } 20)

    # ---------- C. fresh install ----------
    Section 'C. fresh install'
    $code = Invoke-Setup $Setup @('/quiet')
    Check 'setup exits with 0' ($code -eq 0) "exit=$code"
    foreach ($f in 'LOTUS.exe', 'LotusGuard.exe', 'Lotus.Core.dll', 'Uninstall.exe', 'LICENSE.txt', 'LOTUS Notifier.lnk', 'setup.log') {
        Check "installed file $f" (Test-Path (Join-Path $installDir $f))
    }
    $built = @{
        'LOTUS.exe'      = Join-Path $PSScriptRoot '..\..\src\Lotus.App\bin\Release\LOTUS.exe'
        'Lotus.Core.dll' = Join-Path $PSScriptRoot '..\..\src\Lotus.App\bin\Release\Lotus.Core.dll'
        'LotusGuard.exe' = Join-Path $PSScriptRoot '..\..\src\Lotus.Guard\bin\Release\LotusGuard.exe'
    }
    foreach ($f in $built.Keys) {
        if (-not (Test-Path $built[$f])) { Say "   no build output to compare $f with"; continue }
        $same = (Get-FileHash (Join-Path $installDir $f) -Algorithm SHA256).Hash -eq (Get-FileHash $built[$f] -Algorithm SHA256).Hash
        Check "installed $f is the freshly built one" $same
    }
    Check 'uninstaller is a copy of setup' ((Get-FileHash (Join-Path $installDir 'Uninstall.exe') -Algorithm SHA256).Hash -eq (Get-FileHash $Setup -Algorithm SHA256).Hash)
    $svc = Get-ServiceInfo
    Check 'service is running' ($svc.State -eq 'Running') "state=$($svc.State)"
    Check 'service starts automatically' ($svc.StartMode -eq 'Auto') "mode=$($svc.StartMode)"
    Check 'service runs as LocalSystem' ($svc.StartName -eq 'LocalSystem') "account=$($svc.StartName)"
    Check 'service path is quoted' ($svc.PathName -eq "`"$guard`"") "path=$($svc.PathName)"
    Check 'start menu shortcut' (Test-Path (Join-Path $env:ProgramData 'Microsoft\Windows\Start Menu\Programs\LOTUS.lnk'))
    Check 'desktop shortcut' (Test-Path (Join-Path ([Environment]::GetFolderPath('CommonDesktopDirectory')) 'LOTUS.lnk'))
    $run = (Get-ItemProperty 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Run' -ErrorAction SilentlyContinue).LOTUS
    Check 'notifier starts at sign-in' ($run -eq "`"$(Join-Path $installDir 'LOTUS.exe')`" --tray") "value=$run"
    $un = Get-ItemProperty 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\LOTUS' -ErrorAction SilentlyContinue
    Check 'listed in installed apps with the new version' ($un.DisplayName -eq 'LOTUS' -and $un.DisplayVersion -like $newVersion -and $un.UninstallString -like '*Uninstall.exe" /uninstall')
    Check 'no setup log in TEMP' (-not (Test-Path (Join-Path $env:TEMP 'LOTUS-Setup.log')))
    Check 'no event log registration' (-not (Test-Path $eventLogKey))
    Check 'guard logged its start' (Wait-Until { (Count-Events 'guard_started') -ge 1 } 10)
    if ($code -ne 0 -or $svc.State -ne 'Running') {
        Say-SetupLog
        throw 'The fresh install did not produce a running service, so the remaining sections were skipped.'
    }
    Check 'notifier is running' (Wait-Until { @(Get-CimInstance Win32_Process -Filter "Name='LOTUS.exe'" | Where-Object { $_.CommandLine -like '*--tray*' }).Count -ge 1 } 10)

    Section 'C. permissions and what can be read'
    $null = Wait-Until { (Test-Path $storeFile) -and (Test-Path $keyFile) } 15
    $acl = (& icacls.exe $dataDir) -join "`n"
    Say "   data folder: $(($acl -split "`n" | Select-Object -First 4) -join ' | ')"
    Check 'data folder: owned by Administrators' ((Get-Acl $dataDir).Owner -eq 'BUILTIN\Administrators') "owner=$((Get-Acl $dataDir).Owner)"
    Check 'data folder: users can read' ($acl -match 'BUILTIN\\Users:\(OI\)\(CI\)\(RX\)')
    Check 'data folder: users cannot write' (-not ($acl -match 'Users:[^\n]*\((F|M|W)\)'))
    Check 'data folder: administrators cannot write' (-not ($acl -match 'Administrators:[^\n]*\((F|M|W)\)'))
    Check 'data folder: not inherited' (-not ($acl -match '\(I\)'))
    $acl = (& icacls.exe $secureDir) -join "`n"
    Say "   secure folder: $(($acl -split "`n" | Select-Object -First 3) -join ' | ')"
    Check 'secure folder: users have no access at all' (-not ($acl -match 'Users'))
    Check 'secure folder: administrators can only read' (($acl -match 'Administrators:\(OI\)\(CI\)\(RX\)') -and -not ($acl -match 'Administrators:[^\n]*\((F|M|W)\)'))
    $acl = (& icacls.exe $keyFile) -join "`n"
    Say "   key: $(($acl -split "`n" | Select-Object -First 2) -join ' | ')"
    Check 'key: only the service account is listed' (($acl -match 'NT AUTHORITY\\SYSTEM:\(F\)') -and -not ($acl -match 'Administrators') -and -not ($acl -match 'Users'))
    $read = $true; try { $null = [IO.File]::ReadAllBytes($keyFile) } catch { $read = $false }
    Check 'an administrator cannot read the key' (-not $read)
    $wrote = $true; try { [IO.File]::AppendAllText($storeFile, ' ') } catch { $wrote = $false }
    Check 'an administrator cannot write the policy store' (-not $wrote)
    Check 'no readable policy file exists' (-not (Test-Path $legacyFile) -and -not (Test-Path (Join-Path $secureDir 'ledger.json')))
    Check 'the status file is there for the notifier' (Test-Path $statusFile)
    Check 'the status file says nothing about the policy' (-not ((Read-Text $statusFile) -match 'storage|trusted|pnp|password'))
    Check 'the service reported no problem with the folder permissions' ((Get-Errors).Count -eq 0) ((Get-Errors | ForEach-Object { $_.msg }) -join ' / ')

    Section 'C. the service resists Windows tools'
    & sc.exe stop LotusGuard | Out-Null
    Check 'sc stop is refused' ($LASTEXITCODE -ne 0) "exit=$LASTEXITCODE"
    $stopped = $true; try { Stop-Service LotusGuard -Force -ErrorAction Stop } catch { $stopped = $false }
    Check 'Stop-Service is refused' (-not $stopped)
    & sc.exe config LotusGuard start= disabled | Out-Null
    Check 'sc config is refused' ($LASTEXITCODE -ne 0) "exit=$LASTEXITCODE"
    & sc.exe delete LotusGuard | Out-Null
    Check 'sc delete is refused' ($LASTEXITCODE -ne 0) "exit=$LASTEXITCODE"
    Start-Sleep -Seconds 1
    $svc = Get-ServiceInfo
    Check 'the service is still running and still starts automatically' ($svc.State -eq 'Running' -and $svc.StartMode -eq 'Auto') "state=$($svc.State) mode=$($svc.StartMode)"

    # ---------- D. disarmed, then armed ----------
    Section 'D. disarmed after install'
    $status = Get-Status
    Check 'the service answers on its control channel' $status.Ok $status.Message
    $scan = Get-Scan
    Say "   policy: $($scan.policy)"
    Check 'policy is disarmed' ($scan.policy -like 'disarmed*')
    Check 'the command line gets the policy from the service' ($scan.policySource -eq 'Service') "source=$($scan.policySource) guard=$($scan.guard)"
    Check 'target is present' ($null -ne ($scan.devices | Where-Object { $_.id -eq $Target }))
    Check 'no device is off' (@($scan.devices | Where-Object { $_.problem -ne 0 }).Count -eq 0)
    Check 'every device was read from its own descriptors' (@($scan.devices | Where-Object { -not $_.described }).Count -eq 0) (($scan.devices | Where-Object { -not $_.described } | ForEach-Object { $_.name }) -join ', ')
    Check 'Windows restricts nothing while LOTUS is disarmed' (-not (Test-Path $gateKey))
    $fresh = Get-Policy
    $asNew = $null -ne $fresh -and -not $fresh.Armed -and $fresh.Storage -eq 'Allow' -and $fresh.Pnp -eq 'Allow' -and
        $fresh.AllowInput -and -not $fresh.Strict -and $fresh.Trusted.Count -eq 0
    Check 'a new installation: disarmed, storage and devices allowed, keyboards kept, strict mode off, nothing trusted' $asNew "armed=$($fresh.Armed) storage=$($fresh.Storage) pnp=$($fresh.Pnp) input=$($fresh.AllowInput) strict=$($fresh.Strict) trusted=$($fresh.Trusted.Count)"

    Section 'D. arming'
    $strict = New-Policy $true 'Block' 'Block' $false @($Target)
    Say "   trusting $($strict.Trusted.Count) other devices"
    $t0 = Get-Date
    $reply = Send-Policy $strict
    Check 'arming works' $reply.Ok "code=$($reply.Code) $($reply.Message)"
    $blocked = Wait-Until { (Get-Device $Target).state -eq 'Blocked' } 20
    $t = Get-Device $Target
    Say "   target: state=$($t.state) problem=$($t.problem) started=$($t.started) kinds=$($t.kinds), seen blocked $([int]((Get-Date) - $t0).TotalMilliseconds) ms after the request was sent"
    Check 'target is blocked' $blocked "state=$($t.state)"
    Check 'target is switched off in Windows' ($t.problem -eq 22 -or $t.problem -eq 21) "problem=$($t.problem)"
    $scan = Get-Scan
    $bad = @($scan.devices | Where-Object { $_.id -ne $Target -and ($_.problem -ne 0 -or $_.state -ne 'Trusted') })
    Check 'every other device still works and shows as trusted' ($bad.Count -eq 0) (($bad | ForEach-Object { "$($_.name)=$($_.state)/$($_.problem)" }) -join ', ')
    Check 'block was logged' ((Count-Events 'blocked' $Target) -ge 1)
    Check 'Device Manager agrees it is off' ((Get-PnpDevice -InstanceId $Target -ErrorAction SilentlyContinue).Status -ne 'OK')
    Check 'the status file says armed' ((Read-Text $statusFile) -match '"armed": true')
    $readable = @(Get-ChildItem $secureDir -File | Where-Object { $_.Name -ne 'seal.key' -and -not (Test-Unreadable $_.FullName) })
    Say "   store folder: $((Get-ChildItem $secureDir -File | ForEach-Object { "$($_.Name) $($_.Length)" }) -join ', ')"
    Check 'nothing in the store folder can be read, with a policy and a blocked device in it' ($readable.Count -eq 0) (($readable | ForEach-Object { $_.Name }) -join ', ')
    Check 'the policy and the list of blocked devices are encrypted LOTUS files' ((Test-Sealed $storeFile) -and (Test-Sealed $ledgerFile))

    Section 'D. strict mode is on'
    Check 'the Windows restrictions carry the LOTUS mark' (Wait-Until { Test-Gate } 10)
    $gate = Get-ItemProperty $gateKey -ErrorAction SilentlyContinue
    Check 'layered evaluation is on and the deny list is active' ($gate.AllowDenyLayered -eq 1 -and $gate.DenyDeviceIDs -eq 1 -and $gate.AllowInstanceIDs -eq 1 -and $gate.DenyDeviceIDsRetroactive -eq 0)
    $deny = Get-GateList 'DenyDeviceIDs'
    Check 'every USB class is on the deny list' ($deny.Count -eq 513 -and $deny -contains 'USB\COMPOSITE' -and $deny -contains 'USB\Class_03') "count=$($deny.Count)"
    $allow = Get-GateList 'AllowInstanceIDs'
    Say "   exceptions: $($allow.Count)"
    Check 'the trusted devices are exceptions and the target is not' ($allow.Count -ge $strict.Trusted.Count -and -not ($allow -contains $Target)) "allow=$($allow -join ' ')"
    Check 'the service says strict mode is on' ((Get-StrictState) -eq 'on') "state=$(Get-StrictState)"
    $bad = @((Get-Scan).devices | Where-Object { $_.id -ne $Target -and $_.problem -ne 0 })
    Check 'devices that were running are not affected' ($bad.Count -eq 0) (($bad | ForEach-Object { $_.name }) -join ', ')

    # ---------- E. tampering ----------
    Section 'E. killing the service'
    $before = (Get-ServiceInfo).ProcessId
    $killed = $true; try { Stop-Process -Id $before -Force -ErrorAction Stop } catch { $killed = $false; Say "   could not kill it: $($_.Exception.Message)" }
    if ($killed) {
        $back = Wait-Until { $s = Get-ServiceInfo; $s.State -eq 'Running' -and $s.ProcessId -ne 0 -and $s.ProcessId -ne $before } 30
        Check 'Windows starts the service again by itself' $back "pid before=$before now=$((Get-ServiceInfo).ProcessId)"
        Check 'the target stayed blocked meanwhile' ((Get-PnpDevice -InstanceId $Target).Status -ne 'OK')
        Check 'the Windows restrictions stayed in force meanwhile' (Test-Gate)
        Check 'the policy is still armed after the restart' (Wait-Until { (Get-Status).Armed } 15)
        Check 'it was not blocked a second time' ((Count-Events 'blocked' $Target) -eq 1)
    }
    else { Say '   skipped: this session may not end the service process' }

    Section 'E. changing the policy store behind the service'
    # A determined administrator gives himself write access first. LOTUS cannot
    # prevent that, but it notices the change and undoes it.
    & icacls.exe $storeFile /grant '*S-1-5-32-544:(F)' | Out-Null
    $edited = $false
    try {
        $bytes = [IO.File]::ReadAllBytes($storeFile)
        $middle = [int]($bytes.Length / 2)
        $bytes[$middle] = $bytes[$middle] -bxor 1
        [IO.File]::WriteAllBytes($storeFile, $bytes)
        $edited = $true
    }
    catch { Say "   the change failed: $($_.Exception.Message)" }
    if ($edited) {
        Check 'a changed byte is logged as tampering' (Wait-Until { (Count-Events 'tamper') -ge 1 } 15)
        Check 'the policy did not change' ((Get-Status).Armed -and (Get-Device $Target).state -eq 'Blocked')
    }
    $tampers = Count-Events 'tamper'
    Start-Sleep -Seconds 1
    & icacls.exe $storeFile /grant '*S-1-5-32-544:(F)' | Out-Null
    $replaced = $true
    try { [IO.File]::WriteAllText($storeFile, (New-Policy $false 'Allow' 'Allow' $true).ToJson()) } catch { $replaced = $false; Say "   replacing failed: $($_.Exception.Message)" }
    if ($replaced) {
        Check 'a readable policy put in its place is logged as tampering' (Wait-Until { (Count-Events 'tamper') -gt $tampers } 15)
        Check 'the encrypted store is back' (Wait-Until { Test-Sealed $storeFile } 15)
        Check 'LOTUS is still armed and the target still blocked' ((Get-Status).Armed -and (Get-Device $Target).state -eq 'Blocked')
    }
    $deleted = $true; try { Remove-Item $storeFile -Force -ErrorAction Stop } catch { $deleted = $false }
    if ($deleted) {
        Check 'a deleted store is put back' (Wait-Until { Test-Path $storeFile } 15)
        Check 'still armed after the delete' ((Get-Status).Armed)
    }
    else { Say '   the store could not be deleted, which is fine too' }

    Section 'E. enabling the device behind LOTUS'
    $enabled = $true; try { Enable-PnpDevice -InstanceId $Target -Confirm:$false -ErrorAction Stop } catch { $enabled = $false; Say "   enable failed: $($_.Exception.Message)" }
    if ($enabled) {
        Check 'LOTUS switches it off again' (Wait-Until { (Get-PnpDevice -InstanceId $Target).Status -ne 'OK' } 15)
    }

    Section 'E. replugging a blocked device'
    # Resetting the hub port is the same as pulling the plug and putting it back.
    Start-Sleep -Seconds 2
    $t0 = Get-Date
    $replug = [Lotus.Core.DeviceControl]::ForceOff($Target)
    Say "   port reset: ok=$($replug.Ok) $($replug.Message)"
    Check 'the device left the bus and came back switched off' ($replug.Ok -and (Get-PnpDevice -InstanceId $Target -ErrorAction SilentlyContinue).Status -ne 'OK') $replug.Message
    Start-Sleep -Seconds 2
    $replugEvents = Get-PnpEvents $t0 $Target
    Say-PnpEvents $replugEvents
    Check 'Windows did not start it, or any part of it, after the replug' (@($replugEvents | Where-Object { $_.Id -eq 410 }).Count -eq 0)
    Check 'it is still blocked two seconds later' ((Get-Device $Target).state -eq 'Blocked')

    # ---------- F. devices Windows has never seen ----------
    Section 'F. a blocked device Windows has never seen (strict mode on)'
    # Deleting the device record makes Windows forget the device. Scanning for
    # hardware then finds it as if it had never been seen.
    $blocksBefore = Count-Events 'blocked' $Target
    Say "   forget exit=$(Forget-Device $Target)"
    Start-Sleep -Seconds 2
    $t0 = Get-Date
    $watch = [Diagnostics.Stopwatch]::StartNew()
    Start-Process pnputil.exe -ArgumentList '/scan-devices' -WindowStyle Hidden
    $seen = -1; $started = -1; $off = -1
    while ($watch.ElapsedMilliseconds -lt 12000 -and $off -lt 0) {
        $d = [Lotus.Core.DeviceScanner]::ScanOne($Target)
        if ($null -ne $d) {
            if ($seen -lt 0) { $seen = $watch.ElapsedMilliseconds }
            if ($d.Started -and $started -lt 0) { $started = $watch.ElapsedMilliseconds }
            if ($d.IsOff) { $off = $watch.ElapsedMilliseconds }
        }
        [Lotus.Core.DeviceScanner]::Forget($Target)
    }
    Say "   after the scan was requested: seen at $seen ms, seen running at $(if ($started -lt 0) { 'never' } else { "$started ms" }), seen switched off by LOTUS at $off ms"
    Start-Sleep -Seconds 2
    $pnpEvents = Get-PnpEvents $t0 $Target
    Say-PnpEvents $pnpEvents
    $mine = @(Get-Events | Where-Object { $_.id -eq $Target -and (Get-EventTime $_) -ge $t0 })
    foreach ($e in $mine) { Say "   LOTUS:   $((Get-EventTime $e).ToString('HH:mm:ss.fff')) $($e.type)" }
    Check 'Windows refused to install it' (@($pnpEvents | Where-Object { $_.Id -eq 402 }).Count -ge 1)
    Check 'Windows never started it, or any part of it' (@($pnpEvents | Where-Object { $_.Id -eq 410 }).Count -eq 0) "started events=$(@($pnpEvents | Where-Object { $_.Id -eq 410 }).Count)"
    Check 'this test never saw it running' ($started -lt 0) "running at $started ms"
    Check 'LOTUS switched it off as well, so it stays off without the restrictions' ($off -ge 0 -and (Get-Device $Target).state -eq 'Blocked') "state=$((Get-Device $Target).state)"
    Check 'the block was logged' ((Count-Events 'blocked' $Target) -gt $blocksBefore)

    Section 'F. a new device the policy allows is let in'
    # Trusted only, keyboards and mice allowed, and nothing trusted by name that
    # is under test: the policy allows the target because it is an input device,
    # but Windows refuses it until LOTUS names it as an exception.
    $untrusted = @($Target)
    if ($Composite) { $untrusted += $Composite }
    $inputAllowed = New-Policy $true 'Allow' 'Block' $true $untrusted
    $reply = Send-Policy $inputAllowed
    Check 'the policy with the input exception is applied' $reply.Ok "code=$($reply.Code) $($reply.Message)"
    Check 'the target runs under it' (Wait-Until { (Get-PnpDevice -InstanceId $Target -ErrorAction SilentlyContinue).Status -eq 'OK' } 25) "status=$((Get-PnpDevice -InstanceId $Target -ErrorAction SilentlyContinue).Status)"
    Check 'strict mode is still on' (Test-Gate)
    $warningsBefore = Count-Events 'warning'
    $blocksBefore = Count-Events 'blocked' $Target
    Start-Sleep -Seconds 70
    Say "   forget exit=$(Forget-Device $Target)"
    Start-Sleep -Seconds 4
    Check 'a device that is gone is no longer an exception' (Wait-Until { -not ((Get-GateList 'AllowInstanceIDs') -contains $Target) } 10)
    $t0 = Get-Date
    $watch = [Diagnostics.Stopwatch]::StartNew()
    Start-Process pnputil.exe -ArgumentList '/scan-devices' -WindowStyle Hidden
    $letIn = Wait-Until { (Get-PnpDevice -InstanceId $Target -ErrorAction SilentlyContinue).Status -eq 'OK' } 25
    Say "   working $($watch.ElapsedMilliseconds) ms after the scan was requested"
    Start-Sleep -Seconds 2
    $pnpEvents = Get-PnpEvents $t0 $Target
    Say-PnpEvents $pnpEvents
    Check 'Windows refused it first' (@($pnpEvents | Where-Object { $_.Id -eq 402 }).Count -ge 1)
    Check 'LOTUS let it in and it works' $letIn "status=$((Get-PnpDevice -InstanceId $Target -ErrorAction SilentlyContinue).Status)"
    Check 'it is an exception now' ((Get-GateList 'AllowInstanceIDs') -contains $Target)
    Check 'it was not reported as blocked' ((Count-Events 'blocked' $Target) -eq $blocksBefore)
    Check 'LOTUS had nothing to complain about' ((Count-Events 'warning') -eq $warningsBefore) ((Get-Events | Where-Object { $_.type -eq 'warning' } | Select-Object -Last 2 | ForEach-Object { $_.msg }) -join ' / ')

    if ($Composite) {
        Section 'F. a new device with several functions is let in'
        $before = Get-Family $Composite
        Say "   before: $(($before | ForEach-Object { "$($_.InstanceId)=$($_.Status)" }) -join ', ')"
        $functions = @($before | Where-Object { $_.InstanceId -ne $Composite }).Count
        $warningsBefore = Count-Events 'warning'
        Say "   forget exit=$(Forget-Device $Composite)"
        Start-Sleep -Seconds 4
        $t0 = Get-Date
        $watch = [Diagnostics.Stopwatch]::StartNew()
        Start-Process pnputil.exe -ArgumentList '/scan-devices' -WindowStyle Hidden
        $whole = Wait-Until {
            $family = Get-Family $Composite
            @($family | Where-Object { $_.InstanceId -ne $Composite }).Count -ge $functions -and @($family | Where-Object { $_.Status -ne 'OK' }).Count -eq 0
        } 40
        Say "   all of it working $($watch.ElapsedMilliseconds) ms after the scan was requested"
        Start-Sleep -Seconds 2
        $after = Get-Family $Composite
        Say "   after: $(($after | ForEach-Object { "$($_.InstanceId)=$($_.Status)/$((Get-PnpDeviceProperty -InstanceId $_.InstanceId -KeyName DEVPKEY_Device_ProblemCode -ErrorAction SilentlyContinue).Data)" }) -join ', ')"
        $pnpEvents = Get-PnpEvents $t0 $Composite
        Say-PnpEvents $pnpEvents
        Check 'Windows refused the device and its functions first' (@($pnpEvents | Where-Object { $_.Id -eq 402 }).Count -ge 2) "refusals=$(@($pnpEvents | Where-Object { $_.Id -eq 402 }).Count)"
        Check 'LOTUS let the device and every function in' $whole (($after | Where-Object { $_.Status -ne 'OK' } | ForEach-Object { "$($_.InstanceId)=$($_.Status)" }) -join ', ')
        Check 'LOTUS had nothing to complain about with it' ((Count-Events 'warning') -eq $warningsBefore) ((Get-Events | Where-Object { $_.type -eq 'warning' } | Select-Object -Last 2 | ForEach-Object { $_.msg }) -join ' / ')
    }

    Section 'F. a blocked device Windows has never seen (strict mode off)'
    $loose = New-Policy $true 'Block' 'Block' $false @($Target) $false
    $reply = Send-Policy $loose
    Check 'switching strict mode off works' $reply.Ok "code=$($reply.Code) $($reply.Message)"
    Check 'the Windows restrictions are gone' (Wait-Until { -not (Test-Path $gateKey) } 15)
    Check 'the target is blocked again' (Wait-Until { (Get-Device $Target).state -eq 'Blocked' } 20)
    $blocksBefore = Count-Events 'blocked' $Target
    Say "   forget exit=$(Forget-Device $Target)"
    Start-Sleep -Seconds 2
    $t0 = Get-Date
    Start-Process pnputil.exe -ArgumentList '/scan-devices' -WindowStyle Hidden
    Check 'the device is switched off after Windows found it anew' (Wait-Until { (Count-Events 'blocked' $Target) -gt $blocksBefore -and (Get-PnpDevice -InstanceId $Target -ErrorAction SilentlyContinue).Status -ne 'OK' } 20)
    Start-Sleep -Seconds 2
    $pnpEvents = Get-PnpEvents $t0 $Target
    Say-PnpEvents $pnpEvents
    $mine = @(Get-Events | Where-Object { $_.id -eq $Target -and (Get-EventTime $_) -ge $t0 })
    foreach ($e in $mine) { Say "   LOTUS:   $((Get-EventTime $e).ToString('HH:mm:ss.fff')) $($e.type)" }
    $deviceStart = $pnpEvents | Where-Object { $_.Id -eq 410 -and $_.Message -like "*$Target*" } | Select-Object -First 1
    $blockEvent = $mine | Where-Object { $_.type -eq 'blocked' } | Select-Object -Last 1
    if ($null -ne $deviceStart -and $null -ne $blockEvent) {
        $ran = [int]((Get-EventTime $blockEvent) - $deviceStart.TimeCreated).TotalMilliseconds
        Say "   without strict mode the device ran for about $ran ms before LOTUS had it switched off"
        Check 'without strict mode a never-seen device is off within a quarter of a second' ($ran -lt 250) "ran for $ran ms"
    }
    else { Say '   Windows logged no start for the device this time' }

    Section 'F. forcing a device off the bus'
    # Trust the target so it runs, then use the forced path directly: mark it
    # disabled and cycle its hub port. This is what LOTUS falls back to when a
    # program holds a device open and Windows refuses to stop it.
    $all = New-Policy $true 'Block' 'Block' $false @()
    $reply = Send-Policy $all
    Check 'trusting the target works' $reply.Ok "code=$($reply.Code) $($reply.Message)"
    Check 'target works again and shows as trusted' (Wait-Until { $d = Get-Device $Target; $d.state -eq 'Trusted' -and $d.problem -eq 0 } 25)
    # The service writes the event once it has finished switching the device on.
    # Until then the device is still its to manage, and it would undo the mark.
    Check 'release was logged' (Wait-Until { (Count-Events 'released' $Target) -ge 1 } 15)
    Start-Sleep -Seconds 3
    $watch = [Diagnostics.Stopwatch]::StartNew()
    $forced = [Lotus.Core.DeviceControl]::ForceOff($Target)
    Say "   forced off: ok=$($forced.Ok) in $($watch.ElapsedMilliseconds) ms $($forced.Message)"
    Check 'the forced switch-off works on this hardware' $forced.Ok $forced.Message
    Check 'Device Manager shows it disabled' ((Get-PnpDevice -InstanceId $Target -ErrorAction SilentlyContinue).Status -ne 'OK')
    $restored = [Lotus.Core.DeviceControl]::Unblock($Target)
    Check 'the device comes back when it is enabled again' ($restored.Ok -and (Wait-Until { (Get-PnpDevice -InstanceId $Target).Status -eq 'OK' } 15)) $restored.Message

    Section 'F. devices that are not plugged in'
    $absent = @([Lotus.Core.DeviceScanner]::ScanAbsent())
    Say "   Windows remembers $($absent.Count) unplugged USB device(s)"
    $certain = @($absent | Where-Object { $_.FunctionsKnown })
    if ($certain.Count -gt 0) {
        $marked = Wait-Until { @([Lotus.Core.DeviceScanner]::ScanAbsent() | Where-Object { -not $_.IsOff }).Count -eq 0 } 15
        $still = @([Lotus.Core.DeviceScanner]::ScanAbsent() | Where-Object { -not $_.IsOff })
        Check 'every unplugged device is marked as switched off' $marked (($still | ForEach-Object { $_.InstanceId }) -join ', ')
        Check 'the marks are in the list of blocked devices' ((Get-Scan).ledgerEntries -ge $absent.Count) "ledger=$((Get-Scan).ledgerEntries)"
    }
    else { Say '   skipped: no unplugged device with known functions on this PC' }

    # ---------- G. read-only, disarm ----------
    Section 'G. read-only'
    $ro = New-Policy $true 'ReadOnly' 'Allow' $true @()
    $reply = Send-Policy $ro
    Check 'switching to read-only works' $reply.Ok "code=$($reply.Code)"
    $on = Wait-Until { (Get-ItemProperty "$roKey\{53f5630d-b6bf-11d0-94f2-00a0c91efb8b}" -ErrorAction SilentlyContinue).Deny_Write -eq 1 } 15
    Check 'Windows read-only policy is set for removable disks' $on
    foreach ($cls in '{53f56308-b6bf-11d0-94f2-00a0c91efb8b}', '{53f56311-b6bf-11d0-94f2-00a0c91efb8b}', '{6AC27878-A6FA-4155-BA85-F98F491D4F33}', '{F33FDC04-D1AC-4E8E-9A30-19BBD4B108AE}') {
        Check "read-only policy for $cls" ((Get-ItemProperty "$roKey\$cls" -ErrorAction SilentlyContinue).Deny_Write -eq 1)
    }
    Remove-ItemProperty "$roKey\{53f5630d-b6bf-11d0-94f2-00a0c91efb8b}" -Name Deny_Write -ErrorAction SilentlyContinue
    Check 'a deleted read-only setting is restored' (Wait-Until { (Get-ItemProperty "$roKey\{53f5630d-b6bf-11d0-94f2-00a0c91efb8b}" -ErrorAction SilentlyContinue).Deny_Write -eq 1 } 15)
    Check 'unplugged devices are released when nothing blocks them any more' (Wait-Until { @([Lotus.Core.DeviceScanner]::ScanAbsent() | Where-Object { $_.IsOff }).Count -eq 0 } 15)
    Check 'read-only alone needs no installation restrictions' (Wait-Until { -not (Test-Path $gateKey) } 15)

    Section 'G. storage blocked only'
    $reply = Send-Policy (New-Policy $true 'Block' 'Allow' $true @($Target))
    Check 'blocking storage only works' $reply.Ok "code=$($reply.Code)"
    Check 'Windows is told to refuse storage, and only storage' (Wait-Until { (Test-Gate) -and (Get-GateList 'DenyDeviceIDs').Count -eq 3 } 15) "deny=$((Get-GateList 'DenyDeviceIDs') -join ' ')"
    Check 'the target, which is no storage device, works' ((Get-PnpDevice -InstanceId $Target).Status -eq 'OK')
    Check 'read-only policy is removed again' (Wait-Until { -not (Test-ReadOnly) } 15)

    Section 'G. block again, then disarm'
    $reply = Send-Policy (New-Policy $true 'Allow' 'Block' $false @($Target))
    Check 'target is blocked again' ($reply.Ok -and (Wait-Until { (Get-Device $Target).state -eq 'Blocked' } 20))
    $reply = Send-Policy (New-Policy $false 'Allow' 'Block' $false @($Target))
    Check 'disarming switches it back on' ($reply.Ok -and (Wait-Until { $d = Get-Device $Target; $d.problem -eq 0 -and $d.state -eq 'Allowed' } 20))
    Check 'disarming takes the Windows restrictions away' (Wait-Until { -not (Test-Path $gateKey) } 15)

    Section 'G. trust entries added by hand'
    $parts = $Target -split '\\'
    $vendor = ($parts[1] -split '&')[0].Substring(4)
    $product = ($parts[1] -split '&')[1].Substring(4)
    $base = New-Policy $true 'Allow' 'Block' $false @($Target)
    $reply = Send-Policy $base
    Check 'trusted only, target not trusted: it is blocked' ($reply.Ok -and (Wait-Until { (Get-Device $Target).state -eq 'Blocked' } 20))
    $typed = @(
        @{ kind = 'Vendor'; text = $vendor.ToLower(); what = "the vendor $vendor" },
        @{ kind = 'Model'; text = "${vendor}:$product"; what = "the model ${vendor}:$product" },
        @{ kind = 'Device'; text = $Target.ToLower(); what = 'the instance path' }
    )
    foreach ($case in $typed) {
        $entry = $null; $problem = $null
        $parsed = [Lotus.Core.TrustEntry]::TryParse([Lotus.Core.TrustKind]$case.kind, $case.text, 'e2e by hand', [ref]$entry, [ref]$problem)
        Check "typing $($case.what) makes an entry" ($parsed -and $null -ne $entry) "problem=$problem"
        if (-not $parsed) { continue }
        $with = $base.Clone()
        [void]$with.AddTrust($entry)
        $reply = Send-Policy $with
        $works = $reply.Ok -and (Wait-Until { $d = Get-Device $Target; $d.problem -eq 0 -and $d.state -eq 'Trusted' } 25)
        Check "trusting $($case.what) switches the target on" $works "code=$($reply.Code) state=$((Get-Device $Target).state)"
        $kept = Get-Policy
        Check "the entry for $($case.what) is in the stored policy" ($null -ne $kept -and @($kept.Trusted | Where-Object { $_.Kind -eq $case.kind -and $_.Id -eq $entry.Id }).Count -eq 1)
        $reply = Send-Policy $base
        Check "without the entry the target is blocked again" ($reply.Ok -and (Wait-Until { (Get-Device $Target).state -eq 'Blocked' } 25))
    }
    $bad = $null; $problem = $null
    Check 'something that is not an ID is refused with a reason' (-not [Lotus.Core.TrustEntry]::TryParse([Lotus.Core.TrustKind]::Model, 'printer', $null, [ref]$bad, [ref]$problem) -and $problem)
    $reply = Send-Policy (New-Policy $false 'Allow' 'Block' $false @($Target))
    Check 'disarmed again, the target works' ($reply.Ok -and (Wait-Until { $d = Get-Device $Target; $d.problem -eq 0 -and $d.state -eq 'Allowed' } 20))

    # ---------- H. the app ----------
    Section 'H. app'
    $app = Start-Process -FilePath (Join-Path $installDir 'LOTUS.exe') -PassThru
    $up = Wait-Until { $app.Refresh(); $app.MainWindowHandle -ne 0 } 15
    Check 'main window opens' $up
    Start-Sleep -Seconds 3
    $app.Refresh()
    Check 'app is still running and responding' (-not $app.HasExited -and $app.Responding)
    $second = Start-Process -FilePath (Join-Path $installDir 'LOTUS.exe') -PassThru
    Check 'a second copy steps aside' ($second.WaitForExit(8000))
    if (-not $app.HasExited) { $app.CloseMainWindow() | Out-Null; if (-not $app.WaitForExit(5000)) { $app.Kill() } }

    # ---------- I. the rescue command, lockdown ----------
    Section 'I. the rescue command with the service running'
    $reply = Send-Policy (New-Policy $true 'ReadOnly' 'Block' $false @($Target))
    Check 'state before: target blocked, read-only on, restrictions on' ($reply.Ok -and (Wait-Until { (Get-Device $Target).state -eq 'Blocked' -and (Test-ReadOnly) -and (Test-Gate) } 25))
    $out = (& $guard --release-all) -join ' '
    $releaseCode = $LASTEXITCODE
    Say "   $out"
    Check 'the rescue command exits with 0' ($releaseCode -eq 0) "exit=$releaseCode"
    Check 'LOTUS is disarmed' (-not (Get-Status).Armed)
    Check 'the target works' (Wait-Until { (Get-PnpDevice -InstanceId $Target).Status -eq 'OK' } 15)
    Check 'read-only and the restrictions are gone' (Wait-Until { -not (Test-ReadOnly) -and -not (Test-Path $gateKey) } 15)
    Check 'the trusted list was kept' ((Get-Policy).Trusted.Count -ge 1)

    Section 'I. the rescue command without the service'
    $reply = Send-Policy (New-Policy $true 'ReadOnly' 'Block' $false @($Target))
    Check 'state before: target blocked, read-only on, restrictions on (again)' ($reply.Ok -and (Wait-Until { (Get-Device $Target).state -eq 'Blocked' -and (Test-ReadOnly) -and (Test-Gate) } 25))
    Check 'the service is stopped the hard way' (Stop-ServiceHard)
    $out = (& $guard --release-all) -join ' '
    $releaseCode = $LASTEXITCODE
    Say "   $out"
    Check 'the rescue command exits with 0 without the service' ($releaseCode -eq 0) "exit=$releaseCode"
    Check 'the target works without the service' (Wait-Until { (Get-PnpDevice -InstanceId $Target).Status -eq 'OK' } 15)
    Check 'read-only and the restrictions are gone without the service' (-not (Test-ReadOnly) -and -not (Test-Path $gateKey))
    $errorsBefore = (Get-Errors).Count
    Check 'the service starts again' (Start-ServiceAgain)
    Check 'LOTUS stayed disarmed' (-not (Get-Status).Armed -and -not (Get-Status).Lockdown) "armed=$((Get-Status).Armed) lockdown=$((Get-Status).Lockdown)"
    Check 'the trusted list survived the rescue' ((Get-Policy).Trusted.Count -ge 1)
    Start-Sleep -Seconds 3
    Check 'the target still works with the service back' ((Get-PnpDevice -InstanceId $Target).Status -eq 'OK')
    $acl = (& icacls.exe $secureDir) -join "`n"
    Check 'the service closed the store folder to administrators again' (-not ($acl -match 'Administrators:[^\n]*\((F|M|W)\)')) $acl
    $acl = (& icacls.exe $keyFile 2>&1) -join "`n"
    Check 'the key belongs to the service account alone again' (-not ($acl -match 'Administrators:')) $acl
    Check 'the service reported no error after the rescue' ((Get-Errors).Count -eq $errorsBefore) ((Get-Errors | Select-Object -Last 2 | ForEach-Object { $_.msg }) -join ' / ')

    Section 'I. a wrecked store locks LOTUS down, and setup resets it'
    Check 'the service is stopped the hard way (again)' (Stop-ServiceHard)
    & icacls.exe $secureDir /grant '*S-1-5-32-544:(OI)(CI)F' | Out-Null
    & icacls.exe $storeFile /grant '*S-1-5-32-544:(F)' | Out-Null
    $wrecked = $true
    try {
        [IO.File]::WriteAllText($storeFile, 'wrecked')
        Remove-Item "$storeFile.bak" -Force -ErrorAction SilentlyContinue
        Remove-Item "$storeFile.tmp" -Force -ErrorAction SilentlyContinue
    }
    catch { $wrecked = $false; Say "   could not wreck the store: $($_.Exception.Message)" }
    Check 'the service starts with the wrecked store' (Start-ServiceAgain)
    if ($wrecked) {
        Check 'LOTUS is locked down' ((Get-Status).Lockdown -and (Get-Status).Armed)
        Check 'the status file says so' ((Read-Text $statusFile) -match '"lockdown": true')
        Check 'a policy change is refused' ((Send-Policy (New-Policy $false 'Allow' 'Allow' $true)).Code -eq 'lockdown')
        Check 'the lockdown was logged as tampering' (@(Get-Events | Where-Object { $_.type -eq 'tamper' -and $_.msg -like '*locked down*' }).Count -ge 1)
        Check 'Windows refuses new devices during the lockdown' (Wait-Until { Test-Gate } 15)
        Check 'a restart of the service does not end the lockdown' ((Stop-ServiceHard) -and (Start-ServiceAgain) -and (Get-Status).Lockdown)
        $code = Invoke-Setup $Setup @('/quiet')
        Check 'running setup again exits with 0' ($code -eq 0) "exit=$code"
        Say-SetupLog
        Check 'the lockdown is over and LOTUS is disarmed' (Wait-Until { $s = Get-Status; $s.Ok -and -not $s.Lockdown -and -not $s.Armed } 25) "lockdown=$((Get-Status).Lockdown) armed=$((Get-Status).Armed)"
        Check 'the restrictions of the lockdown are gone' (Wait-Until { -not (Test-Path $gateKey) } 15)
        Check 'every device works after the reset' (Wait-Until { (Get-NotWorking).Count -eq 0 } 25) ((Get-NotWorking | ForEach-Object { $_.InstanceId }) -join ', ')
        Check 'the reset note is gone from the registry' ($null -eq (Get-ItemProperty 'HKLM:\SOFTWARE\LOTUS' -ErrorAction SilentlyContinue).ResetStore)
    }
    else {
        $code = Invoke-Setup $Setup @('/quiet')
        Say "   setup after the failed attempt: exit=$code"
    }
    & sc.exe stop LotusGuard | Out-Null
    Check 'the service is protected again after setup ran' ($LASTEXITCODE -ne 0) "exit=$LASTEXITCODE"
    Check 'the service starts automatically again after setup ran' ((Get-ServiceInfo).StartMode -eq 'Auto')

    # ---------- J. removal ----------
    Section 'J. removal'
    # Leave a blocked device, read-only mode and the restrictions behind on purpose.
    $reply = Send-Policy (New-Policy $true 'ReadOnly' 'Block' $false @($Target))
    $prepared = $reply.Ok -and (Wait-Until { (Get-Device $Target).state -eq 'Blocked' -and (Test-ReadOnly) -and (Test-Gate) } 25)
    Check 'state before removal: target blocked, read-only on, restrictions on' $prepared "code=$($reply.Code) $($reply.Message)"
    $code = Remove-Quietly
    Check 'removal exits with 0' ($code -eq 0) "exit=$code"
    Test-NothingLeft 'after removal:'
    $pnp = Get-PnpDevice -InstanceId $Target -ErrorAction SilentlyContinue
    Check 'target works after removal' ($pnp.Status -eq 'OK') "status=$($pnp.Status)"

    Section 'J. an installation that trusts what is connected'
    $code = Invoke-Setup $Setup @('/quiet', '/trust-connected')
    Check 'setup with /trust-connected exits with 0' ($code -eq 0) "exit=$code"
    Say-SetupLog
    $null = Wait-Until { $null -ne (Get-Policy) } 25
    $after = Get-Policy
    $connected = @([Lotus.Core.DeviceScanner]::Scan())
    $missed = @($connected | Where-Object { $null -eq $after -or -not $after.IsTrusted($_) })
    Say "   connected: $($connected.Count), trusted entries: $($after.Trusted.Count)"
    Check 'every connected device is on the trusted list' ($null -ne $after -and $connected.Count -gt 0 -and $missed.Count -eq 0) (($missed | ForEach-Object { $_.Name }) -join ', ')
    Check 'nothing else was set: still disarmed, everything allowed, strict mode off' ($null -ne $after -and -not $after.Armed -and $after.Storage -eq 'Allow' -and $after.Pnp -eq 'Allow' -and -not $after.Strict)
    Check 'no device was switched off by it' (@((Get-Scan).devices | Where-Object { $_.problem -ne 0 }).Count -eq 0)
    Check 'the setup log says what was trusted' ((Get-Content (Join-Path $installDir 'setup.log') -Raw) -match 'put on the trusted list')
    $code = Remove-Quietly
    Check 'removal after it exits with 0' ($code -eq 0) "exit=$code"

    # ---------- K. the setup window ----------
    Section 'K. setup window: install'
    $p = Start-Process -FilePath $Setup -PassThru
    $w = Get-SetupWindow $p
    Check 'setup window opens' ($null -ne $w)
    if ($null -ne $w) {
        Check 'welcome page offers INSTALL' (Wait-Button $w 'PrimaryButton' 'INSTALL' 10) "button=$(Get-PartText $w 'PrimaryButton')"
        Check 'the window has no password field' ($null -eq (Find-Part $w 'PasswordField'))
        $ask = Find-Part $w 'TrustConnected'
        $asked = $null -ne $ask -and -not $ask.Current.IsOffscreen
        Check 'the window asks whether to trust the connected devices' $asked
        if ($asked) {
            $toggle = $ask.GetCurrentPattern([System.Windows.Automation.TogglePattern]::Pattern)
            Check 'the answer is no until someone ticks it' ($toggle.Current.ToggleState -eq [System.Windows.Automation.ToggleState]::Off) "state=$($toggle.Current.ToggleState)"
            $toggle.Toggle()
            Check 'it can be ticked' ($toggle.Current.ToggleState -eq [System.Windows.Automation.ToggleState]::On)
        }
        Push-Button $w 'PrimaryButton'
        $done = Wait-Button $w 'PrimaryButton' 'OPEN LOTUS' 90
        $summary = Get-PartText $w 'WorkSummary'
        Say "   summary: $summary"
        Check 'install finishes and offers OPEN LOTUS' $done "button=$(Get-PartText $w 'PrimaryButton')"
        Check 'install reports no warnings' ($summary -eq 'LOTUS is installed and disarmed. Open it to choose your policy.') "summary=$summary"
        Check 'policy is disarmed after the window install' ((Get-Scan).policy -like 'disarmed*')
        if ($asked) {
            $after = Get-Policy
            $missed = @([Lotus.Core.DeviceScanner]::Scan() | Where-Object { $null -eq $after -or -not $after.IsTrusted($_) })
            Check 'with the box ticked, every connected device is trusted after the install' ($null -ne $after -and $after.Trusted.Count -gt 0 -and $missed.Count -eq 0) (($missed | ForEach-Object { $_.Name }) -join ', ')
        }
        Push-Button $w 'SecondaryButton'
        $code = Wait-Exit $p
        Check 'CLOSE ends setup with exit code 0' ($code -eq 0) "exit=$code"
    }

    Section 'K. setup window: update'
    $kept = New-Policy $true 'Allow' 'Block' $true @()
    $reply = Send-Policy $kept
    Check 'a policy is set for the window test' $reply.Ok "code=$($reply.Code) $($reply.Message)"
    # The service is switching devices off right now and answers slowly. The
    # window must come up all the same. How slowly is timed on the side.
    $timer = [powershell]::Create()
    $null = $timer.AddScript({
        $clock = [Diagnostics.Stopwatch]::StartNew()
        try { $null = (New-Object Lotus.Core.GuardClient ([Lotus.Core.GuardProtocol]::PipeName), $true).Status() } catch { }
        $clock.ElapsedMilliseconds
    })
    $pending = $timer.BeginInvoke()
    $p = Start-Process -FilePath $Setup -PassThru
    $w = Get-SetupWindow $p
    Check 'setup window opens again' ($null -ne $w)
    $watch = [Diagnostics.Stopwatch]::StartNew()
    $offered = $null -ne $w -and (Wait-Button $w 'PrimaryButton' 'UPDATE' 60)
    Say "   the UPDATE button was there after $($watch.ElapsedMilliseconds) ms"
    Check 'second run offers UPDATE' $offered "button=$(if ($null -ne $w) { Get-PartText $w 'PrimaryButton' })"
    Check 'the window does not wait for the service' ($offered -and $watch.ElapsedMilliseconds -lt 5000) "ms=$($watch.ElapsedMilliseconds)"
    try { Say "   the service took $($timer.EndInvoke($pending)) ms to answer a question asked at the same moment" } catch { Say "   the service could not be timed: $($_.Exception.Message)" }
    $timer.Dispose()
    if ($offered) {
        $ask = Find-Part $w 'TrustConnected'
        Check 'an update does not ask about trusting devices' ($null -eq $ask -or $ask.Current.IsOffscreen)
        Push-Button $w 'PrimaryButton'
        Check 'update finishes' (Wait-Button $w 'PrimaryButton' 'OPEN LOTUS' 90) "summary=$(Get-PartText $w 'WorkSummary')"
        $null = Wait-Until { $null -ne (Get-Policy) } 20
        $now = Get-Policy
        Check 'update keeps the policy' ($null -ne $now -and $now.Armed -and $now.Pnp -eq 'Block' -and $now.Trusted.Count -eq $kept.Trusted.Count) "trusted=$($now.Trusted.Count)"
        & sc.exe stop LotusGuard | Out-Null
        Check 'the service is protected again after the update' ($LASTEXITCODE -ne 0) "exit=$LASTEXITCODE"
        Get-Process LOTUS -ErrorAction SilentlyContinue | Where-Object { $_.MainWindowHandle -ne 0 } | Stop-Process -Force -ErrorAction SilentlyContinue
        Push-Button $w 'PrimaryButton'
        $code = Wait-Exit $p
        Check 'OPEN LOTUS ends setup with exit code 0' ($code -eq 0) "exit=$code"
        Check 'OPEN LOTUS opens the app' (Wait-Until { @(Get-Process LOTUS -ErrorAction SilentlyContinue | Where-Object { $_.MainWindowHandle -ne 0 }).Count -ge 1 } 15)
        Get-Process LOTUS -ErrorAction SilentlyContinue | Where-Object { $_.MainWindowHandle -ne 0 } | ForEach-Object { $_.CloseMainWindow() | Out-Null }
    }

    Section 'K. setup window: removal'
    $p = Start-Process -FilePath (Join-Path $installDir 'Uninstall.exe') -PassThru
    $w = Get-SetupWindow $p
    Check 'uninstaller window opens' ($null -ne $w)
    if ($null -ne $w) {
        Check 'uninstaller offers REMOVE' (Wait-Button $w 'PrimaryButton' 'REMOVE' 10) "button=$(Get-PartText $w 'PrimaryButton')"
        Push-Button $w 'PrimaryButton'
        $done = Wait-Button $w 'PrimaryButton' 'CLOSE' 90
        $summary = Get-PartText $w 'WorkSummary'
        Say "   summary: $summary"
        Check 'removal finishes' $done "button=$(Get-PartText $w 'PrimaryButton')"
        Check 'removal reports no warnings' ($summary -eq 'LOTUS is removed. Every USB device works on this PC again.') "summary=$summary"
        Push-Button $w 'PrimaryButton'
        $code = Wait-Exit $p
        Check 'CLOSE ends the uninstaller with exit code 0' ($code -eq 0) "exit=$code"
        Test-NothingLeft 'after window removal:'
    }
}
catch {
    $script:failed++
    Say "EXCEPTION: $($_.Exception.Message)"
    Say $_.ScriptStackTrace
}
finally {
    # ---------- L. leave the PC in a known state ----------
    Section 'L. final state'
    Get-Process LOTUS-Setup, Uninstall -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue
    try {
        if (Test-Installed) {
            $left = Get-ServiceInfo
            if ($left -and $left.StartMode -eq 'Disabled') { & sc.exe config LotusGuard start= auto | Out-Null }
            $code = Remove-Quietly
            Say "   removed what was left: exit=$code"
            $null = Wait-Until { -not (Test-Installed) } 20
        }
        # Whatever happened above, the Windows restrictions must not stay behind.
        if ((Test-Path $gateKey) -and (Test-Gate)) {
            Say '   the Windows restrictions were still there and are removed by hand'
            Remove-Item $gateKey -Recurse -Force -ErrorAction SilentlyContinue
            Start-Process pnputil.exe -ArgumentList '/scan-devices' -WindowStyle Hidden -Wait
        }
        if (-not $LeaveRemoved) {
            if (Test-Installed) { Say '   LOTUS could not be removed cleanly, so it is not installed again' }
            else {
                $code = Invoke-Setup $Setup @('/quiet')
                Check 'LOTUS is installed for you to use' ($code -eq 0 -and (Wait-Until { (Get-ServiceInfo).State -eq 'Running' -and (Get-Status).Ok } 25)) "exit=$code"
                if ($savedPolicy) {
                    $mine = [Lotus.Core.Policy]::FromJson($savedPolicy)
                    $mine.Armed = $false
                    $reply = Send-Policy $mine
                    Check 'your earlier policy is back, disarmed' $reply.Ok "code=$($reply.Code) $($reply.Message)"
                    Say "   $($mine.Trusted.Count) trusted entries restored"
                    if ($reply.Ok) { Remove-Item $savedPolicyFile -Force -ErrorAction SilentlyContinue }
                }
                Check 'final state: disarmed, nothing blocked, no restrictions' ((Get-Scan).policy -like 'disarmed*' -and @((Get-Scan).devices | Where-Object { $_.problem -ne 0 }).Count -eq 0 -and -not (Test-Path $gateKey))
            }
        }
    }
    catch {
        $script:failed++
        Say "EXCEPTION in the final step: $($_.Exception.Message)"
    }
    # A device that was refused or forgotten along the way gets one more chance.
    Repair-NotWorking
    $off = Get-NotWorking
    Check 'no connected USB device is switched off at the end' ($off.Count -eq 0) (($off | ForEach-Object { $_.InstanceId }) -join ', ')
    Check 'no installation restrictions are left at the end' (-not (Test-Path $gateKey))
    Say ''
    Say "RESULT: passed=$($script:passed) failed=$($script:failed)"
}
exit $script:failed
