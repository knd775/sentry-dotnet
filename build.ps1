$ErrorActionPreference = "Stop"

dotnet test -c Release /p:CollectCoverage=true /p:CoverletOutputFormat=opencover /p:Exclude="[Sentry.Test*]*"
 if ($LASTEXITCODE -ne 0) { exit 1 }

dotnet pack -c Release --no-build --include-symbols
 if ($LASTEXITCODE -ne 0) { exit 1 }
