#!/usr/bin/env bash
set -euo pipefail

export FOREXBOT_E2E_WITH_SQLSERVER=0

for arg in "$@"; do
  case "$arg" in
    --with-sqlserver)
      export FOREXBOT_E2E_WITH_SQLSERVER=1
      ;;
    *)
      echo "Unknown argument: $arg" >&2
      exit 2
      ;;
  esac
done

dotnet test tests/e2e/ForexTradingBot.EndToEnd.csproj -c Release --logger "console;verbosity=normal"
