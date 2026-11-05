#!/usr/bin/env bash
# Plans the E2E matrix: one job per leg, with the framework the example runs on, the .NET SDK that builds it, and the
# NLog and Newtonsoft.Json versions the leg tests. Prints the matrix as a JSON array, the include list of the test job
# in e2e.yml.
#   floor   .NET Framework 4.8 on Windows, the oldest framework the CI tests the netstandard2.0 build on, with NLog and
#           Newtonsoft.Json as BetterStack.Logs.NLog.csproj references them, the oldest versions the package accepts
#   latest  the newest GA .NET (the newest active or maintained channel in Microsoft's releases index) with the newest
#           NLog and Newtonsoft.Json releases on nuget.org
#   canary  the next .NET channel as a preview or release candidate, once the index lists one (until then the newest
#           GA channel), and a pre-release of NLog or Newtonsoft.Json when one is newer than its newest release
# Env: LEG (all, floor, latest or canary), CLIENT_FROM (checkout or nuget), and DOTNET_SDK_VERSION, NLOG_VERSION,
#      NEWTONSOFT_VERSION: exact versions that replace the planned ones in every leg. An exact SDK brings its own
#      framework (10.0.100 builds and runs the example for net10.0), except on the floor, which stays on .NET Framework.
set -euo pipefail

ROOT=$(cd "$(dirname "$0")/.." && pwd)
RELEASES_INDEX=https://builds.dotnet.microsoft.com/dotnet/release-metadata/releases-index.json
FLATCONTAINER=https://api.nuget.org/v3-flatcontainer
UNQUALIFIED='^[0-9]+(\.[0-9]+)*$'

# The version BetterStack.Logs.NLog.csproj references package $1 at.
csproj_reference() {
  sed -n "s:.*<PackageReference Include=\"$1\" Version=\"\([^\"]*\)\".*:\1:p" "$ROOT/BetterStack.Logs.NLog.csproj"
}

# Every version of package $1 on nuget.org, one per line, as NuGet lists them (lowercase, normalized).
versions() { curl -fsS -m 60 --retry 3 "$FLATCONTAINER/$(tr '[:upper:]' '[:lower:]' <<<"$1")/index.json" | jq -r '.versions[]'; }

# Whether version $1 sorts above version $2.
newer() { [ "$1" != "$2" ] && [ "$(printf '%s\n%s\n' "$1" "$2" | sort -V | tail -n 1)" = "$1" ]; }

# The newest release among the versions on stdin: the highest one without a pre-release label such as -beta2.
newest_release() { grep -E "$UNQUALIFIED" | sort -V | tail -n 1; }

# The newest pre-release above the newest release among the versions on stdin, or that release when there is none.
newest_prerelease() {
  local all release candidate pick=""
  all=$(cat)
  release=$(newest_release <<<"$all")
  while read -r candidate; do
    # 14.0.1-beta2 is a pre-release of 14.0.1: it counts when 14.0.1 is above the newest release.
    if newer "${candidate%%-*}" "$release" && { [ -z "$pick" ] || newer "$candidate" "$pick"; }; then pick=$candidate; fi
  done < <(grep -vE "$UNQUALIFIED" <<<"$all")
  echo "${pick:-$release}"
}

INDEX=$(curl -fsS -m 60 --retry 3 "$RELEASES_INDEX")
# The newest channel whose support phase is one of $1, as {channel, sdk, runtime}.
channel() {
  jq -c --argjson phases "$1" '[.["releases-index"][] | select(.["support-phase"] | IN($phases[]))]
    | max_by(.["channel-version"] | split(".") | map(tonumber))
    | {channel: .["channel-version"], sdk: .["latest-sdk"], runtime: .["latest-runtime"]}' <<<"$INDEX"
}
GA=$(channel '["active", "maintenance"]')
PREVIEW=$(channel '["preview", "go-live"]')
GA_CHANNEL=$(jq -r .channel <<<"$GA")
CANARY_CHANNEL=$GA_CHANNEL
if [ "$(jq -r .channel <<<"$PREVIEW")" != null ] && newer "$(jq -r .channel <<<"$PREVIEW")" "$GA_CHANNEL"; then
  CANARY_CHANNEL=$(jq -r .channel <<<"$PREVIEW")
