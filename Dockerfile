# syntax=docker/dockerfile:1
# Imagen de servired-indicadores (ASP.NET Core). Puerto 8083, healthcheck en /health.

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY Servired.Indicadores.slnx ./
COPY src/Servired.Indicadores/Servired.Indicadores.csproj src/Servired.Indicadores/
RUN dotnet restore src/Servired.Indicadores/Servired.Indicadores.csproj
COPY src/ src/
RUN dotnet publish src/Servired.Indicadores/Servired.Indicadores.csproj -c Release -o /app --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
# curl solo para el HEALTHCHECK (la imagen base no trae herramientas HTTP).
RUN apt-get update \
    && apt-get install -y --no-install-recommends curl \
    && rm -rf /var/lib/apt/lists/*
WORKDIR /app
COPY --from=build /app ./
ENV ASPNETCORE_HTTP_PORTS=8083 \
    DOTNET_RUNNING_IN_CONTAINER=true
EXPOSE 8083
# Usuario sin privilegios que trae la imagen oficial.
USER $APP_UID
HEALTHCHECK --interval=15s --timeout=3s --start-period=30s --retries=3 \
    CMD curl -fsS http://localhost:8083/health || exit 1
ENTRYPOINT ["dotnet", "Servired.Indicadores.dll"]
