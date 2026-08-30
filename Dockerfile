# Build Stage
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /app

# Copy solution and project files
COPY src/StaadPortalEngine/StaadPortalEngine.csproj src/StaadPortalEngine/
COPY src/StaadPortalApp/StaadPortalApp.csproj src/StaadPortalApp/

# Restore dependencies
RUN dotnet restore src/StaadPortalApp/StaadPortalApp.csproj

# Copy all sources and build
COPY src/ src/
RUN dotnet publish src/StaadPortalApp/StaadPortalApp.csproj -c Release -o /app/publish --no-restore

# Runtime Stage
FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app
COPY --from=build /app/publish ./

# Expose HTTP port (cloud platforms use PORT env var, default 8080)
ENV ASPNETCORE_URLS=http://+:8080
EXPOSE 8080

ENTRYPOINT ["dotnet", "StaadPortalApp.dll"]
