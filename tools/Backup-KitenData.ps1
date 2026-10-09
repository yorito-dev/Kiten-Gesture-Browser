[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [ValidateSet('stable', 'development')]
    [string]$Channel,

    [Parameter(Mandatory = $true)]
    [string]$Destination,

    [string]$DataRoot,
    [string]$ReleaseInformationPath,
    [string]$ToolPath = (Join-Path $PSScriptRoot 'Kiten.DataTool.exe'),
    [switch]$IncludeBrowsingHistory,
    [switch]$IncludeRecentItems,
    [switch]$IncludeDownloadHistory
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

if ([string]::IsNullOrWhiteSpace($DataRoot)) {
    $directoryName = if ($Channel -eq 'development') { 'TabBrowser-Dev' } else { 'TabBrowser' }
    $DataRoot = Join-Path ([Environment]::GetFolderPath('LocalApplicationData')) $directoryName
}

if (-not (Test-Path -LiteralPath $ToolPath -PathType Leaf)) {
    throw "Kiten.DataTool.exe が見つかりません: $ToolPath"
}

if ([string]::IsNullOrWhiteSpace($ReleaseInformationPath)) {
    $candidate = Join-Path (Split-Path -Parent $PSScriptRoot) 'release-info.json'
    if (Test-Path -LiteralPath $candidate -PathType Leaf) {
        $ReleaseInformationPath = $candidate
    }
}

$arguments = @(
    'backup', '--channel', $Channel,
    '--data-root', $DataRoot,
    '--destination', $Destination,
    '--plan'
)
if ($IncludeBrowsingHistory) { $arguments += '--include-browsing-history' }
if ($IncludeRecentItems) { $arguments += '--include-recent-items' }
if ($IncludeDownloadHistory) { $arguments += '--include-download-history' }
if (-not [string]::IsNullOrWhiteSpace($ReleaseInformationPath)) {
    $arguments += @('--release-info', $ReleaseInformationPath)
}

& $ToolPath @arguments
if ($LASTEXITCODE -ne 0) {
    throw "バックアップ計画の確認に失敗しました。終了コード: $LASTEXITCODE"
}

$answer = Read-Host '表示した対象をバックアップする場合は BACKUP と入力してください'
if ($answer -cne 'BACKUP') {
    Write-Host 'キャンセルしました。データと既存バックアップは変更していません。'
    return
}

$applyArguments = $arguments | Where-Object { $_ -ne '--plan' }
$applyArguments += @('--confirm', 'BACKUP')
& $ToolPath @applyArguments
if ($LASTEXITCODE -ne 0) {
    throw "バックアップに失敗しました。終了コード: $LASTEXITCODE"
}
