#Requires -Version 5.1
<#
.SYNOPSIS
    pxd Uninstaller (CLI) - remove programs and Store apps, and what they leave behind, right in the console.

.DESCRIPTION
    The console edition of pxd Uninstaller. Nothing is installed and nothing is saved: Windows is only changed when
    you uninstall something or remove leftovers you picked, and each of those asks first. Run it straight from the web:

        irm pxdtek.com/u | iex

    (pxdtek.com serves the latest copy of
        https://raw.githubusercontent.com/pxdtek/pxdUninstaller/main/cli/pxdUninstaller.ps1)

    It lists every program and Store app the way Apps & features does, runs a program's own uninstaller (normally,
    or quietly when the installer type is known to support it: MSI, Inno Setup, NSIS, Squirrel, WiX, Chromium and
    more), then finds the leftovers: folders, shortcuts, registry keys, startup entries, services and scheduled tasks.
    Files and folders go to the Recycle Bin; registry keys, services and tasks are removed for good, after a warning.

    Programs installed for all users need an elevated prompt for quiet uninstalls and most leftovers (A restarts
    it as administrator).

    With parameters through the web:
        & ([scriptblock]::Create((irm pxdtek.com/u))) -List -Filter zip

.PARAMETER List
    Non-interactive: print the installed programs (optionally filtered) and return.
.PARAMETER Filter
    Only programs whose name or publisher contains this text.
#>
[CmdletBinding()]
param(
    [switch]$List,
    [string]$Filter = ''
)

$script:PxuVersion = '1.0.0'
$script:PxuUrl = 'pxdtek.com/u'
# In-memory mode (set by the pxd Toolkit console while it runs this script). This edition keeps no settings, logs,
# backups or temp files either way; the mode is shown and carried over to an elevated window (A).
$script:PxuInMemory = $env:PXD_INMEMORY -eq '1'
$script:PxuEof = $false

# ======================================================================================
#  Helpers
# ======================================================================================

function Test-PxuAdmin {
    try { ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator) } catch { $false }
}

function Get-PxuSize([double]$bytes) {
    if ($bytes -lt 1024) { return ('{0:0} B' -f $bytes) }
    $units = 'KB', 'MB', 'GB', 'TB'; $i = -1
    while ($bytes -ge 1024 -and $i -lt $units.Count - 1) { $bytes /= 1024; $i++ }
    if ($bytes -ge 100) { return ('{0:0} {1}' -f $bytes, $units[$i]) }
    return ('{0:0.#} {1}' -f $bytes, $units[$i])
}

# Lower-case letters and digits only: "Mozilla Firefox (x64)" -> "mozillafirefoxx64".
function Get-PxuNorm([string]$s) { if (-not $s) { return '' }; ($s.ToLowerInvariant() -replace '[^\p{L}\p{Nd}]', '') }

# The program's name without version, bitness or language: "7-Zip 24.08 (x64)" -> "7-Zip".
function Get-PxuCleanName([string]$Name) {
    $s = $Name -replace '\s*[\(\[][^\)\]]*[\)\]]', ' '
    $s = $s -replace '(?i)\s+v?\d+(\.\d+)+[a-z]?(\s|$)|\s+\d{4}(\s|$)', ' '
    $s = $s -replace '(?i)\b(x64|x86|amd64|arm64|64-bit|32-bit|64 bit|32 bit|win64|win32)\b', ' '
    $s = $s -replace '(?i)\s-\s[a-z]{2}(-[a-z]{2})?$', ' '
    $s = ($s -replace '\s+', ' ').Trim(' ', '-', [char]0x2013, ',', '.')
    if (-not $s) { return $Name.Trim() }
    $s
}

function Get-PxuCleanPublisher([string]$Publisher) {
    $s = $Publisher -replace '(?i)[,\.]?\s*\b(inc|incorporated|llc|ltd|limited|corp|corporation|co|company|gmbh|ag|s\.?a|b\.?v|pty|plc|srl|sas|kg)\b\.?', ' '
    ($s -replace '\s+', ' ').Trim(' ', ',', '.', '-')
}

function Get-PxuGuid([string]$s) {
    $m = [regex]::Match($s, '\{[0-9A-Fa-f]{8}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{12}\}')
    if ($m.Success) { return $m.Value.ToUpperInvariant() }
    $null
}

function Get-PxuQuote([string]$a) { if ($a.Contains(' ') -or -not $a) { return '"' + $a + '"' }; $a }

# Splits a command line into the program and its arguments: quoted paths, unquoted paths with spaces
# ("C:\Program Files\X\uninst.exe /S") and bare names (MsiExec.exe /X{...}).
function Test-PxuFile([string]$Path) { try { [IO.File]::Exists($Path) } catch { $false } }   # no error on quotes or other odd characters
function Test-PxuExists([string]$Path) { try { [IO.File]::Exists($Path) -or [IO.Directory]::Exists($Path) } catch { $false } }

