# Lumind API: image for RAILWAY'S BUILDER ONLY (see CLAUDE.md "Docker" exception and docs/decisions/006).
# Nobody builds or runs this locally: local work is `dotnet run`, no Docker, no compose, no Testcontainers.
# Railway detects this file at the root of the service's source directory (the LumindApi repository root = this folder).
#
# No secret is in this file or in the image. Everything secret arrives at RUN time as environment variables
# set in the Railway panel (ConnectionStrings__Default, Auth__CodePepper, Auth__JwtSigningKey, Privacy__IpHashKey, ...).
# The build needs no variable, so no ARG is declared.

# ---------------------------------------------------------------- build
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

# 1) Restore layer: only the files that decide the package graph. It is rebuilt only when a project file or a
#    Directory.*.props changes, not on every source edit.
COPY Chatbot.sln Directory.Build.props Directory.Packages.props ./
COPY src/Chatbot.Domain/Chatbot.Domain.csproj src/Chatbot.Domain/
COPY src/Chatbot.Application/Chatbot.Application.csproj src/Chatbot.Application/
COPY src/Chatbot.Infrastructure/Chatbot.Infrastructure.csproj src/Chatbot.Infrastructure/
COPY src/Chatbot.Api/Chatbot.Api.csproj src/Chatbot.Api/
# Only the API project (and what it references) is restored: the test project is not part of the image.
RUN dotnet restore src/Chatbot.Api/Chatbot.Api.csproj

# 2) Sources, then publish the API (framework-dependent: the aspnet runtime image provides the framework).
COPY . .
RUN dotnet publish src/Chatbot.Api/Chatbot.Api.csproj -c Release -o /app/publish --no-restore -p:UseAppHost=false

# ---------------------------------------------------------------- run
FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS final
WORKDIR /app
COPY --from=build /app/publish .

# Not root: the official aspnet images ship a non-root user and define APP_UID for it.
USER $APP_UID

ENV ASPNETCORE_ENVIRONMENT=Production

# No EXPOSE and no HEALTHCHECK: Railway injects PORT, and the health check is the path set in the Railway panel
# (/api/health). Program.cs binds http://[::]:$PORT (IPv4 + IPv6, needed for Railway's private network) when PORT is set.
ENTRYPOINT ["dotnet", "Chatbot.Api.dll"]
