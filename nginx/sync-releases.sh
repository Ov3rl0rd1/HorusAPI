#!/bin/sh
# Mirrors the newest Horus client release from GitHub into the nginx document
# tree, so the site hands out the binaries itself instead of linking to
# github.com (which is exactly the kind of outbound link the audience often
# cannot reach). Runs on container start and every RELEASE_SYNC_INTERVAL
# seconds after that — see entrypoint.sh.
#
# Layout it maintains under $ROOT (a Docker volume, so it survives rebuilds):
#
#   releases/v0.1.0/files/Horus-0.1.0-win-x64.msi   real asset, exact name
#   releases/v0.1.0/Horus-win-x64.msi -> files/…    version-free stable name
#   releases/v0.1.0/latest.json                     manifest for the frontend
#   current -> releases/v0.1.0                      swapped atomically at the end
#
# nginx serves $ROOT/current, so /download/Horus-win-x64.msi is always the
# newest build and never changes URL. A half-finished sync stays invisible: the
# release is assembled in a temp dir and only then moved into place.
set -eu

REPO="${RELEASE_REPO:-Ov3rl0rd1/Horus-Release}"
ROOT="${RELEASE_DIR:-/var/www/downloads}"
# The current release is published as a prerelease, and /releases/latest skips
# those — hence the list endpoint plus a filter we can turn off later.
INCLUDE_PRERELEASE="${RELEASE_INCLUDE_PRERELEASE:-1}"
API="https://api.github.com/repos/${REPO}/releases?per_page=20"

log() { echo "[releases] $*"; }

# Asset name → "<manifest key> <stable name>". Anything not listed here (debug
# symbols, certificate thumbprint) stays on GitHub and is never mirrored.
meta_for() {
    case "$1" in
        *-win-x64.msi)           echo "win-msi Horus-win-x64.msi" ;;
        *-win-x64-portable.zip)  echo "win-zip Horus-win-x64-portable.zip" ;;
        *-android-arm64-v8a.apk) echo "android-arm64 Horus-android-arm64-v8a.apk" ;;
        *-android-x86_64.apk)    echo "android-x86_64 Horus-android-x86_64.apk" ;;
        SHA256SUMS.txt)          echo "sha256sums SHA256SUMS.txt" ;;
        *)                       echo "" ;;
    esac
}

TAB="$(printf '\t')"

mkdir -p "$ROOT/releases"

# curl options live in a file so a token never shows up in the process list.
CURL_CFG="$(mktemp)"
trap 'rm -f "$CURL_CFG"' EXIT
{
    echo 'user-agent = "horus-release-sync"'
    printf 'silent\nshow-error\nfail\nlocation\nretry = 3\nretry-delay = 3\n'
    if [ -n "${GITHUB_TOKEN:-}" ]; then
        printf 'header = "Authorization: Bearer %s"\n' "$GITHUB_TOKEN"
    fi
} > "$CURL_CFG"

# ── Pick the release to mirror ────────────────────────────────
if ! api_json="$(curl -K "$CURL_CFG" -m 60 -H 'Accept: application/vnd.github+json' "$API" < /dev/null)"; then
    log "GitHub API unreachable — keeping the mirror as it is"
    exit 0
fi

if [ "$INCLUDE_PRERELEASE" = "1" ]; then PRE=true; else PRE=false; fi
release="$(printf '%s' "$api_json" | jq -c --argjson pre "$PRE" '
    [ .[] | select(.draft == false) | select($pre or (.prerelease == false)) ]
    | sort_by(.published_at) | reverse | .[0] // empty')"

if [ -z "$release" ]; then
    log "no published release in ${REPO} — nothing to mirror"
    exit 0
fi

tag="$(printf '%s' "$release" | jq -r '.tag_name')"
published="$(printf '%s' "$release" | jq -r '.published_at // ""')"
version="${tag#v}"
# The tag becomes a directory name — keep it to characters we chose.
tag_dir="$(printf '%s' "$tag" | tr -c 'A-Za-z0-9._-' '_')"
dest="$ROOT/releases/$tag_dir"

if [ -f "$dest/.complete" ] && [ "$(readlink "$ROOT/current" 2>/dev/null || true)" = "releases/$tag_dir" ]; then
    log "$tag already mirrored"
    exit 0
fi

