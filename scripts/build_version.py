"""Work out a build's version. The same file in every DashyNMS repository.

    edge      <version>-edge.<run number>       every commit to main
    nightly   <version>-nightly.<run number>    00:00 UTC if main changed, or by hand
    release   <version>                         a pushed v<version> tag
    check     nothing: only checks that the release notes are there

<version> is the version being built towards, in Directory.Build.props. The
result goes to $GITHUB_OUTPUT: version, tag, commit, changed and store_build.

Release notes are docs/releases/<version>.md, one file for everything since the
last release (docs/DEVELOPMENT.md, "Release notes"). A nightly's notes are a
copy of it with a link to what changed since the last build people were given;
a release's are the file itself.

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

HEADINGS = ("Breaking", "New", "Changed", "Fixed")


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


def release_notes(base):
    """The version's release notes, checked: nothing is built without them."""
    path = Path(f"docs/releases/{base}.md")
    if not path.is_file():
        raise SystemExit(f"{path} is missing. It's the release notes for the version in Directory.Build.props.")
    text = path.read_text(encoding="utf-8").strip() + "\n"
    headings = re.findall(r"^## (.+)$", text, re.M)
    unknown = [heading for heading in headings if heading not in HEADINGS]
    if unknown:
        raise SystemExit(f"{path} has the heading {unknown[0]!r}. Use {', '.join(HEADINGS)}.")
    if not headings or not re.search(r"^- \S", text, re.M):
        raise SystemExit(f"{path} needs at least one change, as a list item under a ## heading.")
    return text


def nightly_notes(text, tag, commit, repository):
    since = newest_tag("v[0-9]*")
    changes = f" [Changes since {since}](https://github.com/{repository}/compare/{since}...{tag})." if since else ""
    return f"{text}\n---\n\nNightly build of {commit[:7]}.{changes}\n"


def main():
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("channel", choices=["edge", "nightly", "release", "check"])
    parser.add_argument("--tag", help="the release's tag (release only)")
    parser.add_argument("--notes", type=Path, help="where to write the notes to publish")
    args = parser.parse_args()

    base = base_version()
    text = release_notes(base)
    if args.channel == "check":
        return
    commit = git("rev-parse", "HEAD")
    changed = True
    if args.channel == "release":
        if args.tag != f"v{base}":
            raise SystemExit(f"The release tag {args.tag} must be v{base}, the version in Directory.Build.props.")
        if git("rev-parse", "--verify", f"refs/tags/{args.tag}^{{commit}}") != commit:
            raise SystemExit(f"{args.tag} must be the newest commit on main: that's what is built and signed.")
        # A release pins other DashyNMS packages at their releases, not builds of them.
        builds = re.findall(r"<(DashyNMS\w+Version)>[^<]*-[^<]*<", Path("Directory.Build.props").read_text(encoding="utf-8"))
        if builds:
            raise SystemExit(f"Pin a release of {', '.join(builds)} in Directory.Build.props before releasing this.")
        version = base
    else:
        version = f"{base}-{args.channel}.{run_number()}"
    if args.channel == "nightly":
        last = newest_tag("v*-nightly.*")
        # By hand is asking for one, so only the schedule is skipped.
        changed = os.environ.get("GITHUB_EVENT_NAME") != "schedule" or last is None or git("rev-parse", f"{last}^{{commit}}") != commit
    if args.notes:
        body = nightly_notes(text, f"v{version}", commit, os.environ.get("GITHUB_REPOSITORY", "")) if args.channel == "nightly" else text
        args.notes.parent.mkdir(parents=True, exist_ok=True)
        args.notes.write_text(body, encoding="utf-8", newline="\n")

    result = {"version": version, "tag": f"v{version}", "commit": commit, "changed": str(changed).lower(), "store_build": store_build()}
    lines = "".join(f"{key}={value}\n" for key, value in result.items())
    sys.stdout.write(lines)
    if "GITHUB_OUTPUT" in os.environ:
        with open(os.environ["GITHUB_OUTPUT"], "a", encoding="utf-8") as output:
            output.write(lines)


if __name__ == "__main__":
    main()
