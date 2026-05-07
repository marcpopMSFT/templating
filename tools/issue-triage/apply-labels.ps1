#!/usr/bin/env pwsh
# apply-labels.ps1 — Reads results.json and applies triage:<bucket> labels to issues
#
# Usage:
#   ./tools/issue-triage/apply-labels.ps1                  # apply all labels
#   ./tools/issue-triage/apply-labels.ps1 -DryRun          # preview without applying
#   ./tools/issue-triage/apply-labels.ps1 -Bucket transfer-to-sdk  # only one bucket

param(
    [string]$ResultsFile = "$PSScriptRoot/results.json",
    [string]$Repo = "dotnet/templating",
    [switch]$DryRun,
    [string]$Bucket  # optional: filter to a single bucket
)

if (-not (Test-Path $ResultsFile)) {
    Write-Error "Results file not found: $ResultsFile"
    exit 1
}

$results = Get-Content $ResultsFile -Raw | ConvertFrom-Json
$issues = $results.issues

if ($Bucket) {
    $issues = $issues | Where-Object { $_.bucket -eq $Bucket }
}

$total = $issues.Count
$applied = 0
$failed = 0

Write-Host "Applying triage labels to $total issues in $Repo"
if ($DryRun) { Write-Host "(DRY RUN — no changes will be made)" }
Write-Host ""

foreach ($issue in $issues) {
    $label = "triage:$($issue.bucket)"
    $applied++

    Write-Host ("  [{0}/{1}] #{2,-6} -> {3}" -f $applied, $total, $issue.number, $label)

    if (-not $DryRun) {
        $output = gh issue edit $issue.number --repo $Repo --add-label $label 2>&1
        if ($LASTEXITCODE -ne 0) {
            Write-Host "    ERROR: $output" -ForegroundColor Red
            $failed++
        }
    }
}

Write-Host ""
Write-Host "Done. Applied: $($applied - $failed), Failed: $failed"
