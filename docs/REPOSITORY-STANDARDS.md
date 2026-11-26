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
The first CI run may be superseded by the body-edit run that adds the reference.

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
approved direct feed updates.

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

Dependabot updates the shared scanner requirements in `DashyNMS/.github`.
Consumer copies are owned by that shared source and move through the same
reviewed distribution step as the scripts; their vulnerability audits still run
on every PR and schedule. A consumer update to a shared scanner pin must update
the canonical source and parity lock too.

## Updating shared files

Change and test the canonical files in a `.github` topic branch first. Commit
that reviewed revision, then distribute it from the shared checkout:

```sh
python scripts/sync_standards.py ../core ../desktop ../mobile ../mcp ../sensor-definitions
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

`policy/labels.json` defines common names, colours and descriptions. Preview a
label sync with `python scripts/sync_labels.py core desktop mobile mcp sensor-definitions`;
add `--apply` to write the changes. It preserves extra labels and updates existing
ecosystem descriptions. Case changes keep existing assignments. Merging different
labels or deleting stale labels requires a separate usage review, so the command
never deletes labels or strips classifications from issues.

CODEOWNERS identifies the maintainer without requiring self-approval. Public
security labels are only for safely disclosed work. Contributor Covenant retains
its upstream attribution and licence; original project material uses MIT and
third-party/data licences remain intact. Mobile's curated What's New and the
coordinated production-package pin policy are intentional exceptions.
