# =============================================================================
# Slack bot container — runs alongside main stack on the Spark host
# =============================================================================

FROM mcr.microsoft.com/dotnet/sdk:8.0-jammy AS build
WORKDIR /src
COPY *.csproj ./
RUN dotnet restore
COPY . .
RUN dotnet publish -c Release -o /app --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:8.0-jammy AS runtime
RUN apt-get update && apt-get install -y --no-install-recommends \
        bash curl docker.io \
    && rm -rf /var/lib/apt/lists/*
WORKDIR /app
COPY --from=build /app .

# Mount scripts from host: -v /opt/dgx-spark-llm-stack/scripts:/scripts
EXPOSE 5050
HEALTHCHECK --interval=30s --timeout=3s --retries=3 \
  CMD curl -fsS http://localhost:5050/health || exit 1

ENTRYPOINT ["dotnet", "SetYazilim.Llm.SlackBot.dll"]
