# =========================
# Build stage
# =========================
FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build

WORKDIR /src

COPY ["VirtualTickets.Api.csproj", "./"]
RUN dotnet restore

COPY . .

RUN dotnet publish "VirtualTickets.Api.csproj" \
    -c Release \
    -o /app/publish \
    /p:UseAppHost=false

# =========================
# Runtime stage
# =========================
FROM mcr.microsoft.com/dotnet/aspnet:8.0 AS runtime

WORKDIR /app

COPY --from=build /app/publish .

ENV ASPNETCORE_URLS=http://+:8080
ENV ASPNETCORE_ENVIRONMENT=Production

EXPOSE 8080

ENTRYPOINT ["dotnet", "VirtualTickets.Api.dll"]
