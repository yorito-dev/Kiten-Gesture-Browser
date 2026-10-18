#requires -Version 7.0

[CmdletBinding()]
param(
    [string]$SourceDirectory = (Split-Path $PSScriptRoot -Parent),
    [string]$NodePath = 'node',
    [string]$ChromePath = (Join-Path $env:ProgramFiles 'Google/Chrome/Application/chrome.exe')
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$OutputEncoding = [Console]::OutputEncoding = [System.Text.UTF8Encoding]::new($false)

# 既存の必須回帰件数を下限とし、空の試験やskipだけの成功を拒否する。
$suites = @(
    @{ Name = 'GestureHost.browser.test.cjs'; MinimumTests = 6 },
    @{ Name = 'ChromeTestLauncher.browser.test.cjs'; MinimumTests = 9 },
    @{ Name = 'NativeBridge.browser.test.cjs'; MinimumTests = 9 },
    @{ Name = 'YouTubeMiniPlayerLayout.browser.test.cjs'; MinimumTests = 25 }
)
$sourceRoot = [System.IO.Path]::GetFullPath($SourceDirectory)
foreach ($suite in $suites) {
    $suite.Path = Join-Path $sourceRoot ("Tests/TabBrowser.Tests/" + $suite.Name)
    if (-not (Test-Path -LiteralPath $suite.Path -PathType Leaf)) {
        throw "browser回帰の対象ファイルがありません: $($suite.Name)"
    }
}
if (-not (Test-Path -LiteralPath $ChromePath -PathType Leaf)) {
    throw "browser回帰に必要なChromeがありません: $ChromePath"
}
$chromeExecutable = (Resolve-Path -LiteralPath $ChromePath).Path
$nodeExecutable = (Get-Command $NodePath -CommandType Application -ErrorAction Stop | Select-Object -First 1).Source
$nodeVersion = (& $nodeExecutable --version | Out-String).Trim()
if ($LASTEXITCODE -ne 0 -or $nodeVersion -notmatch '^v(\d+)\.' -or [int]$Matches[1] -lt 22) {
    throw "browser回帰にはNode.js 22以降が必要です: $nodeVersion"
}
$chromeVersion = [System.Diagnostics.FileVersionInfo]::GetVersionInfo($chromeExecutable).ProductVersion
Write-Host "Node: $nodeVersion ($nodeExecutable)"
Write-Host "Chrome: $chromeVersion ($chromeExecutable)"

function Get-TapCount {
    param([string]$Output, [string]$Name)
    $counts = [regex]::Matches($Output, "(?m)^# $Name ([0-9]+)\s*$")
    if ($counts.Count -ne 1) {
        throw "browser回帰の結果件数を確定できません: $Name"
    }
    return [int]$counts[0].Groups[1].Value
}

$previousDirectory = Get-Location
$previousChrome = $env:KITEN_BROWSER_TEST_CHROME
try {
    Set-Location -LiteralPath $sourceRoot
    $env:KITEN_BROWSER_TEST_CHROME = $chromeExecutable
    $total = 0
    foreach ($suite in $suites) {
        Write-Host "[browser回帰] $($suite.Name)"
        $lines = @(& $nodeExecutable --test --test-reporter=tap $suite.Path 2>&1)
        $exitCode = $LASTEXITCODE
        $lines | ForEach-Object { Write-Host $_ }
        if ($exitCode -ne 0) {
            throw "browser回帰に失敗しました: $($suite.Name)（終了コード $exitCode）"
        }
        $output = $lines -join "`n"
        $tests = Get-TapCount -Output $output -Name 'tests'
        $passed = Get-TapCount -Output $output -Name 'pass'
        if ($tests -lt $suite.MinimumTests -or $passed -ne $tests) {
            throw "browser回帰が全件実行されていません: $($suite.Name)（成功 $passed／実行 $tests／必須 $($suite.MinimumTests)）"
        }
        foreach ($name in @('fail', 'cancelled', 'skipped', 'todo')) {
            if ((Get-TapCount -Output $output -Name $name) -ne 0) {
                throw "browser回帰に未完了の試験があります: $($suite.Name) ($name)"
            }
        }
        $total += $tests
    }
    Write-Output "browser回帰: $total 件成功（失敗・取消・skip・todoなし）"
}
finally {
    $env:KITEN_BROWSER_TEST_CHROME = $previousChrome
    Set-Location -LiteralPath $previousDirectory
}
