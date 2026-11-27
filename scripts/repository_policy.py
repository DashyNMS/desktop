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
DEPENDABOT_SUBJECT = re.compile(r"build(?:\(deps(?:-dev)?\))?: (?:\[[Ss]ecurity\] )?(?:[Bb]ump|[Uu]pdate) \S.*")
SHA = re.compile(r"[0-9a-f]{40}")


class WorkflowLoader(yaml.SafeLoader):
    """Reject duplicate mapping keys before PyYAML can discard a value."""

    def construct_mapping(self, node, deep=False):
        if not isinstance(node, yaml.MappingNode):
            return super().construct_mapping(node, deep=deep)
        keys = set()
        for key, _ in node.value:
            if not isinstance(key, yaml.ScalarNode):
                continue
            name = key.value.casefold()
            if name in keys:
                raise yaml.constructor.ConstructorError(
                    None, None, f"duplicate workflow key {key.value!r}", key.start_mark
                )
            keys.add(name)
        return super().construct_mapping(node, deep=deep)


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


def check_issue(pr, repository, token, policy_since=None, maintenance_issue=None):
    references = issue_references(pr.get("body"), repository)
    # Dependabot's metadata edit uses GITHUB_TOKEN and cannot trigger another run.
    # Validate the configured maintenance issue directly, even on the opened payload.
    user = pr.get("user", {})
    if (user.get("login") == "dependabot[bot]" and user.get("type") == "Bot"
            and re.fullmatch(r"[1-9]\d*", maintenance_issue or "")):
        references.append((repository, int(maintenance_issue)))
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


def check_title(title, author=None):
    author = author or {}
    generated = (
        author.get("login") == "dependabot[bot]"
        and author.get("type") == "Bot"
        and author.get("id") == 49699333
        and DEPENDABOT_SUBJECT.fullmatch(title)
    )
    errors = []
    if (not (generated or SUBJECT.fullmatch(title)) or title.endswith(".")
            or re.search(r"(?:^|\s)#\d+", title)):
        errors.append("Use type(scope): lower-case result, without a final full stop or issue number.")
    if len(title) > 72 and not generated:
        errors.append("Keep the complete PR title within 72 characters.")
    return errors


def is_privileged_workflow(data):
    trigger = data.get("on", data.get(True, {}))
    events = [trigger] if isinstance(trigger, str) else trigger
    return isinstance(events, (dict, list)) and "pull_request_target" in events


def workflow_errors(data, path, retained_credentials=()):
    errors = []
    permissions = data.get("permissions", {})
    if not isinstance(permissions, dict) or any(value == "write" for value in permissions.values()):
        errors.append(f"{path}: keep workflow permissions read-only; grant writes per job")
    privileged_event = is_privileged_workflow(data)
    for job_id, job in data.get("jobs", {}).items():
        for step in job.get("steps", []):
            uses = step.get("uses", "")
            if (step.get("run", "").strip() == "python scripts/security_check.py"
                    and step.get("if") not in ("!cancelled()", "${{ !cancelled() }}")):
                errors.append(f"{path}/{job_id}: Python and secret scans must run independently of earlier audit failures")
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
    lock = json.loads((root / ".github/repository-standards.json").read_text(encoding="utf-8"))
    errors = []
    if lock["source"] != "DashyNMS/.github" or not SHA.fullmatch(lock["revision"]):
        errors.append("The standards lock must identify the canonical repository and an immutable revision")
    for filename, digest in lock["files"].items():
        path = root / filename
        if not path.is_file() or hashlib.sha256(path.read_bytes()).hexdigest() != digest:
            errors.append(f"{filename}: differs from the pinned shared standards; update the canonical source first")
    block = (root / "CLAUDE.md").read_text(encoding="utf-8").split("## Repository conventions\n", 1)[-1].split("\n## Validation and releases", 1)[0]
    if hashlib.sha256(block.encode()).hexdigest() != lock["claude_sha256"]:
        errors.append("CLAUDE.md: common repository conventions differ from the canonical section")
    for filename, digest in lock.get("helpers", {}).items():
        if hashlib.sha256((root / filename).read_bytes()).hexdigest() != digest:
            errors.append(f"{filename}: shared helper changed; update its source and parity record together")
    return errors


def check_links(root):
    errors = []
    for name in ("CLAUDE.md", "CONTRIBUTING.md", "ACCESSIBILITY.md", "SECURITY.md", "README.md"):
        text = (root / name).read_text(encoding="utf-8")
        for target in re.findall(r"\]\(([^\s)]+)\)", text):
            if re.match(r"(?:[a-z]+:|#)", target):
                continue
            filename, _, anchor = target.partition("#")
            path = (root / filename).resolve()
            if not path.exists():
                errors.append(f"{name}: missing local link {filename}")
            elif anchor and path.suffix == ".md":
                headings = {re.sub(r"[^\w -]", "", line.lstrip("#").strip().lower()).replace(" ", "-")
                            for line in path.read_text(encoding="utf-8").splitlines() if line.startswith("#")}
                if anchor not in headings:
                    errors.append(f"{name}: missing heading {target}")
    return errors


