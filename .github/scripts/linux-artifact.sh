#!/usr/bin/env bash
set -euo pipefail
export LC_ALL=C
umask 077
unset GZIP TAR_OPTIONS
export GIT_ALLOW_PROTOCOL='' GIT_NO_LAZY_FETCH=1 GIT_NO_REPLACE_OBJECTS=1
shopt -u nocasematch

fail() {
  printf 'netdeck_artifact status=failed reason=%s\n' "$1" >&2
  exit "${2:-1}"
}

verify_elf_metadata() {
  local binary=$1 size mode
  size=$(stat -c '%s' -- "$binary" 2>/dev/null) || fail publish_output
  mode=$(stat -c '%a' -- "$binary" 2>/dev/null) || fail publish_output
  [[ $size =~ ^[0-9]+$ && $size -ge 64 && $mode =~ ^[0-7]+$ ]] || fail publish_output
  (( (8#$mode & 0111) != 0 )) || fail publish_output
}

verify_elf_bytes() {
  local binary=$1 header machine
  header=$(od -An -tx1 -N6 -- "$binary" 2>/dev/null | tr -d ' \n') || fail publish_output
  machine=$(od -An -tx1 -j18 -N2 -- "$binary" 2>/dev/null | tr -d ' \n') || fail publish_output
  [[ $header == 7f454c460201 && $machine == 3e00 ]] || fail publish_output
}

verify_elf() {
  local dir=$1 binary=$1/dotnet-deck
  [[ -d $dir && ! -L $dir && -f $binary && ! -L $binary ]] || fail publish_output
  verify_elf_metadata "$binary"
  verify_elf_bytes "$binary"
}

verify_publish_layout() {
  local publish=$1 entry
  shopt -s dotglob nullglob
  for entry in "$publish"/*; do
    case ${entry##*/} in
      dotnet-deck) ;;
      dotnet-deck.dbg) [[ -f $entry && ! -L $entry ]] || fail publish_layout ;;
      *) fail publish_layout ;;
    esac
  done
}

ci_ref_kind() {
  local event=$1 ref=$2
  [[ $event != *:* ]] || return 1
  case $event:$ref in
    push:refs/tags/*) printf 'tag\n' ;;
    pull_request:refs/pull/*)
      [[ $ref =~ ^refs/pull/[1-9][0-9]*/merge$ ]] && printf 'pull\n' ;;
    push:refs/heads/?*|workflow_dispatch:refs/heads/?*)
      git check-ref-format "$ref" >/dev/null 2>&1 && printf 'branch\n' ;;
    workflow_dispatch:refs/tags/?*)
      git check-ref-format "$ref" >/dev/null 2>&1 && printf 'tag\n' ;;
    *) return 1 ;;
  esac
}

read_ci_identity() {
  event=${GITHUB_EVENT_NAME:-} ref=${GITHUB_REF:-} event_sha=${GITHUB_SHA:-}
  run_number=${GITHUB_RUN_NUMBER:-} run_id=${GITHUB_RUN_ID:-} attempt=${GITHUB_RUN_ATTEMPT:-}
  [[ $event_sha =~ ^[0-9a-f]{40}$ && $run_number =~ ^[1-9][0-9]{0,19}$ &&
     $run_id =~ ^[1-9][0-9]{0,19}$ && $attempt =~ ^[1-9][0-9]{0,19}$ ]] || fail source_identity
  ref_kind=$(ci_ref_kind "$event" "$ref") || fail source_identity
}

read_checkout_head() {
  local state
  state=$(git rev-parse --is-inside-work-tree 2>/dev/null) || fail source_identity
  [[ $state == true ]] || fail source_identity
  sha=$(git rev-parse --verify --end-of-options 'HEAD^{commit}' 2>/dev/null) || fail source_identity
  [[ $sha =~ ^[0-9a-f]{40}$ ]] || fail source_identity
  state=$(git rev-parse --is-shallow-repository 2>/dev/null) || fail source_history
  [[ $state == false ]] || fail source_history
  [[ $ref_kind == tag || $event_sha == "$sha" ]] || fail source_identity
}

