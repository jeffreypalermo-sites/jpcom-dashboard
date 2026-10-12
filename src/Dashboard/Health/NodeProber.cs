using Microsoft.AspNetCore.Components.WebAssembly.Http;

namespace Dashboard.Health;

/// <summary>Checks one endpoint: calls its probe path, classifies the answer and reads the app version.</summary>
public sealed class NodeProber(HttpClient http, TimeProvider time)
{
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(10);

    /// <summary>How long a node may take to answer before it counts as unreachable.</summary>
    public TimeSpan Timeout { get; init; } = DefaultTimeout;

    /// <summary>
    /// Never throws for a failing node: no answer is the result Unreachable. Only the caller's cancellation throws.
    /// </summary>
    public async Task<ProbeResult> ProbeAsync(
        Uri baseAddress,
        string probePath,
        string? versionPath,
        CancellationToken cancellationToken)
    {
        var checkedAt = time.GetUtcNow();
        using var timeout = new CancellationTokenSource(Timeout, time);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeout.Token);

        // The version is read at the same time as the probe, so a node costs one timeout at most.
        var version = versionPath is null
            ? Task.FromResult<string?>(null)
            : ReadVersionAsync(ProbeUrl.Combine(baseAddress, versionPath), linked.Token);

        ProbeResult result;
        var started = time.GetTimestamp();
        try
        {
            using var request = NewRequest(ProbeUrl.Combine(baseAddress, probePath));
            using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, linked.Token);
            var latency = (int)Math.Round(time.GetElapsedTime(started).TotalMilliseconds);
            var status = (int)response.StatusCode;
            result = new ProbeResult(HealthClassifier.FromStatusCode(status), status, latency, checkedAt);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            result = Unreachable(checkedAt, $"No answer within {Timeout.TotalSeconds:0} s.");
        }
        catch (HttpRequestException)
        {
            result = Unreachable(
                checkedAt,
                "The browser could not read an answer: the network failed, or the node does not allow this origin (CORS).");
        }

        var reported = await version;
        cancellationToken.ThrowIfCancellationRequested();
        return result with { Version = reported };
    }

    private static ProbeResult Unreachable(DateTimeOffset checkedAt, string detail) =>
        new(HealthState.Unreachable, StatusCode: null, LatencyMs: null, checkedAt, Detail: detail);

    /// <summary>
    /// Asks a container app (<see cref="ContainerAppProbe"/>): its health path with the probe Health check, its root
    /// otherwise. An answer the browser may not read (the app does not allow this page's origin) is asked again
    /// without reading it, so an app that answers is told from one that does not; with
    /// <paramref name="corsRefused"/> it is asked that way at once, one request per probe. Never throws for a failing
    /// app: no answer is an answer without a status. Only the caller's cancellation throws.
    /// </summary>
    public async Task<ContainerAppAnswer> ProbeContainerAppAsync(ContainerAppTarget target, ProbeKind probe, bool corsRefused, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(target);
        var path = target.PathFor(probe);
        var readsHealth = target.ReadsHealth(probe);
        var address = ProbeUrl.Combine(target.Url, path);
        var checkedAt = time.GetUtcNow();
        using var timeout = new CancellationTokenSource(Timeout, time);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeout.Token);
        var started = time.GetTimestamp();
        try
        {
            if (!corsRefused)
            {
                try
                {
                    using var request = NewRequest(address);
                    using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, linked.Token);
                    return new ContainerAppAnswer(path, readsHealth, (int)response.StatusCode, false, Elapsed(started), checkedAt);
                }
                catch (HttpRequestException)
                {
                    // Not readable: refused by CORS, or no answer at all. Asked without reading, the browser tells which.
                    started = time.GetTimestamp();
                }
            }

            using var unread = NewRequest(address);
            unread.SetBrowserRequestMode(BrowserRequestMode.NoCors);
            using var answer = await http.SendAsync(unread, HttpCompletionOption.ResponseHeadersRead, linked.Token);
            return new ContainerAppAnswer(path, readsHealth, null, true, Elapsed(started), checkedAt);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return new ContainerAppAnswer(path, readsHealth, null, false, null, checkedAt, $"No answer within {Timeout.TotalSeconds:0} s.");
        }
        catch (HttpRequestException)
        {
            return new ContainerAppAnswer(path, readsHealth, null, false, null, checkedAt, "The browser got no answer: the network failed, or nothing answers at the address.");
        }
    }

    private int Elapsed(long started) => (int)Math.Round(time.GetElapsedTime(started).TotalMilliseconds);

    /// <summary>The version is a courtesy: when it cannot be read, the node has no version, not a failure.</summary>
    private async Task<string?> ReadVersionAsync(Uri address, CancellationToken cancellationToken)
    {
        try
        {
            using var request = NewRequest(address);
            using var response = await http.SendAsync(request, cancellationToken);
            return response.IsSuccessStatusCode
                ? VersionText.FromVersionResponse(await response.Content.ReadAsStringAsync(cancellationToken))
                : null;
        }
        catch (OperationCanceledException)
        {
            return null;
        }
        catch (HttpRequestException)
        {
            return null;
        }
    }

    /// <summary>
    /// A node's own count of its calls (<see cref="TelemetrySnapshot"/>). Like the version, a courtesy: a node without
    /// the endpoint, or one that does not answer in time, has no numbers, and that is no failure.
    /// </summary>
    public async Task<TelemetrySnapshot?> ReadTelemetryAsync(Uri baseAddress, string telemetryPath, CancellationToken cancellationToken)
    {
        using var timeout = new CancellationTokenSource(Timeout, time);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeout.Token);
        try
        {
            using var request = NewRequest(ProbeUrl.Combine(baseAddress, telemetryPath));
            using var response = await http.SendAsync(request, linked.Token);
            return response.IsSuccessStatusCode
                ? TelemetrySnapshot.Parse(await response.Content.ReadAsStringAsync(linked.Token), time.GetUtcNow())
                : null;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return null;
        }
        catch (HttpRequestException)
        {
            return null;
        }
    }

    /// <summary>
    /// The entries of a node's detailed health check (<see cref="HealthDetail"/>). A courtesy like the telemetry: no
    /// answer, or anything but the expected JSON, is no entries. The body is read whatever the HTTP status: a health
    /// check that fails answers 503 with the same JSON, and that answer is the one worth reading.
    /// </summary>
    public async Task<HealthDetail?> ReadHealthDetailAsync(Uri baseAddress, string detailPath, CancellationToken cancellationToken)
    {
        using var timeout = new CancellationTokenSource(Timeout, time);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeout.Token);
        try
        {
            using var request = NewRequest(ProbeUrl.Combine(baseAddress, detailPath));
            using var response = await http.SendAsync(request, linked.Token);
            return HealthDetail.Parse(await response.Content.ReadAsStringAsync(linked.Token), time.GetUtcNow());
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return null;
        }
        catch (HttpRequestException)
        {
            return null;
        }
    }

    /// <summary>
    /// The build a node runs (<see cref="BuildInfo"/>), from its build endpoint. A courtesy like the telemetry: no
    /// answer, another status or anything but the expected JSON is no build facts.
    /// </summary>
    public Task<BuildInfo?> ReadBuildAsync(Uri baseAddress, string buildPath, CancellationToken cancellationToken) =>
        ReadAsync(ProbeUrl.Combine(baseAddress, buildPath), BuildInfo.Parse, cancellationToken);

    /// <summary>
    /// The build of the dashboard itself (<see cref="BuildInfo"/>): a file of its own site, read from the page's own
    /// address (the path is taken from the site's root, as <c>topology.json</c> is, whatever it starts with; an
    /// address of another site is not read). A courtesy like a node's build: no file, or anything but the expected
    /// JSON, is no build facts.
    /// </summary>
    public Task<BuildInfo?> ReadOwnBuildAsync(string buildPath, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(buildPath);
        return Uri.TryCreate(buildPath.TrimStart('/'), UriKind.Relative, out var address)
            ? ReadAsync(address, BuildInfo.Parse, cancellationToken)
            : Task.FromResult<BuildInfo?>(null);
    }

    /// <summary>The system's delivery facts (<see cref="DeliveryReport"/>), from the address the topology gives.</summary>
    public Task<DeliveryReport?> ReadDeliveryAsync(Uri address, CancellationToken cancellationToken) =>
        ReadAsync(address, DeliveryReport.Parse, cancellationToken);

    /// <summary>What the system cost in Azure (<see cref="CostReport"/>), from the address the topology gives.</summary>
    public Task<CostReport?> ReadCostAsync(Uri address, CancellationToken cancellationToken) =>
        ReadAsync(address, CostReport.Parse, cancellationToken);

    /// <summary>The system's deployments in flight (<see cref="DeploymentsReport"/>), from the address the topology gives.</summary>
    public Task<DeploymentsReport?> ReadDeploymentsAsync(Uri address, CancellationToken cancellationToken) =>
        ReadAsync(address, DeploymentsReport.Parse, cancellationToken);

    /// <summary>What Resource Health said of the system's resources (<see cref="ResourceHealthReport"/>), from <c>containers.json</c>.</summary>
    public Task<ResourceHealthReport?> ReadResourceHealthAsync(Uri address, CancellationToken cancellationToken) =>
        ReadAsync(address, ResourceHealthReport.Parse, cancellationToken);

    /// <summary>An optional JSON answer, read with the node's timeout: null whenever it cannot be read.</summary>
    private async Task<T?> ReadAsync<T>(Uri address, Func<string?, T?> parse, CancellationToken cancellationToken)
        where T : class
    {
        using var timeout = new CancellationTokenSource(Timeout, time);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeout.Token);
        try
        {
            using var request = NewRequest(address);
            using var response = await http.SendAsync(request, linked.Token);
            return response.IsSuccessStatusCode ? parse(await response.Content.ReadAsStringAsync(linked.Token)) : null;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return null;
        }
        catch (HttpRequestException)
        {
            return null;
        }
    }

    /// <summary>A GET that the browser neither answers from its cache nor stores in it.</summary>
    internal static HttpRequestMessage NewRequest(Uri address)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, address);
        request.SetBrowserRequestCache(BrowserRequestCache.NoStore);
        return request;
    }
}
