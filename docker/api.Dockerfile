# Builds the API. Also used, with the "migrate" argument, as the one-shot migrator in docker-compose.
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
# Restore first, from project files only, so code changes don't invalidate the package cache layer.
COPY Directory.Build.props ./
COPY src/TenantManager.Domain/TenantManager.Domain.csproj src/TenantManager.Domain/
COPY src/TenantManager.Application/TenantManager.Application.csproj src/TenantManager.Application/
COPY src/TenantManager.Infrastructure/TenantManager.Infrastructure.csproj src/TenantManager.Infrastructure/
COPY src/TenantManager.Api/TenantManager.Api.csproj src/TenantManager.Api/
RUN dotnet restore src/TenantManager.Api/TenantManager.Api.csproj
COPY src/ src/
RUN dotnet publish src/TenantManager.Api/TenantManager.Api.csproj -c Release -o /app --no-restore -p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app
COPY --from=build /app .
# The .NET images ship a non-root "app" user; never run the API as root.
USER $APP_UID
ENV ASPNETCORE_HTTP_PORTS=8080
EXPOSE 8080
ENTRYPOINT ["dotnet", "TenantManager.Api.dll"]
