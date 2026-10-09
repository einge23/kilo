[CmdletBinding()]
param(
    [ValidateSet('linux/amd64', 'linux/arm64')]
    [string] $Platform = 'linux/amd64'
)

$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$checkId = [guid]::NewGuid().ToString('N')
$project = "kilo-compose-check-$checkId"
$work = Join-Path $root ".artifacts/$project"
$context = Join-Path $work 'context'
$apiImage = "${project}:api"
$migrationImage = "${project}:migrations"
$previousPassword = $env:POSTGRES_PASSWORD
$previousIssuer = $env:CLERK_ISSUER
$previousParty = $env:CLERK_AUTHORIZED_PARTY
$password = [guid]::NewGuid().ToString('N')
$composeArgs = @()

function Invoke-Docker {
    & docker @args
    if ($LASTEXITCODE -ne 0) { throw "Docker failed: $($args[0])" }
}

function Invoke-Compose { Invoke-Docker @composeArgs @args }

function Assert-Check([bool] $Condition, [string] $Message) {
    if (-not $Condition) { throw $Message }
}

function Get-ServiceContainer([string] $Service) {
    $id = Invoke-Compose ps --all --quiet $Service
    Assert-Check ([bool] $id) "Missing $Service container."
    (Invoke-Docker inspect $id | ConvertFrom-Json)[0]
}

function Invoke-Sql([string] $Sql) {
    Invoke-Compose exec -T db psql -U postgres -d kilo -v ON_ERROR_STOP=1 -Atc $Sql
}

function Assert-Healthy {
    $db = Get-ServiceContainer db
    $migration = Get-ServiceContainer migrations
    $api = Get-ServiceContainer api
    Assert-Check ($db.State.Health.Status -eq 'healthy') 'Postgres is not healthy.'
    Assert-Check ($migration.State.Status -eq 'exited' -and $migration.State.ExitCode -eq 0) 'Migration did not complete successfully.'
    Assert-Check ([DateTimeOffset]::Parse($api.State.StartedAt) -ge
        [DateTimeOffset]::Parse($migration.State.FinishedAt)) 'API started before migration success.'
    foreach ($container in @($db, $api)) {
        $bindings = @($container.NetworkSettings.Ports.PSObject.Properties.Value | Where-Object { $_ })
        Assert-Check ($bindings.Count -gt 0) 'Development port is not published.'
        foreach ($binding in $bindings) {
            Assert-Check ($binding.HostIp -eq '127.0.0.1') 'Development port is not loopback-only.'
        }
    }
    $port = $api.NetworkSettings.Ports.'8080/tcp'[0].HostPort
    $healthy = $false
    for ($attempt = 0; $attempt -lt 30; $attempt++) {
        try {
            $response = Invoke-WebRequest "http://127.0.0.1:$port/health" -NoProxy -TimeoutSec 3
            if ($response.StatusCode -eq 200 -and $response.Content -eq 'Healthy') { $healthy = $true; break }
        } catch { }
        Start-Sleep -Milliseconds 250
    }
    Assert-Check $healthy 'API did not return 200 Healthy.'
    Write-Host 'Dependency order, loopback bindings, and 200 Healthy passed.'
}

