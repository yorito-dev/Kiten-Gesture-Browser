@echo off
setlocal DisableDelayedExpansion
chcp 65001 >nul
title Kiten データのバックアップ

rem Maintenance内の同梱DataToolだけを使い、配布版の既定データを対象にする。
set "KitenBackupTool=%~dp0Kiten.DataTool.exe"
set "KitenBackupExit=1"
if not defined LOCALAPPDATA goto missing_environment
if not defined USERPROFILE goto missing_environment
set "KitenBackupDataRoot=%LOCALAPPDATA%\TabBrowser"
set "KitenBackupDestination=%USERPROFILE%\KitenBackup"
set "KitenBackupReleaseInfo="
if exist "%~dp0..\release-info.json" set "KitenBackupReleaseInfo=%~dp0..\release-info.json"

echo KitenのすべてのWindowとミニプレイヤーを通常終了してから実行してください。
echo 対象: 配布版（stable）の保存データ
echo 保存先: "%KitenBackupDestination%"
echo バックアップZIPは暗号化されません。
echo.
if not exist "%KitenBackupTool%" goto missing_tool

call :run_tool --plan
if errorlevel 1 goto failed
if not errorlevel 0 goto failed

echo.
set "KitenBackupConfirmation="
set /p "KitenBackupConfirmation=表示した対象をバックアップする場合は BACKUP と入力してください: "
rem 入力文字列をコマンドとして再解釈せず、大文字のBACKUPだけを許可する。
setlocal EnableDelayedExpansion
if not "!KitenBackupConfirmation!"=="BACKUP" goto cancelled_confirmation
endlocal

call :run_tool --confirm BACKUP
if errorlevel 1 goto failed
if not errorlevel 0 goto failed
echo.
echo バックアップと作成後の検証が完了しました。
echo 出力されたZIPと、その隣の .zip.sha256 をセットで保管してください。
set "KitenBackupExit=0"
goto finish

:cancelled_confirmation
endlocal
echo キャンセルしました。バックアップは作成していません。
set "KitenBackupExit=0"
goto finish

:missing_environment
echo ユーザーの保存先を取得できません。バックアップは実行していません。
goto finish

:missing_tool
echo このファイルを、展開済みKitenのMaintenanceフォルダーへ置いてください。
echo Kiten.DataTool.exe が見つからないため、バックアップは実行していません。
goto finish

:failed
echo.
echo バックアップは完了していません。上のエラーを確認してください。
echo 起動中・別の保守処理・未完了の復元などの拒否条件は回避しないでください。
goto finish

:finish
echo.
pause
endlocal & exit /b %KitenBackupExit%

:run_tool
if defined KitenBackupReleaseInfo (
    "%KitenBackupTool%" backup --channel stable --data-root "%KitenBackupDataRoot%" --destination "%KitenBackupDestination%" --release-info "%KitenBackupReleaseInfo%" %*
) else (
    "%KitenBackupTool%" backup --channel stable --data-root "%KitenBackupDataRoot%" --destination "%KitenBackupDestination%" %*
)
exit /b
