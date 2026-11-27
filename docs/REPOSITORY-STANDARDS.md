# Repository standards

The DashyNMS maintainer, [@teacupuk](https://github.com/teacupuk), maintains the
canonical policy, community files and shared helpers in
[DashyNMS/.github](https://github.com/DashyNMS/.github). Each application repository
records its reviewed source revision and file hashes in
`.github/repository-standards.json`. CI checks local copies, so it does not run a
mutable script downloaded from another repository's main branch.

## Local checks

Use Python 3.12 or newer in a virtual environment. Install
`requirements-security.txt`, then run:

```sh
python -m unittest discover -s scripts/tests -v
python scripts/repository_policy.py
python scripts/security_check.py
```

In the shared `.github` source repository, add `--canonical` to the policy check.
For .NET repositories, `python scripts/security_check.py --nuget` also restores
the projects listed in `.github/repository-config.json`. Mobile's audit runner
needs its Android workload. Existing iOS jobs audit their own platform restores,
including the native PDF test project. The security workflow does not change
package pins, NuGet lock files or workload-set selection.

## Security gates

Every PR must reference an earlier issue describing the bug or intended change.
CI checks the issue exists and predates new PRs. Existing PRs still need an issue,
but the creation-order rule starts at the adoption time recorded in
`.github/repository-config.json`; old PRs cannot acquire an earlier history. Each repository has an earlier,
ongoing dependency-maintenance issue. A narrowly scoped `pull_request_target`
workflow links that issue to new Dependabot PRs using
`DEPENDENCY_MAINTENANCE_ISSUE`; it only edits metadata and never checks out PR code.
The policy check validates the configured earlier maintenance issue directly for
Dependabot, so it does not depend on the body edit or another workflow run.
Edits made with GITHUB_TOKEN do not trigger a replacement run.

Every PR's required `CI result` depends on repository policy and security checks.
The security workflow also runs on main and weekly. Edge, nightly and production
builds call CI before publishing. Sensor upstream/manual publication explicitly
depends on security, including retries that only promote a previously built
catalogue. Its feed activation still uses a normal fast-forward push.

NuGet audits include transitive dependencies and fail on high/critical advisories
or unavailable audit metadata. Every attempted NuGet restore records its result
in `artifacts/security/nuget-audit.json`, including failed restores. Python and
secret checks still run after a NuGet failure, while the security gate stays
failed; cancellation stops further work. pip-audit checks every tracked
requirements file
and fails on unresolved advisories or resolution errors. Bandit checks Python
helpers at medium/high severity. CodeQL uses `security-extended` on every
configured language. GitHub secret scanning and push protection stay enabled.
Code-scanning merge protection in the four PR-protected repositories blocks new
high/critical CodeQL findings; a successful scan/upload alone does not prove the
absence of findings. Sensor uses workflow publication gates to preserve its
approved direct feed updates. The website uses GitHub Pages branch publishing.
Required PR checks protect its `main` branch before Pages receives changes.
Its existing HTTPS custom domain and `github-pages` environment stay in use.

The CI secret scan compares candidate hashes with `.secrets.baseline`. Existing
entries are reviewed synthetic fixtures, public integrity hashes or scanner false
positives; the recorded reason explains each entry. New values or files fail.
Review the source before accepting a candidate. Never regenerate the baseline to
make a failing check pass. A real credential must be removed and rotated, then
reported privately. Reports contain hashes and locations, not candidate values;
keep any wider investigation private.

Workflow checks require full SHA pins, read-only default permissions and
`persist-credentials: false` for ordinary checkouts. Sensor's two publishing jobs
retain credentials explicitly for their Git tag/feed pushes. The wiki workflow
authenticates its own separate clone. Tokens passed through environment variables
or action inputs still need their own least-privilege permissions.

Configure `commit-message.prefix: build(deps)` in every Dependabot ecosystem.
The explicit prefix keeps the scope consistent across single and grouped updates.
The title check recognises generated bump and update summaries from GitHub's
Dependabot account, including capitalised summaries and the security marker.
It also recognises the earlier `build:` and `build(deps-dev):` forms for
existing Dependabot PRs. Check the next new Dependabot PR after adoption and
record its original title and Security result in the adoption issue.

Dependabot updates the shared scanner requirements in `DashyNMS/.github`.
Consumer copies are owned by that shared source and move through the same
reviewed distribution step as the scripts; their vulnerability audits still run
on every PR and schedule. A consumer update to a shared scanner pin must update
the canonical source and parity lock too.

## Updating shared files

Change and test the canonical files in a `.github` topic branch first. Commit
that reviewed revision, then distribute it from the shared checkout:

```sh
python scripts/sync_standards.py ../core ../desktop ../mobile ../mcp ../sensor-definitions ../website
```

The command updates local files and immutable source/hash records. Review and
open PRs for the shared source and every affected consumer. Push the source
commit before consumer CI runs; merge the shared community defaults first.
`build_version.py`, the wiki publisher and release-asset helper have one maintained
source and parity records where used. Sensor's catalogue-specific version script
remains independent. Changes to a helper must include its affected tests.

Repository-specific architecture, contribution policy, build instructions,
accessibility scope and release exceptions stay in the owning repository. The
common CLAUDE conventions are maintained in `policy/claude-conventions.md`; its
context pointers lead agents to the detailed instructions they need. Keep human
contribution guidance readable without consulting agent instructions.

## Community and labels

Issue forms, the PR template, code of conduct and ownership rules share a source.
Local contribution/accessibility/security files explain each product. Conduct
reports go to the existing project contact, `dashynms@pckp.net`; vulnerabilities
use the affected repository's private reporting page. The maintainer handles both.

`policy/labels.json` defines common names, colours and descriptions. From the
shared checkout, preview a label sync with
`python scripts/sync_labels.py core desktop mobile mcp sensor-definitions website`;
add `--apply` to write the changes. It preserves extra labels and updates existing
ecosystem descriptions. Case changes keep existing assignments. Merging different
labels or deleting stale labels requires a separate usage review, so the command
never deletes labels or strips classifications from issues.

CODEOWNERS identifies the maintainer without requiring self-approval. Public
security labels are only for safely disclosed work. Contributor Covenant retains
its upstream attribution and licence. Application repositories use MIT for
project-owned material. The website reserves all rights to its original
material. Preserve third-party and data licences in every repository. Mobile's curated What's New and the
coordinated production-package pin policy are intentional exceptions.

## Issue categorisation

Every new issue needs an organisation issue type (usually Bug, Feature or Task),
at least one relevant category or topic label, and a deliberately assigned
milestone. Choose a release milestone when the work is committed to that release.
Use **Maintenance** for recurring work and **Unscheduled** for work awaiting a
release decision. Set up those milestones in the repository before using them.
Review label relevance during triage. Workflow status labels alone are insufficient.

Bug and feature forms supply a type and starter label and ask for a proposed
milestone. Maintainers apply the actual metadata when contributors lack sidebar
access. A proposal in the body cannot fill a sidebar field.

People, agents and automation use the same rules. For API creation, send the
type name, existing label names and repository-local milestone number with the
create request. Read the returned issue and verify all three fields. GitHub can
drop fields the caller lacks permission to set. Treat missing fields as a failed
filing and correct the same issue before proceeding. For example, after choosing
the metadata and creating the issue, verify it with:

```sh
gh api "repos/$REPOSITORY/issues/$ISSUE_NUMBER" | python3 scripts/issue_metadata.py
```

The read-only **Issue metadata** workflow checks newly opened or reclassified
issues and lists missing fields as errors in the Actions run. Update the sidebar
to resolve them. Run the workflow manually with the issue number to recheck
metadata. Workflows creating issues with `GITHUB_TOKEN` must run the verifier
themselves because GitHub suppresses most webhook-triggered runs for that token.
