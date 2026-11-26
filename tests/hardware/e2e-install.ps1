# End-to-end test of the real product on this PC. Needs an elevated prompt.
#
# It takes whatever LOTUS is installed (including 1.0) through an update, then
# installs fresh and checks, on real hardware:
#
#   - a data folder planted by someone else before installation is not trusted
#   - permissions: who can read and write the policy store and its key
#   - the service cannot be stopped, reconfigured or deleted with Windows tools,
#     and comes back by itself when its process is killed
#   - the policy only changes with the LOTUS password, through the service
#   - an edited policy store is put back and the edit is logged
#   - one chosen USB device is blocked, others keep working
#   - how fast a device is switched off the first time Windows sees it
#   - a device can be forced off the bus when Windows will not stop it
#   - devices that are not plugged in are marked ahead of time
#   - read-only mode, a service restart, trust, disarm
#   - removal needs the password and leaves nothing behind
#   - the setup window: install, update with password, remove with password
#
# -Target is the instance ID of a USB device that may be switched off for a few
# seconds at a time. Every other connected device is put on the trusted list.
#
# At the end LOTUS is installed, disarmed and without a password, and the policy
# that was there before the test (if any) is put back, disarmed.

param(
    [string]$Target = '',
    [string]$Setup = '',
    [string]$Log = '',
    [string]$Core = '',
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
$keyFile = Join-Path $secureDir 'seal.key'
$guard = Join-Path $installDir 'LotusGuard.exe'
$eventsFile = Join-Path $dataDir 'events.jsonl'
$roKey = 'HKLM:\SOFTWARE\Policies\Microsoft\Windows\RemovableStorageDevices'
$eventLogKey = 'HKLM:\SYSTEM\CurrentControlSet\Services\EventLog\Application\LotusGuard'
$password = 'e2e test password 1'
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
function Test-ReadOnly {
    foreach ($cls in '{53f5630d-b6bf-11d0-94f2-00a0c91efb8b}', '{53f56308-b6bf-11d0-94f2-00a0c91efb8b}', '{53f56311-b6bf-11d0-94f2-00a0c91efb8b}', '{6AC27878-A6FA-4155-BA85-F98F491D4F33}', '{F33FDC04-D1AC-4E8E-9A30-19BBD4B108AE}') {
        if ((Get-ItemProperty "$roKey\$cls" -ErrorAction SilentlyContinue).Deny_Write -eq 1) { return $true }
    }
    return $false
}
function Test-Installed {
    return (Test-Path $installDir) -or [bool](Get-Service LotusGuard -ErrorAction SilentlyContinue) -or (Test-Path $dataDir)
}
function Get-ServiceInfo { return Get-CimInstance Win32_Service -Filter "Name='LotusGuard'" }

# ---------- talking to the service ----------
function New-Guard { return New-Object Lotus.Core.GuardClient ([Lotus.Core.GuardProtocol]::PipeName), $true }
function New-Policy([bool]$armed, [string]$storage, [string]$pnp, [bool]$allowInput, [string[]]$exclude = @()) {
    $policy = New-Object Lotus.Core.Policy
    $policy.Armed = $armed
    $policy.Storage = [Lotus.Core.StorageMode]$storage
    $policy.Pnp = [Lotus.Core.PnpMode]$pnp
    $policy.AllowInput = $allowInput
    foreach ($device in [Lotus.Core.DeviceScanner]::Scan()) {
        if ($exclude -contains $device.InstanceId) { continue }
        [void]$policy.AddTrust([Lotus.Core.TrustEntry]::ForDevice($device))
    }
    return $policy
}
function Send-Policy($policy, [string]$pw = '', [string]$newPw = $null) {
    try { return (New-Guard).Apply($policy.ToJson(), $pw, $newPw) }
    catch {
        $reply = New-Object Lotus.Core.GuardReply
        $reply.Code = 'unavailable'; $reply.Message = $_.Exception.Message
        return $reply
    }
}
function Get-Stored { return [Lotus.Core.SecureStore]::ReadUnverified($storeFile) }
function Get-Status {
    try { return (New-Guard).Status() }
    catch { return New-Object Lotus.Core.GuardReply }
}
function Get-EventTime($e) {
    if ($e.t -is [DateTime]) { return $e.t.ToLocalTime() }
    return [DateTime]::Parse($e.t).ToLocalTime()
}

# ---------- setup ----------
function Invoke-Setup([string]$exe, [string[]]$arguments, [string]$pw = '') {
    if ($pw) { $env:LOTUS_PASSWORD = $pw } else { $env:LOTUS_PASSWORD = $null }
    try { $p = Start-Process -FilePath $exe -ArgumentList $arguments -Wait -PassThru; return $p.ExitCode }
    finally { $env:LOTUS_PASSWORD = $null }
}
function Remove-Quietly([string]$pw = '') {
    $uninstaller = Join-Path $installDir 'Uninstall.exe'
    if (Test-Path $uninstaller) { return Invoke-Setup $uninstaller @('/uninstall', '/quiet') $pw }
    return Invoke-Setup $Setup @('/uninstall', '/quiet') $pw
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
    Check "$label no LOTUS process is left" (Wait-Until { @(Get-Process LOTUS, LotusGuard -ErrorAction SilentlyContinue).Count -eq 0 } 10)
    $off = @(Get-PnpDevice | Where-Object { $_.InstanceId -like 'USB\VID_*' -and $_.Problem -eq 'CM_PROB_DISABLED' })
    Check "$label no USB device is left switched off" ($off.Count -eq 0) (($off | ForEach-Object { $_.InstanceId }) -join ', ')
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
function Set-Field($window, [string]$id, [string]$text) {
    $field = Find-Part $window $id
    $field.GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern).SetValue($text)
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
Say "setup=$Setup"
Say "setup sha256=$((Get-FileHash $Setup -Algorithm SHA256).Hash)"
Say "windows=$([Environment]::OSVersion.Version)"

$savedPolicy = $null

try {
    # ---------- A. whatever is installed now ----------
    Section 'A. starting point'
    $existing = Get-ServiceInfo
    if ($existing) {
        $oldVersion = (Get-Item $guard -ErrorAction SilentlyContinue).VersionInfo.FileVersion
        Say "   LOTUS $oldVersion is installed, service state $($existing.State)"
        $legacyFile = Join-Path $dataDir 'policy.json'
        $oldTrusted = -1; $oldArmed = $false
        if (Test-Path $legacyFile) {
            $savedPolicy = Get-Content $legacyFile -Raw
            $old = [Lotus.Core.Policy]::FromJson($savedPolicy)
            $oldTrusted = $old.Trusted.Count; $oldArmed = $old.Armed
            Say "   its policy: armed=$oldArmed, $oldTrusted trusted entries"
        }
        elseif (Test-Path $storeFile) {
            $stored = Get-Stored
            if ($stored) {
                $savedPolicy = $stored.Policy.ToJson()
                $oldTrusted = $stored.Policy.Trusted.Count; $oldArmed = $stored.Policy.Armed
                if ($stored.Password) { Say 'RESULT: the installed LOTUS has a password this test does not know. Remove LOTUS by hand first.'; exit 1 }
            }
        }

        Section 'A. update over the installed version'
        $code = Invoke-Setup $Setup @('/quiet')
        Check 'update exits with 0' ($code -eq 0) "exit=$code"
        $svc = Get-ServiceInfo
        Check 'service runs after the update' ($svc.State -eq 'Running') "state=$($svc.State)"
        Check 'the new version is installed' ((Get-Item $guard).VersionInfo.FileVersion -like '1.1.*') "version=$((Get-Item $guard).VersionInfo.FileVersion)"
        Check 'the policy was moved into the sealed store' (Wait-Until { Test-Path $storeFile } 15)
        $stored = Get-Stored
        if ($oldTrusted -ge 0) {
            Check 'update keeps the trusted list' ($stored.Policy.Trusted.Count -eq $oldTrusted) "before=$oldTrusted after=$($stored.Policy.Trusted.Count)"
            Check 'update keeps the armed setting' ($stored.Policy.Armed -eq $oldArmed)
        }
        Check 'the old policy file is gone' (Wait-Until { -not (Test-Path (Join-Path $dataDir 'policy.json')) } 10)
        Check 'the old event log registration is gone' (-not (Test-Path $eventLogKey))
        Check 'no device was switched off by the update' (@((Get-Scan).devices | Where-Object { $_.problem -ne 0 }).Count -eq 0 -or $oldArmed)

        $code = Remove-Quietly
        Check 'removal after the update exits with 0' ($code -eq 0) "exit=$code"
        Test-NothingLeft 'after update:'
    }
    else {
        Say '   nothing is installed'
        if (Test-Path $installDir) { Remove-Item $installDir -Recurse -Force -ErrorAction SilentlyContinue }
    }
    Check 'nothing is installed before the fresh install' (-not (Test-Installed))

    # ---------- B. a data folder someone planted before installation ----------
    Section 'B. planted data folder'
    $outside = Join-Path $env:TEMP ('lotus-e2e-outside-' + [Guid]::NewGuid().ToString('N'))
    New-Item -ItemType Directory -Force $outside | Out-Null
    Set-Content (Join-Path $outside 'keep.txt') 'this file must survive'
    New-Item -ItemType Directory -Force $dataDir | Out-Null
    Set-Content (Join-Path $dataDir 'policy.json') '{"version":1,"armed":false,"storage":"allow","pnp":"allow","allowInput":true,"trusted":[{"kind":"model","id":"VID_DEAD&PID_BEEF","name":"planted"}]}'
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
    Check 'the planted policy file is gone' (-not (Test-Path (Join-Path $dataDir 'policy.json')))
    $null = Wait-Until { Test-Path $storeFile } 15
    $stored = Get-Stored
    Check 'the planted trust entry was not taken over' ($null -ne $stored -and $stored.Policy.Trusted.Count -eq 0) "trusted=$($stored.Policy.Trusted.Count)"
    $setupLog = Join-Path $installDir 'setup.log'
    Check 'setup wrote down that it replaced the folder' ((Test-Path $setupLog) -and ((Get-Content $setupLog -Raw) -like '*could not be trusted*'))
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
    Check 'listed in installed apps as 1.1' ($un.DisplayName -eq 'LOTUS' -and $un.DisplayVersion -like '1.1.*' -and $un.UninstallString -like '*Uninstall.exe" /uninstall')
    Check 'no setup log in TEMP' (-not (Test-Path (Join-Path $env:TEMP 'LOTUS-Setup.log')))
    Check 'no event log registration' (-not (Test-Path $eventLogKey))
    Check 'guard logged its start' (Wait-Until { (Count-Events 'guard_started') -ge 1 } 10)
    Check 'notifier is running' (Wait-Until { @(Get-CimInstance Win32_Process -Filter "Name='LOTUS.exe'" | Where-Object { $_.CommandLine -like '*--tray*' }).Count -ge 1 } 10)

    Section 'C. permissions'
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
    Say "   seal key: $(($acl -split "`n" | Select-Object -First 2) -join ' | ')"
    Check 'seal key: only the service account is listed' (($acl -match 'NT AUTHORITY\\SYSTEM:\(F\)') -and -not ($acl -match 'Administrators') -and -not ($acl -match 'Users'))
    $read = $true; try { $null = [IO.File]::ReadAllBytes($keyFile) } catch { $read = $false }
    Check 'an administrator cannot read the seal key' (-not $read)
    $wrote = $true; try { [IO.File]::AppendAllText($storeFile, ' ') } catch { $wrote = $false }
    Check 'an administrator cannot write the policy store' (-not $wrote)
    Check 'the status file is there for the notifier' (Test-Path (Join-Path $dataDir 'status.json'))
    Check 'the status file does not hold the password record' (-not ((Get-Content (Join-Path $dataDir 'status.json') -Raw) -match 'salt|hash'))

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

    # ---------- D. disarmed, then armed with a password ----------
    Section 'D. disarmed after install'
    $status = Get-Status
    Check 'the service answers on its control channel' $status.Ok $status.Message
    Check 'no password is set yet' (-not $status.HasPassword)
    $scan = Get-Scan
    Say "   policy: $($scan.policy)"
    Check 'policy is disarmed' ($scan.policy -like 'disarmed*')
    Check 'target is present' ($null -ne ($scan.devices | Where-Object { $_.id -eq $Target }))
    Check 'no device is off' (@($scan.devices | Where-Object { $_.problem -ne 0 }).Count -eq 0)
    Check 'every device was read from its own descriptors' (@($scan.devices | Where-Object { -not $_.described }).Count -eq 0) (($scan.devices | Where-Object { -not $_.described } | ForEach-Object { $_.name }) -join ', ')

    Section 'D. arming needs a password'
    $strict = New-Policy $true 'Block' 'Block' $false @($Target)
    Say "   trusting $($strict.Trusted.Count) other devices"
    $reply = Send-Policy $strict
    Check 'arming without a password is refused' ($reply.Code -eq 'new_password_needed') "code=$($reply.Code) $($reply.Message)"
    $reply = Send-Policy $strict '' 'short'
    Check 'a short password is refused' ($reply.Code -eq 'weak_password') "code=$($reply.Code)"
    Check 'still disarmed' ((Get-Scan).policy -like 'disarmed*')
    $t0 = Get-Date
    $reply = Send-Policy $strict '' $password
    Check 'arming with a password works' $reply.Ok "code=$($reply.Code) $($reply.Message)"
    $blocked = Wait-Until { (Get-Device $Target).state -eq 'Blocked' } 20
    $t = Get-Device $Target
    Say "   target: state=$($t.state) problem=$($t.problem) started=$($t.started) kinds=$($t.kinds), seen blocked $([int]((Get-Date) - $t0).TotalMilliseconds) ms after the request was sent"
    Check 'target is blocked' $blocked "state=$($t.state)"
    Check 'target is switched off in Windows' ($t.problem -eq 22 -or $t.problem -eq 21) "problem=$($t.problem)"
    $scan = Get-Scan
    $bad = @($scan.devices | Where-Object { $_.id -ne $Target -and ($_.problem -ne 0 -or $_.state -ne 'Trusted') })
    Check 'every other device still works and shows as trusted' ($bad.Count -eq 0) (($bad | ForEach-Object { "$($_.name)=$($_.state)/$($_.problem)" }) -join ', ')
    Check 'block was logged' ((Count-Events 'blocked' $Target) -ge 1)
    Check 'the password event was logged' ((Count-Events 'security') -ge 1)
    $pnp = Get-PnpDevice -InstanceId $Target -ErrorAction SilentlyContinue
    Check 'Device Manager agrees it is off' ($pnp.Status -ne 'OK')
    Check 'the status file says armed' ((Get-Content (Join-Path $dataDir 'status.json') -Raw) -match '"armed": true')

    Section 'D. the password guards every change'
    $open = New-Policy $false 'Allow' 'Allow' $true
    $reply = Send-Policy $open
    Check 'a change without the password is refused' ($reply.Code -eq 'password_needed') "code=$($reply.Code)"
    $reply = Send-Policy $open 'not the password'
    Check 'a change with a wrong password is refused' ($reply.Code -eq 'wrong_password') "code=$($reply.Code)"
    Check 'the wrong password was logged' (@(Get-Events | Where-Object { $_.type -eq 'security' -and $_.msg -like '*wrong LOTUS password*' }).Count -ge 1)
    Check 'still armed and target still blocked' ((Get-Scan).policy -like 'armed*' -and (Get-Device $Target).state -eq 'Blocked')
    $env:LOTUS_PASSWORD = 'not the password'
    & $guard --release-all | Out-Null
    $releaseCode = $LASTEXITCODE
    $env:LOTUS_PASSWORD = $null
    Check '--release-all with a wrong password is refused' ($releaseCode -eq 3 -and (Get-Device $Target).state -eq 'Blocked') "exit=$releaseCode"

    # ---------- E. tampering ----------
    Section 'E. killing the service'
    $before = (Get-ServiceInfo).ProcessId
    $killed = $true; try { Stop-Process -Id $before -Force -ErrorAction Stop } catch { $killed = $false; Say "   could not kill it: $($_.Exception.Message)" }
    if ($killed) {
        $back = Wait-Until { $s = Get-ServiceInfo; $s.State -eq 'Running' -and $s.ProcessId -ne 0 -and $s.ProcessId -ne $before } 30
        Check 'Windows starts the service again by itself' $back "pid before=$before now=$((Get-ServiceInfo).ProcessId)"
        Check 'the target stayed blocked meanwhile' ((Get-PnpDevice -InstanceId $Target).Status -ne 'OK')
        Check 'the policy is still armed after the restart' (Wait-Until { (Get-Status).Armed } 15)
        Check 'the password is still set after the restart' ((Get-Status).HasPassword)
        Check 'it was not blocked a second time' ((Count-Events 'blocked' $Target) -eq 1)
    }
    else { Say '   skipped: this session may not end the service process' }

    Section 'E. editing the policy store'
    # A determined administrator gives himself write access first. LOTUS cannot
    # prevent that, but it notices the edit and undoes it.
    & icacls.exe $storeFile /grant '*S-1-5-32-544:(F)' | Out-Null
    $text = [IO.File]::ReadAllText($storeFile)
    $edited = $false
    try { [IO.File]::WriteAllText($storeFile, $text.Replace('"armed": true', '"armed": false')); $edited = $true } catch { Say "   edit failed: $($_.Exception.Message)" }
    if ($edited) {
        Check 'the edit is logged as tampering' (Wait-Until { (Count-Events 'tamper') -ge 1 } 15)
        Check 'the store is put back' (Wait-Until { try { [IO.File]::ReadAllText($storeFile).Contains('"armed": true') } catch { $false } } 15)
        Check 'the policy did not change' ((Get-Status).Armed -and (Get-Device $Target).state -eq 'Blocked')
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

    # ---------- F. how fast is a device switched off ----------
    Section 'F. first contact'
    # Deleting the device record makes Windows forget the device. Scanning for
    # hardware then finds it as if it had never been seen: the hard case.
    $blocksBefore = Count-Events 'blocked' $Target
    & pnputil.exe /remove-device "$Target" | Out-Null
    Say "   pnputil remove exit=$LASTEXITCODE"
    Start-Sleep -Seconds 2
    $t0 = Get-Date
    $watch = [Diagnostics.Stopwatch]::StartNew()
    Start-Process pnputil.exe -ArgumentList '/scan-devices' -WindowStyle Hidden
    $seen = -1; $started = -1; $off = -1
    while ($watch.ElapsedMilliseconds -lt 15000 -and $off -lt 0) {
        $d = [Lotus.Core.DeviceScanner]::ScanOne($Target)
        if ($null -ne $d) {
            if ($seen -lt 0) { $seen = $watch.ElapsedMilliseconds }
            if ($d.Started -and $started -lt 0) { $started = $watch.ElapsedMilliseconds }
            if ($d.IsOff) { $off = $watch.ElapsedMilliseconds }
        }
        [Lotus.Core.DeviceScanner]::Forget($Target)
    }
    Say "   after the scan was requested: device seen at $seen ms, seen running at $(if ($started -lt 0) { 'never' } else { "$started ms" }), seen switched off at $off ms"
    if ($seen -ge 0 -and $off -ge 0) { Say "   switched off $($off - $seen) ms after this test first saw it" }
    Check 'the device is switched off again after Windows found it anew' ($off -ge 0)
    Check 'this test never saw it running' ($started -lt 0) "running at $started ms"
    Start-Sleep -Seconds 2
    $pnpEvents = @(Get-WinEvent -FilterHashtable @{ LogName = 'Microsoft-Windows-Kernel-PnP/Configuration'; StartTime = $t0 } -ErrorAction SilentlyContinue | Where-Object { $_.Message -like "*$Target*" } | Sort-Object TimeCreated)
    foreach ($e in $pnpEvents) { Say "   Windows: $($e.TimeCreated.ToString('HH:mm:ss.fff')) event $($e.Id) $(($e.Message -split "`n")[0])" }
    $startedByWindows = @($pnpEvents | Where-Object { $_.Id -eq 410 }).Count
    Check 'Windows never reports the device as started' ($startedByWindows -eq 0) "started events=$startedByWindows"
    foreach ($e in (Get-Events | Where-Object { $_.id -eq $Target -and (Get-EventTime $_) -ge $t0 })) { Say "   LOTUS:   $((Get-EventTime $e).ToString('HH:mm:ss.fff')) $($e.type)" }
    Check 'the new block was logged' ((Count-Events 'blocked' $Target) -gt $blocksBefore)

    Section 'F. forcing a device off the bus'
    # Trust the target so it runs, then use the forced path directly: mark it
    # disabled and cycle its hub port. This is what LOTUS falls back to when a
    # program holds a device open and Windows refuses to stop it.
    $all = New-Policy $true 'Block' 'Block' $false @()
    $reply = Send-Policy $all $password
    Check 'trusting the target works' $reply.Ok "code=$($reply.Code) $($reply.Message)"
    Check 'target works again and shows as trusted' (Wait-Until { $d = Get-Device $Target; $d.state -eq 'Trusted' -and $d.problem -eq 0 } 20)
    Check 'release was logged' ((Count-Events 'released' $Target) -ge 1)
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
        # With everything blocked and nothing unplugged on the trusted list, each of them must be marked.
        $marked = Wait-Until { @([Lotus.Core.DeviceScanner]::ScanAbsent() | Where-Object { -not $_.IsOff }).Count -eq 0 } 15
        $still = @([Lotus.Core.DeviceScanner]::ScanAbsent() | Where-Object { -not $_.IsOff })
        Check 'every unplugged device is marked as switched off' $marked (($still | ForEach-Object { $_.InstanceId }) -join ', ')
        Check 'the marks are in the ledger' ((Get-Scan).ledgerEntries -ge $absent.Count) "ledger=$((Get-Scan).ledgerEntries)"
    }
    else { Say '   skipped: no unplugged device with known functions on this PC' }

    # ---------- G. read-only, disarm ----------
    Section 'G. read-only'
    $ro = New-Policy $true 'ReadOnly' 'Allow' $true @()
    $reply = Send-Policy $ro $password
    Check 'switching to read-only works' $reply.Ok "code=$($reply.Code)"
    $on = Wait-Until { (Get-ItemProperty "$roKey\{53f5630d-b6bf-11d0-94f2-00a0c91efb8b}" -ErrorAction SilentlyContinue).Deny_Write -eq 1 } 15
    Check 'Windows read-only policy is set for removable disks' $on
    foreach ($cls in '{53f56308-b6bf-11d0-94f2-00a0c91efb8b}', '{53f56311-b6bf-11d0-94f2-00a0c91efb8b}', '{6AC27878-A6FA-4155-BA85-F98F491D4F33}', '{F33FDC04-D1AC-4E8E-9A30-19BBD4B108AE}') {
        Check "read-only policy for $cls" ((Get-ItemProperty "$roKey\$cls" -ErrorAction SilentlyContinue).Deny_Write -eq 1)
    }
    Remove-ItemProperty "$roKey\{53f5630d-b6bf-11d0-94f2-00a0c91efb8b}" -Name Deny_Write -ErrorAction SilentlyContinue
    Check 'a deleted read-only setting is restored' (Wait-Until { (Get-ItemProperty "$roKey\{53f5630d-b6bf-11d0-94f2-00a0c91efb8b}" -ErrorAction SilentlyContinue).Deny_Write -eq 1 } 15)
    Check 'unplugged devices are released when nothing blocks them any more' (Wait-Until { @([Lotus.Core.DeviceScanner]::ScanAbsent() | Where-Object { $_.IsOff }).Count -eq 0 } 15)

    Section 'G. block again, then disarm'
    $reply = Send-Policy (New-Policy $true 'Allow' 'Block' $false @($Target)) $password
    Check 'target is blocked again' ($reply.Ok -and (Wait-Until { (Get-Device $Target).state -eq 'Blocked' } 20))
    Check 'read-only policy is removed again' (Wait-Until { -not (Test-ReadOnly) } 15)
    $reply = Send-Policy (New-Policy $false 'Allow' 'Block' $false @($Target)) $password
    Check 'disarming switches it back on' ($reply.Ok -and (Wait-Until { $d = Get-Device $Target; $d.problem -eq 0 -and $d.state -eq 'Allowed' } 20))
    Check 'disarming keeps the password' ((Get-Status).HasPassword)

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

    # ---------- I. removal ----------
    Section 'I. removal needs the password'
    # Leave a blocked device and read-only mode behind on purpose.
    $reply = Send-Policy (New-Policy $true 'ReadOnly' 'Block' $false @($Target)) $password
    $prepared = $reply.Ok -and (Wait-Until { (Get-Device $Target).state -eq 'Blocked' -and (Test-ReadOnly) } 20)
    Check 'state before removal: target blocked and read-only on' $prepared
    $code = Remove-Quietly
    Check 'removal without the password fails' ($code -ne 0) "exit=$code"
    Check 'LOTUS is still installed and running' ((Get-ServiceInfo).State -eq 'Running' -and (Test-Path $guard))
    $code = Remove-Quietly 'not the password'
    Check 'removal with a wrong password fails' ($code -ne 0 -and (Get-ServiceInfo).State -eq 'Running') "exit=$code"
    Check 'the target is still blocked' ((Get-PnpDevice -InstanceId $Target).Status -ne 'OK')
    $code = Remove-Quietly $password
    Check 'removal with the password exits with 0' ($code -eq 0) "exit=$code"
    Test-NothingLeft 'after removal:'
    $pnp = Get-PnpDevice -InstanceId $Target -ErrorAction SilentlyContinue
    Check 'target works after removal' ($pnp.Status -eq 'OK') "status=$($pnp.Status)"

    # ---------- J. the setup window ----------
    Section 'J. setup window: install'
    $p = Start-Process -FilePath $Setup -PassThru
    $w = Get-SetupWindow $p
    Check 'setup window opens' ($null -ne $w)
    if ($null -ne $w) {
        Check 'welcome page offers INSTALL' (Wait-Button $w 'PrimaryButton' 'INSTALL' 10) "button=$(Get-PartText $w 'PrimaryButton')"
        Check 'no password is asked on a new PC' ($null -eq (Find-Part $w 'PasswordField'))
        Push-Button $w 'PrimaryButton'
        $done = Wait-Button $w 'PrimaryButton' 'OPEN LOTUS' 90
        $summary = Get-PartText $w 'WorkSummary'
        Say "   summary: $summary"
        Check 'install finishes and offers OPEN LOTUS' $done "button=$(Get-PartText $w 'PrimaryButton')"
        Check 'install reports no warnings' ($summary -eq 'LOTUS is installed and disarmed. Open it to choose your policy.') "summary=$summary"
        Check 'policy is disarmed after the window install' ((Get-Scan).policy -like 'disarmed*')
        Push-Button $w 'SecondaryButton'
        $code = Wait-Exit $p
        Check 'CLOSE ends setup with exit code 0' ($code -eq 0) "exit=$code"
    }

    Section 'J. setup window: update needs the password'
    $kept = New-Policy $true 'Allow' 'Block' $true @()
    $reply = Send-Policy $kept '' $password
    Check 'a password is set for the window test' $reply.Ok "code=$($reply.Code) $($reply.Message)"
    $p = Start-Process -FilePath $Setup -PassThru
    $w = Get-SetupWindow $p
    Check 'setup window opens again' ($null -ne $w)
    if ($null -ne $w) {
        Check 'second run offers UPDATE' (Wait-Button $w 'PrimaryButton' 'UPDATE' 10) "button=$(Get-PartText $w 'PrimaryButton')"
        Check 'the window asks for the password' ($null -ne (Find-Part $w 'PasswordField'))
        Set-Field $w 'PasswordField' 'not the password'
        Push-Button $w 'PrimaryButton'
        Check 'a wrong password is refused in the window' (Wait-Until { (Get-PartText $w 'PasswordError') -ne '' } 15) "error=$(Get-PartText $w 'PasswordError')"
        Check 'nothing was changed by the wrong password' ((Get-ServiceInfo).State -eq 'Running' -and (Get-PartText $w 'PrimaryButton') -eq 'UPDATE')
        Set-Field $w 'PasswordField' $password
        Push-Button $w 'PrimaryButton'
        Check 'update finishes' (Wait-Button $w 'PrimaryButton' 'OPEN LOTUS' 90) "summary=$(Get-PartText $w 'WorkSummary')"
        $stored = Get-Stored
        Check 'update keeps the policy' ($null -ne $stored -and $stored.Policy.Armed -and $stored.Policy.Pnp -eq 'Block' -and $stored.Policy.Trusted.Count -eq $kept.Trusted.Count)
        Check 'update keeps the password' ($null -ne $stored.Password -and (Wait-Until { (Get-Status).HasPassword } 15))
        & sc.exe stop LotusGuard | Out-Null
        Check 'the service is protected again after the update' ($LASTEXITCODE -ne 0) "exit=$LASTEXITCODE"
        Get-Process LOTUS -ErrorAction SilentlyContinue | Where-Object { $_.MainWindowHandle -ne 0 } | Stop-Process -Force -ErrorAction SilentlyContinue
        Push-Button $w 'PrimaryButton'
        $code = Wait-Exit $p
        Check 'OPEN LOTUS ends setup with exit code 0' ($code -eq 0) "exit=$code"
        Check 'OPEN LOTUS opens the app' (Wait-Until { @(Get-Process LOTUS -ErrorAction SilentlyContinue | Where-Object { $_.MainWindowHandle -ne 0 }).Count -ge 1 } 15)
        Get-Process LOTUS -ErrorAction SilentlyContinue | Where-Object { $_.MainWindowHandle -ne 0 } | ForEach-Object { $_.CloseMainWindow() | Out-Null }
    }

    Section 'J. setup window: removal needs the password'
    $p = Start-Process -FilePath (Join-Path $installDir 'Uninstall.exe') -PassThru
    $w = Get-SetupWindow $p
    Check 'uninstaller window opens' ($null -ne $w)
    if ($null -ne $w) {
        Check 'uninstaller offers REMOVE' (Wait-Button $w 'PrimaryButton' 'REMOVE' 10) "button=$(Get-PartText $w 'PrimaryButton')"
        Check 'the uninstaller asks for the password' ($null -ne (Find-Part $w 'PasswordField'))
        Push-Button $w 'PrimaryButton'
        Check 'an empty password is refused' (Wait-Until { (Get-PartText $w 'PasswordError') -ne '' } 15)
        Check 'LOTUS is still there' ((Get-ServiceInfo).State -eq 'Running')
        Set-Field $w 'PasswordField' $password
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
    # ---------- K. leave the PC in a known state ----------
    Section 'K. final state'
    Get-Process LOTUS-Setup, Uninstall -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue
    try {
        if (Get-Service LotusGuard -ErrorAction SilentlyContinue) {
            $code = Remove-Quietly $(if ((Get-Status).HasPassword) { $password } else { '' })
            Say "   removed what was left: exit=$code"
        }
        if (-not $LeaveRemoved) {
            if (Test-Installed) { Say '   LOTUS could not be removed cleanly, so it is not installed again' }
            else {
                $code = Invoke-Setup $Setup @('/quiet')
                Check 'LOTUS is installed for you to use' ($code -eq 0 -and (Wait-Until { (Get-ServiceInfo).State -eq 'Running' } 20)) "exit=$code"
                if ($savedPolicy) {
                    $mine = [Lotus.Core.Policy]::FromJson($savedPolicy)
                    $mine.Armed = $false
                    $reply = Send-Policy $mine
                    Check 'your earlier policy is back, disarmed' $reply.Ok "code=$($reply.Code) $($reply.Message)"
                    Say "   $($mine.Trusted.Count) trusted entries restored"
                }
                Check 'final state: disarmed, no password, nothing blocked' ((Get-Scan).policy -like 'disarmed*' -and -not (Get-Status).HasPassword -and @((Get-Scan).devices | Where-Object { $_.problem -ne 0 }).Count -eq 0)
            }
        }
    }
    catch {
        $script:failed++
        Say "EXCEPTION in the final step: $($_.Exception.Message)"
    }
    $off = @(Get-PnpDevice -PresentOnly | Where-Object { $_.InstanceId -like 'USB\VID_*' -and $_.Status -ne 'OK' })
    Check 'no connected USB device is switched off at the end' ($off.Count -eq 0) (($off | ForEach-Object { $_.InstanceId }) -join ', ')
    Say ''
    Say "RESULT: passed=$($script:passed) failed=$($script:failed)"
}
exit $script:failed
