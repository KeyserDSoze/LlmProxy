FROM node:22-alpine AS admin-build
WORKDIR /admin
COPY src/LlmProxy.Admin/package.json ./
RUN npm install --no-audit --no-fund
COPY src/LlmProxy.Admin/ ./
RUN npm run build

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS api-build
WORKDIR /src
COPY global.json Directory.Build.props Directory.Packages.props ./
COPY src/LlmProxy.Domain/LlmProxy.Domain.csproj src/LlmProxy.Domain/
COPY src/LlmProxy.Application/LlmProxy.Application.csproj src/LlmProxy.Application/
COPY src/LlmProxy.Infrastructure/LlmProxy.Infrastructure.csproj src/LlmProxy.Infrastructure/
COPY src/LlmProxy.Api/LlmProxy.Api.csproj src/LlmProxy.Api/
RUN dotnet restore src/LlmProxy.Api/LlmProxy.Api.csproj
COPY src/ src/
RUN dotnet publish src/LlmProxy.Api/LlmProxy.Api.csproj -c Release -o /app/publish --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
ARG LLMPROXY_VERSION=dev
ARG LLMPROXY_BUILD_SHA=
ARG LLMPROXY_BUILD_DATE=
WORKDIR /app
ENV ASPNETCORE_URLS=http://+:8080 \
    DOTNET_EnableDiagnostics=0 \
    LLMPROXY_BUILD_SHA=${LLMPROXY_BUILD_SHA} \
    LLMPROXY_BUILD_DATE=${LLMPROXY_BUILD_DATE}
LABEL org.opencontainers.image.title="LlmProxy" \
      org.opencontainers.image.version="${LLMPROXY_VERSION}" \
      org.opencontainers.image.revision="${LLMPROXY_BUILD_SHA}" \
      org.opencontainers.image.created="${LLMPROXY_BUILD_DATE}"
EXPOSE 8080
COPY --from=api-build /app/publish ./
COPY --from=admin-build /admin/dist ./wwwroot/admin
USER $APP_UID
ENTRYPOINT ["dotnet", "LlmProxy.Api.dll"]
