<#
.SYNOPSIS
Removes the Windows "downloaded from the internet" mark from Wallpaper Studio.

.DESCRIPTION
Windows stores a Zone.Identifier alternate data stream on files downloaded from a
browser. While an unsigned executable keeps that mark, SmartScreen asks for
confirmation every time it starts. Wallpaper Studio removes the mark itself on
startup, but you can also run this script once after extracting a new build, for
example before the very first launch.

.PARAMETER Path
Folder that contains WallpaperStudio.exe. Defaults to the folder holding this
script, then the current folder.

.EXAMPLE
.\Unblock-WallpaperStudio.ps1 -Path "$env:USERPROFILE\Downloads\WallpaperStudio-win-x64"
#>
[CmdletBinding()]
param(
    [string]$Path
)

Add-Type -TypeDefinition @"
using System;
using System.Runtime.InteropServices;

public static class WallpaperStudioZone
{
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool DeleteFile(string fileName);

    public static bool Remove(string path)
    {
        return DeleteFile(path + ":Zone.Identifier");
    }
}
"@

function Resolve-TargetFolder {
    param([string]$Candidate)
    if ([string]::IsNullOrWhiteSpace($Candidate)) {
        return $null
    }

    $resolved = Resolve-Path -LiteralPath $Candidate -ErrorAction SilentlyContinue
    if ($null -eq $resolved) {
        return $null
    }

    return $resolved.Path
}

$folder = Resolve-TargetFolder $Path
if ($null -eq $folder) {
    $folder = Resolve-TargetFolder $PSScriptRoot
}
if ($null -eq $folder) {
    $folder = Resolve-TargetFolder (Get-Location).Path
}

if ($null -eq $folder) {
    Write-Error "Could not find the folder. Pass -Path with the folder that contains WallpaperStudio.exe."
    exit 1
}

$targets = @(Get-ChildItem -LiteralPath $folder -Filter *.exe -File -ErrorAction SilentlyContinue)
if ($targets.Count -eq 0) {
    Write-Error "No .exe files found in '$folder'. Pass -Path with the folder that contains WallpaperStudio.exe."
    exit 1
}

foreach ($file in $targets) {
    if ([WallpaperStudioZone]::Remove($file.FullName)) {
        Write-Host "Unblocked $($file.Name)"
    }
    else {
        Write-Host "$($file.Name) was already unblocked"
    }
}

Write-Host ""
Write-Host "Done. Windows will stop showing the SmartScreen download prompt for these files."
Write-Host "Wallpaper Studio also does this automatically every time it starts."
