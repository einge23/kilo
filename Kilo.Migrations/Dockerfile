FROM mcr.microsoft.com/dotnet/runtime:10.0 AS base
USER $APP_UID
WORKDIR /app

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
ARG BUILD_CONFIGURATION=Release
WORKDIR /src
COPY global.json ./
COPY Kilo.Migrations/Kilo.Migrations.csproj Kilo.Migrations/packages.lock.json Kilo.Migrations/
RUN dotnet restore Kilo.Migrations/Kilo.Migrations.csproj --locked-mode
COPY Kilo.Migrations/ Kilo.Migrations/
RUN dotnet publish Kilo.Migrations/Kilo.Migrations.csproj -c $BUILD_CONFIGURATION --no-restore -o /out /p:UseAppHost=false

FROM base AS final
COPY --from=build /out .
ENTRYPOINT ["dotnet", "Kilo.Migrations.dll"]
