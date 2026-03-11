# ── Build stage ──
FROM mcr.microsoft.com/dotnet/sdk:8.0-jammy AS build
WORKDIR /src

COPY src/WA.Backend/WA.Backend.csproj ./WA.Backend/
RUN dotnet restore ./WA.Backend/WA.Backend.csproj

COPY src/WA.Backend/ ./WA.Backend/
RUN dotnet publish ./WA.Backend/WA.Backend.csproj -c Release -o /app/publish --no-restore

# ── Runtime stage ──
FROM mcr.microsoft.com/dotnet/aspnet:8.0-jammy AS final
WORKDIR /app

COPY --from=build /app/publish .

# Ma'lumotlar bazasi va xotira uchun volume
VOLUME ["/data"]

EXPOSE 5000

ENV ASPNETCORE_URLS=http://+:5000
ENV ASPNETCORE_ENVIRONMENT=Production

ENTRYPOINT ["dotnet", "WA.Backend.dll"]
