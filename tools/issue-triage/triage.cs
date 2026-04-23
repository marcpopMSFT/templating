using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

return await TriageApp.RunAsync(args);

// Source-generated JSON context for .NET 11+ (reflection-free)
[JsonSerializable(typeof(List<TriageApp.Issue>))]
[JsonSerializable(typeof(TriageApp.TriageResults))]
[JsonSerializable(typeof(string))]
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    PropertyNameCaseInsensitive = true)]
internal partial class TriageJsonContext : JsonSerializerContext { }

static class TriageApp
{
    private const string Repo = "dotnet/templating";
    private const int MaxBodyLength = 8 * 1024;       // 8 KB body cap
    private const int MaxCommentsToInclude = 5;
    private const int MaxCommentLength = 2 * 1024;     // 2 KB per comment
    private const int InitialDelayMs = 2_000;
    private const int MaxRetries = 2;

    public static async Task<int> RunAsync(string[] args)
    {
        if (args.Any(a => a is "--help" or "-h" or "/?"))
        {
            PrintUsage();
            return 0;
        }

        var options = ParseArguments(args);
        if (options.Error is not null)
        {
            Console.Error.WriteLine($"Error: {options.Error}");
            PrintUsage();
            return 1;
        }

        var toolDir = GetToolDirectory();
        var workDir = toolDir; // issues.json, results.json live alongside the tool
        var issuesFile = Path.Combine(workDir, "issues.json");
        var resultsFile = Path.Combine(workDir, "results.json");
        var promptFile = Path.Combine(toolDir, "prompt.md");
        var currentIssueFile = Path.Combine(workDir, "current-issue.json");

        if (!File.Exists(promptFile))
        {
            Console.Error.WriteLine($"Missing prompt file: {promptFile}");
            return 1;
        }

        // --- Fetch mode ---
        if (options.Fetch)
        {
            Console.WriteLine($"Fetching open issues from {Repo}...");
            var exitCode = await FetchIssuesAsync(issuesFile);
            if (exitCode != 0) return exitCode;
            Console.WriteLine($"Issues saved to {issuesFile}");
            if (!options.RunTriage) return 0; // --fetch only
        }

        // --- Triage mode ---
        if (!File.Exists(issuesFile))
        {
            Console.Error.WriteLine($"No issues file found. Run with --fetch first.");
            return 1;
        }

        var copilot = ResolveExecutable("copilot");
        if (copilot is null)
        {
            Console.Error.WriteLine("Could not find 'copilot' on PATH.");
            return 1;
        }

        var issues = LoadIssues(issuesFile);
        var results = LoadResults(resultsFile);
        var existingNumbers = new HashSet<int>(results.Issues.Select(r => r.Number));

        var toTriage = issues
            .Where(i => !existingNumbers.Contains(i.Number))
            .ToList();

        if (options.Max > 0 && toTriage.Count > options.Max)
            toTriage = toTriage.Take(options.Max).ToList();

        Console.WriteLine($"Total issues: {issues.Count}, Already triaged: {existingNumbers.Count}, To triage: {toTriage.Count}");

        if (toTriage.Count == 0)
        {
            Console.WriteLine("Nothing to triage.");
            return 0;
        }

        var promptText = await File.ReadAllTextAsync(promptFile);
        var triaged = 0;
        var failed = 0;

        foreach (var issue in toTriage)
        {
            triaged++;
            Console.WriteLine();
            Console.WriteLine($"═══════════════════════════════════════════════════════════");
            Console.WriteLine($"  [{triaged}/{toTriage.Count}] Issue #{issue.Number}: {Truncate(issue.Title, 60)}");
            Console.WriteLine($"═══════════════════════════════════════════════════════════");

            // Write the current issue context to a file for the AI to read
            var issueContext = BuildIssueContext(issue);
            await File.WriteAllTextAsync(currentIssueFile, issueContext);

            TriageResult? result = null;
            var attempt = 0;
            var delay = InitialDelayMs;

            while (attempt <= MaxRetries && result is null)
            {
                if (attempt > 0)
                {
                    Console.WriteLine($"  Retry {attempt}/{MaxRetries} after {delay}ms...");
                    await Task.Delay(delay);
                    delay *= 2; // exponential backoff
                }

                try
                {
                    var output = await RunCopilotAsync(copilot, promptText, workDir);
                    result = ParseTriageResult(output, issue.Number);
                }
                catch (Exception ex)
                {
                    Console.Error.WriteLine($"  Error: {ex.Message}");
                }

                attempt++;
            }

            if (result is not null)
            {
                result.Title = issue.Title;
                result.Url = issue.Url;
                result.TriagedAt = DateTime.UtcNow.ToString("o");
                results.Issues.Add(result);
                results.Metadata.Triaged = results.Issues.Count;
                SaveResultsAtomic(resultsFile, results);

                Console.WriteLine($"  → Bucket: {result.Bucket}  Confidence: {result.Confidence}  Priority: {result.Priority}");
                Console.WriteLine($"  → {result.Reasoning}");
            }
            else
            {
                failed++;
                Console.Error.WriteLine($"  ✗ Failed to triage issue #{issue.Number} after {MaxRetries + 1} attempts.");

                // Record the failure so we can see it in results
                results.Issues.Add(new TriageResult
                {
                    Number = issue.Number,
                    Title = issue.Title,
                    Url = issue.Url,
                    Bucket = "needs-human-review",
                    Confidence = "low",
                    Priority = "n/a",
                    Reasoning = "AI triage failed after retries. Needs manual review.",
                    Summary = issue.Title,
                    TriagedAt = DateTime.UtcNow.ToString("o"),
                });
                results.Metadata.Triaged = results.Issues.Count;
                SaveResultsAtomic(resultsFile, results);
            }

            // Delay between issues
            if (triaged < toTriage.Count)
                await Task.Delay(InitialDelayMs);
        }

        // Clean up temp file
        if (File.Exists(currentIssueFile))
            File.Delete(currentIssueFile);

        Console.WriteLine();
        Console.WriteLine($"Done. Triaged {triaged} issues ({failed} failures). Results in {resultsFile}");
        return 0;
    }

