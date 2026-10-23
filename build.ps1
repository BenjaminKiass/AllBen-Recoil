# Build and Publish AllBen-Recoil as Self-Contained Win-x64 Application
$ErrorActionPreference = 'Stop'
$here = $PSScriptRoot
$proj = Join-Path $here 'AllBen-Recoil.csproj'
$out  = Join-Path $here 'out'

Get-Process AllBen-Recoil -ErrorAction SilentlyContinue | Stop-Process -Force
Write-Host "Publishing AllBen-Recoil (Release x64)..."
dotnet publish $proj -c Release -o $out
Copy-Item (Join-Path $out '*') $here -Recurse -Force
Remove-Item $out -Recurse -Force -ErrorAction SilentlyContinue

Write-Host "Build Complete: $(Join-Path $here 'AllBen-Recoil.exe')"
