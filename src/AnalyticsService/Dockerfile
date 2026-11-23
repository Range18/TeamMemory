FROM mcr.microsoft.com/dotnet/sdk:10.0-noble AS restore
WORKDIR /source
COPY global.json ./
COPY shared/Shared.Contracts/Shared.Contracts.csproj shared/Shared.Contracts/
COPY src/AnalyticsService/AnalyticsService.csproj src/AnalyticsService/
RUN dotnet restore src/AnalyticsService/AnalyticsService.csproj

FROM restore AS build
COPY shared/Shared.Contracts/ shared/Shared.Contracts/
COPY src/AnalyticsService/ src/AnalyticsService/
RUN dotnet build src/AnalyticsService/AnalyticsService.csproj -c Release --no-restore

FROM build AS publish
RUN dotnet publish src/AnalyticsService/AnalyticsService.csproj -c Release --no-build -o /out /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/runtime:10.0-noble AS final
WORKDIR /app
COPY --from=publish /out/ ./
USER $APP_UID
ENTRYPOINT ["dotnet", "AnalyticsService.dll"]
