using System.Globalization;
using System.Net;

namespace Dashboard.Tests;

/// <summary>
/// A system with everything the optics read: nodes that report telemetry with their process and their build, links
/// into the portal, a delivery file and a cost file. Hosts: fd-uat, uat-west (primary), uat-east (standby), tdd-west, raw.
/// </summary>
internal static class Optics
{
    public const string Topology = """
        {
          "system": { "slug": "demo", "name": "Demo", "deliveryUrl": "https://raw.example.net/org/demo-system/status/delivery.json" },
          "environments": [
            { "name": "tdd",
              "links": { "applicationInsights": "https://portal.example.net/#appi-tdd/overview", "database": "https://portal.example.net/#sqldb-tdd", "resourceGroup": "https://portal.example.net/#rg" },
              "deployables": [ {
                "name": "ui", "telemetryPath": "/_telemetry", "buildPath": "_build",
                "nodes": [ { "name": "tdd-west", "region": "westus3", "role": "primary", "url": "https://tdd-west.example.net" } ] } ] },
            { "name": "uat",
              "links": { "applicationInsights": "https://portal.example.net/#appi-uat/overview", "database": "https://portal.example.net/#sqldb-uat" },
              "deployables": [ {
                "name": "ui", "frontDoor": "https://fd-uat.example.net", "telemetryPath": "/_telemetry", "buildPath": "/_build",
                "projectUrl": "https://octopus.example.net/app#/Spaces-1/projects/demo-ui",
                "links": { "frontDoor": "https://portal.example.net/#afd", "logs": "https://portal.example.net/#logs/q/H4sI%2Bx%3D%3D/timespan/PT1H", "broken": "not an address", "script": "javascript:alert(1)" },
                "nodes": [
                  { "name": "uat-west", "region": "westus3", "role": "primary", "url": "https://uat-west.example.net",
                    "links": { "portal": "https://portal.example.net/#west", "liveMetrics": "https://portal.example.net/#appi-uat/quickPulse",
                               "performance": "https://portal.example.net/#appi-uat/performance", "failures": "https://portal.example.net/#appi-uat/failures",
                               "dependencies": "https://portal.example.net/#appi-uat/dependencies" } },
                  { "name": "uat-east", "region": "eastus2", "role": "standby", "url": "https://uat-east.example.net" } ] } ] }
          ]
        }
        """;

    public const string Build = """
        { "version": "2.4.15+0a1b2c3", "commit": "0a1b2c3d4e5f60718293a4b5c6d7e8f901234567", "commitUrl": "https://github.example.net/o/r/commit/0a1b2c3d4e5f60718293a4b5c6d7e8f901234567",
          "builtAt": "2026-10-04T20:00:00Z", "buildUrl": "https://github.example.net/o/r/actions/runs/1",
          "code": { "linesOfCode": 84210, "files": 1203, "languages": [
            { "name": "Razor", "lines": 9000, "files": 120 }, { "name": "C#", "lines": 61234, "files": 800 }, { "name": "SQL", "lines": 5000, "files": 60 },
            { "name": "JavaScript", "lines": 4000, "files": 20 }, { "name": "CSS", "lines": 3000, "files": 10 }, { "name": "PowerShell", "lines": 1500, "files": 30 },
            { "name": "YAML", "lines": 476, "files": 12 } ] },
          "tests": { "unit": 1009, "integration": 240, "acceptance": 168 },
          "coverage": { "linePercent": 81.2, "branchPercent": 70.1 },
          "complexity": { "average": 1.9, "max": 34, "methods": 5210 },
          "crap": { "max": 28.5, "threshold": 30, "overThreshold": 0 },
          "analysis": { "qodanaProblems": 0 } }
        """;

    public const string Delivery = """
        { "generated": "2026-10-04T21:00:00Z",
          "environments": [
            { "name": "tdd", "deployables": [
              { "name": "ui", "version": "2.4.15", "deployedAt": "2026-10-04T20:30:00Z", "signedOffBy": null, "reason": null, "commit": "0a1b2c3d4e5f", "commitAt": "2026-10-04T19:45:00Z",
                "leadTimeHours": 0.75, "behindFirst": { "versions": 0, "days": 0 }, "deploymentsLast7Days": 9, "failedLast7Days": 1, "releaseUrl": "https://octopus.example.net/r/2.4.15",
                "lastPersonSignOff": null },
              { "name": "system", "version": "1.0.33", "deployedAt": "2026-10-03T10:00:00Z", "deploymentsLast7Days": 2, "failedLast7Days": 0 } ] },
            { "name": "uat", "deployables": [
              { "name": "dashboard", "version": "1.0.7", "deployedAt": "2026-10-02T10:00:00Z" },
              { "name": "ui", "version": "2.4.14", "deployedAt": "2026-10-04T16:37:00Z", "signedOffBy": "cm-ai-ops", "reason": "Acceptance tests passed in tdd", "commit": "9f8e7d6c5b4a",
                "commitAt": "2026-10-04T11:25:00Z", "leadTimeHours": 5.2, "behindFirst": { "versions": 2, "days": 3.4 }, "deploymentsLast7Days": 4, "failedLast7Days": 0,
                "releaseUrl": "https://octopus.example.net/r/2.4.14",
                "lastPersonSignOff": { "version": "2.4.11", "by": "pat.morgan", "at": "2026-10-01T15:12:41Z", "reason": "Reviewed with the customer",
                                       "releaseUrl": "https://octopus.example.net/r/2.4.11" } },
              { "name": "system", "version": "1.0.33", "deployedAt": "2026-10-03T11:00:00Z", "signedOffBy": "cm-ai-ops", "reason": "tdd applied" } ] } ],
          "failover": { "environment": "uat", "at": "2026-10-04T05:00:00Z", "seconds": 44 } }
        """;