function Split-PxuCommand([string]$Command) {
    $cmd = [Environment]::ExpandEnvironmentVariables($Command.Trim())
    if (-not $cmd) { return @('', '') }
    if ($cmd[0] -eq '"') {
        $end = $cmd.IndexOf('"', 1)
        if ($end -lt 0) { return @($cmd.Trim('"'), '') }
        return @($cmd.Substring(1, $end - 1), $cmd.Substring($end + 1).Trim())
    }
    if ($cmd -match '^[A-Za-z]:\\' -and (Test-PxuFile $cmd)) { return @($cmd, '') }   # an unquoted path with spaces and no arguments
    $first = $null
    foreach ($m in [regex]::Matches($cmd, '(?i)\.(exe|cmd|bat)(?=\s|$)')) {
        $exe = $cmd.Substring(0, $m.Index + $m.Length)
        if (-not $first) { $first = $exe }
        if ($exe.Contains('\') -and (Test-PxuFile $exe)) { return @($exe, $cmd.Substring($exe.Length).Trim()) }
    }
    if ($first) { return @($first, $cmd.Substring($first.Length).Trim()) }
    $sp = $cmd.IndexOf(' ')
    if ($sp -lt 0) { return @($cmd, '') }
    @($cmd.Substring(0, $sp), $cmd.Substring($sp + 1).Trim())
}

# "C:\x\app.exe,0" -> "C:\x\app.exe"
function Get-PxuIconPath([string]$Icon) {
    $s = [Environment]::ExpandEnvironmentVariables($Icon.Trim().Trim('"'))
    $comma = $s.LastIndexOf(',')
    if ($comma -gt 2 -and $s.Substring($comma + 1).Trim() -match '^-?\d+$') { return $s.Substring(0, $comma).Trim().Trim('"') }
    $s
}

# The first path in a registry command value ("\"C:\x\a.exe\" --b", "rundll32 C:\x\a.dll,Run").
function Get-PxuPathIn([string]$Value) {
    if (-not $Value -or -not $Value.Trim()) { return $null }
    $v = [Environment]::ExpandEnvironmentVariables($Value.Trim())
    $colon = $v.IndexOf(':\')
    if ($colon -lt 1) { return $null }
    $start = $colon - 1
    if ($start -gt 0 -and $v[$start - 1] -eq '"') {
        $end = $v.IndexOf('"', $start)
        if ($end -lt 0) { return $v.Substring($start) }
        return $v.Substring($start, $end - $start)
    }
    $rest = $v.Substring($start)
    $m = [regex]::Match($rest, '(?i)\.(exe|dll|ocx|cpl|com|bat|cmd|ps1|lnk|sys|scr|msc|vbs|js|ax)(?=[\s,"]|$)')
    if ($m.Success) { return $rest.Substring(0, $m.Index + $m.Length) }
    $cut = $rest.Length
    foreach ($stop in ' /', ' -', ',', '"') { $i = $rest.IndexOf($stop); if ($i -gt 0 -and $i -lt $cut) { $cut = $i } }
    $rest.Substring(0, $cut).TrimEnd()
}

function Test-PxuWithin([string]$Path, [string]$Root) {
    if (-not $Path -or -not $Root) { return $false }
    $p = $Path.TrimEnd('\'); $r = $Root.TrimEnd('\')
    $p.Equals($r, [StringComparison]::OrdinalIgnoreCase) -or $p.StartsWith($r + '\', [StringComparison]::OrdinalIgnoreCase)
}

function Test-PxuSame([string]$a, [string]$b) { $a -and $b -and $a.TrimEnd('\').Equals($b.TrimEnd('\'), [StringComparison]::OrdinalIgnoreCase) }

$script:P = @{}
function Initialize-PxuPlaces {
    $gf = { param($n) try { [Environment]::GetFolderPath($n) } catch { '' } }
    $local = $env:LOCALAPPDATA
    $script:P = @{
        Windows = $env:windir
        ProgramFiles = $env:ProgramFiles
        ProgramFilesX86 = ${env:ProgramFiles(x86)}
        CommonFiles = $env:CommonProgramFiles
        CommonFilesX86 = ${env:CommonProgramFiles(x86)}
        ProgramData = $env:ProgramData
        AppData = $env:APPDATA
        LocalAppData = $local
        LocalLow = $(if ($local) { Join-Path (Split-Path $local -Parent) 'LocalLow' } else { '' })
        LocalPrograms = $(if ($local) { Join-Path $local 'Programs' } else { '' })
        Packages = $(if ($local) { Join-Path $local 'Packages' } else { '' })
        Documents = & $gf 'MyDocuments'
        PublicDocuments = & $gf 'CommonDocuments'
        Temp = [IO.Path]::GetTempPath().TrimEnd('\')
        StartMenu = & $gf 'StartMenu'
        CommonStartMenu = & $gf 'CommonStartMenu'
        Desktop = & $gf 'DesktopDirectory'
        PublicDesktop = & $gf 'CommonDesktopDirectory'
        UserProfile = $env:USERPROFILE
        QuickLaunch = $(if ($env:APPDATA) { Join-Path $env:APPDATA 'Microsoft\Internet Explorer\Quick Launch' } else { '' })
        TaskbarPins = $(if ($env:APPDATA) { Join-Path $env:APPDATA 'Microsoft\Internet Explorer\Quick Launch\User Pinned\TaskBar' } else { '' })
    }
}

# Folders that are never removed, whatever their name: drive roots, Windows, the roots programs live in, the
# profile's own top-level folders (Downloads, Pictures, OneDrive...) and anything that holds one of those.
function Test-PxuProtected([string]$Path) {
    try { $full = [IO.Path]::GetFullPath($Path).TrimEnd('\') } catch { return $true }
    if ($full.Length -le 3) { return $true }
    if (Test-PxuWithin $full $P.Windows) { return $true }
    $exact = @($P.ProgramFiles, $P.ProgramFilesX86, $P.CommonFiles, $P.CommonFilesX86, $P.ProgramData, $P.AppData, $P.LocalAppData, $P.LocalLow,
        $P.LocalPrograms, $P.Documents, $P.PublicDocuments, $P.Temp, $P.StartMenu, $P.CommonStartMenu, $P.Desktop, $P.PublicDesktop, $P.UserProfile,
        $P.Packages, $P.TaskbarPins, $P.QuickLaunch)
    if ($P.UserProfile) { $exact += @((Split-Path $P.UserProfile -Parent), (Join-Path $P.UserProfile 'AppData')) }
    if ($P.StartMenu) { $exact += (Join-Path $P.StartMenu 'Programs'), (Join-Path $P.StartMenu 'Programs\Startup') }
    if ($P.CommonStartMenu) { $exact += (Join-Path $P.CommonStartMenu 'Programs'), (Join-Path $P.CommonStartMenu 'Programs\Startup') }
    foreach ($r in 'ProgramData', 'AppData', 'LocalAppData') { if ($P[$r]) { $exact += (Join-Path $P[$r] 'Microsoft') } }
    foreach ($r in 'ProgramFiles', 'ProgramFilesX86') { if ($P[$r]) { $exact += (Join-Path $P[$r] 'Microsoft'), (Join-Path $P[$r] 'WindowsApps'), (Join-Path $P[$r] 'Windows Defender') } }
    if ($P.ProgramData) { $exact += (Join-Path $P.ProgramData 'Package Cache') }
    $exact = @($exact | Where-Object { $_ })
    foreach ($e in $exact) { if ($full.Equals($e.TrimEnd('\'), [StringComparison]::OrdinalIgnoreCase)) { return $true } }
    if ($P.UserProfile -and (Split-Path $full -Parent).Equals($P.UserProfile.TrimEnd('\'), [StringComparison]::OrdinalIgnoreCase)) { return $true }
    foreach ($e in $exact) { if ((Test-PxuWithin $e $full) -and -not $e.TrimEnd('\').Equals($full, [StringComparison]::OrdinalIgnoreCase)) { return $true } }
    $false
}

# ======================================================================================
#  Registry
# ======================================================================================

function Open-PxuBase([string]$Hive, [string]$View) {
    $h = if ($Hive -eq 'HKLM') { [Microsoft.Win32.RegistryHive]::LocalMachine } else { [Microsoft.Win32.RegistryHive]::CurrentUser }
    $v = switch ($View) { '32' { [Microsoft.Win32.RegistryView]::Registry32 } '64' { [Microsoft.Win32.RegistryView]::Registry64 } default { [Microsoft.Win32.RegistryView]::Default } }
    [Microsoft.Win32.RegistryKey]::OpenBaseKey($h, $v)
}

# A registry place: hive (HKLM/HKCU), view (64/32) and path; Text is how it's shown.
function New-PxuReg([string]$Hive, [string]$View, [string]$Path) {
    $label = $Hive + '\' + $Path
    if ($Hive -eq 'HKLM' -and $View -eq '32' -and [Environment]::Is64BitOperatingSystem -and $Path -match '^(?i)SOFTWARE\\') { $label = 'HKLM\SOFTWARE\WOW6432Node\' + $Path.Substring(9) }
    [pscustomobject]@{ Hive = $Hive; View = $View; Path = $Path; Text = $label }
}

function Get-PxuSubKeys($Reg) {
    $b = $null; $k = $null
    try {
        $b = Open-PxuBase $Reg.Hive $Reg.View
        $k = $b.OpenSubKey($Reg.Path)
        if ($k) { return @($k.GetSubKeyNames()) }
    } catch { } finally { if ($k) { $k.Close() }; if ($b) { $b.Close() } }
    @()
}

function Test-PxuRegExists($Reg) {
    $b = $null; $k = $null
    try { $b = Open-PxuBase $Reg.Hive $Reg.View; $k = $b.OpenSubKey($Reg.Path); return [bool]$k } catch { return $false } finally { if ($k) { $k.Close() }; if ($b) { $b.Close() } }
}

function Get-PxuValues($Reg) {
    $b = $null; $k = $null; $h = @{}
    try {
        $b = Open-PxuBase $Reg.Hive $Reg.View
        $k = $b.OpenSubKey($Reg.Path)
        if ($k) {
            foreach ($n in $k.GetValueNames()) {
                try { $v = $k.GetValue($n, $null, [Microsoft.Win32.RegistryValueOptions]::DoNotExpandEnvironmentNames) } catch { continue }
                if ($v -is [string]) { $h[$n] = $v } elseif ($v -is [int] -or $v -is [long]) { $h[$n] = [long]$v }
            }
        }
    } catch { } finally { if ($k) { $k.Close() }; if ($b) { $b.Close() } }
    $h
}

function Remove-PxuRegKey($Reg) {
    $b = Open-PxuBase $Reg.Hive $Reg.View
    try { $b.DeleteSubKeyTree($Reg.Path, $false) } finally { $b.Close() }
}

function Remove-PxuRegValue($Reg, [string]$Name) {
    $b = Open-PxuBase $Reg.Hive $Reg.View
    $k = $null
    try { $k = $b.OpenSubKey($Reg.Path, $true); if ($k) { $k.DeleteValue($Name, $false) } } finally { if ($k) { $k.Close() }; $b.Close() }
}

# ======================================================================================
#  Installed programs
# ======================================================================================

$script:UninstallPath = 'SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall'
# PXDU_TEST_ROOT (self-test only): read programs from HKCU\<that key> instead of the real Uninstall keys.
$script:Roots = @(@('HKLM', '64', $script:UninstallPath, $false), @('HKLM', '32', $script:UninstallPath, $false), @('HKCU', '64', $script:UninstallPath, $true))
if ($env:PXDU_TEST_ROOT) { $script:Roots = @(, @('HKCU', '64', $env:PXDU_TEST_ROOT, $true)) }

function Get-PxuStr($Values, [string]$Name) { $v = $Values[$Name]; if ($v -is [string] -and $v.Trim()) { return $v.Trim() }; $null }

function Read-PxuEntry($Reg, [bool]$JustForMe) {
    $v = Get-PxuValues $Reg
    $name = Get-PxuStr $v 'DisplayName'
    if (-not $name) { return $null }
    if ($v['SystemComponent'] -eq 1) { return $null }
    if (Get-PxuStr $v 'ParentKeyName') { return $null }   # an update of another entry
    $release = [string](Get-PxuStr $v 'ReleaseType')
    if ($release -match '(?i)Update|Hotfix|Service Pack') { return $null }
    $keyName = Split-Path $Reg.Path -Leaf
    if ($name -match '(?i)^(Security Update|Update|Hotfix) for |^Service Pack \d' -or $keyName -match '(?i)^KB\d{6,}') { return $null }
    $uninstall = [string](Get-PxuStr $v 'UninstallString')
    $quiet = [string](Get-PxuStr $v 'QuietUninstallString')
    $msi = $v['WindowsInstaller'] -eq 1
    $code = $null
    if ($keyName.Length -eq 38) { $code = Get-PxuGuid $keyName }
    if (-not $uninstall -and -not $quiet -and -not ($msi -and $code)) { return $null }
    $loc = Get-PxuStr $v 'InstallLocation'
    if (-not $loc) { $loc = Get-PxuStr $v 'Inno Setup: App Path' }
    $loc = if ($loc) { [Environment]::ExpandEnvironmentVariables($loc.Trim().Trim('"')).TrimEnd('\') } else { '' }
    $size = 0L; if ($v['EstimatedSize'] -is [long] -and $v['EstimatedSize'] -gt 0) { $size = $v['EstimatedSize'] * 1024L }
    $date = $null
    $ds = Get-PxuStr $v 'InstallDate'
    if ($ds) {
        foreach ($f in 'yyyyMMdd', 'yyyy-MM-dd', 'M/d/yyyy', 'MM/dd/yyyy', 'd.M.yyyy', 'dd.MM.yyyy', 'yyyy/MM/dd') {
            $d = [datetime]::MinValue
            if ([datetime]::TryParseExact($ds, $f, [Globalization.CultureInfo]::InvariantCulture, [Globalization.DateTimeStyles]::None, [ref]$d) -and $d.Year -gt 1990 -and $d -le (Get-Date).AddDays(2)) { $date = $d; break }
        }
    }
    [pscustomobject]@{
        Name = $name; Publisher = [string](Get-PxuStr $v 'Publisher'); Version = [string](Get-PxuStr $v 'DisplayVersion')
        Store = $false; JustForMe = $JustForMe; InstallLocation = $loc
        UninstallString = $uninstall; QuietUninstallString = $quiet; DisplayIcon = [string](Get-PxuStr $v 'DisplayIcon')
        ProductCode = $(if ($msi) { $code } else { $null }); WindowsInstaller = $msi
        InstallDate = $date; SizeBytes = $size; Key = $Reg; Values = $v
        PackageFullName = $null; PackageFamilyName = $null; Plan = $null
    }
}

function Get-PxuStoreApps {
    $list = @()
    if ($env:PXDU_TEST_ROOT) { return $list }
    try { $pkgs = @(Get-AppxPackage -ErrorAction Stop) } catch { return $list }
    foreach ($p in $pkgs) {
        try {
            if ($p.IsFramework -or $p.IsResourcePackage -or $p.NonRemovable -or [string]$p.SignatureKind -eq 'System') { continue }
            if (-not $p.InstallLocation -or (Test-PxuWithin $p.InstallLocation $P.Windows)) { continue }
            if ($p.Name -match '(?i)^(Microsoft\.(UI\.Xaml|VCLibs|NET\.|DirectX|Services\.Store|WindowsAppRuntime|WinAppRuntime|LanguageExperiencePack|AAD\.BrokerPlugin|AccountsControl|AsyncTextService|BioEnrollment|CredDialogHost|ECApp|LockApp|Win32WebViewHost|XboxGameCallableUI|SecHealthUI|WebpImageExtension|HEIFImageExtension|VP9VideoExtensions|WebMediaExtensions|RawImageExtension|AV1VideoExtension|HEVCVideoExtension|MPEG2VideoExtension|ApplicationCompatibilityEnhancements|OneDriveSync|StorePurchaseApp|DesktopAppInstaller|WindowsStore)\b)|^(windows\.|InputApp|NcsiUwpApp)') { continue }
            $display = $null; $publisher = ''
            $mf = Join-Path $p.InstallLocation 'AppxManifest.xml'
            if (Test-Path -LiteralPath $mf) {
                try {
                    $x = [xml](Get-Content -LiteralPath $mf -Raw -ErrorAction Stop)
                    $props = $x.Package.Properties
                    if ($props.DisplayName -and $props.DisplayName -notmatch '^ms-resource:') { $display = [string]$props.DisplayName }
                    if ($props.PublisherDisplayName -and $props.PublisherDisplayName -notmatch '^ms-resource:') { $publisher = [string]$props.PublisherDisplayName }
                    if (-not $x.Package.Applications) { continue }   # no app inside
                } catch { }
            }
            if (-not $display) {
                # "Microsoft.WindowsCalculator" -> "Windows Calculator"
                $display = ($p.Name.Split('.') | Select-Object -Last 1) -creplace '([a-z])([A-Z])', '$1 $2'
            }
            $list += [pscustomobject]@{
                Name = $display; Publisher = $publisher; Version = [string]$p.Version
                Store = $true; JustForMe = $true; InstallLocation = [string]$p.InstallLocation
                UninstallString = ''; QuietUninstallString = ''; DisplayIcon = ''
                ProductCode = $null; WindowsInstaller = $false
                InstallDate = $(try { (Get-Item -LiteralPath $p.InstallLocation -ErrorAction Stop).CreationTime } catch { $null }); SizeBytes = 0L; Key = $null; Values = @{}
                PackageFullName = [string]$p.PackageFullName; PackageFamilyName = [string]$p.PackageFamilyName; Plan = $null
            }
        } catch { }
    }
    $list
}

function Get-PxuApps {
    $list = New-Object System.Collections.Generic.List[object]
    $seen = @{}
    foreach ($r in $script:Roots) {
        $root = New-PxuReg $r[0] $r[1] $r[2]
        foreach ($name in Get-PxuSubKeys $root) {
            $a = Read-PxuEntry (New-PxuReg $r[0] $r[1] ($r[2] + '\' + $name)) $r[3]
            if (-not $a) { continue }
            # The same program written to both views (or both hives) shows once.
            $id = (Get-PxuNorm $a.Name) + '|' + $a.Version + '|' + (Get-PxuNorm $a.Publisher)
            if ($seen.ContainsKey($id)) { continue }
            $seen[$id] = $true
            $list.Add($a)
        }
    }
    foreach ($s in Get-PxuStoreApps) { $list.Add($s) }
    foreach ($a in $list) { $a.Plan = Get-PxuPlan $a }
    @($list | Sort-Object Name)
}

# ======================================================================================
#  How a program comes off
# ======================================================================================

function Resolve-PxuExe([string]$Exe) {
    if (-not $Exe) { return $null }
    try {
        if ([IO.Path]::IsPathRooted($Exe) -and $Exe -match '^[A-Za-z]:\\|^\\\\') { if (Test-PxuFile $Exe) { return $Exe }; return $null }
        $dirs = @([Environment]::SystemDirectory, $env:windir) + @(([string]$env:PATH).Split(';') | Where-Object { $_ }) + @((Join-Path $env:LOCALAPPDATA 'Microsoft\WindowsApps'))
        foreach ($d in $dirs) {
            try {
                $p = Join-Path $d.Trim() $Exe
                if (Test-PxuFile $p) { return $p }
                if (-not $Exe.Contains('.') -and (Test-PxuFile ($p + '.exe'))) { return $p + '.exe' }
            } catch { }
        }
    } catch { }
    $null
}

# NSIS uninstallers carry "Nullsoft" in their header and manifest.
function Test-PxuNsis([string]$Exe) {
    $f = $null
    try {
        $f = [IO.File]::Open($Exe, 'Open', 'Read', 'ReadWrite, Delete')
        $len = [int][Math]::Min($f.Length, 4MB)
        $buf = New-Object byte[] $len
        $read = 0
        while ($read -lt $len) { $n = $f.Read($buf, $read, $len - $read); if ($n -le 0) { break }; $read += $n }
        return [Text.Encoding]::ASCII.GetString($buf, 0, $read).Contains('Nullsoft')
    } catch { return $false } finally { if ($f) { $f.Dispose() } }
}

# Adds switches the command doesn't have yet.
function Add-PxuSwitch([string]$Command, [string]$Extra) {
    $have = @($Command.Split(@(' ', '"'), [StringSplitOptions]::RemoveEmptyEntries))
    $add = @($Extra.Split(' ') | Where-Object { $_ -and -not ($have -contains $_) })
    if (-not $add) { return $Command }
    $Command + ' ' + ($add -join ' ')
}

function New-PxuPlan([string]$Method, $Command, $Quiet, $Why, [bool]$Missing = $false, [bool]$Msi = $false, [bool]$Store = $false) {
    [pscustomobject]@{ Method = $Method; Command = $Command; QuietCommand = $Quiet; WhyNotQuiet = $Why; Missing = $Missing; IsMsi = $Msi; IsStore = $Store }
}

function Get-PxuPlan($a) {
    if ($a.Store) { return New-PxuPlan 'Store app' 'store' 'store' $null -Store $true }
    $code = $a.ProductCode
    if (-not $code -and $a.UninstallString -match '(?i)msiexec') { $code = Get-PxuGuid $a.UninstallString }
    if ($code -and ($a.WindowsInstaller -or $a.UninstallString -match '(?i)msiexec' -or -not $a.UninstallString)) {
        return New-PxuPlan 'Windows Installer (MSI)' "MsiExec.exe /X$code" "MsiExec.exe /X$code /qn /norestart" $null -Msi $true
    }
    $source = if ($a.UninstallString) { $a.UninstallString } else { $a.QuietUninstallString }
    if (-not $source) { return New-PxuPlan 'No uninstaller' $null $null 'This entry has no uninstaller. Its leftovers can still be cleaned up.' -Missing $true }
    $parts = Split-PxuCommand $source
    $exe = $parts[0]; $args0 = $parts[1]
    $full = Resolve-PxuExe $exe
    if (-not $full) { return New-PxuPlan 'Uninstaller missing' $null $null "Its uninstaller ($(Split-Path $exe -Leaf)) is no longer there. The leftovers can still be cleaned up." -Missing $true }
    $normal = (Get-PxuQuote $full) + $(if ($args0) { ' ' + $args0 } else { '' })
    $file = Split-Path $full -Leaf
    $quiet = $null; $why = $null
    $keyName = if ($a.Key) { Split-Path $a.Key.Path -Leaf } else { '' }
    if ($a.QuietUninstallString) { $method = 'Own uninstaller'; $quiet = $a.QuietUninstallString }
    elseif ($keyName -match '(?i)_is1$' -or $a.Values.ContainsKey('Inno Setup: App Path') -or $file -match '(?i)^unins\d{3}\.exe$') { $method = 'Inno Setup'; $quiet = Add-PxuSwitch $normal '/VERYSILENT /SUPPRESSMSGBOXES /NORESTART' }
    elseif ($file -eq 'Update.exe' -and $args0 -match '(?i)--uninstall') { $method = 'Squirrel'; $quiet = Add-PxuSwitch $normal '-s' }
    elseif ($a.Values.ContainsKey('BundleCachePath') -or $a.Values.ContainsKey('BundleUpgradeCode') -or $a.Values.ContainsKey('BundleProviderKey')) {
        $method = 'WiX bundle'; $base = if ($args0 -match '(?i)/uninstall') { $normal } else { Add-PxuSwitch $normal '/uninstall' }; $quiet = Add-PxuSwitch $base '/quiet /norestart'
    }
    elseif ($file -eq 'setup.exe' -and $args0 -match '(?i)--uninstall') { $method = 'Chromium setup'; $quiet = Add-PxuSwitch $normal '--force-uninstall' }
    elseif ($file -eq 'winget.exe' -and $args0 -match '(?i)uninstall') { $method = 'winget'; $quiet = Add-PxuSwitch $normal '--silent --disable-interactivity --accept-source-agreements' }
    elseif ($file -eq 'OfficeClickToRun.exe') { $method = 'Office Click-to-Run'; $quiet = Add-PxuSwitch $normal 'DisplayLevel=False' }
    elseif ($file -eq 'OneDriveSetup.exe' -and $args0 -match '(?i)/uninstall') { $method = 'OneDrive setup'; $quiet = $normal }
    elseif ($full -match '(?i)Microsoft Visual Studio\\Installer' -and $args0 -match '(?i)^uninstall') { $method = 'Visual Studio Installer'; $quiet = Add-PxuSwitch $normal '--quiet --norestart' }
    elseif ($file -eq 'sdbinst.exe') { $method = 'Compatibility fix'; $quiet = Add-PxuSwitch $normal '-q' }
    elseif ($args0 -match '(?i)steam://uninstall') { $method = 'Steam game'; $why = 'Steam asks before it removes a game; it has no quiet mode.' }
    elseif ($args0 -match '(?i)--uninstall-app-id') { $method = 'Browser web app'; $why = 'Web apps are removed by their browser, which asks first.' }
    elseif ($full -match '(?i)InstallShield Installation Information') { $method = 'InstallShield'; $why = 'InstallShield setups only run quietly with an answer file recorded on this PC.' }
    elseif (Test-PxuNsis $full) { $method = 'NSIS'; $quiet = Add-PxuSwitch $normal '/S' }
    else { $method = 'Own uninstaller'; $why = "Its uninstaller ($file) has no known quiet mode." }
    New-PxuPlan $method $normal $quiet $why
}

function Test-PxuStillInstalled($a) {
    if ($a.Store) {
        try { return [bool](Get-AppxPackage -ErrorAction Stop | Where-Object { $_.PackageFullName -eq $a.PackageFullName }) } catch { return $false }
    }
    $a.Key -and (Test-PxuRegExists $a.Key)
}

# Uninstallers that copy themselves to Temp and carry on after the first process ends (NSIS's Au_.exe, Inno's
# _iu14D2N.tmp, InstallShield's _is*.exe...), or the msiexec service doing the work.
function Get-PxuHelpers {
    @(Get-Process -ErrorAction SilentlyContinue | Where-Object {
            $_.ProcessName -match '(?i)^(Au_|Un_A|_iu|_is|unins\d|uninst|uninstall)' -or
            ($_.Path -and $P.Temp -and (Test-PxuWithin $_.Path $P.Temp) -and $_.ProcessName -match '(?i)un|setup|_')
        })
}

# Runs the uninstall and waits for it, helpers included. Returns 'removed', 'still', 'cancelled' or an error text.
function Invoke-PxuUninstall($a, [bool]$Quiet) {
    if ($a.Store) {
        try { Remove-AppxPackage -Package $a.PackageFullName -ErrorAction Stop } catch { return $_.Exception.Message }
        if (Test-PxuStillInstalled $a) { return 'still' }
        return 'removed'
    }
    $cmd = if ($Quiet) { $a.Plan.QuietCommand } else { $a.Plan.Command }
    if (-not $cmd) { return 'There is no way to run it.' }
    $parts = Split-PxuCommand $cmd
    $exe = Resolve-PxuExe $parts[0]
    if (-not $exe) { $exe = $parts[0] }
    $sp = @{ FilePath = $exe; PassThru = $true; ErrorAction = 'Stop' }
    if ($parts[1]) { $sp.ArgumentList = $parts[1] }
    if ($Quiet) { $sp.WindowStyle = 'Hidden' }
    try { $proc = Start-Process @sp } catch {
        if ($_.Exception.Message -match '(?i)cancel') { return 'cancelled' }
        return $_.Exception.Message
    }
    Write-Host '  Uninstalling' -NoNewline -ForegroundColor DarkCyan
    $start = Get-Date
    try { while (-not $proc.HasExited) { Start-Sleep -Milliseconds 500; if (((Get-Date) - $start).TotalSeconds % 5 -lt 0.5) { Write-Host '.' -NoNewline -ForegroundColor DarkCyan } } } catch { }
    $exit = try { $proc.ExitCode } catch { $null }
    # Keep waiting while the entry is still there and a helper is working (up to 15 minutes).
    $calm = 0
    while ((Test-PxuStillInstalled $a) -and ((Get-Date) - $start).TotalMinutes -lt 15) {
        if (@(Get-PxuHelpers).Count -gt 0) { $calm = 0 } else { $calm++ }
        if ($calm -ge 6) { break }   # three seconds with nothing running: it's done
        Start-Sleep -Milliseconds 500
    }
    Write-Host ''
    if ($a.Plan.IsMsi -and $exit -eq 1602) { return 'cancelled' }
    if ($a.Plan.IsMsi -and $exit -eq 1603 -and -not (Test-PxuAdmin) -and -not $a.JustForMe) { return 'Windows Installer needs administrator rights for this one (A restarts as administrator).' }
    if (Test-PxuStillInstalled $a) {
        if ($exit -and $exit -ne 0 -and $Quiet) { return "the uninstaller ended with code $exit." }
        return 'still'
    }
    'removed'
}

# ======================================================================================
#  Leftovers (same rules as the app's deep uninstall)
# ======================================================================================

$script:Generic = @('microsoft', 'windows', 'update', 'updates', 'updater', 'common', 'commonfiles', 'system', 'driver', 'drivers', 'runtime', 'tools', 'tool',
    'program', 'programs', 'setup', 'installer', 'install', 'app', 'apps', 'application', 'software', 'data', 'temp', 'cache', 'user',
    'users', 'games', 'game', 'plugins', 'plugin', 'config', 'settings', 'logs', 'log', 'packages', 'package', 'shared', 'service',
    'services', 'client', 'server', 'helper', 'launcher', 'default', 'local', 'roaming', 'documents', 'desktop', 'files', 'browser',
    'player', 'office', 'studio', 'redistributable', 'framework', 'sdk', 'net', 'uninstall', 'uninstaller', 'support', 'assistant',
    'manager', 'inc', 'llc', 'corp', 'corporation', 'company', 'intel', 'amd', 'nvidia', 'google', 'adobe', 'apple')
$script:RegistryKeep = @('Microsoft', 'Classes', 'Policies', 'WOW6432Node', 'Clients', 'RegisteredApplications', 'ODBC', 'Windows', 'Intel',
    'Partner', 'DefaultUserEnvironment', 'Khronos', 'OEM', 'Setup', 'Description', 'Hardware', 'Mozilla Plugins')

# Match levels: 0 = Regular (what its own uninstaller removes), 1 = Strong (clearly its), 2 = Possible (similar name).
$script:LevelNames = @('regular', 'its own', 'maybe')

function New-PxuFinder($App, $All) {
    $f = @{ App = $App; Strong = @{}; Weak = @{}; Publisher = @{}; Others = @{}; OtherDirs = @(); SharedDirs = @(); OtherUsed = @{}; Dirs = @(); Derived = @{}; Found = [ordered]@{} }
    foreach ($o in $All) {
        if ([object]::ReferenceEquals($o, $App)) { continue }
        $n = Get-PxuNorm (Get-PxuCleanName $o.Name)
        if ($n.Length -ge 3) { $f.Others[$n] = $true }
        if ($o.InstallLocation.Length -gt 3 -and -not $o.Store) {
            $f.OtherDirs += $o.InstallLocation.TrimEnd('\')
            if (Test-PxuSame $o.InstallLocation $App.InstallLocation) { $f.SharedDirs += , @($o.InstallLocation, $o.Name) }
        }
        if (-not $o.Store) {
            foreach ($path in @((Split-PxuCommand $o.UninstallString)[0], (Get-PxuIconPath $o.DisplayIcon))) {
                if ($path -and $path.Length -gt 3 -and $path -match '^[A-Za-z]:\\') { try { $d = Split-Path $path -Parent; if ($d.Length -gt 3) { $f.OtherUsed[$d.TrimEnd('\')] = $true } } catch { } }
            }
        }
    }
    $addStrong = { param($s) $n = Get-PxuNorm $s; if ($n.Length -ge 3 -and -not ($script:Generic -contains $n)) { $f.Strong[$n] = $true } }
    $clean = Get-PxuCleanName $App.Name
    & $addStrong $App.Name; & $addStrong $clean
    $pub = Get-PxuCleanPublisher $App.Publisher
    foreach ($p in @($App.Publisher, $pub, ($pub.Split(' ') | Select-Object -First 1))) { $n = Get-PxuNorm $p; if ($n.Length -ge 3 -and -not ($script:Generic -contains $n)) { $f.Publisher[$n] = $true } }
    # "Mozilla Firefox" by Mozilla -> also "Firefox".
    $cn = Get-PxuNorm $clean
    foreach ($p in @($f.Publisher.Keys)) { if ($cn.StartsWith($p) -and $cn.Length - $p.Length -ge 4) { & $addStrong $cn.Substring($p.Length) } }
    if ($App.Store -and $App.PackageFullName) { $pn = $App.PackageFullName.Split('_')[0]; & $addStrong $pn; & $addStrong ($pn.Split('.') | Select-Object -Last 1) }

    # Install folders: the recorded one, else the folder its uninstaller or icon is in (when that's a program's own folder).
    if ($App.InstallLocation.Length -gt 3) { $f.Dirs += $App.InstallLocation.TrimEnd('\') }
    if (-not $App.Store) {
        foreach ($path in @((Split-PxuCommand $App.UninstallString)[0], (Get-PxuIconPath $App.DisplayIcon))) {
            if (-not $path -or $path -notmatch '^[A-Za-z]:\\') { continue }
            try { $dir = (Split-Path $path -Parent).TrimEnd('\') } catch { continue }
            if (-not $dir -or (Test-PxuProtected $dir) -or @($f.Dirs | Where-Object { Test-PxuWithin $dir $_ }).Count) { continue }
            if (-not (Test-PxuUnderProgramRoot $dir)) { continue }
            if (@($f.OtherDirs | Where-Object { (Test-PxuWithin $dir $_) -or (Test-PxuWithin $_ $dir) }).Count) { continue }
            if (@($f.OtherUsed.Keys | Where-Object { Test-PxuWithin $_ $dir }).Count) { continue }
            if ($dir -match '(?i)InstallShield Installation Information|\\Package Cache\\|\\Installer\\' -or (Test-PxuWithin $dir $P.CommonFiles) -or (Test-PxuWithin $dir $P.CommonFilesX86)) { continue }
            $f.Dirs += $dir; $f.Derived[$dir] = $true
        }
    }
    foreach ($d in $f.Dirs) { & $addStrong (Split-Path $d -Leaf) }
    foreach ($s in @($f.Strong.Keys)) { if ($s.Length -ge 5) { $f.Weak[$s] = $true } }
    $f
}

function Test-PxuUnderProgramRoot([string]$Dir) {
    foreach ($r in @($P.ProgramFiles, $P.ProgramFilesX86, $P.LocalPrograms, $P.AppData, $P.LocalAppData, $P.ProgramData)) {
        if ($r -and (Test-PxuWithin $Dir $r) -and -not (Test-PxuSame $Dir $r)) { return $true }
    }
    $false
}

# How a folder or key name relates to the program: $null when it doesn't, else 1 (Strong) or 2 (Possible).
function Get-PxuNameMatch($F, [string]$Name) {
    $n = Get-PxuNorm $Name
    if ($n.Length -lt 3 -or ($script:Generic -contains $n)) { return $null }
    if ($F.Strong.ContainsKey($n)) { return 1 }
    if ($F.Others.ContainsKey($n)) { return $null }   # another installed program's exact name
    foreach ($w in $F.Weak.Keys) {
        if (($n.StartsWith($w) -or ($n.Length -ge 5 -and $w.StartsWith($n))) -and [Math]::Abs($n.Length - $w.Length) -le [Math]::Max(6, [int]($w.Length / 2))) { return 2 }
    }
    $null
}

function Test-PxuInDirs($F, [string]$Path) { if (-not $Path) { return $false }; foreach ($d in $F.Dirs) { if (Test-PxuWithin $Path $d) { return $true } }; $false }

function Add-PxuLeftover($F, [string]$Kind, [string]$Group, [string]$Path, [int]$Level, [string]$Why, $Reg = $null, [string]$Value = '') {
    $id = "$Kind|$Path|$Value".ToLowerInvariant()
    if ($F.Found.Contains($id) -and $F.Found[$id].Level -le $Level) { return }
    $F.Found[$id] = [pscustomobject]@{ Kind = $Kind; Group = $Group; Path = $Path; Level = $Level; Why = $Why; Reg = $Reg; Value = $Value; Size = $null; Selected = $Level -eq 0 }
}

function Get-PxuDirs([string]$Root) { try { @([IO.Directory]::GetDirectories($Root)) } catch { @() } }
function Get-PxuFiles([string]$Root) { try { @([IO.Directory]::GetFiles($Root)) } catch { @() } }

function Test-PxuFolderAllowed($F, [string]$Path) {
    if (Test-PxuProtected $Path) { return $false }
    foreach ($d in $F.Dirs) { if ((Test-PxuWithin $Path $d) -and -not (Test-PxuSame $Path $d)) { return $false } }   # covered by the install folder
    foreach ($o in $F.OtherDirs) {
        if (@($F.Dirs | Where-Object { Test-PxuSame $_ $o }).Count) { continue }
        if (Test-PxuWithin $o $Path) { return $false }                                                           # holds another program
        if ((Test-PxuWithin $Path $o) -and -not @($F.Dirs | Where-Object { Test-PxuWithin $o $_ }).Count) { return $false }   # inside another program
    }
    $true
}

function Find-PxuInstallFolders($F) {
    foreach ($d in $F.Dirs) {
        if (-not (Test-Path -LiteralPath $d -PathType Container) -or (Test-PxuProtected $d)) { continue }
        if (@($F.OtherDirs | Where-Object { (Test-PxuWithin $_ $d) -and -not (Test-PxuSame $_ $d) }).Count) { continue }   # another program lives inside it
        $shared = @($F.SharedDirs | Where-Object { Test-PxuSame $_[0] $d }) | Select-Object -First 1
        $usedByOther = @($F.OtherUsed.Keys | Where-Object { Test-PxuWithin $_ $d }).Count -gt 0
        $root = [IO.Path]::GetPathRoot($d)
        $depth = @($d.TrimEnd('\').Substring($root.Length).Split('\') | Where-Object { $_ }).Count
        $usual = (Test-PxuUnderProgramRoot $d) -or ($depth -ge 2 -and -not (Test-PxuWithin $d $P.UserProfile))
        if ($shared) { Add-PxuLeftover $F 'Folder' 'Program files' $d 2 "Install folder, also used by $($shared[1])" }
        elseif ($usedByOther) { Add-PxuLeftover $F 'Folder' 'Program files' $d 2 "Install folder, but another program's uninstaller or icon is inside it" }
        elseif ($F.Derived.ContainsKey($d)) { Add-PxuLeftover $F 'Folder' 'Program files' $d 1 'Folder its uninstaller or icon is in' }
        elseif (-not $usual) { Add-PxuLeftover $F 'Folder' 'Program files' $d 1 'Install folder (in an unusual place: check it first)' }
        else { Add-PxuLeftover $F 'Folder' 'Program files' $d 0 'Install folder' }
    }
}

function Find-PxuFolders($F) {
    $roots = @(
        @($P.ProgramFiles, 'Program files', 'Program Files'), @($P.ProgramFilesX86, 'Program files', 'Program Files (x86)'),
        @($P.LocalPrograms, 'Program files', 'your Programs folder'), @($P.CommonFiles, 'Program files', 'Common Files'),
        @($P.CommonFilesX86, 'Program files', 'Common Files (x86)'), @($P.ProgramData, 'App data', 'ProgramData (all users)'),
        @($P.AppData, 'App data', 'AppData\Roaming'), @($P.LocalAppData, 'App data', 'AppData\Local'), @($P.LocalLow, 'App data', 'AppData\LocalLow'),
        @($P.Documents, 'App data', 'Documents'), @($P.PublicDocuments, 'App data', 'Public Documents'), @($P.Temp, 'App data', 'Temp'))
    foreach ($r in $roots) {
        $root = $r[0]; $group = $r[1]; $what = $r[2]
        if (-not $root -or -not (Test-Path -LiteralPath $root -PathType Container)) { continue }
        $docs = (Test-PxuSame $root $P.Documents) -or (Test-PxuSame $root $P.PublicDocuments)
        foreach ($dir in Get-PxuDirs $root) {
            $name = Split-Path $dir -Leaf
            $m = Get-PxuNameMatch $F $name
            if ($null -ne $m) {
                if (-not (Test-PxuFolderAllowed $F $dir)) { continue }
                # Documents: only exact names (people keep their own files there).
                if ($docs -and $m -ne 1) { continue }
                if ($docs) { Add-PxuLeftover $F 'Folder' $group $dir 2 "Folder in $what with its name (may hold your own files)" }
                else { Add-PxuLeftover $F 'Folder' $group $dir $m "Folder in $what with its name" }
            } elseif ($F.Publisher.ContainsKey((Get-PxuNorm $name))) {
                $inside = @(Get-PxuDirs $dir)
                $ours = @($inside | Where-Object { ($null -ne (Get-PxuNameMatch $F (Split-Path $_ -Leaf))) -and (Test-PxuFolderAllowed $F $_) })
                foreach ($i in $ours) { Add-PxuLeftover $F 'Folder' $group $i (Get-PxuNameMatch $F (Split-Path $i -Leaf)) "In the publisher's folder in $what" }
                # The publisher's folder holds nothing else: offer it too.
                if ($ours.Count -gt 0 -and $ours.Count -eq $inside.Count -and -not (Get-PxuFiles $dir).Count -and (Test-PxuFolderAllowed $F $dir)) {
                    Add-PxuLeftover $F 'Folder' $group $dir 2 "Publisher's folder in $what, holds only this program"
                }
            }
        }
    }
}

function Find-PxuShortcuts($F) {
    $shell = $null
    try { $shell = New-Object -ComObject WScript.Shell } catch { }
    $target = { param($lnk) if (-not $shell) { return '' }; try { $s = $shell.CreateShortcut($lnk); return @([string]$s.TargetPath, [string]$s.Arguments) } catch { return @('', '') } }
    $roots = @(@($P.StartMenu, $true, 'Start menu'), @($P.CommonStartMenu, $true, 'Start menu (all users)'), @($P.Desktop, $false, 'Desktop'),
        @($P.PublicDesktop, $false, 'Public desktop'), @($P.TaskbarPins, $false, 'Taskbar'), @($P.QuickLaunch, $false, 'Quick Launch'))
    foreach ($r in $roots) {
        $root = $r[0]; $deep = $r[1]; $what = $r[2]
        if (-not $root -or -not (Test-Path -LiteralPath $root -PathType Container)) { continue }
        $links = @(Get-ChildItem -LiteralPath $root -Filter *.lnk -File -Recurse:$deep -Depth 4 -ErrorAction SilentlyContinue | ForEach-Object FullName)
        foreach ($lnk in $links) {
            $t = & $target $lnk
            $tp = [string]$t[0]; $ta = [string]$t[1]
            $name = [IO.Path]::GetFileNameWithoutExtension($lnk)
            if ($tp -and (Test-PxuInDirs $F $tp)) {
                Add-PxuLeftover $F 'File' 'Shortcuts' $lnk $(if ($what -match '^Start|[Dd]esktop') { 0 } else { 1 }) "$what shortcut to the program"
            } elseif ($F.App.Store -and $F.App.PackageFamilyName -and ($ta -like "*$($F.App.PackageFamilyName)*" -or $tp -like "*$($F.App.PackageFamilyName)*")) {
                Add-PxuLeftover $F 'File' 'Shortcuts' $lnk 1 "$what shortcut to the app"
            } elseif ((Get-PxuNameMatch $F $name) -eq 1 -and (-not $tp -or -not (Test-PxuExists $tp))) {
                Add-PxuLeftover $F 'File' 'Shortcuts' $lnk 1 "$what shortcut with its name, pointing nowhere"
            }
        }
        if (-not $deep) { continue }
        # Start menu folders with the program's name.
        foreach ($dir in @(Get-PxuDirs $root) + @(Get-PxuDirs (Join-Path $root 'Programs'))) {
            $m = Get-PxuNameMatch $F (Split-Path $dir -Leaf)
            if ($null -eq $m -or (Test-PxuProtected $dir)) { continue }
            $allOurs = $true
            foreach ($file in @(Get-ChildItem -LiteralPath $dir -File -Recurse -ErrorAction SilentlyContinue | ForEach-Object FullName)) {
                if ($file -match '(?i)\.(url|ini)$') { continue }
                if ($file -notmatch '(?i)\.lnk$') { $allOurs = $false; break }
                $tp = [string](& $target $file)[0]
                if (-not ((Test-PxuInDirs $F $tp) -or ($tp -and -not (Test-PxuExists $tp)))) { $allOurs = $false; break }
            }
            $lvl = if ($allOurs -and $m -eq 1) { if ($F.Dirs.Count) { 0 } else { 1 } } else { 2 }
            Add-PxuLeftover $F 'Folder' 'Shortcuts' $dir $lvl "$what folder"
        }
    }
}

# Windows Installer stores product codes "packed": each GUID part reversed.
function Get-PxuPacked([string]$Guid) {
    $g = $Guid.Trim('{', '}').Replace('-', '')
    if ($g.Length -ne 32) { return $null }
    $rev = { param($s) $c = $s.ToCharArray(); [array]::Reverse($c); -join $c }
    $b = (& $rev $g.Substring(0, 8)) + (& $rev $g.Substring(8, 4)) + (& $rev $g.Substring(12, 4))
    for ($i = 16; $i -lt 32; $i += 2) { $b += $g[$i + 1]; $b += $g[$i] }
    $b.ToUpperInvariant()
}

function Find-PxuRegistry($F) {
    $a = $F.App
    if ($a.Key -and (Test-PxuRegExists $a.Key)) { Add-PxuLeftover $F 'Key' 'Registry' $a.Key.Text 0 'Its Apps & features entry' $a.Key }
    if ($a.ProductCode) {
        $packed = Get-PxuPacked $a.ProductCode
        if ($packed) {
            foreach ($pl in @(@((New-PxuReg 'HKLM' '64' "SOFTWARE\Classes\Installer\Products\$packed"), 'Windows Installer product record'),
                    @((New-PxuReg 'HKLM' '64' "SOFTWARE\Microsoft\Windows\CurrentVersion\Installer\UserData\S-1-5-18\Products\$packed"), 'Windows Installer product record'),
                    @((New-PxuReg 'HKCU' '64' "Software\Microsoft\Installer\Products\$packed"), 'Windows Installer product record (this user)'))) {
                if (Test-PxuRegExists $pl[0]) { Add-PxuLeftover $F 'Key' 'Registry' $pl[0].Text 0 $pl[1] $pl[0] }
            }
        }
    }
    $softRoot = @(@('HKCU', '64', 'Software'), @('HKLM', '64', 'SOFTWARE'), @('HKLM', '32', 'SOFTWARE'))
    if ($env:PXDU_TEST_SOFTWARE) { $softRoot = @(, @('HKCU', '64', $env:PXDU_TEST_SOFTWARE)) }
    foreach ($sv in $softRoot) {
        $soft = New-PxuReg $sv[0] $sv[1] $sv[2]
        foreach ($name in Get-PxuSubKeys $soft) {
            if ($script:RegistryKeep -contains $name) { continue }
            $key = New-PxuReg $sv[0] $sv[1] ($sv[2] + '\' + $name)
            $m = Get-PxuNameMatch $F $name
            if ($null -ne $m) { Add-PxuLeftover $F 'Key' 'Registry' $key.Text $m 'Settings key with its name' $key }
            elseif ($F.Publisher.ContainsKey((Get-PxuNorm $name))) {
                $inside = @(Get-PxuSubKeys $key)
                $ours = @($inside | Where-Object { $null -ne (Get-PxuNameMatch $F $_) })
                foreach ($i in $ours) { $k2 = New-PxuReg $sv[0] $sv[1] ($key.Path + '\' + $i); Add-PxuLeftover $F 'Key' 'Registry' $k2.Text (Get-PxuNameMatch $F $i) "Settings key under the publisher's key" $k2 }
                if ($ours.Count -gt 0 -and $ours.Count -eq $inside.Count) { Add-PxuLeftover $F 'Key' 'Registry' $key.Text 2 "Publisher's key, holds only this program" $key }
            }
        }
    }
}

function Find-PxuStartup($F) {
    if ($env:PXDU_TEST_ROOT) { return }
    foreach ($sv in @(@('HKCU', '64'), @('HKLM', '64'), @('HKLM', '32'))) {
        foreach ($sub in 'Run', 'RunOnce') {
            $key = New-PxuReg $sv[0] $sv[1] "SOFTWARE\Microsoft\Windows\CurrentVersion\$sub"
            $vals = Get-PxuValues $key
            foreach ($n in $vals.Keys) {
                $path = Get-PxuPathIn ([string]$vals[$n])
                if ($path -and (Test-PxuInDirs $F $path)) { Add-PxuLeftover $F 'Value' 'Startup' "$($key.Text) : $n" 0 'Starts with Windows, from its folder' $key $n }
                elseif ((Get-PxuNameMatch $F $n) -eq 1 -and $path -and -not (Test-PxuExists $path)) { Add-PxuLeftover $F 'Value' 'Startup' "$($key.Text) : $n" 1 'Startup entry with its name, pointing nowhere' $key $n }
            }
        }
    }
}

function Find-PxuServices($F) {
    if ($env:PXDU_TEST_ROOT -or -not $F.Dirs.Count) { return }
    try { $svcs = @(Get-CimInstance Win32_Service -Property Name, DisplayName, PathName -ErrorAction Stop) } catch { return }
    foreach ($s in $svcs) {
        $path = Get-PxuPathIn ([string]$s.PathName)
        if ($path -and (Test-PxuInDirs $F $path)) { Add-PxuLeftover $F 'Service' 'Services and tasks' $s.Name 1 "Service ($($s.DisplayName)) running from its folder" }
    }
}

function Find-PxuTasks($F) {
    if ($env:PXDU_TEST_ROOT) { return }
    try { $tasks = @(Get-ScheduledTask -ErrorAction Stop) } catch { return }
    foreach ($t in $tasks) {
        if ($t.TaskPath -like '\Microsoft\*') { continue }
        $hit = $false
        foreach ($act in @($t.Actions)) { $path = Get-PxuPathIn ([string]$act.Execute); if ($path -and (Test-PxuInDirs $F $path)) { $hit = $true } }
        $full = $t.TaskPath + $t.TaskName
        if ($hit) { Add-PxuLeftover $F 'Task' 'Services and tasks' $full 1 'Scheduled task that runs something in its folder' }
        else {
            # "Firefox Default Browser Agent 308046B0...": a task named after the program counts as similar.
            foreach ($w in @($F.Strong.Keys)) { if ($w.Length -ge 5 -and (Get-PxuNorm $t.TaskName).StartsWith($w)) { Add-PxuLeftover $F 'Task' 'Services and tasks' $full 2 'Scheduled task with its name'; break } }
        }
    }
}

function Find-PxuStoreLeftovers($F) {
    $a = $F.App
    if ($a.PackageFamilyName -and $P.Packages) {
        $d = Join-Path $P.Packages $a.PackageFamilyName
        if (Test-Path -LiteralPath $d -PathType Container) { Add-PxuLeftover $F 'Folder' 'App data' $d 0 "The app's data folder" }
    }
}

function Get-PxuFolderSize([string]$Dir) {
    $total = 0L; $count = 0
    $stack = New-Object System.Collections.Generic.Stack[string]
    $stack.Push($Dir)
    while ($stack.Count -gt 0 -and $count -lt 50000) {
        $d = $stack.Pop()
        try { foreach ($f in [IO.Directory]::GetFiles($d)) { try { $total += (New-Object IO.FileInfo $f).Length; $count++ } catch { } } } catch { }
        try { foreach ($s in [IO.Directory]::GetDirectories($d)) { $stack.Push($s) } } catch { }
    }
    $total
}

function Find-PxuLeftovers($App, $All) {
    $f = New-PxuFinder $App $All
    $steps = if ($App.Store) { @('Find-PxuStoreLeftovers', 'Find-PxuShortcuts') } else { @('Find-PxuInstallFolders', 'Find-PxuFolders', 'Find-PxuShortcuts', 'Find-PxuRegistry', 'Find-PxuStartup', 'Find-PxuServices', 'Find-PxuTasks') }
    foreach ($s in $steps) { try { & $s $f } catch { } }   # one place failing never stops the rest
    $order = @('Program files', 'App data', 'Shortcuts', 'Registry', 'Startup', 'Services and tasks')
    # Files and folders inside a folder that's already listed come off with it: list the folder only.
    $folders = @($f.Found.Values | Where-Object Kind -eq 'Folder' | ForEach-Object Path)
    $keep = foreach ($l in $f.Found.Values) {
        $inside = $false
        if ($l.Kind -in 'Folder', 'File') { foreach ($d in $folders) { if ((Test-PxuWithin $l.Path $d) -and -not (Test-PxuSame $l.Path $d)) { $inside = $true; break } } }
        if (-not $inside) { $l }
    }
    $list = @($keep | Sort-Object @{ e = { [array]::IndexOf($order, $_.Group) } }, Level, Path)
    foreach ($l in $list) { if ($l.Kind -eq 'Folder') { $l.Size = Get-PxuFolderSize $l.Path } }
    $list
}

function Remove-PxuLeftover($l) {
    switch ($l.Kind) {
        'Folder' {
            if (-not (Test-Path -LiteralPath $l.Path)) { return }
            Add-Type -AssemblyName Microsoft.VisualBasic
            [Microsoft.VisualBasic.FileIO.FileSystem]::DeleteDirectory($l.Path, 'OnlyErrorDialogs', 'SendToRecycleBin')
        }
        'File' {
            if (-not (Test-Path -LiteralPath $l.Path)) { return }
            Add-Type -AssemblyName Microsoft.VisualBasic
            [Microsoft.VisualBasic.FileIO.FileSystem]::DeleteFile($l.Path, 'OnlyErrorDialogs', 'SendToRecycleBin')
        }
        'Key' { Remove-PxuRegKey $l.Reg }
        'Value' { Remove-PxuRegValue $l.Reg $l.Value }
        'Service' {
            try { Stop-Service -Name $l.Path -Force -ErrorAction SilentlyContinue } catch { }
            $out = & sc.exe delete $l.Path 2>&1
            if ($LASTEXITCODE -ne 0 -and $LASTEXITCODE -ne 1060) { throw ([string]($out | Select-Object -Last 1)) }
        }
        'Task' {
            $i = $l.Path.LastIndexOf('\')
            Unregister-ScheduledTask -TaskPath $l.Path.Substring(0, $i + 1) -TaskName $l.Path.Substring($i + 1) -Confirm:$false -ErrorAction Stop
        }
    }
    if ($l.Kind -in 'Folder', 'File' -and (Test-Path -LiteralPath $l.Path)) { throw 'it is still there (a file in it may be in use).' }
}

# ======================================================================================
#  Console
# ======================================================================================

function Read-PxuKey([string]$Prompt) {
    $k = Read-Host $Prompt
    if ($null -eq $k) { $script:PxuEof = $true; return 'q' }   # piped input ran out
    $k.Trim()
}

function Test-PxuYes([string]$Prompt) { (Read-PxuKey $Prompt) -match '^(?i)y(es)?$' }

function Write-PxuHeader([string]$Title) {
    Write-Host ''
    Write-Host "  $Title" -ForegroundColor Cyan
    Write-Host ('  ' + ('-' * [Math]::Min(76, $Title.Length + 2))) -ForegroundColor DarkGray
}

function Get-PxuWidth { $w = 100; try { $w = $Host.UI.RawUI.WindowSize.Width } catch { }; if ($w -lt 60) { $w = 60 }; $w }

function Get-PxuCut([string]$s, [int]$n) { if (-not $s) { return '' }; if ($s.Length -le $n) { return $s }; $s.Substring(0, [Math]::Max(1, $n - 1)) + '~' }

function Restart-PxuElevated {
    $self = $PSCommandPath
    $exe = (Get-Process -Id $PID).Path
    $set = if ($script:PxuInMemory) { "`$env:PXD_INMEMORY='1'; " } else { '' }   # the elevated window doesn't inherit this environment
    $arg = if ($self) { "-NoExit -ExecutionPolicy Bypass -Command `"$set& '$self'`"" }
    else { "-NoExit -ExecutionPolicy Bypass -Command `"$set& ([scriptblock]::Create((irm $($script:PxuUrl))))`"" }
    try { Start-Process -FilePath $exe -ArgumentList $arg -Verb RunAs -ErrorAction Stop; $true } catch { Write-Host '  Not elevated (the prompt was cancelled).' -ForegroundColor Yellow; $false }
}

function Get-PxuMatches($Apps, [string]$Text) {
    if (-not $Text) { return @($Apps) }
    @($Apps | Where-Object { $_.Name -like "*$Text*" -or $_.Publisher -like "*$Text*" })
}

function Show-PxuList($Rows, [int]$From, [int]$Count) {
    $w = Get-PxuWidth
    $nameW = [Math]::Max(20, [Math]::Min(46, $w - 48))
    $pubW = [Math]::Max(10, $w - $nameW - 34)
    for ($i = $From; $i -lt [Math]::Min($Rows.Count, $From + $Count); $i++) {
        $a = $Rows[$i]
        Write-Host ('  {0,4}  ' -f ($i + 1)) -NoNewline -ForegroundColor DarkGray
        Write-Host (Get-PxuCut $a.Name $nameW).PadRight($nameW) -NoNewline -ForegroundColor $(if ($a.Plan.Missing) { 'DarkYellow' } else { 'White' })
        Write-Host ('  ' + (Get-PxuCut $a.Version 14).PadRight(14)) -NoNewline -ForegroundColor DarkGray
        $tag = if ($a.Store) { 'Store' } elseif ($a.Plan.QuietCommand) { 'quiet' } else { '' }
        Write-Host ('  ' + $tag.PadRight(5)) -NoNewline -ForegroundColor DarkCyan
        Write-Host ('  ' + (Get-PxuCut $a.Publisher $pubW)) -ForegroundColor Gray
    }
}

function Show-PxuApp($a) {
    Write-PxuHeader $a.Name
    $row = { param($k, $v, $c = 'Gray') if ($v) { Write-Host ('  ' + $k.PadRight(14)) -NoNewline -ForegroundColor DarkGray; Write-Host $v -ForegroundColor $c } }
    & $row 'Publisher' $a.Publisher
    & $row 'Version' $a.Version
    & $row 'Installed' $(if ($a.InstallDate) { $a.InstallDate.ToString('yyyy-MM-dd') })
    & $row 'Size' $(if ($a.SizeBytes) { Get-PxuSize $a.SizeBytes })
    & $row 'For' $(if ($a.Store) { 'this user (Store app)' } elseif ($a.JustForMe) { 'this user' } else { 'all users' })
    & $row 'Folder' $a.InstallLocation
    & $row 'Uninstaller' $a.Plan.Method
    if ($a.Plan.QuietCommand) { & $row 'Quiet' 'yes: it can be removed with no windows or questions' 'Green' }
    elseif ($a.Plan.WhyNotQuiet) { & $row 'Quiet' $a.Plan.WhyNotQuiet 'DarkYellow' }
    if ($a.Key) { & $row 'Registry' $a.Key.Text 'DarkGray' }
}

function Show-PxuLeftovers($App, $All, [bool]$After) {
    Write-Host '  Looking for leftovers...' -ForegroundColor DarkCyan
    $items = @(Find-PxuLeftovers $App $All)
    if ($After) {
        # Its uninstaller just ran: whatever's left is a leftover, so ticked unless it's only a similar name.
        foreach ($l in $items) { $l.Selected = $l.Level -le 1 }
    }
    if (-not $items.Count) { Write-Host '  Nothing left behind.' -ForegroundColor Green; return }
    while (-not $script:PxuEof) {
        Write-PxuHeader "Leftovers of $($App.Name)"
        $w = Get-PxuWidth
        $group = ''
        for ($i = 0; $i -lt $items.Count; $i++) {
            $l = $items[$i]
            if ($l.Group -ne $group) { $group = $l.Group; Write-Host "  $group" -ForegroundColor Cyan }
            $box = if ($l.Selected) { '[x]' } else { '[ ]' }
            Write-Host ('  {0,3} {1} ' -f ($i + 1), $box) -NoNewline -ForegroundColor $(if ($l.Selected) { 'Green' } else { 'DarkGray' })
            $size = if ($null -ne $l.Size) { '  ' + (Get-PxuSize $l.Size) } else { '' }
            Write-Host (Get-PxuCut $l.Path ($w - 34 - $size.Length)) -NoNewline -ForegroundColor White
            Write-Host $size -NoNewline -ForegroundColor DarkGray
            Write-Host ('  ' + $script:LevelNames[$l.Level]) -ForegroundColor $(switch ($l.Level) { 0 { 'Green' } 1 { 'Cyan' } default { 'DarkYellow' } })
            Write-Host ('            ' + $l.Why) -ForegroundColor DarkGray
        }
        $sel = @($items | Where-Object Selected)
        Write-Host ''
        Write-Host '  regular = what its own uninstaller removes   its own = clearly the program''s   maybe = only a similar name: check it' -ForegroundColor DarkGray
        Write-Host "  Numbers (1 3 5-8) tick or untick   A all   N none   R remove the $($sel.Count) ticked   B back" -ForegroundColor DarkGray
        $k = Read-PxuKey '  Choose'
        switch -Regex ($k.ToLowerInvariant()) {
            '^(b|q|)$' { return }
            '^a$' { foreach ($l in $items) { $l.Selected = $true }; break }
            '^n$' { foreach ($l in $items) { $l.Selected = $false }; break }
            '^r$' {
                if (-not $sel.Count) { Write-Host '  Nothing is ticked.' -ForegroundColor Yellow; break }
                if (Test-PxuStillInstalled $App) { Write-Host '  It is still installed: run its uninstaller first (U) unless that is broken, or it may stop working half-removed.' -ForegroundColor Yellow }
                $files = @($sel | Where-Object { $_.Kind -in 'Folder', 'File' }).Count
                $other = $sel.Count - $files
                Write-Host ''
                if ($files) { Write-Host "  $files file or folder item(s) go to the Recycle Bin." -ForegroundColor Yellow }
                if ($other) { Write-Host "  $other registry key(s), startup entries, services or tasks are removed for good: there is no backup or undo." -ForegroundColor Yellow }
                if (@($sel | Where-Object Level -eq 2).Count) { Write-Host '  Some ticked items only have a similar name; make sure they really are this program''s.' -ForegroundColor Yellow }
                if (-not (Test-PxuYes '  Remove them? (y/N)')) { break }
                $failed = 0
                foreach ($l in $sel) {
                    try { Remove-PxuLeftover $l; Write-Host "  Removed  $($l.Path)" -ForegroundColor Green }
                    catch {
                        $failed++
                        $msg = $_.Exception.Message
                        if ($_.Exception -is [UnauthorizedAccessException] -or $msg -match '(?i)denied|access') { $msg = 'access denied (A restarts as administrator)' }
                        Write-Host "  Couldn't remove $($l.Path): $msg" -ForegroundColor Red
                    }
                }
                Write-Host $(if ($failed) { "  Done; $failed couldn't be removed." } else { '  All removed.' }) -ForegroundColor $(if ($failed) { 'Yellow' } else { 'Green' })
                return
            }
            default {
                foreach ($part in ($k -split '[\s,]+' | Where-Object { $_ })) {
                    if ($part -match '^(\d+)-(\d+)$') { $lo = [int]$Matches[1]; $hi = [int]$Matches[2] } elseif ($part -match '^\d+$') { $lo = [int]$part; $hi = $lo } else { continue }
                    for ($n = $lo; $n -le $hi; $n++) { if ($n -ge 1 -and $n -le $items.Count) { $items[$n - 1].Selected = -not $items[$n - 1].Selected } }
                }
            }
        }
    }
}

function Invoke-PxuAppMenu($a) {
    while (-not $script:PxuEof) {
        Show-PxuApp $a
        $admin = Test-PxuAdmin
        $still = Test-PxuStillInstalled $a
        Write-Host ''
        if (-not $still) { Write-Host '  It is not installed anymore.' -ForegroundColor Green }
        $opts = @()
        if ($still -and -not $a.Plan.Missing) { $opts += 'U uninstall' }
        if ($still -and $a.Plan.QuietCommand -and -not $a.Store) { $opts += 'S quiet uninstall' }
        $opts += 'L leftovers'
        if (-not $admin) { $opts += 'A restart as administrator' }
        $opts += 'B back'
        Write-Host ('  ' + ($opts -join '   ')) -ForegroundColor DarkGray
        $k = Read-PxuKey '  Choose'
        switch -Regex ($k.ToLowerInvariant()) {
            '^(b|q|)$' { return }
            '^l$' { Show-PxuLeftovers $a $script:Apps (-not $still); break }
            '^a$' { if (-not $admin -and (Restart-PxuElevated)) { Write-Host '  Opened an elevated window; this one can be closed.' -ForegroundColor Green }; break }
            '^[us]$' {
                if (-not $still -or ($k -eq 's' -and (-not $a.Plan.QuietCommand -or $a.Store)) -or ($k -eq 'u' -and $a.Plan.Missing)) { break }
                $quiet = $k -eq 's'
                if ($quiet -and -not $admin -and -not $a.JustForMe) { Write-Host '  It is installed for all users, so a quiet uninstall needs administrator rights (A).' -ForegroundColor Yellow; break }
                $how = if ($quiet) { 'quietly, with no windows' } else { 'with its own uninstaller (answer its windows)' }
                if (-not (Test-PxuYes "  Uninstall $($a.Name) $how? (y/N)")) { break }
                $r = Invoke-PxuUninstall $a $quiet
                switch ($r) {
                    'removed' { Write-Host "  $($a.Name) is uninstalled." -ForegroundColor Green }
                    'cancelled' { Write-Host '  Cancelled; nothing was removed.' -ForegroundColor Yellow }
                    'still' { Write-Host '  Its uninstaller ended but the program is still listed (it may have been cancelled, or need a restart).' -ForegroundColor Yellow }
                    default { Write-Host "  That didn't work: $r" -ForegroundColor Red }
                }
                if ($r -eq 'removed') {
                    $ans = Read-PxuKey '  Look for what it left behind? (Y/n)'
                    if ($ans -notmatch '^(?i)n(o)?$' -and -not $script:PxuEof) { Show-PxuLeftovers $a $script:Apps $true }
                }
                break
            }
        }
    }
}

# ---- PD header ---------------------------------------------------------------------------------------------------
# The pxd logo beside the tool's name, shown when a pxdtek CLI tool starts. The master copy is cli/PdHeader.ps1 in the
# pxdtek repo; each tool carries a copy (they run through irm | iex, so they can't load it). tools/sync-header.ps1
# copies it into them.
# Ten drawings of the logo, from tools/pdlogos.mjs: solid blocks, braille dots, shades and plain ASCII, tilted like the
# logo or upright, the pxd in white and the ring in cyan. They're stored as gzip + base64 so this stays ASCII and
# irm | iex never trips on encoding. Each time, one is picked at random from those that fit the window (braille only
# in Windows Terminal or VS Code, whose fonts have the dots); a window too narrow for any gets a one-line header.
# Nothing is kept between runs: under irm | iex the script scope is the caller's own session.

# A random logo no wider than $Room (@{ Style; Width; Text = lines; Color = lines }), or $null if none fits.
function Get-PdLogo([int]$Room = [int]::MaxValue) {
    $logos = @(
        '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'
    ) -join ''
    $all = @()
    try {
        $gz = New-Object IO.Compression.GZipStream((New-Object IO.MemoryStream(, [Convert]::FromBase64String($logos))), ([IO.Compression.CompressionMode]::Decompress))
        $body = (New-Object IO.StreamReader($gz, [Text.Encoding]::UTF8)).ReadToEnd()
        foreach ($chunk in ($body -split "`n~`n")) {
            $l = $chunk -split "`n"
            $head = $l[0] -split ' '
            $n = [int]$head[2]
            $all += @{ Style = $head[0]; Width = [int]$head[1]; Text = $l[1..$n]; Color = $l[($n + 1)..(2 * $n)] }
        }
    } catch { return $null }
    $dots = [bool]($env:WT_SESSION -or $env:TERM_PROGRAM -eq 'vscode')
    $fit = @($all | Where-Object { ($dots -or $_.Style -ne 'd') -and $_.Width -le $Room })
    if (-not $fit.Count) { return $null }
    $fit[(Get-Random -Maximum $fit.Count)]
}

# Line $Row of a logo in its colours (W the pxd, C the ring, D its faint edge), no newline; -Pad fills it to the
# logo's width. A row past the logo's end writes only the padding.
function Write-PdLogoLine($Logo, [int]$Row, [switch]$Pad) {
    $line = ''; $mask = ''
    if ($Row -ge 0 -and $Row -lt $Logo.Text.Count) { $line = [string]$Logo.Text[$Row]; $mask = [string]$Logo.Color[$Row] }
    $colors = @{ 'W' = 'White'; 'C' = 'Cyan'; 'D' = 'DarkCyan' }
    # one colour run at a time; spaces join whichever run they're in
    $run = ''; $col = 'Cyan'
    for ($k = 0; $k -lt $line.Length; $k++) {
        $c = $col
        if ($line[$k] -ne ' ' -and $k -lt $mask.Length -and $colors.ContainsKey([string]$mask[$k])) { $c = $colors[[string]$mask[$k]] }
        if ($c -ne $col -and $run) { Write-Host $run -NoNewline -ForegroundColor $col; $run = '' }
        $col = $c; $run += $line[$k]
    }
    if ($run) { Write-Host $run -NoNewline -ForegroundColor $col }
    if ($Pad -and $line.Length -lt $Logo.Width) { Write-Host (' ' * ($Logo.Width - $line.Length)) -NoNewline }
}

function Show-PdHeader {
    param([string]$Name, [string]$Version = '', [string[]]$About = @(), [string]$Command = '')
    $text = New-Object System.Collections.Generic.List[object]
    $text.Add(@($Name, 'White'))
    $text.Add(@(((@($Version, 'pxdtek.com') | Where-Object { $_ }) -join '  -  '), 'DarkGray'))
    if ($About) { $text.Add(@('', 'Gray')); foreach ($a in $About) { $text.Add(@($a, 'Gray')) } }
    if ($Command) { $text.Add(@('', 'Gray')); $text.Add(@($Command, 'DarkCyan')) }
    $textWidth = ($text | ForEach-Object { $_[0].Length } | Measure-Object -Maximum).Maximum

    $w = 0
    try { $w = $Host.UI.RawUI.WindowSize.Width } catch {}
    $logo = Get-PdLogo $(if ($w -gt 0) { $w - $textWidth - 7 } else { [int]::MaxValue })

    Write-Host ''
    if (-not $logo) {
        Write-Host '  (pd) ' -NoNewline -ForegroundColor Cyan
        Write-Host $Name -NoNewline -ForegroundColor White
        Write-Host $(if ($Version) { "  $Version" } else { '' }) -ForegroundColor DarkGray
        Write-Host ''
        return
    }
    $rows = $logo.Text.Count
    $top = [Math]::Max(0, [Math]::Floor(($rows - $text.Count) / 2))
    for ($i = 0; $i -lt [Math]::Max($rows, $top + $text.Count); $i++) {
        $j = $i - $top
        $say = $j -ge 0 -and $j -lt $text.Count -and $text[$j][0]
        Write-Host '  ' -NoNewline
        Write-PdLogoLine $logo $i -Pad:$say
        if ($say) { Write-Host ('    ' + $text[$j][0]) -NoNewline -ForegroundColor $text[$j][1] }
        Write-Host ''
    }
    Write-Host ''
}
# ---- end of PD header --------------------------------------------------------------------------------------------

function Start-PxuConsole {
    Show-PdHeader -Name 'pxd Uninstaller' -Version "CLI $($script:PxuVersion)" -Command 'irm pxdtek.com/u|iex'
    if ($script:PxuInMemory) { Write-Host '  In-memory: nothing is saved on this PC except what you choose to remove.' -ForegroundColor DarkGray }
    Write-Host '  Reading installed programs...' -ForegroundColor DarkCyan
    $script:Apps = @(Get-PxuApps)
    $filter = $Filter
    $from = 0
    while (-not $script:PxuEof) {
        $rows = @(Get-PxuMatches $script:Apps $filter)
        $page = 20
        try { $page = [Math]::Max(8, $Host.UI.RawUI.WindowSize.Height - 12) } catch { }
        if ($from -ge $rows.Count) { $from = 0 }
        $title = if ($filter) { "Programs matching `"$filter`" ($($rows.Count) of $($script:Apps.Count))" } else { "Installed programs ($($rows.Count))" }
        Write-PxuHeader $title
        if (-not $rows.Count) { Write-Host '  None.' -ForegroundColor DarkGray } else { Show-PxuList $rows $from $page }
        Write-Host ''
        $more = if ($rows.Count -gt $page) { "   Showing $($from + 1)-$([Math]::Min($rows.Count, $from + $page)): N next  P previous" } else { '' }
        Write-Host "  Number to open it   type text to search   * show all   F refresh$more" -ForegroundColor DarkGray
        Write-Host "  $(if (-not (Test-PxuAdmin)) { 'A restart as administrator   ' })Q quit" -ForegroundColor DarkGray
        $k = Read-PxuKey '  Choose'
        $n = 0
        if ([int]::TryParse($k, [ref]$n)) {
            if ($n -ge 1 -and $n -le $rows.Count) {
                $a = $rows[$n - 1]
                Invoke-PxuAppMenu $a
                if (-not (Test-PxuStillInstalled $a)) { $script:Apps = @($script:Apps | Where-Object { -not [object]::ReferenceEquals($_, $a) }) }
            } else { Write-Host "  There's no $n." -ForegroundColor Yellow }
            continue
        }
        switch -Regex ($k) {
            '^(?i)q$' { return }
            '^$' { return }
            '^(?i)n$' { if ($from + $page -lt $rows.Count) { $from += $page }; break }
            '^(?i)p$' { $from = [Math]::Max(0, $from - $page); break }
            '^\*$' { $filter = ''; $from = 0; break }
            '^(?i)f$' { Write-Host '  Reading installed programs...' -ForegroundColor DarkCyan; $script:Apps = @(Get-PxuApps); break }
            '^(?i)a$' { if (-not (Test-PxuAdmin) -and (Restart-PxuElevated)) { Write-Host '  Opened an elevated window; this one can be closed.' -ForegroundColor Green }; break }
            default { $filter = $k; $from = 0 }
        }
    }
}

# ======================================================================================
#  Entry
# ======================================================================================

if ($env:OS -ne 'Windows_NT') { Write-Host 'pxd Uninstaller removes Windows programs, so it runs on Windows only.' -ForegroundColor Yellow; return }
Initialize-PxuPlaces

if ($List) {
    $script:Apps = @(Get-PxuApps)
    $rows = @(Get-PxuMatches $script:Apps $Filter)
    Show-PxuList $rows 0 $rows.Count
    return
}

Start-PxuConsole