try {
    New-Item -ItemType Directory -Path $context -Force | Out-Null
    $files = & git -C $root ls-files --cached --others --exclude-standard
    if ($LASTEXITCODE -ne 0) { throw 'Cannot enumerate repository source.' }
    foreach ($file in $files) {
        if ($file -notmatch '^(\.dockerignore|global\.json|Kilo\.slnx|compose(\.override)?\.yaml)$|^Kilo(\.Migrations|\.Persistence)?/') { continue }
        if (-not (Test-Path -LiteralPath (Join-Path $root $file))) { continue }
        $destination = Join-Path $context $file
        New-Item -ItemType Directory -Path (Split-Path $destination -Parent) -Force | Out-Null
        Copy-Item -LiteralPath (Join-Path $root $file) -Destination $destination
    }
    $emptyEnv = Join-Path $work 'empty.env'
    Set-Content -LiteralPath $emptyEnv -Value ''
    $baseFile = Join-Path $context 'compose.yaml'
    $devFile = Join-Path $context 'compose.override.yaml'
    $overrideFile = Join-Path $work 'check.yaml'
    @"
services:
  api:
    image: $apiImage
    ports: !override
      - "127.0.0.1::8080"
  migrations:
    image: $migrationImage
  db:
    ports: !override
      - "127.0.0.1::5432"
"@ | Set-Content -LiteralPath $overrideFile
    $common = @('compose', '--project-name', $project, '--env-file', $emptyEnv)
    $composeArgs = $common + @('-f', $baseFile, '-f', $devFile, '-f', $overrideFile)
    $env:POSTGRES_PASSWORD = $password
    $env:CLERK_ISSUER = 'https://clerk.kilo.test'
    $env:CLERK_AUTHORIZED_PARTY = 'https://frontend.kilo.test'

    # Inspect the actual base/development files before replacing fixed ports for isolation.
    $base = (Invoke-Docker @common -f $baseFile config --format json | ConvertFrom-Json)
    foreach ($service in @($base.services.api, $base.services.db, $base.services.migrations)) {
        Assert-Check (@($service.ports | Where-Object { $_ }).Count -eq 0) 'Base configuration publishes ports.'
    }
    Assert-Check ($base.services.db.environment.POSTGRES_PASSWORD -eq $password) 'Database password is hardcoded.'
    foreach ($service in @($base.services.api, $base.services.migrations)) {
        $connection = $service.environment.ConnectionStrings__Postgres
        Assert-Check ($connection.Contains("Password=$password;") -and $connection.Contains('Host=db;Database=kilo;')) 'Container connection configuration is incorrect.'
    }
    Assert-Check ($base.services.api.environment.Clerk__Issuer -eq $env:CLERK_ISSUER -and
        $base.services.api.environment.Clerk__AuthorizedParties__0 -eq $env:CLERK_AUTHORIZED_PARTY) 'Clerk configuration was not passed to the API.'
    $development = (Invoke-Docker @common -f $baseFile -f $devFile config --format json | ConvertFrom-Json)
    Assert-Check ($development.services.api.ports[0].published -eq '8080' -and
        $development.services.db.ports[0].published -eq '5432') 'Development ports changed unexpectedly.'
    foreach ($service in @($development.services.api, $development.services.db)) {
        foreach ($port in $service.ports) { Assert-Check ($port.host_ip -eq '127.0.0.1') 'Development config publishes externally.' }
    }
    $env:POSTGRES_PASSWORD = ''
    $missingPasswordOutput = & docker @common -f $baseFile config --quiet 2>&1
    Assert-Check ($LASTEXITCODE -ne 0) 'Base configuration accepts a missing password.'
    $env:POSTGRES_PASSWORD = $password
    Write-Host 'Base/development configuration and required-password checks passed.'

    Invoke-Docker build --pull --platform $Platform --build-arg "RELEASE_VERSION=$project" `
        --file (Join-Path $context 'Kilo/Dockerfile') --tag $apiImage $context
    Invoke-Docker build --pull --platform $Platform --build-arg "RELEASE_VERSION=$project" `
        --file (Join-Path $context 'Kilo.Migrations/Dockerfile') --tag $migrationImage $context
    Invoke-Compose up -d --no-build
    Assert-Healthy
    $originalDb = Get-ServiceContainer db
    $volume = ($originalDb.Mounts | Where-Object Destination -eq '/var/lib/postgresql').Name
    Assert-Check ($volume -eq "${project}_pgdata") 'Database volume is not isolated.'
    $fixture = "compose_fixture_$checkId"
    $fixtureId = Invoke-Sql "INSERT INTO users(clerk_user_id) VALUES ('$fixture') RETURNING id"
    $fixtureId = @($fixtureId | Where-Object { $_ -match '^\d+$' })[0]
    Assert-Check ([int] $fixtureId -gt 0) 'Users fixture was not created.'
    $initialScripts = [int] (Invoke-Sql 'SELECT count(*) FROM public."__EFMigrationsHistory"')

    Invoke-Compose down
    Invoke-Docker volume inspect $volume | Out-Null
    Invoke-Compose up -d --no-build
    Assert-Healthy
    $recreatedDb = Get-ServiceContainer db
    Assert-Check ($recreatedDb.Id -ne $originalDb.Id) 'Database container was not recreated.'
    Assert-Check (($recreatedDb.Mounts | Where-Object Destination -eq '/var/lib/postgresql').Name -eq $volume) 'Database volume changed.'
    Assert-Check ((Invoke-Sql "SELECT id FROM users WHERE clerk_user_id = '$fixture'") -eq $fixtureId) 'Users fixture did not survive recreation.'
    Assert-Check ([int] (Invoke-Sql 'SELECT count(*) FROM public."__EFMigrationsHistory"') -eq $initialScripts) 'Applied migrations reran.'
    Write-Host 'Container recreation preserved the users fixture and migration journal.'

    # This deliberately bad EF migration exists only in the disposable source snapshot.
    $testTable = "compose_gate_$checkId"
    $migrationId = "20991231235959_ComposeGate_$checkId"
    $testMigration = Join-Path $context "Kilo.Persistence/Migrations/ComposeGate.cs"
    $repairSql = "CREATE TABLE $testTable (marker text PRIMARY KEY); INSERT INTO $testTable VALUES ('repaired');"
    $migrationSource = @"
using Kilo.Persistence;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
namespace Kilo.Persistence.Migrations;
[DbContext(typeof(KiloDbContext))]
[Migration("$migrationId")]
public sealed class ComposeGate : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
        => migrationBuilder.Sql("__CHECK_SQL__");
    protected override void Down(MigrationBuilder migrationBuilder)
        => migrationBuilder.Sql("DROP TABLE $testTable;");
}
"@
    Set-Content -LiteralPath $testMigration -Value $migrationSource.Replace('__CHECK_SQL__', "$repairSql SELECT 1 / 0;")
    Invoke-Docker build --platform $Platform --build-arg "RELEASE_VERSION=$project" `
        --file (Join-Path $context 'Kilo.Migrations/Dockerfile') --tag $migrationImage $context
    # Dependency gates apply to startup, so remove the running API before exercising failure.
    Invoke-Compose rm --stop --force api migrations
    $failureOutput = & docker @composeArgs up -d --no-build 2>&1
    Assert-Check ($LASTEXITCODE -ne 0) 'Compose accepted a failed migration.'
    $failedMigration = Get-ServiceContainer migrations
    Assert-Check ($failedMigration.State.Status -eq 'exited' -and $failedMigration.State.ExitCode -ne 0) 'Migrator failure was not visible.'
    $blockedApi = Get-ServiceContainer api
    Assert-Check ($blockedApi.State.Status -eq 'created' -and -not $blockedApi.State.Running) 'API started despite migration failure.'
    $logs = Invoke-Compose logs --no-color migrations 2>&1
    Assert-Check (($logs -join "`n").Contains('Migration failed; release not activated.')) 'Migration failure diagnostic is missing.'
    Assert-Check ((Invoke-Sql "SELECT to_regclass('public.$testTable') IS NULL") -eq 't') 'Failed migration changes did not roll back.'
    Assert-Check ([int] (Invoke-Sql "SELECT count(*) FROM public.`"__EFMigrationsHistory`" WHERE `"MigrationId`" = '$migrationId'") -eq 0) 'Failed migration was journaled.'
    Assert-Check ((Invoke-Sql "SELECT id FROM users WHERE clerk_user_id = '$fixture'") -eq $fixtureId) 'Failed migration damaged existing data.'
    Write-Host 'Failed EF migration exited nonzero, blocked API startup, and rolled back.'

    Set-Content -LiteralPath $testMigration -Value $migrationSource.Replace('__CHECK_SQL__', $repairSql)
    Invoke-Docker build --platform $Platform --build-arg "RELEASE_VERSION=$project" `
        --file (Join-Path $context 'Kilo.Migrations/Dockerfile') --tag $migrationImage $context
    Invoke-Compose rm --stop --force api migrations
    Invoke-Compose up -d --no-build
    Assert-Healthy
    Assert-Check ((Invoke-Sql "SELECT marker FROM $testTable") -eq 'repaired') 'Repaired migration was not applied.'
    Assert-Check ([int] (Invoke-Sql 'SELECT count(*) FROM public."__EFMigrationsHistory"') -eq ($initialScripts + 1)) 'Repaired migration journal is incorrect.'
    Assert-Check ((Invoke-Sql "SELECT id FROM users WHERE clerk_user_id = '$fixture'") -eq $fixtureId) 'Repair damaged existing data.'
    Invoke-Compose rm --stop --force api migrations
    Invoke-Compose up -d --no-build
    Assert-Healthy
    Assert-Check ([int] (Invoke-Sql 'SELECT count(*) FROM public."__EFMigrationsHistory"') -eq ($initialScripts + 1)) 'Successful migration reran.'
    Write-Host 'Repair and a fresh repeat migration succeeded without data loss or duplicate journal entries.'

    [pscustomobject]@{
        VerifiedAtUtc = [DateTimeOffset]::UtcNow.ToString('O')
        Platform = $Platform
        FreshStartup = $true
        HostDbNetworking = $true
        DataSurvivesRecreation = $true
        MigrationFailureBlocksApi = $true
        FailedMigrationRollsBack = $true
        RepairAndRepeatSucceed = $true
        DevelopmentPortsAreLoopback = $true
        BasePublishesNoPorts = $true
        MissingPasswordRejected = $true
    } | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $work 'checks.json')
    Write-Host "Slice 03 checks passed. Evidence: $work/checks.json"
} finally {
    try {
        $env:POSTGRES_PASSWORD = $password
        Assert-Check ($project -eq "kilo-compose-check-$checkId") 'Unsafe cleanup project.'
        if ($composeArgs.Count -gt 0) { Invoke-Compose down --volumes }
        foreach ($image in @($apiImage, $migrationImage)) {
            & docker image inspect $image 2>$null | Out-Null
            if ($LASTEXITCODE -eq 0) { Invoke-Docker image rm $image | Out-Null }
        }
    } finally {
        $env:POSTGRES_PASSWORD = $previousPassword
        $env:CLERK_ISSUER = $previousIssuer
        $env:CLERK_AUTHORIZED_PARTY = $previousParty
        foreach ($name in @('context', 'check.yaml', 'empty.env')) {
            $target = [IO.Path]::GetFullPath((Join-Path $work $name))
            $boundary = [IO.Path]::GetFullPath($work) + [IO.Path]::DirectorySeparatorChar
            Assert-Check ($target.StartsWith($boundary, [StringComparison]::OrdinalIgnoreCase)) 'Unsafe scratch cleanup path.'
            if (Test-Path -LiteralPath $target) { Remove-Item -LiteralPath $target -Recurse -Force }
        }
    }
}
