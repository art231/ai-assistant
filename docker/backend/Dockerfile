# ============================================================
# Stage 1: Build
# ============================================================
FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src

# Copy solution and project files
COPY src/backend/VoiceChatAI.Docker.sln ./VoiceChatAI.sln
COPY src/backend/src/VoiceChatAI.Domain/*.csproj ./src/VoiceChatAI.Domain/
COPY src/backend/src/VoiceChatAI.Application/*.csproj ./src/VoiceChatAI.Application/
COPY src/backend/src/VoiceChatAI.Infrastructure/*.csproj ./src/VoiceChatAI.Infrastructure/
COPY src/backend/src/VoiceChatAI.Presentation/*.csproj ./src/VoiceChatAI.Presentation/

# Restore dependencies
RUN dotnet restore

# Copy all source files
COPY src/backend/src/ ./src/

# Build and publish
WORKDIR /src/src/VoiceChatAI.Presentation
RUN dotnet publish -c Release -o /app/publish

# ============================================================
# Stage 2: Runtime
# ============================================================
FROM mcr.microsoft.com/dotnet/aspnet:8.0 AS runtime
WORKDIR /app

# Install curl for health checks
RUN apt-get update && apt-get install -y curl && rm -rf /var/lib/apt/lists/*

COPY --from=build /app/publish .

EXPOSE 5000

HEALTHCHECK --interval=30s --timeout=10s --retries=3 \
  CMD curl -f http://localhost:5000/health || exit 1

ENTRYPOINT ["dotnet", "VoiceChatAI.Presentation.dll"]
