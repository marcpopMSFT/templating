# Issue Triage Tool

Automated triage of open `dotnet/templating` issues for the SDK consolidation.

## Prerequisites

- [GitHub CLI (`gh`)](https://cli.github.com/) — authenticated with access to `dotnet/templating`
- [GitHub Copilot CLI (`copilot`)](https://docs.github.com/en/copilot/github-copilot-in-the-cli) — on PATH
- .NET SDK 9.0+

## Usage

Run from the repo root:

```bash
# Step 1: Fetch all open issues
dotnet run tools/issue-triage/triage.cs -- --fetch-only

# Step 2: Run triage on all issues
dotnet run tools/issue-triage/triage.cs

# Or fetch + triage in one go
dotnet run tools/issue-triage/triage.cs -- --fetch

# Limit to N issues (useful for testing)
dotnet run tools/issue-triage/triage.cs -- --max 5
```

## How It Works

1. **Fetch**: Pulls all open issues from `dotnet/templating` via `gh issue list` and saves to `issues.json`
2. **Loop**: For each un-triaged issue:
   - Writes issue context (body, comments, reactions, metadata) to `current-issue.json`
   - Spawns a headless Copilot CLI session with the triage prompt
   - Parses the AI's JSON classification
   - Appends result to `results.json`
3. **Resume**: If interrupted, re-running picks up where it left off (skips already-triaged issues)

## Triage Buckets

| Bucket | Description |
|---|---|
| `transfer-to-sdk` | Move to dotnet/sdk — active, legitimate issue |
| `close-wont-fix` | Stale, no activity, unlikely to be addressed |
| `close-resolved-by-consolidation` | SDK move inherently resolves it |
| `close-not-actionable` | Customer question, no repro, duplicate |
| `needs-human-review` | AI uncertain, needs human decision |

## Output

Results are saved to `results.json` with this structure:

```json
{
  "metadata": {
    "repo": "dotnet/templating",
    "fetchedAt": "2026-04-23T...",
    "totalIssues": 223,
    "triaged": 50
  },
  "issues": [
    {
      "number": 9875,
      "title": "...",
      "url": "https://github.com/dotnet/templating/issues/9875",
      "bucket": "transfer-to-sdk",
      "confidence": "high",
      "priority": "medium",
      "reasoning": "Active feature request with community interest...",
      "summary": "Feature request for computed template values",
      "triagedAt": "2026-04-23T..."
    }
  ]
}
```

## Files

| File | Description |
|---|---|
| `triage.cs` | Main loop runner (C# top-level script) |
| `prompt.md` | AI triage prompt template |
| `issues.json` | Fetched issues (generated) |
| `results.json` | Triage results (generated) |
| `current-issue.json` | Temp file for current issue context (cleaned up) |
