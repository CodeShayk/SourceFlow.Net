# syntax=docker/dockerfile:1
# The OrderFulfilment services: one image, and Service (orders, inventory, payments or shipping) picks the service a
# container runs. Build from the repository root:
#   docker build -f samples/OrderFulfilment/Dockerfile -t sourceflow-order-fulfilment .
#   docker run -e Service=orders … sourceflow-order-fulfilment
# The SDK stage runs on the build platform; the framework-dependent output runs on any target architecture.
FROM --platform=$BUILDPLATFORM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /repo
COPY global.json Directory.Build.props Directory.Build.targets Directory.Packages.props ./
COPY src/ src/
COPY samples/Directory.Build.props samples/
COPY samples/OrderFulfilment/ samples/OrderFulfilment/
RUN --mount=type=cache,id=nuget,target=/root/.nuget/packages,sharing=locked \
    dotnet publish samples/OrderFulfilment/OrderFulfilment.Host/OrderFulfilment.Host.csproj -c Release -o /app -p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/aspnet:10.0
LABEL org.opencontainers.image.title="SourceFlow OrderFulfilment sample service" \
      org.opencontainers.image.description="SourceFlow.Net v3 OrderFulfilment sample: the orders, inventory, payments or shipping service (Service=…), on PostgreSQL and a cloud transport" \
      org.opencontainers.image.source="https://github.com/CodeShayk/SourceFlow.Net" \
      org.opencontainers.image.licenses="MIT"
WORKDIR /app
COPY --from=build /app .
ENV ASPNETCORE_HTTP_PORTS=8080
EXPOSE 8080
USER $APP_UID
# `docker compose exec orders dotnet OrderFulfilment.Host.dll sourceflow workflow list` runs the CLI in the service.
ENTRYPOINT ["dotnet", "OrderFulfilment.Host.dll"]
