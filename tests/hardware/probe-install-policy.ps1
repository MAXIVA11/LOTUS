# Probe: how does Windows behave when its device installation restrictions are
# used to keep USB devices from being installed? Needs an elevated prompt.
#
# LOTUS strict mode is built on the answers. The probe writes the policy by
# hand, makes Windows forget one or two devices so that they arrive as new, and
# writes down what happens. Everything is undone before the script ends.
#
# -Target     a single-function USB device that may be off for a minute
# -Composite  a USB device with several functions that may be off for a minute
# Results go to the log file.

param(
    [string]$Target = '',
    [string]$Composite = '',
    [string]$Log = '',
    [string]$Core = ''
)

$ErrorActionPreference = 'Continue'
if (-not $Target) { Write-Host 'Pass -Target with the instance ID of a USB device that may be switched off briefly.'; exit 1 }
if (-not $Core) { $Core = Join-Path $PSScriptRoot '..\..\src\Lotus.Guard\bin\Release\Lotus.Core.dll' }
if (-not $Log) { $Log = Join-Path $env:TEMP 'lotus-probe-install-policy.log' }
$Target = $Target.ToUpperInvariant()
$Composite = $Composite.ToUpperInvariant()
$R = 'HKLM:\SOFTWARE\Policies\Microsoft\Windows\DeviceInstall\Restrictions'

Set-Content -Path $Log -Value "LOTUS install policy probe $(Get-Date -Format s)" -Encoding utf8
function Say($text) { Add-Content -Path $Log -Value $text -Encoding utf8 }
function Section([string]$title) { Say ''; Say "=== $title ===" }

$admin = ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole(
    [Security.Principal.WindowsBuiltInRole]::Administrator)
if (-not $admin) { Say 'not elevated, nothing was tried'; exit 1 }

$coreCopy = Join-Path $env:TEMP ('lotus-probe-core-' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Force $coreCopy | Out-Null
Copy-Item ([System.IO.Path]::GetFullPath($Core)) $coreCopy
Add-Type -Path (Join-Path $coreCopy 'Lotus.Core.dll')

Add-Type -Namespace Probe -Name Cm -MemberDefinition @'
[DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)]
public static extern int CM_Locate_DevNodeW(out int node, string id, int flags);
[DllImport("cfgmgr32.dll")]
public static extern int CM_Reenumerate_DevNode(int node, int flags);
[DllImport("cfgmgr32.dll")]
public static extern int CM_Get_Parent(out int parent, int node, int flags);
[DllImport("cfgmgr32.dll")]
public static extern int CM_Get_DevNode_Status(out int status, out int problem, int node, int flags);
'@

