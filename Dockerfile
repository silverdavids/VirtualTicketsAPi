# =========================
# Build stage
# =========================
FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build

ARG BUILD_SHA=unknown

WORKDIR /src

COPY ["VirtualTickets.Api.csproj", "./"]
RUN dotnet restore

COPY . .

RUN dotnet publish "VirtualTickets.Api.csproj" \
    -c Release \
    -o /app/publish \
    /p:UseAppHost=false \
    /p:BuildSha=$BUILD_SHA

# =========================
# Runtime stage
# =========================
FROM mcr.microsoft.com/dotnet/aspnet:8.0 AS runtime

ARG BUILD_SHA=unknown
ARG REPOSITORY_URL=https://github.com/silverdavids/VirtualTicketsAPi

LABEL org.opencontainers.image.revision=$BUILD_SHA \
      org.opencontainers.image.source=$REPOSITORY_URL

WORKDIR /app

COPY --from=build /app/publish .

ENV ASPNETCORE_URLS=http://+:8080
ENV ASPNETCORE_ENVIRONMENT=Production

EXPOSE 8080

ENTRYPOINT ["dotnet", "VirtualTickets.Api.dll"]
