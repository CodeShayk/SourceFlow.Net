#!/usr/bin/env pwsh
<#
.SYNOPSIS
    Launches LocalStack via Docker and runs AWS integration tests locally.

.DESCRIPTION
    This script:
    1. Checks Docker is running
    2. Starts a LocalStack container (or reuses an existing one)
    3. Waits for services (SQS, SNS, KMS) to be healthy
    4. Sets required environment variables
    5. Runs the integration tests
    6. Tears down the container (unless -KeepRunning is specified)

.PARAMETER KeepRunning
    Keep LocalStack container running after tests complete.

.PARAMETER Filter
    Optional test filter expression (passed to dotnet test --filter).

.PARAMETER Configuration
    Build configuration (default: Debug).

.EXAMPLE
    ./run-integration-tests.ps1
    ./run-integration-tests.ps1 -KeepRunning
    ./run-integration-tests.ps1 -Filter "FullyQualifiedName~SqsStandard"
#>
param(
    [switch]$KeepRunning,
    [string]$Filter = "",
    [string]$Configuration = "Debug"
)

$ErrorActionPreference = "Stop"
$ContainerName = "sourceflow-localstack"
$LocalStackPort = 4566
$LocalStackEndpoint = "http://localhost:$LocalStackPort"
$HealthUrl = "$LocalStackEndpoint/_localstack/health"
$ScriptDir = $PSScriptRoot
$ProjectDir = $ScriptDir

# --- Helper functions ---

function Write-Step($message) {
    Write-Host "`n>> $message" -ForegroundColor Cyan
}

function Test-DockerRunning {
    try {
        docker info 2>&1 | Out-Null
        return $LASTEXITCODE -eq 0
    } catch {
        return $false
    }
}

function Test-LocalStackHealthy {
    try {
        $response = Invoke-RestMethod -Uri $HealthUrl -TimeoutSec 5 -ErrorAction Stop
        return $true
    } catch {
        return $false
    }
}

function Wait-ForLocalStack {
    param([int]$MaxAttempts = 30, [int]$DelaySeconds = 3)

    Write-Host "Waiting for LocalStack to become healthy..."
    for ($i = 1; $i -le $MaxAttempts; $i++) {
        if (Test-LocalStackHealthy) {
            Write-Host "LocalStack is healthy!" -ForegroundColor Green
            $health = Invoke-RestMethod -Uri $HealthUrl -TimeoutSec 5
            Write-Host "Services: $($health.services | ConvertTo-Json -Compress)"
            return $true
        }
        Write-Host "  Attempt $i/$MaxAttempts - not ready yet..."
        Start-Sleep -Seconds $DelaySeconds
    }
    Write-Host "LocalStack did not become healthy in time." -ForegroundColor Red
    return $false
}

function Wait-ForServices {
    param([string[]]$Services = @("sqs", "sns", "kms"), [int]$MaxAttempts = 20, [int]$DelaySeconds = 3)

    Write-Host "Waiting for services: $($Services -join ', ')..."
    for ($i = 1; $i -le $MaxAttempts; $i++) {
        try {
            $health = Invoke-RestMethod -Uri $HealthUrl -TimeoutSec 5
            $allReady = $true
            foreach ($svc in $Services) {
                $status = $health.services.$svc
                if ($status -ne "available" -and $status -ne "running") {
                    $allReady = $false
                    break
                }
            }
            if ($allReady) {
                Write-Host "All services ready!" -ForegroundColor Green
                return $true
            }
        } catch { }
        Write-Host "  Attempt $i/$MaxAttempts - services not all ready..."
        Start-Sleep -Seconds $DelaySeconds
    }
    Write-Host "Services did not become ready in time." -ForegroundColor Red
    return $false
}

# --- Main ---

Write-Step "Checking Docker"
if (-not (Test-DockerRunning)) {
    Write-Host "Docker is not running. Please start Docker Desktop and try again." -ForegroundColor Red
    exit 1
}
Write-Host "Docker is running." -ForegroundColor Green

# Check if LocalStack is already running
$existingContainer = docker ps --filter "name=$ContainerName" --format "{{.Names}}" 2>$null
$alreadyRunning = $false

