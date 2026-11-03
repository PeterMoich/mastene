#!/usr/bin/env bash
# One-off scaffolding script, run by .github/workflows/bootstrap.yml on GitHub's runners.
# It generates the solution, projects and Angular app with the official CLIs so package
# versions are the current stable ones, then writes a report of what it resolved.
set -uo pipefail
REPORT=bootstrap-report.txt
: > "$REPORT"
step() { echo "== $*" | tee -a "$REPORT"; }
run() { "$@" >>"$REPORT" 2>&1 || echo "FAILED: $*" | tee -a "$REPORT"; }

step "dotnet $(dotnet --version)"
run dotnet new gitignore
run dotnet new globaljson --sdk-version "$(dotnet --version)" --roll-forward latestFeature
run dotnet new sln -n TenantManager

run dotnet new classlib -n TenantManager.Domain -o src/TenantManager.Domain
run dotnet new classlib -n TenantManager.Application -o src/TenantManager.Application
run dotnet new classlib -n TenantManager.Infrastructure -o src/TenantManager.Infrastructure
run dotnet new webapi -n TenantManager.Api -o src/TenantManager.Api --use-controllers
run dotnet new xunit -n TenantManager.Domain.Tests -o tests/TenantManager.Domain.Tests
run dotnet new xunit -n TenantManager.Api.IntegrationTests -o tests/TenantManager.Api.IntegrationTests

for p in src/*/*.csproj tests/*/*.csproj; do run dotnet sln add "$p"; done

run dotnet add src/TenantManager.Application reference src/TenantManager.Domain
run dotnet add src/TenantManager.Infrastructure reference src/TenantManager.Application
run dotnet add src/TenantManager.Api reference src/TenantManager.Application src/TenantManager.Infrastructure
run dotnet add tests/TenantManager.Domain.Tests reference src/TenantManager.Domain src/TenantManager.Application
run dotnet add tests/TenantManager.Api.IntegrationTests reference src/TenantManager.Api

add() { local proj=$1; shift; for pkg in "$@"; do run dotnet add "$proj" package "$pkg"; done; }
add src/TenantManager.Application FluentValidation FluentValidation.DependencyInjectionExtensions Microsoft.EntityFrameworkCore
add src/TenantManager.Infrastructure Microsoft.EntityFrameworkCore.SqlServer Microsoft.AspNetCore.Identity.EntityFrameworkCore Microsoft.IdentityModel.JsonWebTokens
add src/TenantManager.Api Asp.Versioning.Mvc Asp.Versioning.Mvc.ApiExplorer Swashbuckle.AspNetCore Microsoft.AspNetCore.Authentication.JwtBearer Serilog.AspNetCore Microsoft.Extensions.Diagnostics.HealthChecks.EntityFrameworkCore Microsoft.EntityFrameworkCore.Design
add tests/TenantManager.Domain.Tests Shouldly
add tests/TenantManager.Api.IntegrationTests Shouldly Microsoft.AspNetCore.Mvc.Testing Testcontainers.MsSql

run dotnet new tool-manifest
run dotnet tool install dotnet-ef

step "Resolved .NET packages"
dotnet list TenantManager.slnx package >>"$REPORT" 2>&1 || dotnet list package >>"$REPORT" 2>&1
cat .config/dotnet-tools.json >>"$REPORT" 2>&1

step "Angular"
export CI=true NG_CLI_ANALYTICS=false
run npx -y @angular/cli@latest new web --directory web --routing --style=scss --ssr=false --skip-git --interactive=false --defaults --package-manager npm
if [ -d web ]; then
  pushd web >/dev/null
  run npx ng add @angular/material --skip-confirmation --interactive=false
  run npx ng add angular-eslint --skip-confirmation --interactive=false
  step "Angular versions"
  npx ng version >>"../$REPORT" 2>&1
  cat package.json >>"../$REPORT"
  popd >/dev/null
fi
step "done"
