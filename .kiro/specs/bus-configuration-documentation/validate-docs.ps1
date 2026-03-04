# Documentation Validation Script for Bus Configuration System
# This script validates that all required documentation elements are present

param(
    [switch]$Verbose
)

$ErrorActionPreference = "Stop"

Write-Host "=== Bus Configuration System Documentation Validation ===" -ForegroundColor Cyan
Write-Host ""

# Define required documentation elements
$requiredElements = @{
    "docs/SourceFlow.Net-README.md" = @(
        "Cloud Configuration with Bus Configuration System",
        "BusConfigurationBuilder",
        "BusConfiguration",
        "Bootstrapper",
        "Send - Command Routing",
        "Raise - Event Publishing",
        "Listen - Command Queue Listeners",
        "Subscribe - Topic Subscriptions",
        "FIFO Queue Configuration",
        "CircuitBreakerOpenException",
        "CircuitBreakerStateChangedEventArgs",
        "Resilience Patterns"
    )
    "README.md" = @(
        "Bus Configuration System"
    )
    ".kiro/steering/sourceflow-cloud-aws.md" = @(
        "SQS Queue URL Resolution",
        "SNS Topic ARN Resolution",
        "FIFO Queue Configuration",
        "Bootstrapper Resource Creation",
        "IAM Permission Requirements"
    )
    ".kiro/steering/sourceflow-cloud-azure.md" = @(
        "Service Bus Queue Name Usage",
        "Service Bus Topic Name Usage",
        "Session-Enabled Queue Configuration",
        "Bootstrapper Resource Creation",
        "Managed Identity Integration"
    )
    "docs/Cloud-Integration-Testing.md" = @(
        "Testing Bus Configuration",
        "Unit Testing Bus Configuration",
        "Integration Testing with Emulators",
        "Validation Strategies"
    )
}

$missingElements = @()
$foundElements = 0
$totalElements = 0

# Check each file for required elements
foreach ($file in $requiredElements.Keys) {
    Write-Host "Checking $file..." -ForegroundColor Yellow
    
    if (-not (Test-Path $file)) {
        Write-Host "  ERROR: File not found!" -ForegroundColor Red
        $missingElements += "File not found: $file"
        continue
    }
    
    $content = Get-Content $file -Raw
    $elements = $requiredElements[$file]
    
    foreach ($element in $elements) {
        $totalElements++
        if ($content -match [regex]::Escape($element)) {
            $foundElements++
            if ($Verbose) {
                Write-Host "  ✓ Found: $element" -ForegroundColor Green
            }
        } else {
            Write-Host "  ✗ Missing: $element" -ForegroundColor Red
            $missingElements += "${file}: $element"
        }
    }
    
    Write-Host ""
}

# Check for code examples using short names (not full URLs/ARNs)
Write-Host "Checking for full URLs/ARNs in configuration code examples..." -ForegroundColor Yellow

$codeFiles = @(
    "docs/SourceFlow.Net-README.md",
    ".kiro/steering/sourceflow-cloud-aws.md",
    ".kiro/steering/sourceflow-cloud-azure.md"
)

$urlPatterns = @(
    'Queue\("https://sqs\.',
    'Queue\("arn:aws:sqs:',
    'Topic\("arn:aws:sns:',
    'Queue\("[^"]*\.servicebus\.windows\.net/'
)

$urlViolations = @()

foreach ($file in $codeFiles) {
    if (Test-Path $file) {
        $content = Get-Content $file -Raw
        
        # Extract code blocks
        $codeBlocks = [regex]::Matches($content, '```csharp(.*?)```', [System.Text.RegularExpressions.RegexOptions]::Singleline)
        
        foreach ($block in $codeBlocks) {
            $code = $block.Groups[1].Value
            
            foreach ($pattern in $urlPatterns) {
                if ($code -match $pattern) {
                    $urlViolations += "${file}: Found full URL/ARN in Queue/Topic configuration: $pattern"
                    Write-Host "  ✗ Found full URL/ARN in configuration in $file" -ForegroundColor Red
                }
            }
        }
    }
}

if ($urlViolations.Count -eq 0) {
    Write-Host "  ✓ No full URLs/ARNs found in Queue/Topic configurations" -ForegroundColor Green
}

Write-Host ""

# Summary
Write-Host "=== Validation Summary ===" -ForegroundColor Cyan
Write-Host "Total elements checked: $totalElements" -ForegroundColor White
Write-Host "Elements found: $foundElements" -ForegroundColor Green
Write-Host "Elements missing: $($missingElements.Count)" -ForegroundColor $(if ($missingElements.Count -eq 0) { "Green" } else { "Red" })
Write-Host "URL/ARN violations: $($urlViolations.Count)" -ForegroundColor $(if ($urlViolations.Count -eq 0) { "Green" } else { "Red" })
Write-Host ""

if ($missingElements.Count -gt 0) {
    Write-Host "Missing Elements:" -ForegroundColor Red
    foreach ($missing in $missingElements) {
        Write-Host "  - $missing" -ForegroundColor Red
    }
    Write-Host ""
}

if ($urlViolations.Count -gt 0) {
    Write-Host "URL/ARN Violations:" -ForegroundColor Red
    foreach ($violation in $urlViolations) {
        Write-Host "  - $violation" -ForegroundColor Red
    }
    Write-Host ""
}

# Exit with appropriate code
$exitCode = 0
if ($missingElements.Count -gt 0 -or $urlViolations.Count -gt 0) {
    Write-Host "VALIDATION FAILED" -ForegroundColor Red
    $exitCode = 1
} else {
    Write-Host "VALIDATION PASSED" -ForegroundColor Green
}

Write-Host ""
exit $exitCode
