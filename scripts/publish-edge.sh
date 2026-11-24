#!/usr/bin/env bash
# Puts an edge build's files on the rolling edge release, so they can be
# downloaded without signing in. The same file in every DashyNMS repository
# that publishes files.
#
#   publish-edge.sh <version> <commit> <files...>
#
# There is one such release, tagged v0.0.0-edge: apps from before the update
# channels read any tag as a version, and this ranks below every real one.
# Its title is the build's version. New files go up before the title moves and
# old ones come down after, so whoever reads it always finds a whole build. A
# retry keeps the files it already put up, and the first one is a draft until then.
#
# Needs: gh with contents: write (GH_TOKEN, GH_REPO).
set -euo pipefail

version=$1 commit=$2
shift 2
tag=v0.0.0-edge

if ! gh release view "$tag" >/dev/null 2>&1; then
  gh release create "$tag" --draft --target "$commit" --prerelease --latest=false --title "$version" --notes "Edge builds"
fi

# Builds can finish out of order: never replace a newer one.
current=$(gh release view "$tag" --json name --jq .name)
newest=$(printf '%s\n%s\n' "$current" "$version" | sort -V | tail -n 1)
if [ "$newest" != "$version" ]; then
  echo "::notice::The edge release already has $current, which is newer than $version."
  exit 0
fi

old=$(gh release view "$tag" --json assets --jq '.assets[].name')
python3 "$(dirname "$0")/upload-missing-release-assets.py" "$tag" "$@"
gh release edit "$tag" --title "$version" --draft=false --prerelease --latest=false \
  --notes "The newest build of \`main\`, from \`$commit\`. It's replaced by every change and may not work: choose a nightly build or a release unless you're testing a change.

What's in it: [release notes](https://github.com/$GH_REPO/blob/$commit/docs/releases/${version%%-*}.md)."
gh api -X PATCH "repos/$GH_REPO/git/refs/tags/$tag" -f sha="$commit" -F force=true --silent

new=$(for file in "$@"; do basename "$file"; done)
while IFS= read -r name; do
  if [ -n "$name" ] && ! grep -qxF "$name" <<<"$new"; then
    gh release delete-asset "$tag" "$name" --yes
  fi
done <<<"$old"
echo "Edge release: $version" >> "${GITHUB_STEP_SUMMARY:-/dev/stdout}"
