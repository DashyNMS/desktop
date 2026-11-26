"""Audit tracked dependencies and Python helpers; report secret locations without values."""
import argparse
import json
from pathlib import Path
import subprocess
import sys

ROOT = Path(__file__).resolve().parents[1]


def tracked_files(root):
    result = subprocess.run(["git", "ls-files", "-z", "--cached", "--others", "--exclude-standard"], cwd=root,
                            check=True, capture_output=True)
    return sorted(set(Path(p.decode()) for p in result.stdout.split(b"\0") if p))


def new_secrets(report, baseline):
    # Baselines retain hashed identities, so moving a reviewed fixture is harmless.
    known = {(path, row["type"], row["hashed_secret"])
             for path, rows in baseline.get("results", {}).items() for row in rows
             if row.get("reviewed_reason") and row.get("is_secret") is not True}
    return [(path, row["line_number"], row["type"]) for path, rows in report["results"].items()
            for row in rows if (path, row["type"], row["hashed_secret"]) not in known]


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--nuget", action="store_true", help="also restore and audit the configured .NET projects")
    parser.add_argument("--nuget-only", action="store_true", help="run only the configured .NET audits")
    args = parser.parse_args()
    config = json.loads((ROOT / ".github/repository-config.json").read_text())
    output = ROOT / "artifacts/security"
    output.mkdir(parents=True, exist_ok=True)
    requirements = [p for p in tracked_files(ROOT) if p.name.startswith("requirements") and p.suffix == ".txt"]
    commands = []
    if requirements:
        command = [sys.executable, "-m", "pip_audit", "--strict", "--progress-spinner", "off", "-f", "json", "-o", str(output / "dependencies.json")]
        for path in requirements:
            command += ["-r", str(path)]
        commands.append(command)
    folders = [p for p in ("scripts", "tools") if (ROOT / p).is_dir()]
    if folders:
        commands.append([sys.executable, "-m", "bandit", "-r", *folders, "-x", "scripts/tests,tools/tests", "-ll", "-f", "json", "-o", str(output / "bandit.json")])
    if args.nuget or args.nuget_only:
        for project in config.get("nuget_projects", []):
            commands.append(["dotnet", "restore", project, "-p:NuGetAudit=true", "-p:NuGetAuditMode=all", "-p:NuGetAuditLevel=high"])
    if args.nuget_only:
        commands = [command for command in commands if command[0] == "dotnet"]
    failed = False
    for command in commands:
        result = subprocess.run(command, cwd=ROOT, check=False)
        failed |= result.returncode != 0
    if args.nuget_only:
        return int(failed)
    command = [sys.executable, "-m", "detect_secrets", "scan", "--no-verify", "--filter", "scripts/security_filters.py::is_integrity_digest", "--exclude-files", r"(^|/)\.secrets\.baseline$"]
    if (ROOT / "tools/security_filters.py").is_file():
        command += ["--filter", "tools/security_filters.py::is_catalogue_digest"]
    result = subprocess.run(command, cwd=ROOT, capture_output=True, text=True)
    if result.returncode:
        print("Secret scan failed; inspect its configuration.")
        failed = True
    else:
        report = json.loads(result.stdout)
        (output / "secrets.json").write_text(json.dumps(report, indent=2) + "\n")
        baseline = json.loads((ROOT / ".secrets.baseline").read_text())
        findings = new_secrets(report, baseline)
        for path, line, kind in findings:
            print(f"Unreviewed secret candidate: {path}:{line} ({kind})")
        failed |= bool(findings)
        print(f"Secret scan: {len(findings)} unreviewed candidates. Reports: artifacts/security")
    return int(failed)


if __name__ == "__main__":
    raise SystemExit(main())
