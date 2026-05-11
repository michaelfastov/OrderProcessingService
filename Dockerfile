# syntax=docker/dockerfile:1
# ----- Build stage -----
FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src

# Copy csproj first to leverage docker layer caching for restore.
COPY OrderProcessingService/OrderProcessingService.csproj OrderProcessingService/
RUN dotnet restore OrderProcessingService/OrderProcessingService.csproj

# Copy the rest of the source.
COPY OrderProcessingService/ OrderProcessingService/

RUN dotnet publish OrderProcessingService/OrderProcessingService.csproj \
    -c Release \
    -o /app/publish \
    --no-restore \
    /p:UseAppHost=false

# ----- Runtime stage -----
FROM mcr.microsoft.com/dotnet/aspnet:8.0 AS runtime
WORKDIR /app

ENV ASPNETCORE_URLS=http://+:8080 \
    DOTNET_RUNNING_IN_CONTAINER=true

COPY --from=build /app/publish ./

EXPOSE 8080
ENTRYPOINT ["dotnet", "OrderProcessingService.dll"]
