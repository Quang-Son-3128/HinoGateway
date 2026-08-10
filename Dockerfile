# --- Build stage: compile server + bundle the Blazor WASM client ---
FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src

# Restore first (better layer caching)
COPY global.json ./
COPY HinoGateway.sln ./
COPY src/ src/
COPY tests/ tests/
RUN dotnet restore src/HinoGateway.Server/HinoGateway.Server.csproj

# Publish the server (this also produces & bundles the WASM client assets)
RUN dotnet publish src/HinoGateway.Server/HinoGateway.Server.csproj -c Release -o /app --no-restore

# --- Runtime stage: ASP.NET Core only, no SDK, runs on Linux ---
FROM mcr.microsoft.com/dotnet/aspnet:8.0 AS runtime
WORKDIR /app
COPY --from=build /app ./
EXPOSE 2000
ENV ASPNETCORE_URLS=http://+:2000
ENTRYPOINT ["dotnet", "HinoGateway.Server.dll"]
