FROM mcr.microsoft.com/dotnet/sdk:10.0-noble AS restore
WORKDIR /source
COPY global.json ./
COPY shared/Shared.Contracts/Shared.Contracts.csproj shared/Shared.Contracts/
COPY src/DashboardService/DashboardService.csproj src/DashboardService/
RUN dotnet restore src/DashboardService/DashboardService.csproj

FROM restore AS build
COPY shared/Shared.Contracts/ shared/Shared.Contracts/
COPY src/DashboardService/ src/DashboardService/
RUN dotnet build src/DashboardService/DashboardService.csproj -c Release --no-restore

FROM build AS publish
RUN dotnet publish src/DashboardService/DashboardService.csproj -c Release --no-build -o /out /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/aspnet:10.0-noble AS final
USER root
RUN apt-get update && apt-get install -y --no-install-recommends curl tzdata && rm -rf /var/lib/apt/lists/*
WORKDIR /app
COPY --from=publish /out/ ./
ENV ASPNETCORE_HTTP_PORTS=8080
EXPOSE 8080
USER $APP_UID
ENTRYPOINT ["dotnet", "DashboardService.dll"]
