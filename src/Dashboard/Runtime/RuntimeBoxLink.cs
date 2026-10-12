using Dashboard.Health;

namespace Dashboard.Runtime;

/// <summary>
/// Where a box of the runtime diagram leads when the topology has no link for it: the one link of the <c>links</c>
/// the deployment wrote for it into the manifest (a frame, a region, a node), with the words of its title. A click
/// anywhere on the box goes where its name goes, so every box has at most one such link.
/// </summary>
public static class RuntimeBoxLink
{
    /// <summary>A region: the resources of its resource group in that region, a query in Azure Resource Graph Explorer.</summary>
    public const string Resources = "resources";

    /// <summary>A namespace's frame: the cluster's namespaces in the Azure portal.</summary>
    public const string Namespaces = "namespaces";

    /// <summary>The boundary of an application with its own runtime: its project in Octopus Deploy.</summary>
    public const string Project = "project";

    /// <summary>An address a browser opens: the browser's box, a public address or a node the system knows no resource of.</summary>
    public const string Site = "site";

    public const string NewTabSuffix = " (opens in a new tab)";

    /// <summary>The keys, the most specific place first: a box with several links leads to the first it has.</summary>
    private static readonly string[] Order = [LinkSet.Portal, Resources, Namespaces, LinkSet.Workloads, Project, Site];

    public static RuntimeLink? Of(RuntimeFrame frame)
    {
        ArgumentNullException.ThrowIfNull(frame);
        return Of(frame.Links, frame.Name, frame.Kind switch
        {
            "subscription" => "The Azure subscription",
            "resourceGroup" => $"The resource group {frame.Name}",
            "frontDoorProfile" => $"The Front Door profile {frame.Name}",
            "plan" => $"The App Service plan {frame.Name}",
            "cluster" => $"The AKS cluster {frame.Name}",
            _ => frame.Name,
        });
    }

    public static RuntimeLink? Of(RuntimeRegion region)
    {
        ArgumentNullException.ThrowIfNull(region);
        return Of(region.Links, region.Name, region.Name);
    }

    public static RuntimeLink? Of(RuntimeNode node)
    {
        ArgumentNullException.ThrowIfNull(node);
        return Of(node.Links, node.Name, node.Kind switch
        {
            RuntimeNodeKind.StaticSite => $"The Static Web App {node.Name}",
            RuntimeNodeKind.Sql => $"The database {node.Name}",
            _ => node.Name,
        });
    }

    /// <param name="links">The element's links; null without any.</param>
    /// <param name="name">The element's name, as drawn.</param>
    /// <param name="resource">The element as a resource in the Azure portal: its kind and its name.</param>
    private static RuntimeLink? Of(LinkSet? links, string name, string resource)
    {
        if (links is null)
        {
            return null;
        }

        foreach (var key in Order)
        {
            if (links[key] is { } address)
            {
                return new RuntimeLink(address.AbsoluteUri, key switch
                {
                    LinkSet.Portal => LinkText.Portal($"{resource} in the Azure portal"),
                    Resources => LinkText.Portal($"The resources in {name}, a query in Azure Resource Graph Explorer"),
                    Namespaces => LinkText.Portal($"The namespaces of the cluster in the Azure portal: {name} is one of them"),
                    LinkSet.Workloads => LinkText.Portal($"The workloads of the cluster in the Azure portal: {name} is one of them"),
                    Project => $"The project that deploys {name} in Octopus Deploy{LinkText.OctopusSuffix}",
                    _ => $"{name}: {address.Host}, as a browser opens it{NewTabSuffix}",
                });
            }
        }

        return null;
    }
}
