# ---- etapa 1: compilar e publicar (SDK completo, só existe durante o build) ----
FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src

# restaura primeiro só com os csproj, para o cache de camadas aproveitar quando só o código muda
COPY Directory.Build.props ./
COPY src/Pedidos.Dominio/Pedidos.Dominio.csproj src/Pedidos.Dominio/
COPY src/Pedidos.Aplicacao/Pedidos.Aplicacao.csproj src/Pedidos.Aplicacao/
COPY src/Pedidos.Infra/Pedidos.Infra.csproj src/Pedidos.Infra/
COPY src/Pedidos.Api/Pedidos.Api.csproj src/Pedidos.Api/
RUN dotnet restore src/Pedidos.Api/Pedidos.Api.csproj

COPY src/ src/
RUN dotnet publish src/Pedidos.Api/Pedidos.Api.csproj -c Release -o /app --no-restore

# ---- etapa 2: imagem final enxuta (só o runtime ASP.NET) ----
FROM mcr.microsoft.com/dotnet/aspnet:8.0 AS final
RUN apt-get update && apt-get install -y --no-install-recommends curl && rm -rf /var/lib/apt/lists/*
WORKDIR /app
COPY --from=build /app .

ENV ASPNETCORE_URLS=http://+:8080 \
    ASPNETCORE_ENVIRONMENT=Production \
    DOTNET_EnableDiagnostics=0
EXPOSE 8080

# usuário sem privilégios que já vem nas imagens do .NET 8
USER app

HEALTHCHECK --interval=15s --timeout=3s --start-period=20s --retries=3 \
  CMD curl -fsS http://localhost:8080/health/live || exit 1

ENTRYPOINT ["dotnet", "Pedidos.Api.dll"]