    /// <summary>The cost file as the system repository's <c>write-cost.ps1</c> writes it: the numbers of 2026-10-03.</summary>
    public const string Cost = """
        { "generated": "2026-10-04T05:00:00Z", "currency": "USD", "asOf": "2026-10-03",
          "system": { "yesterday": 3.41, "last7Days": 22.1, "monthToDate": 1204.8 },
          "environments": [
            { "name": "tdd", "yesterday": 0.0, "last7Days": 1.5, "monthToDate": 1.59, "topServices": [ { "name": "SQL Database", "monthToDate": 0.97 }, { "name": "Azure App Service", "monthToDate": 0.62 } ] },
            { "name": "uat", "yesterday": 1.52, "last7Days": 9.8, "monthToDate": 11.02, "topServices": [ { "name": "Azure App Service", "monthToDate": 6.1 } ] },
            { "name": "retired", "yesterday": null, "last7Days": 0.2, "monthToDate": 0.2, "topServices": [ ] },
            { "name": "shared", "yesterday": 1.1, "last7Days": 7.7, "monthToDate": 8.2, "topServices": [ { "name": "Azure Front Door Service", "monthToDate": 8.11 } ] } ] }
        """;

    public const string CostPath = "/org/demo-system/status/cost.json";

    /// <summary><see cref="Topology"/> with the address of the cost file next to the one of the delivery facts.</summary>
    public static readonly string TopologyWithCost = Topology.Replace(
        "\"deliveryUrl\"",
        $"\"costUrl\": \"https://raw.example.net{CostPath}\", \"deliveryUrl\"",
        StringComparison.Ordinal);

    /// <summary>A telemetry answer; without an uptime the app reports no <c>process</c>, like an older app.</summary>
    public static string Telemetry(
        int requests = 12,
        int errors = 0,
        int sql = 30,
        long? uptime = 5321,
        double cpu = 3.2,
        int exceptions = 0,
        string startedAt = "2026-10-04T20:31:19Z")
    {
        var process = uptime is null
            ? string.Empty
            : string.Create(CultureInfo.InvariantCulture, $$""", "process": { "cpuPercent": {{cpu}}, "workingSetMb": 412, "gcHeapMb": 96, "threads": 41, "inFlight": 2, "exceptionsPerMinute": {{exceptions}}, "uptimeSeconds": {{uptime}} }""");
        return string.Create(
            CultureInfo.InvariantCulture,
            $$"""{ "windowSeconds": 60, "startedAt": "{{startedAt}}", "requests": { "perMinute": {{requests}}, "frontDoor": {{requests - 2}}, "direct": 2, "errors": {{errors}}, "p95Ms": 85 }, "probes": { "perMinute": 4, "frontDoor": 6 }, "sql": { "perMinute": {{sql}}, "p95Ms": 12 }, "http": { "perMinute": 0 }{{process}} }""");
    }

    public static DashboardMonitor Monitor(StubHandler handler, SignallingTimeProvider time, EventLog? events = null, string topology = Topology)
    {
        var http = new HttpClient(handler);
        return new DashboardMonitor(TopologyParser.Parse(topology).Topology!, new NodeProber(http, time), new PinnedVersionsReader(http, time), time, events);
    }

    /// <summary>Every endpoint healthy, on the given version, with telemetry, build facts and the delivery file.</summary>
    public static HttpResponseMessage Answer(HttpRequestMessage request, string version = "2.4.15", string? telemetry = null) =>
        request.RequestUri!.AbsolutePath switch
        {
            "/_version" => StubHandler.Answer(HttpStatusCode.OK, $$"""{"version":"{{version}}+sha"}"""),
            "/_telemetry" => StubHandler.Answer(HttpStatusCode.OK, telemetry ?? Telemetry()),
            "/_build" => StubHandler.Answer(HttpStatusCode.OK, Build),
            "/org/demo-system/status/delivery.json" => StubHandler.Answer(HttpStatusCode.OK, Delivery),
            CostPath => StubHandler.Answer(HttpStatusCode.OK, Cost),
            _ => StubHandler.Answer(HttpStatusCode.OK, "Healthy"),
        };

    public static int Count(StubHandler handler, string path) => handler.Requests.Count(request => request.RequestUri!.AbsolutePath == path);
}
