"""Validate repository conventions without executing repository-supplied commands."""
import argparse
import hashlib
import json
import os
from pathlib import Path
import re
import subprocess
import urllib.error
import urllib.request

import yaml

SUBJECT = re.compile(r"(?:feat|fix|perf|refactor|docs|test|build|ci|chore|revert)(?:\([a-z0-9]+(?:-[a-z0-9]+)*(?:,[a-z0-9]+(?:-[a-z0-9]+)*)*\))?!?: [a-z].+")
SHA = re.compile(r"[0-9a-f]{40}")


def issue_references(body, repository):
    """Accept closing or partial-work references without treating arbitrary URLs as issues."""
    references = []
    pattern = r"(?im)^\s*(?:closes?|fix(?:es)?|resolves?|refs?|references?|related to)\s+(?:https://github\.com/([\w.-]+/[\w.-]+)/issues/(\d+)|(?:([\w.-]+/[\w.-]+))?#(\d+))\b"
    for match in re.finditer(pattern, body or ""):
        full_repo, url_number, short_repo, number = match.groups()
        references.append((full_repo or short_repo or repository, int(url_number or number)))
    return references


def issue_precedes_pr(issue, created_at, policy_since=None):
    earlier = issue["created_at"] <= created_at
    legacy = policy_since is not None and created_at < policy_since
    return "pull_request" not in issue and (earlier or legacy)


def check_issue(pr, repository, token, policy_since=None):
    references = issue_references(pr.get("body"), repository)
    if not references:
        return ["Every PR needs an earlier issue describing the bug or intended change; add Closes #NN or Refs #NN."]
    for repo, number in references:
        req = urllib.request.Request(f"https://api.github.com/repos/{repo}/issues/{number}",
                                     headers={"Authorization": "Bearer " + token, "Accept": "application/vnd.github+json"})
        try:
            # The URL is constructed from a GitHub repository identity and numeric issue ID.
            with urllib.request.urlopen(req, timeout=30) as response:  # nosec B310
                issue = json.load(response)
        except (urllib.error.URLError, ValueError):
            continue
        if issue_precedes_pr(issue, pr["created_at"], policy_since):
            return []
    return ["The linked issue must exist, be an issue rather than a PR, and have been created before this PR."]


def check_title(title, author=""):
    errors = []
    if not SUBJECT.fullmatch(title) or title.endswith(".") or re.search(r"(?:^|\s)#\d+", title):
        errors.append("Use type(scope): lower-case result, without a final full stop or issue number.")
    bot = author == "dependabot[bot]" and title.startswith("build(deps): ")
    if len(title) > 72 and not bot:
        errors.append("Keep the complete PR title within 72 characters.")
    return errors


def workflow_errors(data, path, retained_credentials=()):
    errors = []
    permissions = data.get("permissions", {})
    if not isinstance(permissions, dict) or any(value == "write" for value in permissions.values()):
        errors.append(f"{path}: keep workflow permissions read-only; grant writes per job")
    trigger = data.get("on", data.get(True, {}))
    privileged_event = isinstance(trigger, dict) and "pull_request_target" in trigger
    for job_id, job in data.get("jobs", {}).items():
        for step in job.get("steps", []):
            uses = step.get("uses", "")
            if uses and not uses.startswith("./") and not SHA.fullmatch(uses.rsplit("@", 1)[-1]):
                errors.append(f"{path}/{job_id}: external actions must use full commit SHAs")
            if privileged_event and uses.startswith("actions/checkout@"):
                errors.append(f"{path}/{job_id}: metadata-only pull_request_target workflows must not check out PR code")
            if uses.startswith("actions/checkout@"):
                saved = step.get("with", {}).get("persist-credentials", True)
                allowed = f"{path}:{job_id}" in retained_credentials
                if saved is not False and not (saved is True and allowed):
                    errors.append(f"{path}/{job_id}: disable saved checkout credentials or document the push exception")
            if "run" in step and re.search(r"\$\{\{\s*github\.event\.(?:issue|pull_request|comment)", step["run"]):
                errors.append(f"{path}/{job_id}: pass event text through an environment variable or read the event JSON")
        if "uses" in job and not job["uses"].startswith("./") and not SHA.fullmatch(job["uses"].rsplit("@", 1)[-1]):
            errors.append(f"{path}/{job_id}: reusable workflows must use full commit SHAs")
    return errors


