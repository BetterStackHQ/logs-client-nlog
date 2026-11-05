#!/usr/bin/env bash
# Installs, checks and runs example-project for one leg of the E2E workflow (.github/workflows/e2e.yml). The build's
# output goes to out/<command>.log as well, and a failure ends with one ::error:: line giving the reason.
#   e2e/example.sh install   packs the client from the checkout as the Release workflow does (or leaves the example's
#                            own version range to pick the newest release on nuget.org), references it and the leg's
#                            NLog and Newtonsoft.Json in the example, restores and builds it for the leg's framework
#   e2e/example.sh verify    checks that the framework, the .NET SDK and runtime, the client and the NLog and
#                            Newtonsoft.Json versions are the planned ones, and writes them to out/versions.md
#   e2e/example.sh run       runs the example the way its README says, with BETTER_STACK_SOURCE_TOKEN and
#                            BETTER_STACK_INGESTING_HOST, and fails when NLog's internal log says logs were dropped
# Env: LEG, FRAMEWORK (net48, net10.0, ...), RUNTIME (4.8, an exact .NET version or a channel), SDK (an exact version or
#      a channel such as 11.0.x), CLIENT_FROM (checkout or nuget), CLIENT (the client's version), NLOG, NEWTONSOFT.
set -euo pipefail
# A native jq.exe, as on Windows runners, ends its output lines with CRLF unless given -b.
if [ "${OS:-}" = Windows_NT ]; then jq() { command jq -b "$@"; }; fi

ROOT=$(cd "$(dirname "$0")/.." && pwd)
OUT=$ROOT/out
LOG=$OUT/$1.log
ASSETS=example-project/obj/project.assets.json
mkdir -p "$OUT"
cd "$ROOT"

# The example targets one framework, net10.0 today. A leg builds and runs it for its own framework without editing the
# project file, and C# 7.3, the default for .NET Framework, has no top-level statements.
PROPS=(-p:TargetFramework="$FRAMEWORK")
[[ "$FRAMEWORK" != net4* ]] || PROPS+=(-p:LangVersion=latest)
WHAT="The example for $FRAMEWORK with BetterStack.Logs.NLog $CLIENT, NLog $NLOG and Newtonsoft.Json $NEWTONSOFT"

fail() { echo "::error::$1"; exit 1; }

# Runs a dotnet command with its output on the terminal and in the log, and fails with the first error it reports.
log() {
  "$@" 2>&1 | tee "$LOG" && return
  fail "$WHAT does not build: $( (grep -m 1 -E ': error |^error' "$LOG" || tail -n 1 "$LOG") | sed 's/^ *//')"
}

# The version of package $1 the restore resolved.
resolved() {
  jq -r --arg package "$1/" '.targets | to_entries[0].value | keys[] | select(startswith($package)) | ltrimstr($package)' "$ASSETS"
}

case "$1" in
  install)
    if [ "$CLIENT_FROM" = checkout ]; then
      # The package the Release workflow builds and pushes to nuget.org, versioned $CLIENT. The project packs on build
      # (GeneratePackageOnBuild), which `dotnet pack` cannot be combined with.
      log dotnet build BetterStack.Logs.NLog.csproj --configuration Release -p:Version="$CLIENT" -p:ContinuousIntegrationBuild=true -p:PackageOutputPath=artifacts
      log dotnet add example-project package BetterStack.Logs.NLog --version "$CLIENT" --no-restore
      LOCAL='<add key="checkout" value="../artifacts" />'
    else
      # The example's own version range picks the release, as for a customer who copies the example.
      LOCAL=""
    fi
    # The package sources, in a nuget.config next to the example as for a local feed of one's own. Not --source: on the
    # Windows runner, the nuget.org URL reached NuGet as https:\api.nuget.org\v3\index.json, a local path.
    cat > example-project/nuget.config <<EOF
<?xml version="1.0" encoding="utf-8"?>
<configuration>
  <packageSources>
    <clear />
    $LOCAL
    <add key="nuget.org" value="https://api.nuget.org/v3/index.json" />
  </packageSources>
