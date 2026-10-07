[CmdletBinding()]
param(
    [ValidatePattern('^[a-zA-Z0-9][a-zA-Z0-9_.-]{0,80}$')]
    [string] $Release = 'slice02',
    [ValidateSet('linux/amd64', 'linux/arm64')]
    [string] $Platform = 'linux/amd64'
)

$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$checkId = [guid]::NewGuid().ToString('N')
$work = Join-Path $root ".artifacts/image-check-$checkId"
$context = Join-Path $work 'context'
$containers = @()

function Invoke-Docker {
    & docker @args
    if ($LASTEXITCODE -ne 0) { throw "Docker failed: $($args[0])" }
}

function Assert-Check([bool] $Condition, [string] $Message) {
    if (-not $Condition) { throw $Message }
}

try {
    New-Item -ItemType Directory -Path $context -Force | Out-Null
    # Copy version-controlled and unignored source, never local bin/obj or credentials.
    $files = & git -C $root ls-files --cached --others --exclude-standard
    if ($LASTEXITCODE -ne 0) { throw 'Cannot enumerate repository source.' }
    foreach ($file in $files) {
        if ($file -notmatch '^(\.dockerignore|global\.json|Kilo\.slnx)$|^Kilo(\.Migrations|\.Persistence)?/') { continue }
        if (-not (Test-Path -LiteralPath (Join-Path $root $file))) { continue }
        $destination = Join-Path $context $file
        New-Item -ItemType Directory -Path (Split-Path $destination -Parent) -Force | Out-Null
        Copy-Item -LiteralPath (Join-Path $root $file) -Destination $destination
    }
    Assert-Check (-not (Test-Path (Join-Path $context 'Kilo/bin'))) 'Build context is not clean.'

    # Harmless sentinels exercise the real Docker ignore rules, including inside project folders.
    foreach ($file in @('.env', 'Kilo/.env', 'Kilo/bin/secret-marker', 'Kilo/obj/secret-marker',
        'Kilo/dev.pem', '.git/secret-marker', '.idea/secret-marker', 'tmp/secret-marker',
        'output/secret-marker', '.artifacts/secret-marker')) {
        $destination = Join-Path $context $file
        New-Item -ItemType Directory -Path (Split-Path $destination -Parent) -Force | Out-Null
        Set-Content -LiteralPath $destination -Value 'kilo-ignore-check' -NoNewline
    }
    $auditDockerfile = Join-Path $work 'Context.Dockerfile'
    Set-Content -LiteralPath $auditDockerfile -Value "FROM scratch`nCOPY . /context"
    Invoke-Docker build --file $auditDockerfile --output "type=local,dest=$work/audit" $context
    $leaks = Get-ChildItem (Join-Path $work 'audit') -Recurse -File |
        Where-Object { $_.Name -eq 'secret-marker' -or $_.Name -eq '.env' -or $_.Extension -eq '.pem' }
    Assert-Check ($leaks.Count -eq 0) 'Docker build context contains excluded files.'

    $images = @("kilo:$Release", "kilo-migrations:$Release")
    $dockerfiles = @('Kilo/Dockerfile', 'Kilo.Migrations/Dockerfile')
    for ($i = 0; $i -lt $images.Count; $i++) {
        Invoke-Docker build --pull --no-cache --platform $Platform --build-arg "RELEASE_VERSION=$Release" `
            --file (Join-Path $context $dockerfiles[$i]) --tag $images[$i] $context
    }

    # Compile a dependency-free probe with the same pinned SDK; run it in each final image.
    $probe = Join-Path $work 'probe'
    Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'ImageProbe') -Destination $probe -Recurse
    $sdk = [regex]::Match((Get-Content (Join-Path $context 'Kilo/Dockerfile') -Raw),
        '(?m)^FROM (\S+) AS build').Groups[1].Value
    Invoke-Docker run --rm --platform $Platform --volume "${probe}:/src" --workdir /src $sdk `
        dotnet publish -c Release -o /src/published /p:UseAppHost=false

    $evidence = @()
    foreach ($image in $images) {
        $metadata = (Invoke-Docker image inspect $image | ConvertFrom-Json)[0]
        Assert-Check ("$($metadata.Os)/$($metadata.Architecture)" -eq $Platform) "Wrong platform: $image"
        Assert-Check ($metadata.Config.User -match '^[1-9][0-9]*$') "Root or unspecified UID: $image"
        Assert-Check ($metadata.Config.Labels.'org.opencontainers.image.version' -eq $Release) 'Release labels differ.'
        Assert-Check (-not ($metadata.Config.Env -match '(?i)ConnectionStrings|Password|Token|Secret')) 'Image embeds credentials.'
        $uid = Invoke-Docker run --rm --entrypoint id $image -u
        Assert-Check ($uid -eq $metadata.Config.User) "Runtime UID differs: $image"

        # Inspect every saved layer, including files hidden by later layers.
        $layers = Join-Path $work ("layers-" + $evidence.Count)
        New-Item -ItemType Directory -Path $layers -Force | Out-Null
        $archive = Join-Path $layers 'image.tar'
        Invoke-Docker image save --output $archive $image
        & tar -xf $archive -C $layers manifest.json
        if ($LASTEXITCODE -ne 0) { throw 'Cannot read image manifest.' }
        $manifest = (Get-Content (Join-Path $layers 'manifest.json') -Raw | ConvertFrom-Json)[0]
        foreach ($layer in $manifest.Layers) {
            & tar -xf $archive -C $layers $layer
            if ($LASTEXITCODE -ne 0) { throw 'Cannot read image layer.' }
            $entries = & tar -tf (Join-Path $layers $layer)
            if ($LASTEXITCODE -ne 0) { throw 'Cannot inspect image layer.' }
            $sensitive = $entries -match '(^|/)(\.env(\.[^/]*)?|\.git|\.idea|secrets[^/]*\.json)(/|$)|^app/.*\.(pfx|pem|key)$'
            Assert-Check (-not $sensitive) "Excluded files found in layers: $image"
        }
        Invoke-Docker run --rm --platform $Platform --volume "${probe}/published:/probe:ro" `
            --entrypoint dotnet $image /probe/ImageProbe.dll $Platform
        $evidence += [pscustomobject]@{ Tag = $image; ImageId = $metadata.Id; Platform = $Platform; UID = $metadata.Config.User }
    }

    # Isolate from any user's stack. No database is needed for an unavailable-readiness response.
    $apiName = "kilo-image-check-$checkId"
    $containers += $apiName
    Invoke-Docker run -d --name $apiName --platform $Platform --publish '127.0.0.1::8080' `
        --env DOTNET_ENVIRONMENT=Production `
        --env 'ConnectionStrings__Postgres=Host=127.0.0.1;Port=1;Database=kilo;Username=test;Timeout=1;GSS Encryption Mode=Disable' `
        $images[0] | Out-Null
    $api = (Invoke-Docker inspect $apiName | ConvertFrom-Json)[0]
    $port = $api.NetworkSettings.Ports.'8080/tcp'[0].HostPort
    $http = [System.Net.Http.HttpClient]::new()
    $http.Timeout = [TimeSpan]::FromSeconds(5)
    try {
        $ready = $false
        for ($attempt = 0; $attempt -lt 20; $attempt++) {
            try {
                $response = $http.GetAsync("http://127.0.0.1:$port/health").GetAwaiter().GetResult()
                if ([int] $response.StatusCode -eq 503) { $ready = $true; break }
            } catch { }
            Start-Sleep -Milliseconds 250
        }
        Assert-Check $ready 'API did not bind to port 8080 with environment-only configuration.'
        $openApi = $http.GetAsync("http://127.0.0.1:$port/openapi/v1.json").GetAwaiter().GetResult()
        Assert-Check ([int] $openApi.StatusCode -eq 404) 'Production exposes OpenAPI.'
    } finally { $http.Dispose() }
    Invoke-Docker stop --signal SIGTERM -t 10 $apiName | Out-Null
    $stopped = (Invoke-Docker inspect $apiName | ConvertFrom-Json)[0]
    Assert-Check ($stopped.State.ExitCode -eq 0 -and -not $stopped.State.OOMKilled) 'API did not exit cleanly on SIGTERM.'
    $logs = Invoke-Docker logs $apiName 2>&1
    Assert-Check (($logs -join "`n") -match 'Application is shutting down') 'Host shutdown was not observed.'

    $migrationName = "kilo-image-migration-check-$checkId"
    $containers += $migrationName
    Invoke-Docker create --name $migrationName --env ConnectionStrings__Postgres= $images[1] | Out-Null
    Invoke-Docker start $migrationName | Out-Null
    $exitCode = Invoke-Docker wait $migrationName
    Assert-Check ([int] $exitCode -ne 0) 'Migrator accepted missing environment configuration.'
    $logs = Invoke-Docker logs $migrationName 2>&1
    Assert-Check (($logs -join "`n") -match 'ConnectionStrings:Postgres is required') 'Missing migrator diagnostic.'

    $evidence | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $work 'images.json')
    $evidence | Format-Table
    Write-Host "Slice 02 image checks passed. Evidence: $work/images.json"
    Write-Host 'Transactional shutdown must be rechecked once transactional feature writes exist.'
} finally {
    foreach ($container in $containers) {
        & docker rm -f $container 2>$null | Out-Null
    }
    # Delete only scratch directories allocated under this unique verification workspace.
    foreach ($name in @('context', 'audit', 'probe', 'layers-0', 'layers-1')) {
        $target = [IO.Path]::GetFullPath((Join-Path $work $name))
        $boundary = [IO.Path]::GetFullPath($work) + [IO.Path]::DirectorySeparatorChar
        Assert-Check ($target.StartsWith($boundary, [StringComparison]::OrdinalIgnoreCase)) 'Unsafe scratch cleanup path.'
        if (Test-Path -LiteralPath $target) { Remove-Item -LiteralPath $target -Recurse -Force }
    }
}
