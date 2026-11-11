# syntax=docker/dockerfile:1
# Bank sample service (Accounts, Transfers, Notifications on PostgreSQL). Build from the repository root:
#   docker build -f samples/Bank/Bank.Api/Dockerfile -t sourceflow-bank-api .
# The SDK stage runs on the build platform; the framework-dependent output runs on any target architecture.
FROM --platform=$BUILDPLATFORM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /repo
COPY global.json Directory.Build.props Directory.Build.targets Directory.Packages.props ./
COPY src/Directory.Build.props src/BannedSymbols.txt src/
COPY samples/Directory.Build.props samples/
COPY src/Core/ src/Core/
COPY src/Hosting/ src/Hosting/
COPY src/Storage/SourceFlow.Storage.PostgreSQL/ src/Storage/SourceFlow.Storage.PostgreSQL/
COPY samples/Bank/ samples/Bank/
RUN --mount=type=cache,id=nuget,target=/root/.nuget/packages \
    dotnet publish samples/Bank/Bank.Api/Bank.Api.csproj -c Release -o /app -p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/aspnet:10.0
LABEL org.opencontainers.image.title="SourceFlow Bank sample service" \
      org.opencontainers.image.description="SourceFlow.Net v3 Bank sample: commands, projections, transfers and SFQL queries on PostgreSQL" \
      org.opencontainers.image.source="https://github.com/CodeShayk/SourceFlow.Net" \
      org.opencontainers.image.licenses="MIT"
WORKDIR /app
COPY --from=build /app .
ENV ASPNETCORE_HTTP_PORTS=8080
EXPOSE 8080
USER $APP_UID
ENTRYPOINT ["dotnet", "Bank.Api.dll"]