def check_shared(root):
    lock = json.loads((root / ".github/repository-standards.json").read_text())
    errors = []
    if lock["source"] != "DashyNMS/.github" or not SHA.fullmatch(lock["revision"]):
        errors.append("The standards lock must identify the canonical repository and an immutable revision")
    for filename, digest in lock["files"].items():
        path = root / filename
        if not path.is_file() or hashlib.sha256(path.read_bytes()).hexdigest() != digest:
            errors.append(f"{filename}: differs from the pinned shared standards; update the canonical source first")
    block = (root / "CLAUDE.md").read_text().split("## Repository conventions\n", 1)[-1].split("\n## Validation and releases", 1)[0]
    if hashlib.sha256(block.encode()).hexdigest() != lock["claude_sha256"]:
        errors.append("CLAUDE.md: common repository conventions differ from the canonical section")
    for filename, digest in lock.get("helpers", {}).items():
        if hashlib.sha256((root / filename).read_bytes()).hexdigest() != digest:
            errors.append(f"{filename}: shared helper changed; update its source and parity record together")
    return errors


def check_links(root):
    errors = []
    for name in ("CLAUDE.md", "CONTRIBUTING.md", "ACCESSIBILITY.md", "SECURITY.md", "README.md"):
        text = (root / name).read_text()
        for target in re.findall(r"\]\(([^\s)]+)\)", text):
            if re.match(r"(?:[a-z]+:|#)", target):
                continue
            filename, _, anchor = target.partition("#")
            path = (root / filename).resolve()
            if not path.exists():
                errors.append(f"{name}: missing local link {filename}")
            elif anchor and path.suffix == ".md":
                headings = {re.sub(r"[^\w -]", "", line.lstrip("#").strip().lower()).replace(" ", "-")
                            for line in path.read_text().splitlines() if line.startswith("#")}
                if anchor not in headings:
                    errors.append(f"{name}: missing heading {target}")
    return errors


def check_repository(root, canonical=False):
    config = json.loads((root / ".github/repository-config.json").read_text())
    errors = ([] if canonical else check_shared(root)) + check_links(root)
    for path in sorted((root / ".github/workflows").glob("*.yml")):
        errors += workflow_errors(yaml.safe_load(path.read_text()), path.relative_to(root).as_posix(), config.get("retained_credentials", []))
    for entry in yaml.safe_load((root / ".github/dependabot.yml").read_text())["updates"]:
        if entry.get("commit-message") != {"prefix": "build", "include": "scope"}:
            errors.append("dependabot.yml: every ecosystem must use the shared commit naming")
    for name in ("CODE_OF_CONDUCT.md", "CONTRIBUTING.md", "ACCESSIBILITY.md", "SECURITY.md", "LICENSE", ".github/CODEOWNERS", ".github/pull_request_template.md"):
        if not (root / name).is_file():
            errors.append(f"Missing community file: {name}")
    return errors


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--root", type=Path, default=Path.cwd())
    parser.add_argument("--canonical", action="store_true", help="validate the maintained source without a consumer lock")
    args = parser.parse_args()
    errors = check_repository(args.root, args.canonical)
    event = os.environ.get("GITHUB_EVENT_PATH")
    if event:
        data = json.loads(Path(event).read_text())
        pr = data.get("pull_request")
        if pr:
            errors += check_title(pr["title"], pr.get("user", {}).get("login", ""))
            config = json.loads((args.root / ".github/repository-config.json").read_text())
            errors += check_issue(pr, data["repository"]["full_name"], os.environ.get("GH_TOKEN", ""), config.get("issue_policy_since"))
    if errors:
        for error in errors:
            print(error)
        return 1
    print("Repository conventions and workflow permissions passed.")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
