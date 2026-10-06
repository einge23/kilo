FROM mcr.microsoft.com/dotnet/runtime:10.0@sha256:b89586dc17781f25531909993658aa8161205ae38b8cec8847df4a8221a403d5 AS base
USER $APP_UID
WORKDIR /app

FROM mcr.microsoft.com/dotnet/sdk:10.0@sha256:e70cdb7f80b0348f5cb85f19a8f670fca061f033d57eed12fa003d58b0e06317 AS build
WORKDIR /src
COPY global.json Kilo.slnx ./
COPY Kilo.Migrations/Kilo.Migrations.csproj Kilo.Migrations/packages.lock.json Kilo.Migrations/
RUN dotnet restore Kilo.Migrations/Kilo.Migrations.csproj --locked-mode
COPY Kilo.Migrations/ Kilo.Migrations/
RUN dotnet publish Kilo.Migrations/Kilo.Migrations.csproj -c Release --no-restore -o /out /p:UseAppHost=false

FROM base AS final
ARG RELEASE_VERSION=development
LABEL org.opencontainers.image.version=$RELEASE_VERSION
COPY --from=build /out .
ENTRYPOINT ["dotnet", "Kilo.Migrations.dll"]
