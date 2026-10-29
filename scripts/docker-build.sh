#!/bin/sh
# Builds the container image from any checkout. A git worktree's .git only points into the main repository, so that
# repository is passed as the "git" build context (see the Dockerfile); the build computes the version from it.
# Extra arguments go to docker build, for example: scripts/docker-build.sh -t genonlineservice:dev
set -eu
cd "$(dirname "$0")/.."
if [ -f .git ]; then
  exec docker build --build-context git="$(git rev-parse --path-format=absolute --git-common-dir)" "$@" .
fi
exec docker build "$@" .
