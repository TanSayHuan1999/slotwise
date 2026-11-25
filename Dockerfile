# Slotwise — multi-stage build: SDK image compiles, ASP.NET runtime serves.
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

# Restore first (layer-cached until the project file changes).
COPY global.json ./
COPY Slotwise/Slotwise.csproj Slotwise/
RUN dotnet restore Slotwise/Slotwise.csproj

COPY Slotwise/ Slotwise/
RUN dotnet publish Slotwise/Slotwise.csproj -c Release -o /app/publish --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app
COPY --from=build /app/publish ./
ENV ASPNETCORE_URLS=http://+:8080
EXPOSE 8080
USER app
ENTRYPOINT ["dotnet", "Slotwise.dll"]
