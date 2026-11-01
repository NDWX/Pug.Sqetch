#!/usr/bin/env bash
# Installs a sqetch-deploy release for the GitHub Actions runner it runs on and puts it on PATH.
# action.yml runs it; everything it needs comes from the environment:
#
#   SQETCH_DEPLOY_VERSION   the version, X.Y.Z or X.Y.Z-pre.<stamp>
#   RUNNER_OS, RUNNER_ARCH, RUNNER_TOOL_CACHE, RUNNER_TEMP, GITHUB_PATH, GITHUB_OUTPUT   the runner's
#   SQETCH_RELEASES_URL     replaces https://github.com/NDWX/Pug.Sqetch/releases/download; for testing
#
# The release goes to the runner's tool cache, <tool cache>/sqetch-deploy/<version>/<arch>, with an
# <arch>.complete marker beside it once it is whole, so a self-hosted runner downloads a version once.
set -euo pipefail

name=sqetch-deploy
version="${SQETCH_DEPLOY_VERSION:-}"
releases="${SQETCH_RELEASES_URL:-https://github.com/NDWX/Pug.Sqetch/releases/download}"

fail()
{
	echo "::error title=install-sqetch-deploy::$*" >&2
	exit 1
}

# the tag a version is released under: a pre-release tag names only X.Y.Z
if [[ "$version" =~ ^([0-9]+\.[0-9]+\.[0-9]+)$ ]]; then
	tag="sqetch_deploy-v${BASH_REMATCH[1]}"
elif [[ "$version" =~ ^([0-9]+\.[0-9]+\.[0-9]+)-pre\.[0-9]+$ ]]; then
	tag="sqetch_deploy-pre-v${BASH_REMATCH[1]}"
else
	fail "version '$version' is not X.Y.Z or X.Y.Z-pre.<stamp>"
fi

case "${RUNNER_OS:-}" in
	Linux) os=linux ext=tar.gz exe="$name" ;;
	macOS) os=osx ext=tar.gz exe="$name" ;;
	Windows) os=win ext=zip exe="$name.exe" ;;
	*) fail "unsupported runner OS '${RUNNER_OS:-}'" ;;
esac

case "${RUNNER_ARCH:-}" in
	X64) arch=x64 ;;
	ARM64) arch=arm64 ;;
	*) fail "unsupported runner architecture '${RUNNER_ARCH:-}'" ;;
esac

# Windows runners hand bash Windows paths; work in Git Bash's form and give the runner its own back
unix_path()
{
	if [[ "$os" == win ]]; then cygpath -u "$1"; else printf '%s' "$1"; fi
}

native_path()
{
	if [[ "$os" == win ]]; then cygpath -w "$1"; else printf '%s' "$1"; fi
}

sha256()
{
	if command -v sha256sum > /dev/null; then
		sha256sum "$1" | cut -d ' ' -f 1
	else
		shasum -a 256 "$1" | cut -d ' ' -f 1
	fi
}

cache="$( unix_path "${RUNNER_TOOL_CACHE:?RUNNER_TOOL_CACHE is not set}" )"
[[ -n "$cache" ]] || fail 'RUNNER_TOOL_CACHE is empty'

archive="$name-$version-$os-$arch.$ext"
target="$cache/$name/$version/$arch"

if [[ -f "$target.complete" ]]; then
	echo "using $name $version from the tool cache"
else
	work="$( mktemp -d "$( unix_path "${RUNNER_TEMP:-${TMPDIR:-/tmp}}" )/$name.XXXXXX" )"
	trap '[[ -n "${work:-}" && -d "$work" ]] && rm -rf -- "$work"' EXIT

	echo "downloading $releases/$tag/$archive"
	curl -fsSL --retry 3 -o "$work/$archive" "$releases/$tag/$archive" ||
		fail "could not download $archive from release $tag; is $version a published sqetch-deploy release?"
	curl -fsSL --retry 3 -o "$work/SHA256SUMS" "$releases/$tag/SHA256SUMS" ||
		fail "could not download SHA256SUMS from release $tag"

	expected="$( awk -v file="$archive" '$2 == file { print $1 }' "$work/SHA256SUMS" )"
	[[ -n "$expected" ]] || fail "SHA256SUMS of release $tag has no line for $archive"

	actual="$( sha256 "$work/$archive" )"
	[[ "$actual" == "$expected" ]] || fail "$archive does not match SHA256SUMS: expected $expected, got $actual"

	mkdir "$work/extract"

	if [[ "$ext" == zip ]]; then
		powershell.exe -NoProfile -NonInteractive -Command \
			"Expand-Archive -LiteralPath '$( native_path "$work/$archive" )' -DestinationPath '$( native_path "$work/extract" )'"
	else
		tar -xzf "$work/$archive" -C "$work/extract"
	fi

	# the archive holds one folder, <name>-<version>, and the native libraries must stay beside the
	# executable, so the folder moves into the cache whole
	[[ -f "$work/extract/$name-$version/$exe" ]] || fail "$archive does not hold $name-$version/$exe"

	mkdir -p "$( dirname "$target" )"

	# a folder without the marker is a run that did not finish
	if [[ -e "$target" ]]; then
		rm -rf -- "$target"
	fi

	mv "$work/extract/$name-$version" "$target"
	touch "$target.complete"
fi

installed="$( "$target/$exe" --version | tr -d '\r' )"
[[ "$installed" == "$version" ]] || fail "$target/$exe reports version '$installed', expected $version"

echo "installed $name $installed in $target"
echo "$( native_path "$target" )" >> "${GITHUB_PATH:?GITHUB_PATH is not set}"

{
	echo "path=$( native_path "$target" )"
	echo "version=$version"
} >> "${GITHUB_OUTPUT:?GITHUB_OUTPUT is not set}"
