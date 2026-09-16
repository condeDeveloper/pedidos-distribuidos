# ---- etapa 1: compilar e publicar ----
FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src

COPY Directory.Build.props ./
COPY src/Pedidos.Dominio/Pedidos.Dominio.csproj src/Pedidos.Dominio/
COPY src/Pedidos.Aplicacao/Pedidos.Aplicacao.csproj src/Pedidos.Aplicacao/
COPY src/Pedidos.Infra/Pedidos.Infra.csproj src/Pedidos.Infra/
COPY src/Pedidos.Worker/Pedidos.Worker.csproj src/Pedidos.Worker/
RUN dotnet restore src/Pedidos.Worker/Pedidos.Worker.csproj

COPY src/ src/
RUN dotnet publish src/Pedidos.Worker/Pedidos.Worker.csproj -c Release -o /app --no-restore

# ---- etapa 2: runtime puro, sem ASP.NET, pois o worker não expõe HTTP ----
FROM mcr.microsoft.com/dotnet/runtime:8.0 AS final
WORKDIR /app
COPY --from=build /app .
ENV DOTNET_ENVIRONMENT=Production \
    DOTNET_EnableDiagnostics=0
USER app
ENTRYPOINT ["dotnet", "Pedidos.Worker.dll"]
