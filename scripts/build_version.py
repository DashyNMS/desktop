"""Work out a build's version. The same file in every DashyNMS repository.

    edge      <version>-edge.<run number>       every commit to main
    nightly   <version>-nightly.<run number>    00:00 UTC if main changed, or by hand
    release   <version>                         a published GitHub release

<version> is the version being built towards, in Directory.Build.props. The
result goes to $GITHUB_OUTPUT: version, tag, commit, changed and store_build. A
nightly also gets notes, written from the commits since the last build people
were given.

store_build is the number app stores order builds by. They refuse one they've
seen before, whichever workflow made it, and a run number starts again when a
workflow is renamed, so it's the minutes since 2026 began.
"""
import argparse
import datetime
import os
from pathlib import Path
import re
import subprocess
import sys

HEADINGS = {"feat": "New", "fix": "Fixed", "perf": "Faster"}
COMMIT = re.compile(r"(?P<type>[a-z]+)(?:\((?P<scope>[^)]*)\))?(?P<breaking>!)?: (?P<text>.+)")


def git(*args):
    return subprocess.run(("git", *args), check=True, capture_output=True, text=True).stdout.strip()


def base_version(props="Directory.Build.props"):
    found = re.search(r"<Version>([^<]*)</Version>", Path(props).read_text(encoding="utf-8"))
    if not found or not re.fullmatch(r"\d+\.\d+\.\d+", found[1]):
        raise SystemExit("The version in Directory.Build.props must be numeric, such as 2.0.0.")
    return found[1]


def run_number():
    number = os.environ.get("GITHUB_RUN_NUMBER", "")
    if not re.fullmatch(r"[1-9]\d*", number):
        raise SystemExit("GITHUB_RUN_NUMBER isn't set.")
    return number


def store_build(now=None):
    now = now or datetime.datetime.now(datetime.timezone.utc)
    return int((now - datetime.datetime(2026, 1, 1, tzinfo=datetime.timezone.utc)).total_seconds() // 60)


def newest_tag(pattern):
    """The newest tag matching a pattern on this branch's history, by commit."""
    tags = git("tag", "--list", pattern, "--merged", "HEAD", "--sort=-creatordate").splitlines()
    # The rolling edge tag marks nothing anyone was given notes for.
    return next((tag for tag in tags if tag != "v0.0.0-edge"), None)


def notes(since, repository):
    """User-facing changes since a tag, as release notes."""
    log = git("log", "--no-merges", "--format=%s", f"{since}..HEAD" if since else "-30").splitlines()
    sections = {heading: [] for heading in HEADINGS.values()}
    for subject in log:
        match = COMMIT.fullmatch(subject)
        if not match or match["type"] not in HEADINGS:
            continue
        text = match["text"][0].upper() + match["text"][1:]
        if match["breaking"]:
            text = "**Breaking:** " + text
        sections[HEADINGS[match["type"]]].append(text)
    lines = [f"Changes since [{since.lstrip('v')}](https://github.com/{repository}/releases/tag/{since})." if since else "Recent changes.", ""]
    for heading, items in sections.items():
        if items:
            lines += [f"## {heading}", ""] + [f"- {item}" for item in items] + [""]
    if not any(sections.values()):
        lines += ["## Changed", "", "- Maintenance only.", ""]
    return "\n".join(lines)


def main():
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("channel", choices=["edge", "nightly", "release"])
    parser.add_argument("--tag", help="the published release's tag (release only)")
    parser.add_argument("--notes", type=Path, help="where to write a nightly's notes")
    args = parser.parse_args()

    base = base_version()
    commit = git("rev-parse", "HEAD")
    changed = True
    if args.channel == "release":
        if args.tag != f"v{base}":
            raise SystemExit(f"The release tag {args.tag} must be v{base}, the version in Directory.Build.props.")
        if git("rev-parse", "--verify", f"refs/tags/{args.tag}^{{commit}}") != commit:
            raise SystemExit(f"{args.tag} must be the newest commit on main: that's what is built and signed.")
        version = base
    else:
        version = f"{base}-{args.channel}.{run_number()}"
    if args.channel == "nightly":
        last = newest_tag("v*-nightly.*")
        # By hand is asking for one, so only the schedule is skipped.
        changed = os.environ.get("GITHUB_EVENT_NAME") != "schedule" or last is None or git("rev-parse", f"{last}^{{commit}}") != commit
        if args.notes:
            args.notes.parent.mkdir(parents=True, exist_ok=True)
            since = last or newest_tag("v*-preview.*") or newest_tag("v[0-9]*")
            args.notes.write_text(notes(since, os.environ.get("GITHUB_REPOSITORY", "")), encoding="utf-8", newline="\n")

    result = {"version": version, "tag": f"v{version}", "commit": commit, "changed": str(changed).lower(), "store_build": store_build()}
    lines = "".join(f"{key}={value}\n" for key, value in result.items())
    sys.stdout.write(lines)
    if "GITHUB_OUTPUT" in os.environ:
        with open(os.environ["GITHUB_OUTPUT"], "a", encoding="utf-8") as output:
            output.write(lines)


if __name__ == "__main__":
    main()
