#!/usr/bin/env python3
"""Prove that the actual production project rejects an omitted union case."""
from pathlib import Path
import shutil
import subprocess
import tempfile

root = Path(__file__).resolve().parents[1]
with tempfile.TemporaryDirectory(prefix="inbox-union-coverage-") as temporary:
    project = Path(temporary) / "AgentDesk"
    shutil.copytree(root / "AgentDesk", project,
                    ignore=shutil.ignore_patterns("bin", "obj", "wwwroot"))
    source = project / "Domain" / "WorkflowStates.cs"
    text = source.read_text()
    arm = "        QueueRejected rejected => new FailedDelivery(rejected.Reason),\n"
    if text.count(arm) != 1:
        raise SystemExit("The expected union arm changed; update this probe explicitly.")
    source.write_text(text.replace(arm, ""))
    result = subprocess.run(
        ["dotnet", "build", str(project / "AgentDesk.csproj"), "--nologo", "--verbosity", "quiet"],
        capture_output=True, text=True, timeout=120)
    if result.returncode == 0 or "error CS8509" not in result.stdout + result.stderr:
        print(result.stdout, result.stderr)
        raise SystemExit("Missing union case was not rejected by the production build.")
    print("PASS: removing QueueRejected fails the production build with error CS8509.")
