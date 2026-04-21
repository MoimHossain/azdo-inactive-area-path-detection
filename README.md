# Azure DevOps - Inactive Area Path Detection

A single C# script that identifies area paths with no recent work-item activity in an Azure DevOps project. It makes just **two API calls** — no per-path looping, no expensive WIQL queries — and scales to organisations with thousands of area paths.

## Quick start

Requires [.NET 9+](https://dotnet.microsoft.com/download) (supports file-based C# scripts).

```bash
dotnet run detect_inactive_area_paths.cs -- \
    --org     YOUR_ORG \
    --project YOUR_PROJECT \
    --pat     YOUR_PAT \
    --days    180
```

| Argument | Required | Description |
|---|---|---|
| `--org` | ✅ | Azure DevOps organisation name |
| `--project` | ✅ | Team project name |
| `--pat` | ✅ | Personal Access Token (needs *Work Items: Read* and *Analytics: Read* scopes) |
| `--days` | | Inactivity threshold in days (default: **180**) |

### Sample output

```
Fetching area paths for contoso/Platform ...
  Found 28 area path(s).

Querying Analytics for last work-item activity per area path ...
  8 area path(s) have at least one work item.

================================================================================
INACTIVE AREA PATHS  (no activity since 2025-10-23)
================================================================================
  Platform\Finance\EMEA                                    No work items
  Platform\Engineering\Cloud\Azure-Service-Health-events   Last activity: 2024-07-05
  Platform\HR\Recruitment                                  No work items
  ...

================================================================================
ACTIVE AREA PATHS
================================================================================
  Platform\Engineering\Cloud\Azure-Updates\Containers      Last activity: 2025-09-22
  ...

--------------------------------------------------------------------------------
Total area paths : 28
Active           : 4
Inactive         : 24
Threshold        : 180 days (cutoff 2025-10-23)
--------------------------------------------------------------------------------
```

The script exits with code **1** when inactive paths are found, making it easy to use in CI/CD pipelines or scheduled automation.

---

## Why not WIQL?

The common instinct — iterate over every area path, run a WIQL query per path, check `ChangedDate` — is extremely expensive. At scale (hundreds of area paths across many projects), that means N WIQL calls + M work-item fetches per path. It burns through your TSTU (throttling) budget quickly and doesn't scale.

## How it works

The key insight is to **invert the query**. Instead of asking *"which area paths have no recent work items?"* one path at a time, we ask the Analytics service to aggregate in a single server-side query: *"give me the `MAX(ChangedDate)` per area path"*. We then cross-reference client-side. Zero pagination, zero per-path looping.

### Step 1 — Fetch all area paths (1 API call)

The [Classification Nodes API](https://learn.microsoft.com/en-us/rest/api/azure/devops/wit/classification-nodes) with `$depth=10` returns the full area path tree in a single call:

```
GET https://dev.azure.com/{org}/{project}/_apis/wit/classificationnodes/areas?$depth=10&api-version=7.1
```

### Step 2 — Server-side aggregation via Analytics OData (1 API call)

The [Analytics OData `$apply`](https://learn.microsoft.com/en-us/azure/devops/report/extend-analytics/odata-query-guidelines) extension supports `groupby` and `aggregate` — effectively a SQL `GROUP BY` + `MAX` executed server-side:

```
GET https://analytics.dev.azure.com/{org}/{project}/_odata/v4.0-preview/WorkItems
  ?$apply=groupby(
    (Area/AreaPath),
    aggregate(ChangedDate with max as LastChanged)
  )
```

The response is a compact JSON list:
```json
{ "value": [
  { "Area": { "AreaPath": "Project\\TeamA\\Backend" }, "LastChanged": "2025-10-01T..." },
  { "Area": { "AreaPath": "Project\\TeamB\\Legacy" }, "LastChanged": "2024-09-15T..." }
]}
```

If there are more than 10,000 area paths, the response includes an `@odata.nextLink` for pagination — the script follows these automatically.

### Step 3 — Cross-reference client-side (0 API calls)

Take the full area path list from Step 1, left-join against the Analytics results from Step 2:

- **Missing from Analytics** → zero work items ever → **inactive**
- **`LastChanged` older than threshold** → stale → **inactive**
- **Otherwise** → **active**

### Total API cost

| Step | API | Calls |
|---|---|---|
| Fetch all area paths | `classificationnodes/areas?$depth=10` | 1 |
| Max ChangedDate per area path | Analytics OData `$apply=groupby(…)` | 1–2 (paged if >10k paths) |
| Cross-reference | Client-side | 0 |

---

## API versions (validated April 2026)

| API | Endpoint | Version |
|---|---|---|
| Classification Nodes | `dev.azure.com/{org}/{project}/_apis/wit/classificationnodes/areas` | `api-version=7.1` |
| Analytics OData | `analytics.dev.azure.com/{org}/{project}/_odata/v4.0-preview/WorkItems` | `v4.0-preview` |

## Gotchas

### Path normalisation

The Classification Nodes API returns paths in the format `\Project\Area\Sub\Leaf` (with a leading backslash and a synthetic `Area` segment). The Analytics OData API returns `Project\Sub\Leaf`. **You must strip the leading `\` and remove the `Area` segment** when cross-referencing. The script handles this automatically.

### Use `Area/AreaPath`, not `Node Name`

`Area ID` and `Iteration ID` fields are indexed in the Analytics backend — `Node Name` is **not**. Always group/filter by `Area/AreaPath` (which uses the indexed `AreaId` under the hood). Grouping by `Node Name` triggers full table scans.

### Saved vs unsaved WIQL queries

Due to internal optimisations, saved queries tend to perform better than unsaved ones. If you also run WIQL via the REST API, saving the query through the web portal first can avoid performance regressions. (Not relevant to this script — it uses OData, not WIQL.)

---

## Automation ideas

This approach is fully automatable — for example:

- **Azure Function on a timer** — run monthly, post results to a Teams channel
- **Azure DevOps pipeline** — scheduled pipeline that fails (exit code 1) when inactive paths exceed a threshold
- **PowerAutomate flow** — trigger on the exit code and notify project admins

The minimal API footprint (2 calls) means you won't hit throttling even at enterprise scale.
