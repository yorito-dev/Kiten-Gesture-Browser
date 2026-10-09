#requires -Version 7.0
[CmdletBinding()]
param(
    [string]$DotnetPath = 'dotnet',
    [switch]$BrowserTests,
    [string]$NodePath = 'node',
    [string]$ChromePath = (Join-Path $env:ProgramFiles 'Google/Chrome/Application/chrome.exe')
)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$previousDirectory = Get-Location
try {
    Set-Location -LiteralPath $PSScriptRoot
    & $DotnetPath build Tests/TabBrowser.Tests/TabBrowser.Tests.csproj -c Release -p:KitenPublicBuild=true
    if ($LASTEXITCODE -ne 0) { throw 'アプリとテストのビルドに失敗しました。' }
    & $DotnetPath build tools/Kiten.DataTool/Kiten.DataTool.csproj -c Release
    if ($LASTEXITCODE -ne 0) { throw 'DataToolのビルドに失敗しました。' }
    & $DotnetPath test Tests/TabBrowser.Tests/TabBrowser.Tests.csproj -c Release -p:KitenPublicBuild=true --no-build --no-restore
    if ($LASTEXITCODE -ne 0) { throw '公開構成向け.NETテストに失敗しました。' }
    if ($BrowserTests) {
        & (Join-Path $PSScriptRoot 'tools/Test-BrowserRegression.ps1') -SourceDirectory $PSScriptRoot -NodePath $NodePath -ChromePath $ChromePath
    }
    Write-Host 'ビルドと指定テストが成功しました。実GUI・実動画再生は別途確認が必要です。'
} finally {
    Set-Location -LiteralPath $previousDirectory
}