    // ────────────────── Fetch ──────────────────

    private static async Task<int> FetchIssuesAsync(string outputPath)
    {
        var fields = "number,title,body,createdAt,updatedAt,labels,comments,reactionGroups,author,url";
        var psi = new ProcessStartInfo
        {
            FileName = "gh",
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        psi.ArgumentList.Add("issue");
        psi.ArgumentList.Add("list");
        psi.ArgumentList.Add("--repo");
        psi.ArgumentList.Add(Repo);
        psi.ArgumentList.Add("--state");
        psi.ArgumentList.Add("open");
        psi.ArgumentList.Add("--limit");
        psi.ArgumentList.Add("500");
        psi.ArgumentList.Add("--json");
        psi.ArgumentList.Add(fields);

        using var proc = Process.Start(psi)!;
        var json = await proc.StandardOutput.ReadToEndAsync();
        var stderr = await proc.StandardError.ReadToEndAsync();
        await proc.WaitForExitAsync();

        if (proc.ExitCode != 0)
        {
            Console.Error.WriteLine($"gh failed: {stderr}");
            return 1;
        }

        // Write atomically
        var tmp = outputPath + ".tmp";
        await File.WriteAllTextAsync(tmp, json);
        File.Move(tmp, outputPath, overwrite: true);

        var count = JsonDocument.Parse(json).RootElement.GetArrayLength();
        Console.WriteLine($"Fetched {count} issues.");
        return 0;
    }

    // ────────────────── Issue Context ──────────────────

    private static string BuildIssueContext(Issue issue)
    {
        var sb = new StringBuilder();
        sb.AppendLine("{");
        sb.AppendLine($"  \"number\": {issue.Number},");
        sb.AppendLine($"  \"title\": {JsonEncode(issue.Title)},");
        sb.AppendLine($"  \"url\": {JsonEncode(issue.Url)},");
        sb.AppendLine($"  \"author\": {JsonEncode(issue.Author?.Login ?? "unknown")},");
        sb.AppendLine($"  \"authorAssociation\": {JsonEncode(issue.Author?.IsBot == true ? "BOT" : "USER")},");
        sb.AppendLine($"  \"createdAt\": {JsonEncode(issue.CreatedAt)},");
        sb.AppendLine($"  \"updatedAt\": {JsonEncode(issue.UpdatedAt)},");

        // Labels
        var labelNames = issue.Labels?.Select(l => l.Name).ToList() ?? new List<string>();
        sb.AppendLine($"  \"labels\": [{string.Join(", ", labelNames.Select(JsonEncode))}],");

        // Reactions summary
        var upvotes = issue.ReactionGroups?
            .FirstOrDefault(r => r.Content == "THUMBS_UP")?.Users?.TotalCount ?? 0;
        var totalReactions = issue.ReactionGroups?.Sum(r => r.Users?.TotalCount ?? 0) ?? 0;
        sb.AppendLine($"  \"upvotes\": {upvotes},");
        sb.AppendLine($"  \"totalReactions\": {totalReactions},");

        // Comment count
        var commentCount = issue.Comments?.Count ?? 0;
        sb.AppendLine($"  \"commentCount\": {commentCount},");

        // Body (truncated)
        var body = issue.Body ?? "";
        if (body.Length > MaxBodyLength)
            body = body[..MaxBodyLength] + "\n... [truncated]";
        sb.AppendLine($"  \"body\": {JsonEncode(body)},");

        // Comments (recent, maintainer-prioritized, truncated)
        var comments = issue.Comments ?? new List<Comment>();
        var selected = SelectComments(comments);
        sb.AppendLine("  \"comments\": [");
        for (var i = 0; i < selected.Count; i++)
        {
            var c = selected[i];
            var cBody = c.Body ?? "";
            if (cBody.Length > MaxCommentLength)
                cBody = cBody[..MaxCommentLength] + "\n... [truncated]";
            sb.Append($"    {{\"author\": {JsonEncode(c.Author?.Login ?? "unknown")}, ");
            sb.Append($"\"association\": {JsonEncode(c.AuthorAssociation ?? "NONE")}, ");
            sb.Append($"\"createdAt\": {JsonEncode(c.CreatedAt ?? "")}, ");
            sb.Append($"\"body\": {JsonEncode(cBody)}}}");
            if (i < selected.Count - 1) sb.Append(",");
            sb.AppendLine();
        }
        sb.AppendLine("  ]");
        sb.AppendLine("}");

        return sb.ToString();
    }

    private static List<Comment> SelectComments(List<Comment> comments)
    {
        if (comments.Count <= MaxCommentsToInclude)
            return comments;

        // Prioritize maintainer/member comments, then take most recent
        var maintainer = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            { "MEMBER", "COLLABORATOR", "OWNER" };

        var prioritized = comments
            .Where(c => maintainer.Contains(c.AuthorAssociation ?? ""))
            .ToList();

        var remaining = comments
            .Where(c => !maintainer.Contains(c.AuthorAssociation ?? ""))
            .Reverse() // most recent first
            .ToList();

        var result = new List<Comment>(prioritized);
        foreach (var c in remaining)
        {
            if (result.Count >= MaxCommentsToInclude) break;
            result.Add(c);
        }

        // Sort chronologically for readability
        return result.OrderBy(c => c.CreatedAt).ToList();
    }

    // ────────────────── Copilot Invocation ──────────────────

    private static async Task<string> RunCopilotAsync(string executable, string promptText, string workDir)
    {
        var psi = new ProcessStartInfo
        {
            FileName = executable,
            WorkingDirectory = workDir,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };

        psi.ArgumentList.Add("-p");
        psi.ArgumentList.Add(promptText);
        psi.ArgumentList.Add("--no-ask-user");
        psi.ArgumentList.Add("-s");

        using var proc = new Process { StartInfo = psi };
        if (!proc.Start())
            throw new IOException("Failed to start copilot.");

        var output = new StringBuilder();
        var stdoutTask = PumpAsync(proc.StandardOutput, Console.Out, output);
        var stderrTask = PumpAsync(proc.StandardError, Console.Error, null);

        await Task.WhenAll(proc.WaitForExitAsync(), stdoutTask, stderrTask);

        if (proc.ExitCode != 0)
            Console.Error.WriteLine($"  copilot exited with code {proc.ExitCode}");

        return output.ToString();
    }

    private static async Task PumpAsync(StreamReader reader, TextWriter writer, StringBuilder? sink)
    {
        while (await reader.ReadLineAsync() is { } line)
        {
            sink?.AppendLine(line);
            await writer.WriteLineAsync(line);
        }
    }

    // ────────────────── Result Parsing ──────────────────

    private static TriageResult? ParseTriageResult(string output, int issueNumber)
    {
        // Find JSON object in the output — look for the triage result pattern
        var startIdx = -1;
        var braceDepth = 0;

        for (var i = 0; i < output.Length; i++)
        {
            if (output[i] == '{')
            {
                if (braceDepth == 0) startIdx = i;
                braceDepth++;
            }
            else if (output[i] == '}')
            {
                braceDepth--;
                if (braceDepth == 0 && startIdx >= 0)
                {
                    var candidate = output[startIdx..(i + 1)];
                    try
                    {
                        var result = JsonSerializer.Deserialize<TriageResult>(candidate, JsonOpts);
                        if (result?.Bucket is not null && IsValidBucket(result.Bucket))
                        {
                            result.Number = issueNumber;
                            return result;
                        }
                    }
                    catch (JsonException)
                    {
                        // Not the JSON we're looking for, keep searching
                    }
                    startIdx = -1;
                }
            }
        }

        Console.Error.WriteLine("  Could not parse triage result from AI output.");
        return null;
    }

    private static bool IsValidBucket(string bucket) => bucket is
        "transfer-to-sdk" or
        "close-wont-fix" or
        "close-resolved-by-consolidation" or
        "close-not-actionable" or
        "needs-human-review";

    // ────────────────── Results I/O ──────────────────

    private static TriageResults LoadResults(string path)
    {
        if (!File.Exists(path))
        {
            return new TriageResults
            {
                Metadata = new Metadata
                {
                    Repo = Repo,
                    FetchedAt = DateTime.UtcNow.ToString("o"),
                    TotalIssues = 0,
                    Triaged = 0,
                },
                Issues = new List<TriageResult>(),
            };
        }

        var json = File.ReadAllText(path);
        return JsonSerializer.Deserialize<TriageResults>(json, JsonOpts) ?? throw new InvalidOperationException("Failed to parse results.json");
    }

    private static void SaveResultsAtomic(string path, TriageResults results)
    {
        var json = JsonSerializer.Serialize(results, JsonWriteOpts);
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, json);
        File.Move(tmp, path, overwrite: true);
    }

