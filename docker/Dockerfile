FROM node:22-alpine AS admin-build
WORKDIR /admin
COPY src/LlmProxy.Admin/package.json ./
RUN npm install --no-audit --no-fund
COPY src/LlmProxy.Admin/ ./
RUN npm run build

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS api-build
WORKDIR /src
COPY global.json Directory.Build.props Directory.Packages.props LlmProxy.slnx ./
COPY src/LlmProxy.Domain/LlmProxy.Domain.csproj src/LlmProxy.Domain/
COPY src/LlmProxy.Application/LlmProxy.Application.csproj src/LlmProxy.Application/
COPY src/LlmProxy.Infrastructure/LlmProxy.Infrastructure.csproj src/LlmProxy.Infrastructure/
COPY src/LlmProxy.Api/LlmProxy.Api.csproj src/LlmProxy.Api/
RUN dotnet restore LlmProxy.slnx
COPY src/ src/
RUN dotnet publish src/LlmProxy.Api/LlmProxy.Api.csproj -c Release -o /app/publish --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app
ENV ASPNETCORE_URLS=http://+:8080 \
    DOTNET_EnableDiagnostics=0
EXPOSE 8080
COPY --from=api-build /app/publish ./
COPY --from=admin-build /admin/dist ./wwwroot/admin
USER $APP_UID
HEALTHCHECK --interval=30s --timeout=3s --start-period=10s --retries=3 CMD ["/bin/sh", "-c", "wget -qO- http://127.0.0.1:8080/healthz >/dev/null || exit 1"]
ENTRYPOINT ["dotnet", "LlmProxy.Api.dll"]
