"""Check the category metadata in a GitHub REST issue object."""
import json
import sys

STATUS_LABELS = {
    "blocked-upstream", "duplicate", "good first issue", "help wanted", "invalid",
    "needs-triage", "wontfix",
}
PLANNING_LABEL_PREFIXES = ("roadmap:", "severity:")


def check_metadata(issue):
    if "pull_request" in issue:
        raise ValueError("Expected an issue number.")
    errors = []
    if not (issue.get("type") or {}).get("name"):
        errors.append("Set an organisation issue type, such as Bug, Feature or Task.")
    labels = {label["name"].casefold() for label in issue.get("labels", [])}
    if not any(not label.startswith(PLANNING_LABEL_PREFIXES) for label in labels - STATUS_LABELS):
        errors.append("Add a relevant category or topic label, such as ci or dependencies.")
    if not (issue.get("milestone") or {}).get("number"):
        errors.append("Assign a release milestone, Maintenance or Unscheduled.")
    return errors


def main():
    errors = check_metadata(json.load(sys.stdin))
    for error in errors:
        print(f"::error::{error}")
    if errors:
        print("Edit the issue sidebar to complete categorisation. See docs/REPOSITORY-STANDARDS.md.")
    else:
        print("Issue type, category labels and milestone are present.")
    return bool(errors)


if __name__ == "__main__":
    sys.exit(main())