if ($existingContainer -eq $ContainerName) {
    Write-Step "Found existing LocalStack container '$ContainerName'"
    if (Test-LocalStackHealthy) {
        Write-Host "Container is healthy - reusing it." -ForegroundColor Green
        $alreadyRunning = $true
    } else {
        Write-Host "Container exists but not healthy. Removing and recreating..."
        docker rm -f $ContainerName 2>$null | Out-Null
    }
} else {
    # Also check if any container is using port 4566
    $portInUse = docker ps --format "{{.Ports}}" 2>$null | Select-String ":$LocalStackPort->"
    if ($portInUse) {
        Write-Host "Port $LocalStackPort is already in use by another container." -ForegroundColor Yellow
        if (Test-LocalStackHealthy) {
            Write-Host "LocalStack is responding on port $LocalStackPort - reusing it." -ForegroundColor Green
            $alreadyRunning = $true
        } else {
            Write-Host "Port $LocalStackPort is in use but not responding as LocalStack." -ForegroundColor Red
            Write-Host "Please free port $LocalStackPort and try again."
            exit 1
        }
    }
}

if (-not $alreadyRunning) {
    Write-Step "Starting LocalStack container"
    docker run -d `
        --name $ContainerName `
        -p "${LocalStackPort}:${LocalStackPort}" `
        -e "SERVICES=sqs,sns,kms" `
        -e "DEBUG=1" `
        -e "EAGER_SERVICE_LOADING=1" `
        -e "SKIP_SSL_CERT_DOWNLOAD=1" `
        localstack/localstack:latest

    if ($LASTEXITCODE -ne 0) {
        Write-Host "Failed to start LocalStack container." -ForegroundColor Red
        exit 1
    }
    Write-Host "Container started." -ForegroundColor Green
}

Write-Step "Waiting for LocalStack health"
if (-not (Wait-ForLocalStack)) {
    Write-Host "Dumping container logs for diagnostics:" -ForegroundColor Yellow
    docker logs $ContainerName 2>&1 | Select-Object -Last 30
    exit 1
}

Write-Step "Waiting for AWS services"
if (-not (Wait-ForServices -Services @("sqs", "sns", "kms"))) {
    Write-Host "Dumping container logs for diagnostics:" -ForegroundColor Yellow
    docker logs $ContainerName 2>&1 | Select-Object -Last 30
    exit 1
}

Write-Step "Setting environment variables"
$env:AWS_ACCESS_KEY_ID = "test"
$env:AWS_SECRET_ACCESS_KEY = "test"
$env:AWS_DEFAULT_REGION = "us-east-1"
$env:AWS_ENDPOINT_URL = $LocalStackEndpoint

Write-Host "  AWS_ACCESS_KEY_ID     = $env:AWS_ACCESS_KEY_ID"
Write-Host "  AWS_SECRET_ACCESS_KEY = $env:AWS_SECRET_ACCESS_KEY"
Write-Host "  AWS_DEFAULT_REGION    = $env:AWS_DEFAULT_REGION"
Write-Host "  AWS_ENDPOINT_URL      = $env:AWS_ENDPOINT_URL"

Write-Step "Running integration tests"
$testArgs = @(
    "test"
    $ProjectDir
    "--configuration", $Configuration
    "--logger", "console;verbosity=normal"
    "--", "RunConfiguration.TestSessionTimeout=600000"
)

if ($Filter) {
    $testArgs += "--filter"
    $testArgs += $Filter
}

& dotnet @testArgs
$testExitCode = $LASTEXITCODE

if (-not $KeepRunning -and -not $alreadyRunning) {
    Write-Step "Stopping LocalStack container"
    docker rm -f $ContainerName 2>$null | Out-Null
    Write-Host "Container removed." -ForegroundColor Green
} else {
    Write-Host "`nLocalStack container '$ContainerName' is still running." -ForegroundColor Yellow
    Write-Host "  Stop it with: docker rm -f $ContainerName"
}

Write-Host ""
if ($testExitCode -eq 0) {
    Write-Host "All tests passed!" -ForegroundColor Green
} else {
    Write-Host "Some tests failed (exit code: $testExitCode)." -ForegroundColor Red
}

exit $testExitCode
