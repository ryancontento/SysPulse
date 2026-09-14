<#
.SYNOPSIS
    Publishes SysPulse. If SysPulse is running (including hidden in the tray), it's asked to exit cleanly
    first so its files in the publish folder aren't locked.

.PARAMETER SelfContained
    Bundle the .NET runtime, for PCs without the .NET 10 Desktop Runtime.

.PARAMETER Destination
    Optional folder to copy the published app into afterwards, e.g. C:\Tools\SysPulse.

.EXAMPLE
    .\tools\Publish.ps1
    .\tools\Publish.ps1 -Destination C:\Tools\SysPulse
#>
param(
    [switch]$SelfContained,
    [string]$Destination
)

$ErrorActionPreference = 'Stop'

$repo = Split-Path $PSScriptRoot -Parent
$project = Join-Path $repo 'src\SysPulse.App'
$output = Join-Path $project 'bin\Release\net10.0-windows10.0.19041.0\win-x64\publish'

Add-Type -Namespace SysPulseTools -Name User32 -MemberDefinition @'
[DllImport("user32.dll", CharSet = CharSet.Unicode)]
public static extern uint RegisterWindowMessage(string message);

[DllImport("user32.dll")]
public static extern bool PostMessage(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);
'@

$running = @(Get-Process SysPulse -ErrorAction SilentlyContinue)
if ($running.Count -gt 0) {
    Write-Host 'SysPulse is running; asking it to exit...'

    # SysPulse listens for this broadcast (see App.QuitMessage) and shuts down the same way as tray -> Exit.
    $quit = [SysPulseTools.User32]::RegisterWindowMessage('SysPulse.Quit')
    [SysPulseTools.User32]::PostMessage([IntPtr]0xFFFF, $quit, [IntPtr]::Zero, [IntPtr]::Zero) | Out-Null

    try {
        $running | Wait-Process -Timeout 15 -ErrorAction Stop
    }
    catch {
        throw 'SysPulse is still running (older builds ignore the exit request). Right-click its tray icon, choose Exit, then run this again.'
    }
}

$selfContainedValue = if ($SelfContained) { 'true' } else { 'false' }

# Run from the repo so its nuget.config (nuget.org only) is used.
Push-Location $repo
try {
    dotnet publish $project -c Release -r win-x64 --self-contained $selfContainedValue
    if ($LASTEXITCODE -ne 0) {
        throw "dotnet publish failed with exit code $LASTEXITCODE."
    }
}
finally {
    Pop-Location
}

if ($Destination) {
    New-Item -ItemType Directory -Force -Path $Destination | Out-Null
    Copy-Item -Path (Join-Path $output '*') -Destination $Destination -Recurse -Force
    Write-Host "Copied to $Destination"
}

Write-Host "Published to $output"
