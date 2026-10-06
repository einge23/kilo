FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS base
USER $APP_UID
WORKDIR /app
ENV ASPNETCORE_HTTP_PORTS=8080
EXPOSE 8080

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
ARG BUILD_CONFIGURATION=Release
WORKDIR /src
COPY global.json ./
COPY Kilo/Kilo.csproj Kilo/packages.lock.json Kilo/
RUN dotnet restore Kilo/Kilo.csproj --locked-mode
COPY Kilo/ Kilo/
RUN dotnet publish Kilo/Kilo.csproj -c $BUILD_CONFIGURATION --no-restore -o /out /p:UseAppHost=false

FROM base AS final
COPY --from=build /out .
ENTRYPOINT ["dotnet", "Kilo.dll"]