</configuration>
EOF
    # Exact versions, so that NuGet cannot quietly resolve another one.
    log dotnet add example-project package NLog --version "$NLOG" --no-restore
    log dotnet add example-project package Newtonsoft.Json --version "$NEWTONSOFT" --no-restore
    log dotnet restore example-project "${PROPS[@]}"
    log dotnet build example-project --no-restore "${PROPS[@]}" ;;

  verify)
    FAILED=0
    BUILT_FOR=$(jq -r '.project.frameworks | keys | join(", ")' "$ASSETS")
    [ "$BUILT_FOR" = "$FRAMEWORK" ] || { echo "::error::The example was restored for $BUILT_FOR, but the $LEG leg plans $FRAMEWORK."; FAILED=1; }

    SDK_RUNNING=$(dotnet --version | tr -d '\r')
    # The planned SDK is an exact version (10.0.403) or a channel (11.0.x), any SDK of it.
    if [ "$SDK_RUNNING" != "$SDK" ] && [[ "$SDK" != *.x || "$SDK_RUNNING" != "${SDK%x}"* ]]; then
      echo "::error::The .NET SDK $SDK_RUNNING is running, but the $LEG leg plans $SDK."; FAILED=1
    fi

    if [[ "$FRAMEWORK" == net4* ]]; then
      # .NET Framework is part of Windows and runs every net4x app. Its release number names the installed version:
      # 528040 and up is 4.8, 533320 and up is 4.8.1.
      RELEASE=$(powershell -NoProfile -Command "(Get-ItemProperty 'HKLM:\SOFTWARE\Microsoft\NET Framework Setup\NDP\v4\Full').Release" | tr -d '\r') || true
      [[ "$RELEASE" =~ ^[0-9]+$ ]] || RELEASE=0
      if [ "$RELEASE" -ge 533320 ]; then INSTALLED=4.8.1; elif [ "$RELEASE" -ge 528040 ]; then INSTALLED=4.8; else INSTALLED="older than 4.8"; fi
      RUNNING=".NET Framework $INSTALLED (release $RELEASE)"
      [[ "$INSTALLED" == "$RUNTIME"* ]] || { echo "::error::$RUNNING is installed, but the $LEG leg plans .NET Framework $RUNTIME."; FAILED=1; }
    else
      # A netX.Y app runs on the newest installed patch of X.Y.
      INSTALLED=$(dotnet --list-runtimes | sed -n "s/^Microsoft\.NETCore\.App \(${FRAMEWORK#net}\.[^ ]*\) .*/\1/p" | sort -V | tail -n 1)
      RUNNING=".NET ${INSTALLED:-(no runtime for $FRAMEWORK)}"
      # The planned runtime is an exact version (10.0.14) or a channel (11.0), any patch or pre-release of it.
      [ "$INSTALLED" = "$RUNTIME" ] || [[ "$RUNTIME" != *.*.* && "$INSTALLED" == "$RUNTIME".* ]] \
        || { echo "::error::$RUNNING runs the example, but the $LEG leg plans .NET $RUNTIME."; FAILED=1; }
    fi

    CLIENT_RESOLVED=$(resolved BetterStack.Logs.NLog)
    NLOG_RESOLVED=$(resolved NLog)
    NEWTONSOFT_RESOLVED=$(resolved Newtonsoft.Json)
    # Where NuGet took the package from, recorded next to it in the global packages folder.
    PACKAGES=$(jq -r '.packageFolders | keys[0]' "$ASSETS")
    CLIENT_SOURCE=$(jq -r .source "$PACKAGES/betterstack.logs.nlog/$CLIENT_RESOLVED/.nupkg.metadata" 2>/dev/null) || CLIENT_SOURCE=unknown
    if [ "$CLIENT_FROM" = checkout ]; then FROM="packed from the checkout"; else FROM="from $CLIENT_SOURCE"; fi
    echo "- $RUNNING, SDK $SDK_RUNNING, example built for $BUILT_FOR, BetterStack.Logs.NLog $CLIENT_RESOLVED $FROM, NLog $NLOG_RESOLVED, Newtonsoft.Json $NEWTONSOFT_RESOLVED" \
      | tee -a "$OUT/versions.md" "${GITHUB_STEP_SUMMARY:-/dev/null}"

    if [ "$CLIENT_RESOLVED" != "$CLIENT" ]; then
      HINT=""
      [ "$CLIENT_FROM" = checkout ] || HINT=" The version range in example-project/ExampleProject.csproj does not take the newest release."
      echo "::error::NuGet resolved BetterStack.Logs.NLog ${CLIENT_RESOLVED:-to nothing}, but the $LEG leg plans $CLIENT.$HINT"
      FAILED=1
    fi
    if [ "$CLIENT_FROM" = checkout ]; then
      # The package is the one packed here, not one of the same version from a cache: the restore recorded its hash.
      NUPKG=artifacts/BetterStack.Logs.NLog.$CLIENT.nupkg
      PACKED_HASH=$(openssl dgst -sha512 -binary "$NUPKG" | openssl base64 -A)
      RESOLVED_HASH=$(jq -r --arg library "BetterStack.Logs.NLog/$CLIENT_RESOLVED" '.libraries[$library].sha512' "$ASSETS")
      [ "$RESOLVED_HASH" = "$PACKED_HASH" ] || { echo "::error::The restore resolved a BetterStack.Logs.NLog $CLIENT_RESOLVED package with sha512 $RESOLVED_HASH from $CLIENT_SOURCE, not $NUPKG packed here."; FAILED=1; }
      # And the example's build output holds the library built here, for the target the package picked (netstandard2.0
      # on .NET Framework, netstandard2.1 on .NET).
      LIBRARY=$(jq -r --arg library "BetterStack.Logs.NLog/$CLIENT_RESOLVED" '.targets | to_entries[0].value[$library].runtime | keys[0]' "$ASSETS")
      BUILT=bin/Release/$(basename "$(dirname "$LIBRARY")")/BetterStack.Logs.NLog.dll
      COPIED=example-project/bin/Debug/$FRAMEWORK/BetterStack.Logs.NLog.dll
      cmp -s "$BUILT" "$COPIED" || { echo "::error::$COPIED is not $BUILT, the library built here for $LIBRARY."; FAILED=1; }
    elif [ "$CLIENT_SOURCE" != https://api.nuget.org/v3/index.json ]; then
      echo "::error::NuGet took BetterStack.Logs.NLog $CLIENT_RESOLVED from $CLIENT_SOURCE instead of nuget.org."; FAILED=1
    fi
    # The canary takes pre-releases as they come, so its dependencies only report.
    if [ "$LEG" != canary ]; then
      for check in "NLog $NLOG_RESOLVED $NLOG" "Newtonsoft.Json $NEWTONSOFT_RESOLVED $NEWTONSOFT"; do
        read -r name got want <<<"$check"
        [ "$got" = "$want" ] || { echo "::error::NuGet resolved $name $got, but the $LEG leg plans $want."; FAILED=1; }
      done
    fi
    exit "$FAILED" ;;

  run)
    cd example-project
    find . -name internal.txt -delete
    dotnet run --no-build "${PROPS[@]}" 2>&1 | tee "$LOG" || fail "$WHAT failed: $(tail -n 1 "$LOG")"
    # The target reports a request it gave up on, or a shutdown that did not wait for delivery, only in NLog's internal
    # log (internalLogFile in nlog.config), the example's exit code does not show it.
    find . -name internal.txt -exec cat {} + > "$OUT/internal.log"
    cat "$OUT/internal.log"
    DROPPED=$(grep -m 1 -E 'BetterStack\.Logs: (dropped|gave up)' "$OUT/internal.log") || true
    [ -z "$DROPPED" ] || fail "$WHAT lost logs, NLog's internal log says: $DROPPED" ;;

  *) fail "unknown command $1" ;;
esac