parse_version_tag() {
  local input=$1 normalized prerelease identifier
  local -a identifiers
  (( ${#input} >= 1 && ${#input} <= 120 )) || return 1
  normalized=${input#v}
  [[ $normalized =~ ^(0|[1-9][0-9]{0,9})\.(0|[1-9][0-9]{0,9})\.(0|[1-9][0-9]{0,9})(-([0-9A-Za-z-]+(\.[0-9A-Za-z-]+)*))?(\+([0-9A-Za-z-]+(\.[0-9A-Za-z-]+)*))?$ ]] || return 1
  tag_core="${BASH_REMATCH[1]}.${BASH_REMATCH[2]}.${BASH_REMATCH[3]}"
  prerelease=${BASH_REMATCH[5]}
  IFS=. read -r -a identifiers <<< "$prerelease"
  for identifier in "${identifiers[@]}"; do
    [[ ! $identifier =~ ^0[0-9]+$ ]] || return 1
  done
  tag_version=$normalized tag_pre=$prerelease
}

tag_commit() {
  local oid=$1 peeled type
  [[ $oid =~ ^[0-9a-f]{40}$ ]] || return 2
  peeled=$(git rev-parse --verify --end-of-options "$oid^{}" 2>/dev/null) || return 2
  [[ $peeled =~ ^[0-9a-f]{40}$ ]] || return 2
  type=$(git cat-file -t "$peeled" 2>/dev/null) || return 2
  case $type in
    commit) printf '%s\n' "$peeled" ;;
    blob|tree) return 1 ;;
    *) return 2 ;;
  esac
}

require_exact_tag() {
  local raw target
  raw=$(git show-ref --hash --verify -- "$1" 2>/dev/null) || fail tag_target
  target=$(tag_commit "$raw") || fail tag_target
  [[ $target == "$sha" ]] || fail tag_target
  [[ $event_sha == "$sha" || $event_sha == "$raw" ]] || fail source_identity
}

core_greater() {
  local -a left right
  local i
  IFS=. read -r -a left <<< "$1"
  IFS=. read -r -a right <<< "$2"
  for i in 0 1 2; do
    (( ${#left[i]} > ${#right[i]} )) && return 0
    (( ${#left[i]} < ${#right[i]} )) && return 1
    [[ ${left[i]} > ${right[i]} ]] && return 0
    [[ ${left[i]} < ${right[i]} ]] && return 1
  done
  return 1
}

dev_consider_tag() {
  local target status
  parse_version_tag "${1#refs/tags/}" || return 0
  if target=$(tag_commit "$2"); then :; else
    status=$?
    (( status == 1 )) && return 0
    fail source_history
  fi
  if git merge-base --is-ancestor "$target" "$sha" 2>/dev/null; then :; else
    status=$?
    (( status == 1 )) && return 0
    fail source_history
  fi
  if core_greater "$tag_core" "$base_core"; then
    base_core=$tag_core base_tips=("$target")
  elif [[ $tag_core == "$base_core" ]]; then
    base_tips+=("$target")
  fi
}

dev_core() {
  local row_ref oid
  while IFS=$'\t' read -r row_ref oid; do
    [[ -n $row_ref ]] || continue
    [[ $row_ref == refs/tags/* && $oid =~ ^[0-9a-f]{40}$ ]] || fail source_history
    dev_consider_tag "$row_ref" "$oid"
  done <<< "$1"
}

release_consider_tag() {
  local target status
  [[ $1 != "$ref" ]] || return 0
  parse_version_tag "${1#refs/tags/}" || return 0
  [[ $tag_core == "$release_core" && $tag_pre == "$release_pre" ]] || return 0
  if target=$(tag_commit "$2"); then :; else
    status=$?
    (( status == 1 )) && return 0
    fail source_history
  fi
  [[ $target != "$sha" ]] || fail tag_conflict
}

require_unique_release_tag() {
  local row_ref oid
  while IFS=$'\t' read -r row_ref oid; do
    [[ -n $row_ref ]] || continue
    [[ $row_ref == refs/tags/* && $oid =~ ^[0-9a-f]{40}$ ]] || fail source_history
    release_consider_tag "$row_ref" "$oid"
  done <<< "$1"
}

commit_bump() {
  { if (( ${#base_tips[@]} )); then printf '^%s\n' "${base_tips[@]}"; fi; } |
    git log --stdin --no-notes --no-show-signature --no-decorate --no-color --encoding=UTF-8 --format=%B "$1" -- 2>/dev/null |
    awk '/^semver: major$/ { rank=3 } /^semver: minor$/ && rank<2 { rank=2 }
         /^semver: (patch|path)$/ && rank<1 { rank=1 }
         END { if (rank==3) print "major"; else if (rank==2) print "minor";
               else if (rank==1) print "patch"; else print "none" }'
}

apply_core_bump() {
  local major minor patch
  IFS=. read -r major minor patch <<< "$1"
  case $2 in
    major) [[ $major != 9999999999 ]] || fail version_overflow; major=$((major + 1)); minor=0; patch=0 ;;
    minor) [[ $minor != 9999999999 ]] || fail version_overflow; minor=$((minor + 1)); patch=0 ;;
    patch) [[ $patch != 9999999999 ]] || fail version_overflow; patch=$((patch + 1)) ;;
    none) ;;
    *) fail source_history ;;
  esac
  printf '%s.%s.%s\n' "$major" "$minor" "$patch"
}

# Identity is established before any staging path is claimed.
package_identity() {
  local event ref event_sha ref_kind tag_version tag_core tag_pre release_core release_pre
  local rows base_core=0.0.0 directive bumped
  local -a base_tips=()
  read_ci_identity
  read_checkout_head
  if [[ $event == push && $ref_kind == tag ]]; then
    parse_version_tag "${ref#refs/tags/}" || fail tag_invalid
    version=$tag_version release_core=$tag_core release_pre=$tag_pre
  fi
  [[ $ref_kind != tag ]] || require_exact_tag "$ref"
  rows=$(git for-each-ref --format='%(refname)%09%(objectname)' refs/tags 2>/dev/null) || fail source_history
  if [[ $event == push && $ref_kind == tag ]]; then
    require_unique_release_tag "$rows"
    name="dotnet-deck-$version-linux-x64-run.$run_id.attempt.$attempt-g$sha"
  else
    dev_core "$rows"
    directive=$(commit_bump "$sha") || fail source_history
    bumped=$(apply_core_bump "$base_core" "$directive") || exit 1
    version="$bumped-ci.$run_number.$attempt+g$sha"
    name="dotnet-deck-$version-linux-x64-run.$run_id"
  fi
}

package_cleanup() {
  if [[ -n $scratch ]]; then rm -rf -- "$scratch" 2>/dev/null || :; fi
  if (( owned && ! success )); then rm -rf -- "$stage" 2>/dev/null || :; fi
}

package_claim_stage() {
  [[ ! -e $stage && ! -L $stage ]] || fail stage_conflict
  if ! mkdir -m 0700 -- "$stage" 2>/dev/null; then
    [[ ! -e $stage && ! -L $stage ]] || fail stage_conflict
    fail package_io
  fi
  owned=1
  scratch=$(mktemp -d -- "${stage}.scratch.XXXXXXXX" 2>/dev/null) || fail package_io
}

package_prepare_members() {
  cp -- "$publish/dotnet-deck" "$scratch/dotnet-deck" 2>/dev/null || fail package_io
  chmod 0755 -- "$scratch/dotnet-deck" 2>/dev/null || fail package_io
  { printf '{"schema_version":1,"product":"dotnet-deck","version":"%s","commit":"%s","rid":"linux-x64","sdk_version":"10.0.115","run_id":"%s","run_number":"%s","run_attempt":"%s"}\n' \
    "$version" "$sha" "$run_id" "$run_number" "$attempt" > "$scratch/manifest.json"; } 2>/dev/null || fail package_io
  chmod 0644 -- "$scratch/manifest.json" 2>/dev/null || fail package_io
}

package_archive() {
  if ! (tar --format=ustar --numeric-owner --owner=0 --group=0 --mtime=@0 -C "$scratch" -cf - -- dotnet-deck manifest.json | gzip -n > "$stage/dotnet-deck.tar.gz") 2>/dev/null; then
    fail package_io
  fi
  digest=$(sha256sum 2>/dev/null < "$stage/dotnet-deck.tar.gz") || fail package_io
  digest=${digest%% *}
  [[ $digest =~ ^[0-9a-f]{64}$ ]] || fail package_io
  { printf '%s  dotnet-deck.tar.gz\n' "$digest" > "$stage/dotnet-deck.tar.gz.sha256"; } 2>/dev/null || fail package_io
}

package_verify_staged_file() {
  [[ -f $1 && ! -L $1 && -s $1 ]] || fail package_io
}

package() {
  local publish=$1 stage=$2 sha run_number run_id attempt version name scratch='' digest
  local owned=0 success=0
  verify_elf "$publish"
  verify_publish_layout "$publish"
  package_identity
  # Only remove directories successfully created by this invocation.
  trap package_cleanup EXIT
  package_claim_stage
  package_prepare_members
  package_archive
  package_verify_staged_file "$stage/dotnet-deck.tar.gz"
  package_verify_staged_file "$stage/dotnet-deck.tar.gz.sha256"
  rm -rf -- "$scratch" 2>/dev/null || fail package_io
  scratch=''
  success=1
  trap - EXIT
  printf 'artifact_name=%s\narchive_sha256=%s\n' "$name" "$digest"
}

verify_readback_file() {
  [[ -f $1 && ! -L $1 && -s $1 ]] || fail readback_layout
}

verify_readback_layout() {
  local dir=$1 entry count=0
  [[ -d $dir && ! -L $dir ]] || fail readback_layout
  shopt -s dotglob nullglob
  for entry in "$dir"/*; do
    case ${entry##*/} in dotnet-deck.tar.gz|dotnet-deck.tar.gz.sha256) ;; *) fail readback_layout ;; esac
    verify_readback_file "$entry"
    count=$((count + 1))
  done
  (( count == 2 )) || fail readback_layout
}

verify_readback_integrity() {
  local dir=$1 expected=$2 digest
  [[ $expected =~ ^[0-9a-f]{64}$ ]] || fail readback_integrity
  digest=$(sha256sum 2>/dev/null < "$dir/dotnet-deck.tar.gz") || fail readback_integrity
  [[ ${digest%% *} == "$expected" ]] || fail readback_integrity
  printf '%s  dotnet-deck.tar.gz\n' "$expected" | cmp -s -- - "$dir/dotnet-deck.tar.gz.sha256" || fail readback_integrity
}

verify_readback() {
  verify_readback_layout "$1"
  verify_readback_integrity "$1" "$2"
}

case ${1:-} in
  verify-elf) [[ $# == 2 ]] || fail invocation 2; verify_elf "$2" ;;
  package) [[ $# == 3 ]] || fail invocation 2; package "$2" "$3" ;;
  validate-id) [[ $# == 2 ]] || fail invocation 2; [[ $2 =~ ^[1-9][0-9]{0,14}$ ]] || fail upload_id ;;
  verify-readback) [[ $# == 3 ]] || fail invocation 2; verify_readback "$2" "$3" ;;
  *) fail invocation 2 ;;
esac