function Get-State([string]$id) {
    $d = Get-PnpDevice -InstanceId $id -ErrorAction SilentlyContinue
    if ($null -eq $d) { return 'no record' }
    $node = 0
    $located = [Probe.Cm]::CM_Locate_DevNodeW([ref]$node, $id, 0)
    $status = 0; $problem = 0
    if ($located -eq 0) { $null = [Probe.Cm]::CM_Get_DevNode_Status([ref]$status, [ref]$problem, $node, 0) }
    $started = ($status -band 0x8) -ne 0
    return "present=$($d.Present) status=$($d.Status) problem=$problem started=$started class=$($d.Class) name=$($d.FriendlyName)"
}
function Test-Started([string]$id) {
    $node = 0
    if ([Probe.Cm]::CM_Locate_DevNodeW([ref]$node, $id, 0) -ne 0) { return $false }
    $status = 0; $problem = 0
    if ([Probe.Cm]::CM_Get_DevNode_Status([ref]$status, [ref]$problem, $node, 0) -ne 0) { return $false }
    return (($status -band 0x8) -ne 0)
}
function Wait-Started([string]$id, [int]$seconds) {
    $deadline = (Get-Date).AddSeconds($seconds)
    while ((Get-Date) -lt $deadline) { if (Test-Started $id) { return $true }; Start-Sleep -Milliseconds 200 }
    return (Test-Started $id)
}
function Get-Family([string]$id) {
    # The device itself and its interface children on the USB enumerator.
    $model = ($id -split '\\')[1]
    return @(Get-PnpDevice -ErrorAction SilentlyContinue | Where-Object { $_.InstanceId -like "USB\$model*" } | Sort-Object InstanceId)
}
function Show-Family([string]$id) {
    foreach ($d in (Get-Family $id)) { Say "      $($d.InstanceId): $(Get-State $d.InstanceId)" }
    $model = ($id -split '\\')[1]
    foreach ($d in @(Get-PnpDevice -PresentOnly -ErrorAction SilentlyContinue | Where-Object { $_.InstanceId -like "HID\$model*" })) {
        Say "      $($d.InstanceId): status=$($d.Status) class=$($d.Class)"
    }
}
function Show-Events([datetime]$since, [string]$id) {
    $model = ($id -split '\\')[1]
    $events = @(Get-WinEvent -FilterHashtable @{ LogName = 'Microsoft-Windows-Kernel-PnP/Configuration'; StartTime = $since } -ErrorAction SilentlyContinue | Where-Object { $_.Message -like "*$model*" } | Sort-Object TimeCreated)
    foreach ($e in $events) { Say "      Windows $($e.TimeCreated.ToString('HH:mm:ss.fff')) event $($e.Id): $(($e.Message -split "`n")[0])" }
    if ($events.Count -eq 0) { Say '      Windows logged no configuration event' }
}
function Set-List([string]$name, [string[]]$ids) {
    if (-not (Test-Path $R)) { New-Item $R -Force | Out-Null }
    if (Test-Path "$R\$name") { Remove-Item "$R\$name" -Recurse -Force }
    New-Item "$R\$name" -Force | Out-Null
    $i = 1
    foreach ($id in $ids) { New-ItemProperty "$R\$name" -Name "$i" -Value $id -PropertyType String -Force | Out-Null; $i++ }
    Set-ItemProperty $R -Name $name -Value 1 -Type DWord
}
function Forget([string]$id) {
    & pnputil.exe /remove-device "$id" /subtree | Out-Null
    $code = $LASTEXITCODE
    if ($code -ne 0) { & pnputil.exe /remove-device "$id" | Out-Null; $code = "$code then $LASTEXITCODE" }
    return $code
}
function Rescan { Start-Process pnputil.exe -ArgumentList '/scan-devices' -WindowStyle Hidden -Wait }
function Try-Triggers([string]$id) {
    # Which nudge makes Windows install a device once the policy lets it?
    $steps = [ordered]@{
        'nothing, just waiting'                  = { }
        'CM_Reenumerate_DevNode on the device with RETRY_INSTALLATION' = {
            $node = 0
            if ([Probe.Cm]::CM_Locate_DevNodeW([ref]$node, $id, 0) -eq 0) { Say "         returned $([Probe.Cm]::CM_Reenumerate_DevNode($node, 3))" }
        }
        'CM_Reenumerate_DevNode on its parent with RETRY_INSTALLATION' = {
            $node = 0; $parent = 0
            if ([Probe.Cm]::CM_Locate_DevNodeW([ref]$node, $id, 0) -eq 0 -and [Probe.Cm]::CM_Get_Parent([ref]$parent, $node, 0) -eq 0) {
                Say "         returned $([Probe.Cm]::CM_Reenumerate_DevNode($parent, 3))"
            }
        }
        'pnputil /scan-devices'                  = { Rescan }
        'pnputil /restart-device'                = { & pnputil.exe /restart-device "$id" | Out-Null; Say "         exit $LASTEXITCODE" }
        'Disable-PnpDevice then Enable-PnpDevice' = {
            Disable-PnpDevice -InstanceId $id -Confirm:$false -ErrorAction SilentlyContinue
            Enable-PnpDevice -InstanceId $id -Confirm:$false -ErrorAction SilentlyContinue
        }
        'forgetting the device and scanning'     = { $null = Forget $id; Start-Sleep -Seconds 1; Rescan }
    }
    foreach ($name in $steps.Keys) {
        $watch = [Diagnostics.Stopwatch]::StartNew()
        & $steps[$name]
        if (Wait-Started $id 4) { Say "      started after: $name ($($watch.ElapsedMilliseconds) ms)"; return $name }
        Say "      not started after: $name"
    }
    return ''
}

$existed = Test-Path $R
$others = @()
try {
    Section 'before'
    Say "   Windows $([Environment]::OSVersion.Version)"
    Say "   target: $(Get-State $Target)"
    if ($Composite) { Say "   composite:"; Show-Family $Composite }
    if ($existed) {
        Say '   this PC already has device installation restrictions. The probe does not touch them.'
        Get-ItemProperty $R | Out-String | ForEach-Object { Say $_ }
        throw 'existing policy'
    }
    $others = @(Get-PnpDevice -PresentOnly | Where-Object { $_.InstanceId -like 'USB\VID_*' -and $_.Status -eq 'OK' } | ForEach-Object { $_.InstanceId })
    Say "   $($others.Count) USB devices and interfaces are running"

    Section '1. deny every USB class, layered evaluation on'
    $deny = @('USB\COMPOSITE')
    foreach ($n in 0..255) { $deny += ('USB\Class_{0:X2}' -f $n); $deny += ('USB\DevClass_{0:X2}' -f $n) }
    Set-ItemProperty -Path (New-Item $R -Force).PSPath -Name 'AllowDenyLayered' -Value 1 -Type DWord
    Set-List 'DenyDeviceIDs' $deny
    Set-ItemProperty $R -Name 'DenyDeviceIDsRetroactive' -Value 0 -Type DWord
    Say "   wrote $($deny.Count) IDs"
    Start-Sleep -Seconds 3
    $broken = @($others | Where-Object { (Get-PnpDevice -InstanceId $_ -ErrorAction SilentlyContinue).Status -ne 'OK' })
    Say "   devices that were running and are not any more: $($broken.Count) $($broken -join ', ')"

    Section '2. a device Windows has never seen arrives'
    $t0 = Get-Date
    Say "   forget exit=$(Forget $Target)"
    Start-Sleep -Seconds 2
    $watch = [Diagnostics.Stopwatch]::StartNew()
    Rescan
    $seen = -1; $startedAt = -1
    while ($watch.ElapsedMilliseconds -lt 8000) {
        $node = 0
        if ([Probe.Cm]::CM_Locate_DevNodeW([ref]$node, $Target, 0) -eq 0) {
            if ($seen -lt 0) { $seen = $watch.ElapsedMilliseconds }
            if ((Test-Started $Target) -and $startedAt -lt 0) { $startedAt = $watch.ElapsedMilliseconds }
        }
        Start-Sleep -Milliseconds 20
    }
    Say "   seen at $seen ms, started at $(if ($startedAt -lt 0) { 'never' } else { "$startedAt ms" })"
    Say "   state: $(Get-State $Target)"
    Show-Events $t0 $Target
    $described = [Lotus.Core.UsbDescriptors]::ReadById($Target)
    Say "   descriptors readable from the hub: $($null -ne $described)"
    $scanned = [Lotus.Core.DeviceScanner]::ScanOne($Target)
    if ($null -ne $scanned) { Say "   LOTUS sees: started=$($scanned.Started) problem=$($scanned.Problem) off=$($scanned.IsOff) kinds=$($scanned.Kinds) described=$($scanned.Described) functions=$($scanned.Functions -join ' ')" }
    else { Say '   LOTUS does not see the device' }
    $setupLog = @(Get-Content 'C:\Windows\INF\setupapi.dev.log' -Tail 400 -ErrorAction SilentlyContinue | Where-Object { $_ -match 'olicy|estrict|denied|VID_5131' })
    foreach ($line in ($setupLog | Select-Object -Last 12)) { Say "   setupapi: $($line.Trim())" }

    Section '3. can LOTUS disable a device that was refused, and enable it again'
    $method = [Lotus.Core.BlockMethod]::Disabled
    $op = [Lotus.Core.DeviceControl]::Block($Target, [ref]$method)
    Say "   block: ok=$($op.Ok) method=$method $($op.Message)"
    Say "   state: $(Get-State $Target)"
    $op = [Lotus.Core.DeviceControl]::Unblock($Target)
    Say "   unblock: ok=$($op.Ok) $($op.Message)"
    Start-Sleep -Seconds 2
    Say "   state: $(Get-State $Target)"

    Section '4. allow the device by its instance ID'
    Set-List 'AllowInstanceIDs' @($Target)
    $t0 = Get-Date
    $worked = Try-Triggers $Target
    Say "   state: $(Get-State $Target)"
    Show-Events $t0 $Target
    Say "   trigger that worked: $(if ($worked) { $worked } else { 'none' })"

    if ($Composite) {
        Section '5. a device with several functions, never seen'
        $t0 = Get-Date
        Say "   forget exit=$(Forget $Composite)"
        Start-Sleep -Seconds 2
        Rescan
        Start-Sleep -Seconds 4
        Show-Family $Composite
        Show-Events $t0 $Composite

        Section '6. allow only the device itself'
        Set-List 'AllowInstanceIDs' @($Target, $Composite)
        $t0 = Get-Date
        $null = Try-Triggers $Composite
        Start-Sleep -Seconds 3
        Show-Family $Composite
        Show-Events $t0 $Composite

        $children = @(Get-Family $Composite | Where-Object { $_.InstanceId -ne $Composite } | ForEach-Object { $_.InstanceId })
        if ($children.Count -gt 0) {
            Section '7. allow its first function by hardware ID, at the same level as the deny list'
            $first = $children[0]
            $hardware = (($first -split '\\')[0..1]) -join '\'
            Say "   allowing $hardware"
            Set-List 'AllowDeviceIDs' @($hardware)
            $null = Try-Triggers $first
            Say "   $first : $(Get-State $first)"

            Section '8. allow every function by its instance ID'
            Set-List 'AllowInstanceIDs' (@($Target, $Composite) + $children)
            foreach ($child in $children) {
                if (Test-Started $child) { Say "   $child is running already"; continue }
                Say "   $child"
                $null = Try-Triggers $child
            }
            Start-Sleep -Seconds 3
            Show-Family $Composite
        }
        else { Say '   the device has no function children to look at' }
    }

    Section '9. the policy is taken away again'
    Remove-Item $R -Recurse -Force
    Say "   policy key present: $(Test-Path $R)"
    Start-Sleep -Seconds 3
    Say "   target without any nudge: $(Get-State $Target)"
}
catch {
    Say "EXCEPTION: $($_.Exception.Message)"
    Say $_.ScriptStackTrace
}
finally {
    Section 'cleanup'
    if (-not $existed -and (Test-Path $R)) { Remove-Item $R -Recurse -Force }
    Say "   policy key present: $(Test-Path $R)"
    Rescan
    foreach ($id in @($Target, $Composite) | Where-Object { $_ }) {
        foreach ($d in (Get-Family $id)) { Enable-PnpDevice -InstanceId $d.InstanceId -Confirm:$false -ErrorAction SilentlyContinue }
    }
    Start-Sleep -Seconds 3
    $notBack = @($others | Where-Object { (Get-PnpDevice -InstanceId $_ -ErrorAction SilentlyContinue).Status -ne 'OK' })
    if ($notBack.Count -gt 0) {
        Say "   not running yet: $($notBack -join ', '). Forgetting them and scanning again."
        foreach ($id in @($Target, $Composite) | Where-Object { $_ }) { $null = Forget $id }
        Start-Sleep -Seconds 1
        Rescan
        Start-Sleep -Seconds 5
        $notBack = @($others | Where-Object { (Get-PnpDevice -InstanceId $_ -ErrorAction SilentlyContinue).Status -ne 'OK' })
    }
    Say "   target: $(Get-State $Target)"
    if ($Composite) { Say '   composite:'; Show-Family $Composite }
    Say ''
    Say "RESULT: devices that were running before and are not now: $($notBack.Count) $($notBack -join ', ')"
}
