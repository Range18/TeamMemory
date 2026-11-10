FROM mcr.microsoft.com/dotnet/sdk:10.0-noble AS build
WORKDIR /src
ENV DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_PROCESSOR_COUNT=2
COPY global.json ./
COPY shared/ ./shared/
COPY src/ ./src/
# Runtime targets share this build stage; compile sequentially to limit memory use.
RUN --mount=type=cache,target=/root/.nuget/packages \
    for service in AnalyticsService DashboardService IngestionService; do \
      dotnet publish "src/$service/$service.csproj" -c Release -o "/publish/$service" \
        --self-contained false -p:UseAppHost=false -p:UseSharedCompilation=false -m:1 /nr:false || exit $?; \
    done

FROM mcr.microsoft.com/dotnet/aspnet:10.0-noble AS runtime
USER root
RUN sed -i -e 's|http://|https://|g' -e 's/^Components:.*/Components: main/' /etc/apt/sources.list.d/ubuntu.sources \
    && apt-get update && apt-get install -y --no-install-recommends curl libgomp1 \
    && rm -rf /var/lib/apt/lists/*
WORKDIR /app
ENV ASPNETCORE_HTTP_PORTS=8080 DOTNET_PROCESSOR_COUNT=2
USER $APP_UID

FROM runtime AS analytics
COPY --from=build /publish/AnalyticsService/ ./
ENTRYPOINT ["dotnet", "AnalyticsService.dll"]

FROM runtime AS dashboard
COPY --from=build /publish/DashboardService/ ./
EXPOSE 8080
ENTRYPOINT ["dotnet", "DashboardService.dll"]

FROM runtime AS ingestion
COPY --from=build /publish/IngestionService/ ./
EXPOSE 8080
ENTRYPOINT ["dotnet", "IngestionService.dll"]
