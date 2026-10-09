[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [ValidateSet('stable', 'development')]
    [string]$Channel,

    [string]$Archive,
    [string]$DataRoot,
    [string]$ToolPath = (Join-Path $PSScriptRoot 'Kiten.DataTool.exe'),
    [switch]$Apply,
    [switch]$RollbackInterrupted
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

if ($RollbackInterrupted) {
    & $ToolPath recover --channel $Channel --data-root $DataRoot --plan
    if ($LASTEXITCODE -ne 0) {
        throw "中断復旧計画の確認に失敗しました。終了コード: $LASTEXITCODE"
    }

    $answer = Read-Host '中断した復元を前の状態へ戻す場合は ROLLBACK と入力してください'
    if ($answer -cne 'ROLLBACK') {
        Write-Host 'キャンセルしました。データは変更していません。'
        return
    }

    & $ToolPath recover --channel $Channel --data-root $DataRoot --confirm ROLLBACK
    if ($LASTEXITCODE -ne 0) {
        throw "中断復旧に失敗しました。終了コード: $LASTEXITCODE"
    }
    return
}

if ([string]::IsNullOrWhiteSpace($Archive)) {
    throw '復元するバックアップZIPを -Archive で指定してください。'
}

$planOutput = @(& $ToolPath restore --channel $Channel --data-root $DataRoot --archive $Archive --plan 2>&1)
$planExitCode = $LASTEXITCODE
foreach ($line in $planOutput) {
    Write-Host $line
}
if ($planExitCode -ne 0) {
    throw "復元計画の確認に失敗しました。終了コード: $planExitCode"
}
if (-not $Apply) { return }

$hashMatches = @($planOutput | ForEach-Object {
    if ($_.ToString() -match '^確認済みZIP SHA-256: ([0-9A-Fa-f]{64})$') { $Matches[1] }
})
if ($hashMatches.Count -ne 1) {
    throw '復元計画から確認済みZIP SHA-256を取得できません。データは変更していません。'
}
$plannedArchiveSha256 = $hashMatches[0]

$answer = Read-Host '表示した時点へ戻す場合は RESTORE と入力してください。この時点より後の変更は戻りません'
if ($answer -cne 'RESTORE') {
    Write-Host 'キャンセルしました。現在データは変更していません。'
    return
}

& $ToolPath restore --channel $Channel --data-root $DataRoot --archive $Archive --apply --confirm RESTORE --expected-sha256 $plannedArchiveSha256
if ($LASTEXITCODE -ne 0) {
    throw "復元に失敗しました。終了コード: $LASTEXITCODE"
}
