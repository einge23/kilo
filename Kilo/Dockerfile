FROM mcr.microsoft.com/dotnet/aspnet:10.0@sha256:222759b391a1aaf241166672c8f99b2d4ada452e7b5319f3c6e8f265a37b5ad4 AS base
USER $APP_UID
WORKDIR /app
ENV ASPNETCORE_HTTP_PORTS=8080
EXPOSE 8080

FROM mcr.microsoft.com/dotnet/sdk:10.0@sha256:e70cdb7f80b0348f5cb85f19a8f670fca061f033d57eed12fa003d58b0e06317 AS build
WORKDIR /src
COPY global.json Kilo.slnx ./
COPY Kilo/Kilo.csproj Kilo/packages.lock.json Kilo/
COPY Kilo.Persistence/Kilo.Persistence.csproj Kilo.Persistence/packages.lock.json Kilo.Persistence/
RUN dotnet restore Kilo/Kilo.csproj --locked-mode
COPY Kilo/ Kilo/
COPY Kilo.Persistence/ Kilo.Persistence/
RUN dotnet publish Kilo/Kilo.csproj -c Release --no-restore -o /out /p:UseAppHost=false

FROM base AS final
ARG RELEASE_VERSION=development
LABEL org.opencontainers.image.version=$RELEASE_VERSION
COPY --from=build /out .
ENTRYPOINT ["dotnet", "Kilo.dll"]