fi

# The client the example takes: the checkout, packed as 0.0.0-e2e, or the newest release on nuget.org, which the
# example's own version range must pick.
if [ "${CLIENT_FROM:-checkout}" = nuget ]; then
  CLIENT=$(versions BetterStack.Logs.NLog | newest_release)
else
  CLIENT=0.0.0-e2e
fi

LEGS=$([ "${LEG:-all}" = all ] && echo "floor latest canary" || echo "$LEG")
MATRIX='[]'
for leg in $LEGS; do
  # sdk and quality are what actions/setup-dotnet installs; runtime is what the example must run on: an exact .NET
  # version, a channel (any patch or pre-release of it), or the .NET Framework version.
  quality=""
  case "$leg" in
    floor)
      runner=windows-latest
      framework=net48
      runtime=4.8
      sdk=$(jq -r .sdk <<<"$GA")
      nlog=$(csproj_reference NLog)
      newtonsoft=$(csproj_reference Newtonsoft.Json) ;;
    latest)
      runner=ubuntu-latest
      framework=net$GA_CHANNEL
      runtime=$(jq -r .runtime <<<"$GA")
      sdk=$(jq -r .sdk <<<"$GA")
      nlog=$(versions NLog | newest_release)
      newtonsoft=$(versions Newtonsoft.Json | newest_release) ;;
    canary)
      runner=ubuntu-latest
      framework=net$CANARY_CHANNEL
      runtime=$CANARY_CHANNEL
      # The newest preview or release candidate of the channel the feed has, which can be newer than the index.
      sdk=$CANARY_CHANNEL.x
      quality=preview
      nlog=$(versions NLog | newest_prerelease)
      newtonsoft=$(versions Newtonsoft.Json | newest_prerelease) ;;
    *) echo "unknown leg $leg" >&2; exit 1 ;;
  esac
  if [ -n "${DOTNET_SDK_VERSION:-}" ]; then
    sdk=$DOTNET_SDK_VERSION
    quality=""
    if [ "$leg" != floor ]; then
      runtime=$(cut -d. -f1-2 <<<"$sdk")
      framework=net$runtime
    fi
  fi
  nlog=${NLOG_VERSION:-$nlog}
  newtonsoft=${NEWTONSOFT_VERSION:-$newtonsoft}
  for value in "$framework" "$runtime" "$sdk" "$nlog" "$newtonsoft" "$CLIENT"; do
    [ -n "$value" ] && [[ "$value" != *null* ]] || { echo "the $leg leg could not be planned: framework '$framework', runtime '$runtime', sdk '$sdk', NLog '$nlog', Newtonsoft.Json '$newtonsoft', client '$CLIENT'" >&2; exit 1; }
  done
  on=".NET $runtime"
  [ "$leg" != floor ] || on=".NET Framework $runtime"
  [ "$leg" != canary ] || [ "$quality" != preview ] || [ "$CANARY_CHANNEL" = "$GA_CHANNEL" ] || on=".NET $runtime preview"
  MATRIX=$(jq -c --arg leg "$leg" --arg on "$on" --arg runner "$runner" --arg framework "$framework" --arg runtime "$runtime" \
    --arg sdk "$sdk" --arg quality "$quality" --arg nlog "$nlog" --arg newtonsoft "$newtonsoft" \
    --arg source "${CLIENT_FROM:-checkout}" --arg client "$CLIENT" '. + [{
      name: "\($leg) (\($on), NLog \($nlog), Newtonsoft.Json \($newtonsoft))",
      leg: $leg, runner: $runner, framework: $framework, runtime: $runtime, sdk: $sdk, quality: $quality,
      nlog: $nlog, newtonsoft: $newtonsoft, source: $source, client: $client
    }]' <<<"$MATRIX")
done
# Job names become artifact names (e2e-summary-<job name>), which must not contain any of these.
if jq -e 'any(.[]; .name | test("[:\"<>|*?/\\\\]"))' <<<"$MATRIX" >/dev/null; then
  echo "a job name contains one of : \" < > | * ? / \\, which artifact names do not allow: $(jq -c 'map(.name)' <<<"$MATRIX")" >&2
  exit 1
fi
echo "$MATRIX"
