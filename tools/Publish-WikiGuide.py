#!/usr/bin/env python3
"""Turn the user guide in docs/guide into GitHub wiki pages (#257).

The guide is written and reviewed in the repository; the wiki is only where
people read it. Each guide file becomes a page named after its first heading
("Install on macOS" becomes Install-on-macOS), and README.md becomes Home. Links
between guide files become wiki links, links to other repository files become
GitHub links, and a sidebar lists the pages in the order README.md links them.

    Publish-WikiGuide.py <guide folder> <output folder> --repository OWNER/NAME [--branch master]

The output folder's Markdown pages are replaced, so pass a wiki clone or an
empty folder. Every link to another guide page, heading or repository file is
checked; any that doesn't resolve fails the run before anything is written.
"""

import argparse
import pathlib
import re
import sys

LINK = re.compile(r"(!?)\[((?:\\.|[^\]\\])*)\]\(([^)\s]+)\)")
HEADING = re.compile(r"^(#{1,6})\s+(.*?)\s*#*\s*$")
FENCE = re.compile(r"^\s*(```|~~~)")


def slug(heading):
    """GitHub's anchor for a heading: lower case, punctuation dropped, spaces as hyphens."""
    text = re.sub(r"[`*_]|\[([^\]]*)\]\([^)]*\)", r"\1", heading).strip().lower()
    return re.sub(r"\s", "-", re.sub(r"[^\w\- ]", "", text))


def outside_code(lines):
    """Yields (index, line) for lines that aren't inside a fenced code block."""
    fenced = False
    for index, line in enumerate(lines):
        if FENCE.match(line):
            fenced = not fenced
            continue
        if not fenced:
            yield index, line


class Page:
    def __init__(self, path):
        self.path = path
        self.lines = path.read_text(encoding="utf-8").splitlines()
        self.title = next((m.group(2) for _, line in outside_code(self.lines) if (m := HEADING.match(line)) and len(m.group(1)) == 1), None)
        if self.title is None:
            raise SystemExit(f"{path}: the page needs a '# Title' line, which names its wiki page.")
        self.name = "Home" if path.name == "README.md" else re.sub(r"\s+", "-", re.sub(r"[^\w\- ]", "", self.title).strip())
        self.anchors = {slug(m.group(2)) for _, line in outside_code(self.lines) if (m := HEADING.match(line))}


def convert(guide, repository, branch):
    pages = {path.name: Page(path) for path in sorted(guide.glob("*.md"))}
    if "README.md" not in pages:
        raise SystemExit(f"{guide}: README.md is the guide's front page and becomes the wiki's Home.")
    names = [page.name for page in pages.values()]
    if len(set(names)) != len(names):
        raise SystemExit("Two guide pages have the same title, so they'd have the same wiki page name.")

    root = guide.resolve()
    while not (root / ".git").exists() and root.parent != root:
        root = root.parent
    errors = []
    output = {}

    for page in pages.values():
        def rewrite(match):
            image, text, target = match.groups()
            if re.match(r"^[a-z][a-z0-9+.-]*:", target, re.I):
                return match.group(0)
            path, _, fragment = target.partition("#")
            if not path:
                if fragment and fragment not in page.anchors:
                    errors.append(f"{page.path.name}: no heading for #{fragment}")
                return match.group(0)
            if path.endswith(".md") and "/" not in path:
                linked = pages.get(path)
                if linked is None:
                    errors.append(f"{page.path.name}: {path} isn't a guide page")
                    return match.group(0)
                if fragment and fragment not in linked.anchors:
                    errors.append(f"{page.path.name}: {path} has no heading for #{fragment}")
                return f"{image}[{text}]({linked.name}{'#' + fragment if fragment else ''})"
            resolved = (page.path.parent / path).resolve()
            if not resolved.exists() or root not in resolved.parents:
                errors.append(f"{page.path.name}: {path} isn't a file in the repository")
                return match.group(0)
            relative = resolved.relative_to(root).as_posix()
            base = f"https://raw.githubusercontent.com/{repository}/{branch}" if image else f"https://github.com/{repository}/blob/{branch}"
            return f"{image}[{text}]({base}/{relative}{'#' + fragment if fragment else ''})"

        lines = list(page.lines)
        for index, line in outside_code(page.lines):
            lines[index] = LINK.sub(rewrite, line)

        # The wiki shows a page's name as its title, so the heading would repeat it.
        if page.name != "Home":
            first = next(index for index, line in outside_code(lines) if HEADING.match(line))
            del lines[first]
            while first < len(lines) and not lines[first].strip():
                del lines[first]
        output[page.name + ".md"] = "\n".join(lines).rstrip() + "\n"

    # The sidebar follows the front page's own list.
    home = pages["README.md"]
    order = [m.group(3).partition("#")[0] for _, line in outside_code(home.lines) for m in LINK.finditer(line)]
    sidebar = ["**[User guide](Home)**", ""]
    for name in dict.fromkeys(order):
        if name in pages and name != "README.md":
            sidebar.append(f"- [{pages[name].title}]({pages[name].name})")
    for name, page in pages.items():
        if name != "README.md" and name not in order:
            errors.append(f"README.md doesn't link to {name}, so it would be missing from the sidebar")
    output["_Sidebar.md"] = "\n".join(sidebar) + "\n"
    guide_path = guide.resolve().relative_to(root).as_posix()
    output["_Footer.md"] = (f"These pages are copied from [{guide_path}](https://github.com/{repository}/tree/{branch}/{guide_path}) "
                            "in the DashyNMS repository each time the guide changes, so edit the guide there rather than here.\n")
    return output, errors


def main():
    parser = argparse.ArgumentParser(description=__doc__.splitlines()[0])
    parser.add_argument("guide", type=pathlib.Path)
    parser.add_argument("output", type=pathlib.Path)
    parser.add_argument("--repository", required=True)
    parser.add_argument("--branch", default="master")
    arguments = parser.parse_args()

    output, errors = convert(arguments.guide, arguments.repository, arguments.branch)
    if errors:
        for error in errors:
            print(f"::error::{error}")
        return 1

    arguments.output.mkdir(parents=True, exist_ok=True)
    for old in arguments.output.glob("*.md"):
        old.unlink()
    for name, text in output.items():
        (arguments.output / name).write_text(text, encoding="utf-8")
        print(f"Wrote {name}")
    return 0


if __name__ == "__main__":
    sys.exit(main())
