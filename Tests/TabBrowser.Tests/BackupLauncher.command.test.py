"""Windowsのcmdを使い、バックアップ入口の確認・拒否・パス渡しを検証する。

DataToolの代役だけを実行する。実ユーザーデータや本物のDataToolは使わない。
生成した試験フォルダーは、失敗時も証拠として残す。
"""

import argparse
import base64
import json
import os
from pathlib import Path
import shutil
import subprocess
import tempfile


parser = argparse.ArgumentParser()
parser.add_argument("--output-directory", type=Path)
args = parser.parse_args()
repo = Path(__file__).resolve().parents[2]
launcher = repo / "tools" / "Backup-KitenData.cmd"
if args.output_directory:
    output = args.output_directory.resolve()
    output.mkdir(parents=True, exist_ok=False)
else:
    output = Path(tempfile.mkdtemp(prefix="KitenBackupLauncher-"))

compiler = Path(os.environ["WINDIR"]) / "Microsoft.NET/Framework64/v4.0.30319/csc.exe"
assert compiler.is_file(), "試験用の.NET Framework C#コンパイラーがありません"
fixture_source = output / "Fixture.cs"
fixture_source.write_text(
    r'''
using System;
using System.IO;
using System.Text;
class Fixture {
    static int Main(string[] args) {
        Console.OutputEncoding = new UTF8Encoding(false);
        string trace = Environment.GetEnvironmentVariable("KITEN_TEST_TRACE");
        File.AppendAllText(trace, Convert.ToBase64String(Encoding.UTF8.GetBytes(string.Join("\0", args))) + "\n");
        bool plan = Array.IndexOf(args, "--plan") >= 0;
        Console.WriteLine(plan ? "PLAN-OK" : "APPLY-OK");
        return int.Parse(Environment.GetEnvironmentVariable(plan ? "KITEN_TEST_PLAN_EXIT" : "KITEN_TEST_APPLY_EXIT"));
    }
}
''',
    encoding="utf-8",
)
fixture_exe = output / "Fixture.exe"
subprocess.run(
    [str(compiler), "/nologo", "/target:exe", "/out:" + str(fixture_exe), str(fixture_source)],
    check=True,
    stdout=subprocess.PIPE,
    stderr=subprocess.STDOUT,
)

cases = [
    ("confirmed", "BACKUP", 0, 0, True, True, True, 2, True),
    ("cancel_empty", "", 0, 0, True, True, True, 1, False),
    ("cancel_lowercase", "backup", 0, 0, True, True, True, 1, False),
    ("cancel_spaces", " BACKUP ", 0, 0, True, True, True, 1, False),
    ("input_is_not_code", 'BACKUP" & echo INJECTED>injected.txt & rem "', 0, 0, True, True, True, 1, False),
    ("plan_refused", "BACKUP", 2, 0, True, True, True, 1, False),
    ("plan_negative_exit", "BACKUP", -1, 0, True, True, True, 1, False),
    ("apply_refused", "BACKUP", 0, 2, True, True, True, 2, False),
    ("apply_negative_exit", "BACKUP", 0, -1, True, True, True, 2, False),
    ("missing_tool", "BACKUP", 0, 0, False, True, True, 0, False),
    ("missing_environment", "BACKUP", 0, 0, True, True, False, 0, False),
    ("optional_release_info", "BACKUP", 0, 0, True, False, True, 2, True),
]
results = []
for name, confirmation, plan_exit, apply_exit, tool_exists, metadata_exists, environment_exists, expected_calls, success in cases:
    case_root = output / (name + " 日本語 & ! % (試験)")
    maintenance = case_root / "Maintenance"
    maintenance.mkdir(parents=True)
    script = maintenance / "Backup-KitenData.cmd"
    shutil.copyfile(launcher, script)
    if tool_exists:
        shutil.copyfile(fixture_exe, maintenance / "Kiten.DataTool.exe")
    if metadata_exists:
        (case_root / "release-info.json").write_text("{}", encoding="utf-8")
    unrelated_cwd = case_root / "別の作業場所"
    unrelated_cwd.mkdir()
    profile = case_root / "利用者 & ! % (profile)"
    local_data = case_root / "保存先 & ! % (local)"
    trace = case_root / "calls.txt"
    env = os.environ.copy()
    # 子プロセスだけを隔離し、Windowsや実際の利用者の環境は変更しない。
    env.update(USERPROFILE=str(profile), LOCALAPPDATA=str(local_data), KITEN_TEST_TRACE=str(trace),
               KITEN_TEST_PLAN_EXIT=str(plan_exit), KITEN_TEST_APPLY_EXIT=str(apply_exit))
    if not environment_exists:
        env.pop("LOCALAPPDATA", None)
    command = '"' + os.environ["COMSPEC"] + '" /d /s /c ""' + str(script) + '""'
    outcome = subprocess.run(command, input=(confirmation + "\r\n\r\n").encode("utf-8"),
                             stdout=subprocess.PIPE, stderr=subprocess.STDOUT, env=env, cwd=unrelated_cwd, timeout=15)
    text = outcome.stdout.decode("utf-8", errors="replace")
    (case_root / "console.txt").write_text(text, encoding="utf-8")
    calls = [] if not trace.exists() else [base64.b64decode(line).decode("utf-8").split("\0") for line in trace.read_text().splitlines()]
    assert len(calls) == expected_calls, (name, len(calls), text)
    for index, call in enumerate(calls):
        expected = ["backup", "--channel", "stable", "--data-root", str(local_data / "TabBrowser"),
                    "--destination", str(profile / "KitenBackup")]
        if metadata_exists:
            expected += ["--release-info", str(maintenance) + "\\..\\release-info.json"]
        expected += ["--plan"] if index == 0 else ["--confirm", "BACKUP"]
        assert call == expected, (name, call, expected)
    assert not (unrelated_cwd / "injected.txt").exists(), name
    assert ("バックアップと作成後の検証が完了しました。" in text) == success, (name, text)
    failed = not tool_exists or not environment_exists or plan_exit != 0 or apply_exit != 0
    assert (outcome.returncode != 0) == failed, (name, outcome.returncode, text)
    results.append({"name": name, "passed": True, "dataToolCalls": len(calls), "exitCode": outcome.returncode,
                    "actualDataToolExecuted": False})
    (output / "results.json").write_text(json.dumps(results, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")

print(json.dumps({"passed": len(results), "actualDataToolExecuted": False, "evidenceDirectory": str(output)}, ensure_ascii=True))
