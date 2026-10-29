#!/usr/bin/env bash
# Fails when the version the build computed (X.Y.Z+[<distance>.]<commit>) does not match the checked-out commit,
# for example when git or the tags were missing, or, on a vX.Y.Z tag, when it is not exactly that release.
set -euo pipefail
version="$1"
commit="$(git rev-parse --short=8 HEAD)"
expected="${commit}"
if [[ "${GITHUB_REF:-}" == refs/tags/v* ]]; then expected="${GITHUB_REF_NAME#v}+${commit}"; fi
if [[ "${version}" != *"${expected}" ]] || [[ "${version}" != *[+.]"${commit}" ]]; then
  echo "::error::The build reports version ${version}, expected one ending in ${expected}" >&2
  exit 1
fi
echo "Version ${version}"
