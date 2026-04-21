// Detect inactive area paths in Azure DevOps.
//
// Usage:
//   dotnet run detect_inactive_area_paths.cs -- --org <ORG> --project <PROJECT> --pat <PAT> [--days 180]
//
// Author: Moim Hossain
// Note: This script is for demonstration purposes and may require adjustments for production use.
    



using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

// ---------------------------------------------------------------------------
// Parse arguments
// ---------------------------------------------------------------------------
string org = "", project = "", pat = "";
int days = 180;

for (int i = 0; i < args.Length; i++)
{
    switch (args[i])
    {
        case "--org" when i + 1 < args.Length: org = args[++i]; break;
        case "--project" when i + 1 < args.Length: project = args[++i]; break;
        case "--pat" when i + 1 < args.Length: pat = args[++i]; break;
        case "--days" when i + 1 < args.Length: days = int.Parse(args[++i]); break;
    }
}

if (string.IsNullOrEmpty(org) || string.IsNullOrEmpty(project) || string.IsNullOrEmpty(pat))
{
    Console.WriteLine("Usage: dotnet run detect_inactive_area_paths.cs -- --org <ORG> --project <PROJECT> --pat <PAT> [--days 180]");
    return 1;
}

var cutoff = DateTime.UtcNow.AddDays(-days);
var cutoffStr = cutoff.ToString("yyyy-MM-dd");

// ---------------------------------------------------------------------------
// HTTP client setup
// ---------------------------------------------------------------------------
using var http = new HttpClient();
var token = Convert.ToBase64String(Encoding.ASCII.GetBytes(":" + pat));
http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Basic", token);

// ---------------------------------------------------------------------------
// Step 1 — Fetch all area paths via Classification Nodes API
// ---------------------------------------------------------------------------
Console.WriteLine($"Fetching area paths for {org}/{project} ...");

var classUrl = $"https://dev.azure.com/{org}/{project}/_apis/wit/classificationnodes/areas?$depth=10&api-version=7.1";
var classResp = await http.GetAsync(classUrl);
classResp.EnsureSuccessStatusCode();
var classJson = JsonDocument.Parse(await classResp.Content.ReadAsStringAsync());

var allPaths = new List<string>();

void WalkTree(JsonElement node)
{
    string normalised;
    if (node.TryGetProperty("path", out var pathProp))
    {
        // Path looks like  \Project\Area\Sub\Leaf
        // Normalise: strip leading \, remove the synthetic "Area" segment
        var parts = pathProp.GetString()!.TrimStart('\\').Split('\\').ToList();
        if (parts.Count >= 2 && parts[1] == "Area")
            parts.RemoveAt(1);
        normalised = string.Join("\\", parts);
    }
    else
    {
        // Root node — just the project name
        normalised = node.GetProperty("name").GetString()!;
    }

    allPaths.Add(normalised);

    if (node.TryGetProperty("children", out var children))
    {
        foreach (var child in children.EnumerateArray())
            WalkTree(child);
    }
}

WalkTree(classJson.RootElement);
Console.WriteLine($"  Found {allPaths.Count} area path(s).\n");

// ---------------------------------------------------------------------------
// Step 2 — Analytics OData: max ChangedDate per area path
// ---------------------------------------------------------------------------
Console.WriteLine("Querying Analytics for last work-item activity per area path ...");

var activity = new Dictionary<string, DateTime>();
string? analyticsUrl =
    $"https://analytics.dev.azure.com/{org}/{project}/_odata/v4.0-preview/WorkItems"
    + "?$apply=groupby((Area/AreaPath),aggregate(ChangedDate with max as LastChanged))";

while (analyticsUrl is not null)
{
    var resp = await http.GetAsync(analyticsUrl);
    resp.EnsureSuccessStatusCode();
    var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
    var root = doc.RootElement;

    foreach (var row in root.GetProperty("value").EnumerateArray())
    {
        var areaPath = row.GetProperty("Area").GetProperty("AreaPath").GetString()!;
        var lastChanged = row.GetProperty("LastChanged").GetDateTime();
        activity[areaPath] = lastChanged;
    }

    analyticsUrl = root.TryGetProperty("@odata.nextLink", out var next) ? next.GetString() : null;
}

Console.WriteLine($"  {activity.Count} area path(s) have at least one work item.\n");

// ---------------------------------------------------------------------------
// Step 3 — Cross-reference and report
// ---------------------------------------------------------------------------
var inactive = new List<(string Path, string Reason)>();
var active = new List<(string Path, string Last)>();

foreach (var path in allPaths.Order())
{
    if (!activity.TryGetValue(path, out var last))
    {
        inactive.Add((path, "No work items"));
    }
    else if (last < cutoff)
    {
        inactive.Add((path, $"Last activity: {last:yyyy-MM-dd}"));
    }
    else
    {
        active.Add((path, last.ToString("yyyy-MM-dd")));
    }
}

var separator = new string('=', 80);
var thin = new string('-', 80);

Console.WriteLine(separator);
Console.WriteLine($"INACTIVE AREA PATHS  (no activity since {cutoffStr})");
Console.WriteLine(separator);

if (inactive.Count > 0)
{
    int maxLen = inactive.Max(x => x.Path.Length);
    foreach (var (path, reason) in inactive)
        Console.WriteLine($"  {path.PadRight(maxLen)}   {reason}");
}
else
{
    Console.WriteLine("  (none — every area path has recent activity)");
}

Console.WriteLine();
Console.WriteLine(separator);
Console.WriteLine("ACTIVE AREA PATHS");
Console.WriteLine(separator);

if (active.Count > 0)
{
    int maxLen = active.Max(x => x.Path.Length);
    foreach (var (path, last) in active)
        Console.WriteLine($"  {path.PadRight(maxLen)}   Last activity: {last}");
}
else
{
    Console.WriteLine("  (none)");
}

Console.WriteLine();
Console.WriteLine(thin);
Console.WriteLine($"Total area paths : {allPaths.Count}");
Console.WriteLine($"Active           : {active.Count}");
Console.WriteLine($"Inactive         : {inactive.Count}");
Console.WriteLine($"Threshold        : {days} days (cutoff {cutoffStr})");
Console.WriteLine(thin);

return inactive.Count > 0 ? 1 : 0;
