<#
  PadLink - Windows side.
  Finds the Steam Controller shared by the PadLink TV app, attaches it with usbip-win2,
  and re-attaches automatically if the connection drops or the controller is replugged.

  Usage:
    .\padlink.ps1 -TvHost 192.168.1.50          # LAN
    .\padlink.ps1 -TvHost 100.101.102.103       # Tailscale
    .\padlink.ps1 -TvHost my-tv -Vendor 28de    # any vendor id (hex)
  Stop with Ctrl+C (the device is detached on exit).
#>
param(
    [Parameter(Mandatory = $true)][string]$TvHost,
    [string]$Vendor = "28de",          # Valve
    [int]$IntervalSeconds = 3
)

$ErrorActionPreference = "Stop"

# --- locate usbip.exe (usbip-win2) ---
$usbip = $null
$cmd = Get-Command usbip.exe -ErrorAction SilentlyContinue
if ($cmd) { $usbip = $cmd.Source }
if (-not $usbip) {
    $usbip = @("$env:ProgramFiles\USBip\usbip.exe", "${env:ProgramFiles(x86)}\USBip\usbip.exe") |
        Where-Object { Test-Path $_ } | Select-Object -First 1
}
if (-not $usbip) {
    Write-Host "usbip.exe not found. Install usbip-win2: https://github.com/vadimgrn/usbip-win2/releases" -ForegroundColor Red
    exit 1
}

function Get-RemoteDevice {
    # Lines look like:  "  1-3: Valve Software : unknown product (28de:1102)"
    $out = & $usbip list -r $TvHost 2>&1 | Out-String
    foreach ($line in $out -split "`r?`n") {
        if ($line -match '^\s*(\d+-[\d.]+):\s*(.*)\(([0-9a-fA-F]{4}):([0-9a-fA-F]{4})\)') {
            if ($Matches[3].ToLower() -eq $Vendor.ToLower()) {
                return [pscustomobject]@{ BusId = $Matches[1]; Name = $Matches[2].Trim(); Id = "$($Matches[3]):$($Matches[4])" }
            }
        }
    }
    return $null
}

function Test-Attached {
    # "usbip port" lists imported devices with their remote url, e.g. usbip://host:3240/1-3
    $ports = & $usbip port 2>&1 | Out-String
    return $ports -match [regex]::Escape($TvHost)
}

function Detach-All {
    $ports = & $usbip port 2>&1 | Out-String
    $cur = $null
    foreach ($line in $ports -split "`r?`n") {
        if ($line -match '^\s*Port\s+0*(\d+)') { $cur = $Matches[1] }
        if ($cur -and $line -match [regex]::Escape($TvHost)) { & $usbip detach -p $cur | Out-Null; $cur = $null }
    }
}

Write-Host "PadLink: watching $TvHost for vendor $Vendor (Ctrl+C to stop)" -ForegroundColor Cyan
$lastState = ""
try {
    while ($true) {
        if (Test-Attached) {
            $state = "attached"
        } else {
            $dev = $null
            try { $dev = Get-RemoteDevice } catch { }
            if ($dev) {
                Write-Host "$(Get-Date -f HH:mm:ss)  attaching $($dev.Name) [$($dev.Id)] bus $($dev.BusId)..."
                & $usbip attach -r $TvHost -b $dev.BusId
                $state = if ($LASTEXITCODE -eq 0) { "attached" } else { "attach failed" }
            } else {
                $state = "waiting (TV unreachable or controller not plugged in)"
            }
        }
        if ($state -ne $lastState) {
            $color = if ($state -eq "attached") { "Green" } else { "Yellow" }
            Write-Host "$(Get-Date -f HH:mm:ss)  $state" -ForegroundColor $color
            $lastState = $state
        }
        Start-Sleep -Seconds $IntervalSeconds
    }
}
finally {
    Write-Host "Detaching..."
    Detach-All
}
