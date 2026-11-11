# syntax=docker/dockerfile:1
# SourceFlow Studio demo console. Build from the repository root:
#   docker build -f samples/Studio/SourceFlow.Studio.Demo/Dockerfile -t sourceflow-studio-demo .
FROM --platform=$BUILDPLATFORM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /repo
COPY global.json Directory.Build.props Directory.Build.targets Directory.Packages.props ./
COPY samples/Directory.Build.props samples/
COPY samples/Studio/ samples/Studio/
RUN --mount=type=cache,id=nuget,target=/root/.nuget/packages \
    dotnet publish samples/Studio/SourceFlow.Studio.Demo/SourceFlow.Studio.Demo.csproj -c Release -o /app -p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/aspnet:10.0
LABEL org.opencontainers.image.title="SourceFlow Studio demo console" \
      org.opencontainers.image.description="Interim SourceFlow Studio console: schema browser, SFQL/SQL query console and Bank sample commands" \
      org.opencontainers.image.source="https://github.com/CodeShayk/SourceFlow.Net" \
      org.opencontainers.image.licenses="MIT"
WORKDIR /app
COPY --from=build /app .
ENV ASPNETCORE_HTTP_PORTS=8080 \
    Studio__ServiceUrl=http://bank-api:8080/
EXPOSE 8080
USER $APP_UID
ENTRYPOINT ["dotnet", "SourceFlow.Studio.Demo.dll"]