def check_repository(root, canonical=False):
    config = json.loads((root / ".github/repository-config.json").read_text(encoding="utf-8"))
    errors = ([] if canonical else check_shared(root)) + check_links(root)
    workflows = {}
    invalid_workflows = set()
    for path in sorted((root / ".github/workflows").iterdir()):
        if path.suffix not in (".yml", ".yaml"):
            continue
        name = path.relative_to(root).as_posix()
        try:
            loader = WorkflowLoader(path.read_text(encoding="utf-8"))
            try:
                workflow = loader.get_single_data()
            finally:
                loader.dispose()
        except yaml.YAMLError as error:
            mark = getattr(error, "problem_mark", None)
            location = f":{mark.line + 1}:{mark.column + 1}" if mark else ""
            errors.append(f"{name}{location}: {getattr(error, 'problem', None) or 'invalid workflow YAML'}")
            invalid_workflows.add(name)
            continue
        if not isinstance(workflow, dict):
            errors.append(f"{name}: expected a workflow mapping")
            invalid_workflows.add(name)
            continue
        workflows[name] = workflow
        errors += workflow_errors(workflow, name, config.get("retained_credentials", []))
    rank = {"none": 0, "read": 1, "write": 2}

    def required_permissions(workflow_name, seen=()):
        if workflow_name in seen:
            return {}
        seen = (*seen, workflow_name)
        called = workflows[workflow_name]
        required = {}
        sources = [called.get("permissions", {})]
        for called_job in called.get("jobs", {}).values():
            if not isinstance(called_job, dict):
                continue
            sources.append(called_job.get("permissions", {}))
            nested = called_job.get("uses", "")
            if nested.startswith("./.github/workflows/") and nested[2:] in workflows:
                sources.append(required_permissions(nested[2:], seen))
        for source in sources:
            if not isinstance(source, dict):
                continue
            for permission, level in source.items():
                if rank.get(level, 0) > rank.get(required.get(permission, "none"), 0):
                    required[permission] = level
        return required

    privileged_callees = set()
    stack = [name for name, wf in workflows.items() if is_privileged_workflow(wf)]
    visited = set(stack)
    while stack:
        current = stack.pop()
        for job in workflows[current].get("jobs", {}).values():
            if not isinstance(job, dict):
                continue
            target = job.get("uses", "")
            if target.startswith("./.github/workflows/") and target[2:] in workflows and target[2:] not in visited:
                visited.add(target[2:])
                privileged_callees.add(target[2:])
                stack.append(target[2:])
    for callee in sorted(privileged_callees):
        for job_id, job in workflows[callee].get("jobs", {}).items():
            if isinstance(job, dict) and any(step.get("uses", "").startswith("actions/checkout@") for step in job.get("steps", [])):
                errors.append(f"{callee}/{job_id}: metadata-only pull_request_target workflows must not check out PR code")

    for name, workflow in workflows.items():
        for job_id, job in workflow.get("jobs", {}).items():
            target = job.get("uses", "")
            if not target.startswith("./.github/workflows/"):
                continue
            called = workflows.get(target[2:])
            if called is None:
                if target[2:] not in invalid_workflows:
                    errors.append(f"{name}/{job_id}: reusable workflow not found: {target}")
                continue
            granted = job.get("permissions", workflow.get("permissions", {}))
            required = required_permissions(target[2:])
            if isinstance(granted, dict):
                for permission, level in required.items():
                    if rank.get(granted.get(permission, "none"), 0) < rank.get(level, 0):
                        errors.append(f"{name}/{job_id}: reusable workflow requires {permission}: {level}")
    for entry in yaml.safe_load((root / ".github/dependabot.yml").read_text(encoding="utf-8"))["updates"]:
        if entry.get("commit-message") != {"prefix": "build(deps)"}:
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
        data = json.loads(Path(event).read_text(encoding="utf-8"))
        pr = data.get("pull_request")
        if pr:
            errors += check_title(pr["title"], pr.get("user"))
            config = json.loads((args.root / ".github/repository-config.json").read_text(encoding="utf-8"))
            errors += check_issue(pr, data["repository"]["full_name"], os.environ.get("GH_TOKEN", ""), config.get("issue_policy_since"), os.environ.get("DEPENDENCY_MAINTENANCE_ISSUE"))
    if errors:
        for error in errors:
            print(error)
        return 1
    print("Repository conventions and workflow permissions passed.")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