    private static List<Issue> LoadIssues(string path)
    {
        var json = File.ReadAllText(path);
        return JsonSerializer.Deserialize<List<Issue>>(json, JsonOpts) ?? new List<Issue>();
    }

    // ────────────────── CLI Parsing ──────────────────

    private static Options ParseArguments(string[] args)
    {
        var fetch = false;
        var max = 0;
        var runTriage = true;

        for (var i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--fetch":
                    fetch = true;
                    break;
                case "--fetch-only":
                    fetch = true;
                    runTriage = false;
                    break;
                case "--max" when i + 1 < args.Length && int.TryParse(args[i + 1], out var m) && m > 0:
                    max = m;
                    i++;
                    break;
                default:
                    if (int.TryParse(args[i], out var n) && n > 0)
                    {
                        max = n;
                    }
                    else
                    {
                        return new Options(false, true, 0, $"Unrecognized argument: '{args[i]}'");
                    }
                    break;
            }
        }

        return new Options(fetch, runTriage, max, null);
    }

    private static void PrintUsage()
    {
        Console.WriteLine("Issue Triage Tool for dotnet/templating → dotnet/sdk consolidation");
        Console.WriteLine();
        Console.WriteLine("Usage:");
        Console.WriteLine("  dotnet run triage.cs -- --fetch-only      Fetch open issues to issues.json");
        Console.WriteLine("  dotnet run triage.cs -- --fetch           Fetch issues, then triage");
        Console.WriteLine("  dotnet run triage.cs                      Triage (using existing issues.json)");
        Console.WriteLine("  dotnet run triage.cs -- --max 10          Triage at most 10 issues");
        Console.WriteLine("  dotnet run triage.cs -- --fetch --max 5   Fetch, then triage 5 issues");
    }

    // ────────────────── Helpers ──────────────────

    private static string GetToolDirectory([CallerFilePath] string? sourcePath = null)
        => Path.GetDirectoryName(sourcePath) ?? Directory.GetCurrentDirectory();

    private static string? ResolveExecutable(string tool)
    {
        var pathEntries = (Environment.GetEnvironmentVariable("PATH") ?? "")
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        var extensions = OperatingSystem.IsWindows()
            ? (Environment.GetEnvironmentVariable("PATHEXT") ?? ".EXE;.CMD;.BAT;.COM")
                .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            : new[] { "" };

        foreach (var dir in pathEntries)
        {
            if (!Directory.Exists(dir)) continue;
            foreach (var ext in extensions)
            {
                var candidate = Path.Combine(dir, tool + ext.ToLowerInvariant());
                if (File.Exists(candidate)) return candidate;
                candidate = Path.Combine(dir, tool + ext.ToUpperInvariant());
                if (File.Exists(candidate)) return candidate;
            }
            var plain = Path.Combine(dir, tool);
            if (File.Exists(plain)) return plain;
        }
        return null;
    }

    private static string Truncate(string s, int max)
        => s.Length <= max ? s : s[..(max - 3)] + "...";

    private static string JsonEncode(string s)
        => JsonSerializer.Serialize(s, TriageJsonContext.Default.String);

    // ────────────────── JSON Options ──────────────────

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        TypeInfoResolver = TriageJsonContext.Default,
    };

    private static readonly JsonSerializerOptions JsonWriteOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        TypeInfoResolver = TriageJsonContext.Default,
    };

    // ────────────────── Models ──────────────────

    internal sealed record Options(bool Fetch, bool RunTriage, int Max, string? Error);

    internal sealed class Issue
    {
        [JsonPropertyName("number")] public int Number { get; set; }
        [JsonPropertyName("title")] public string Title { get; set; } = "";
        [JsonPropertyName("body")] public string? Body { get; set; }
        [JsonPropertyName("url")] public string Url { get; set; } = "";
        [JsonPropertyName("createdAt")] public string CreatedAt { get; set; } = "";
        [JsonPropertyName("updatedAt")] public string UpdatedAt { get; set; } = "";
        [JsonPropertyName("author")] public Author? Author { get; set; }
        [JsonPropertyName("labels")] public List<Label>? Labels { get; set; }
        [JsonPropertyName("comments")] public List<Comment>? Comments { get; set; }
        [JsonPropertyName("reactionGroups")] public List<ReactionGroup>? ReactionGroups { get; set; }
    }

    internal sealed class Author
    {
        [JsonPropertyName("login")] public string Login { get; set; } = "";
        [JsonPropertyName("is_bot")] public bool IsBot { get; set; }
    }

    internal sealed class Label
    {
        [JsonPropertyName("name")] public string Name { get; set; } = "";
    }

    internal sealed class Comment
    {
        [JsonPropertyName("body")] public string? Body { get; set; }
        [JsonPropertyName("createdAt")] public string? CreatedAt { get; set; }
        [JsonPropertyName("author")] public Author? Author { get; set; }
        [JsonPropertyName("authorAssociation")] public string? AuthorAssociation { get; set; }
    }

    internal sealed class ReactionGroup
    {
        [JsonPropertyName("content")] public string Content { get; set; } = "";
        [JsonPropertyName("users")] public ReactionUsers? Users { get; set; }
    }

    internal sealed class ReactionUsers
    {
        [JsonPropertyName("totalCount")] public int TotalCount { get; set; }
    }

    internal sealed class TriageResults
    {
        [JsonPropertyName("metadata")] public Metadata Metadata { get; set; } = new();
        [JsonPropertyName("issues")] public List<TriageResult> Issues { get; set; } = new();
    }

    internal sealed class Metadata
    {
        [JsonPropertyName("repo")] public string Repo { get; set; } = "";
        [JsonPropertyName("fetchedAt")] public string FetchedAt { get; set; } = "";
        [JsonPropertyName("totalIssues")] public int TotalIssues { get; set; }
        [JsonPropertyName("triaged")] public int Triaged { get; set; }
    }

    internal sealed class TriageResult
    {
        [JsonPropertyName("number")] public int Number { get; set; }
        [JsonPropertyName("title")] public string? Title { get; set; }
        [JsonPropertyName("url")] public string? Url { get; set; }
        [JsonPropertyName("bucket")] public string? Bucket { get; set; }
        [JsonPropertyName("confidence")] public string? Confidence { get; set; }
        [JsonPropertyName("priority")] public string? Priority { get; set; }
        [JsonPropertyName("reasoning")] public string? Reasoning { get; set; }
        [JsonPropertyName("summary")] public string? Summary { get; set; }
        [JsonPropertyName("triagedAt")] public string? TriagedAt { get; set; }
    }
}