log "mirroring $tag from $REPO"

tmp="$ROOT/.tmp-$tag_dir"
rm -rf "$tmp"
mkdir -p "$tmp/files"

printf '%s' "$release" | jq -r '.assets[] | [.name, .browser_download_url] | @tsv' > "$tmp/assets.tsv"

# ── Download the assets we publish ────────────────────────────
: > "$tmp/wanted.tsv"
while IFS="$TAB" read -r name url; do
    meta="$(meta_for "$name")"
    if [ -z "$meta" ]; then
        continue
    fi
    key="${meta% *}"
    stable="${meta#* }"

    log "  downloading $name"
    if ! curl -K "$CURL_CFG" -m 1800 -o "$tmp/files/$name" "$url" < /dev/null; then
        log "download failed for $name — aborting, mirror left untouched"
        rm -rf "$tmp"
        exit 1
    fi
    printf '%s\t%s\t%s\n' "$key" "$name" "$stable" >> "$tmp/wanted.tsv"
done < "$tmp/assets.tsv"

if [ ! -s "$tmp/wanted.tsv" ]; then
    log "$tag carries none of the expected assets — mirror left untouched"
    rm -rf "$tmp"
    exit 1
fi

# ── Verify against the release's own SHA256SUMS.txt ───────────
# Only the lines for files we actually pulled: the sums file also covers assets
# we skip, and busybox sha256sum -c fails on any missing entry.
if [ -f "$tmp/files/SHA256SUMS.txt" ]; then
    if (
        cd "$tmp/files"
        : > ../sums.check
        while IFS="$TAB" read -r _ name _; do
            if [ "$name" != "SHA256SUMS.txt" ]; then
                awk -v f="$name" '$2 == f || $2 == "*" f' SHA256SUMS.txt >> ../sums.check
            fi
        done < ../wanted.tsv
        if [ -s ../sums.check ]; then
            sha256sum -c ../sums.check > /dev/null
        else
            echo "[releases] SHA256SUMS.txt lists none of the assets — skipping verification"
        fi
    ); then
        log "  checksums ok"
    else
        log "checksum verification failed — mirror left untouched"
        rm -rf "$tmp"
        exit 1
    fi
fi

# ── Stable names + manifest ───────────────────────────────────
: > "$tmp/manifest.tsv"
while IFS="$TAB" read -r key name stable; do
    ln -s "files/$name" "$tmp/$stable"
    size="$(wc -c < "$tmp/files/$name" | tr -d ' ')"
    sum="$(sha256sum "$tmp/files/$name" | cut -d' ' -f1)"
    printf '%s\t%s\t%s\t%s\t%s\n' "$key" "$name" "$stable" "$size" "$sum" >> "$tmp/manifest.tsv"
done < "$tmp/wanted.tsv"

jq -n \
    --arg tag "$tag" \
    --arg version "$version" \
    --arg published "$published" \
    --arg synced "$(date -u +%Y-%m-%dT%H:%M:%SZ)" \
    --rawfile rows "$tmp/manifest.tsv" '
    {
      tag: $tag,
      version: $version,
      published_at: $published,
      synced_at: $synced,
      files: ($rows | rtrimstr("\n") | split("\n") | map(select(length > 0) | split("\t") | {
        key:       .[0],
        name:      .[1],
        url:       ("/download/" + .[2]),
        exact_url: ("/download/files/" + .[1]),
        size:      (.[3] | tonumber),
        sha256:    .[4]
      }))
    }' > "$tmp/latest.json"

rm -f "$tmp/assets.tsv" "$tmp/wanted.tsv" "$tmp/manifest.tsv" "$tmp/sums.check"
touch "$tmp/.complete"

# ── Publish: rename into place, then flip the symlink ─────────
rm -rf "$dest"
mv "$tmp" "$dest"
ln -sfn "releases/$tag_dir" "$ROOT/.current.new"
mv -f "$ROOT/.current.new" "$ROOT/current"

# ── Drop everything the site no longer serves ─────────────────
for old in "$ROOT"/releases/*; do
    if [ -d "$old" ] && [ "$old" != "$dest" ]; then
        log "  removing old mirror $(basename "$old")"
        rm -rf "$old"
    fi
done
rm -rf "$ROOT"/.tmp-*

log "now serving $tag"
