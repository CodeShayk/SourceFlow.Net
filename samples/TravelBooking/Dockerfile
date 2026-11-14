# syntax=docker/dockerfile:1
# A TravelBooking service: SERVICE=Bookings or SERVICE=Hotels. Build from the repository root:
#   docker build -f samples/TravelBooking/Dockerfile --build-arg SERVICE=Bookings -t sourceflow-travel-bookings .
# The SDK stage runs on the build platform; the framework-dependent output runs on any target architecture.
ARG SERVICE=Bookings

FROM --platform=$BUILDPLATFORM mcr.microsoft.com/dotnet/sdk:10.0 AS build
ARG SERVICE
WORKDIR /repo
COPY global.json Directory.Build.props Directory.Build.targets Directory.Packages.props ./
COPY src/ src/
COPY samples/Directory.Build.props samples/
COPY samples/TravelBooking/ samples/TravelBooking/
RUN --mount=type=cache,id=nuget,target=/root/.nuget/packages,sharing=locked \
    dotnet publish "samples/TravelBooking/${SERVICE}.Api/${SERVICE}.Api.csproj" -c Release -o /app -p:UseAppHost=false \
    && echo "${SERVICE}.Api.dll" > /app/entrypoint

FROM mcr.microsoft.com/dotnet/aspnet:10.0
LABEL org.opencontainers.image.title="SourceFlow TravelBooking sample service" \
      org.opencontainers.image.description="SourceFlow.Net v3 TravelBooking sample: the Bookings or Hotels service, on PostgreSQL and a cloud transport" \
      org.opencontainers.image.source="https://github.com/CodeShayk/SourceFlow.Net" \
      org.opencontainers.image.licenses="MIT"
WORKDIR /app
COPY --from=build /app .
ENV ASPNETCORE_HTTP_PORTS=8080
EXPOSE 8080
USER $APP_UID
# `docker compose exec bookings sh -c 'dotnet Bookings.Api.dll sourceflow workflow list'` runs the CLI in the service.
ENTRYPOINT ["sh", "-c", "exec dotnet \"$(cat /app/entrypoint)\" \"$@\"", "--"]
