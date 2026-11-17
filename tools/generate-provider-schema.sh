#!/usr/bin/env bash
set -euo pipefail

repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
output="${1:-$repo_root/src/NHAMBS.Data/sqlprovider.schema}"
connection="${NHAMBS_CONNECTION:-Host=localhost;Port=54322;Database=nhambs;Username=nhambs;Password=nhambs}"

rm -f "$output"

NHAMBS_CONNECTION="$connection" dotnet run \
  --project "$repo_root/tools/NHAMBS.SchemaTool/NHAMBS.SchemaTool.fsproj" \
  -- "$output"
