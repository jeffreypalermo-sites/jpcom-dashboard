namespace Dashboard.Runtime;

public enum DashboardView
{
    /// <summary>The tiles of every environment: the default.</summary>
    Health,

    /// <summary>One environment's runtime diagram, updated live.</summary>
    Runtime,

    /// <summary>The cluster the system runs in: the AKS service, its nodes and its pods. Only for a topology with a cluster.</summary>
    Cluster,
}

/// <summary>
/// The view in the page's address (its hash, without <c>#</c>): <c>runtime</c> or <c>runtime/&lt;environment&gt;</c> is the
/// runtime view, <c>runtime/&lt;environment&gt;/database</c> the database view of that environment (the runtime view,
/// zoomed into its database), <c>runtime/&lt;environment&gt;/app/&lt;deployable&gt;</c> the application view of that
/// deployable there (the runtime view, zoomed into one of its boxes), <c>cluster</c> the cluster view, anything else
/// the health view. A link with the hash opens that view.
/// </summary>
/// <param name="Database">The runtime view shows the environment's database view; only with an environment.</param>
/// <param name="Application">
/// The deployable whose application view the runtime view shows; only with an environment, and only a name of
/// letters, digits, <c>-</c> and <c>_</c> (<see cref="ApplicationText.IsName"/>).
/// </param>
public sealed record ViewAddress(DashboardView View, string? Environment = null, bool Database = false, string? Application = null)
{
    private const string RuntimeName = "runtime";
    private const string ClusterName = "cluster";
    private const string DatabaseName = "database";
    private const string ApplicationName = "app";

    public static ViewAddress Health { get; } = new(DashboardView.Health);

    public static ViewAddress Cluster { get; } = new(DashboardView.Cluster);

    public static ViewAddress Parse(string? hash)
    {
        var text = (hash ?? string.Empty).Trim().TrimStart('#').Trim('/');
        var slash = text.IndexOf('/', StringComparison.Ordinal);
        var head = slash < 0 ? text : text[..slash];
        if (string.Equals(head, ClusterName, StringComparison.OrdinalIgnoreCase))
        {
            return Cluster;
        }

        if (!string.Equals(head, RuntimeName, StringComparison.OrdinalIgnoreCase))
        {
            return Health;
        }

        var environment = slash < 0 ? string.Empty : text[(slash + 1)..].Trim('/');

        // An environment's name has no slash: what follows one is a part of its view, "app/<deployable>" or "database".
        // A deployable's name the address refuses names no view: the environment's diagram is shown.
        var last = environment.LastIndexOf('/');
        var marker = $"/{ApplicationName}";
        if (last > marker.Length && environment[..last].EndsWith(marker, StringComparison.OrdinalIgnoreCase))
        {
            var application = environment[(last + 1)..];
            environment = environment[..(last - marker.Length)].TrimEnd('/');
            return string.IsNullOrWhiteSpace(environment)
                ? new ViewAddress(DashboardView.Runtime)
                : new ViewAddress(DashboardView.Runtime, environment, Application: ApplicationText.IsName(application) ? application : null);
        }

        var database = false;
        if (last > 0 && string.Equals(environment[(last + 1)..], DatabaseName, StringComparison.OrdinalIgnoreCase))
        {
            database = true;
            environment = environment[..last].TrimEnd('/');
        }

        return string.IsNullOrWhiteSpace(environment)
            ? new ViewAddress(DashboardView.Runtime)
            : new ViewAddress(DashboardView.Runtime, environment, database);
    }

    /// <summary>The hash for this view, without <c>#</c>: empty for the health view.</summary>
    public string ToHash() => View switch
    {
        DashboardView.Runtime when Environment is { } environment && ApplicationText.IsName(Application) => $"{RuntimeName}/{environment}/{ApplicationName}/{Application}",
        DashboardView.Runtime when Environment is { } environment && Database => $"{RuntimeName}/{environment}/{DatabaseName}",
        DashboardView.Runtime when Environment is { } environment => $"{RuntimeName}/{environment}",
        DashboardView.Runtime => RuntimeName,
        DashboardView.Cluster => ClusterName,
        _ => string.Empty,
    };
}
