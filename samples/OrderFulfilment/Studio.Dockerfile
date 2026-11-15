# syntax=docker/dockerfile:1
# SourceFlow Studio for the OrderFulfilment workspace, as a read-only static site served by nginx: the domain map of the
# four services, the order-fulfilment statechart and swimlane, simulation and the path explorer all run in the browser
# (WebAssembly core). Build from the repository root:
#   docker build -f samples/OrderFulfilment/Studio.Dockerfile -t sourceflow-order-studio .

# 1. The WebAssembly core (the same validator, simulator and exporters as the .NET library).
FROM --platform=$BUILDPLATFORM mcr.microsoft.com/dotnet/sdk:10.0 AS wasm
WORKDIR /repo
COPY global.json Directory.Build.props Directory.Build.targets Directory.Packages.props ./
COPY src/ src/
RUN --mount=type=cache,id=nuget,target=/root/.nuget/packages,sharing=locked \
    dotnet publish src/Workflows/SourceFlow.Studio.Wasm -c Release -o /repo/artifacts/studio-wasm

# 2. The Studio app, and the static site of the workspace (npx @sourceflow/studio build).
FROM --platform=$BUILDPLATFORM node:22.18-alpine AS site
WORKDIR /repo
COPY web/package.json web/package-lock.json web/
COPY web/core-client/package.json web/core-client/
COPY web/app/package.json web/app/
COPY web/actions/workflow-diff/package.json web/actions/workflow-diff/
RUN --mount=type=cache,target=/root/.npm,sharing=locked cd web && npm ci --no-audit --no-fund
COPY web/ web/
COPY schemas/ schemas/
COPY --from=wasm /repo/artifacts/studio-wasm artifacts/studio-wasm
RUN cd web && npm run build
COPY samples/OrderFulfilment/ samples/OrderFulfilment/
RUN node web/app/bin/studio.mjs build --workspace samples/OrderFulfilment --out /site

# 3. Served as static files.
FROM nginx:1.27-alpine
LABEL org.opencontainers.image.title="SourceFlow Studio: OrderFulfilment" \
      org.opencontainers.image.description="SourceFlow Studio (read-only) for the OrderFulfilment sample workspace" \
      org.opencontainers.image.source="https://github.com/CodeShayk/SourceFlow.Net" \
      org.opencontainers.image.licenses="MIT"
COPY --from=site /site /usr/share/nginx/html
EXPOSE 80
