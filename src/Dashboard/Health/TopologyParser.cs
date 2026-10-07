using System.Globalization;
using System.Text.Json;

namespace Dashboard.Health;

/// <summary>The outcome of reading <c>topology.json</c>: a topology, or the reasons there is none.</summary>
public sealed record TopologyParseResult(Topology? Topology, IReadOnlyList<string> Errors)
{
    public bool IsValid => Topology is not null;

    public static TopologyParseResult Failed(params string[] errors) => new(null, errors);
}

/// <summary>
/// Reads <c>topology.json</c>. Required: <c>environments</c> (an array), each environment's <c>name</c> and each
/// node's <c>url</c>. Everything else is optional and gets a default; unknown fields are ignored.
/// </summary>
public static class TopologyParser
{
    private static readonly JsonDocumentOptions Options = new()
    {
        AllowTrailingCommas = true,
        CommentHandling = JsonCommentHandling.Skip,
    };

    public static TopologyParseResult Parse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return TopologyParseResult.Failed("The file is empty.");
        }

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(json, Options);
        }
        catch (JsonException exception)
        {
            // Line and position, not the exception's message: the published app carries no framework message texts.
            return TopologyParseResult.Failed(
                $"The file is not valid JSON (line {exception.LineNumber + 1}, position {exception.BytePositionInLine + 1}).");
        }

        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return TopologyParseResult.Failed("The file must contain a JSON object.");
            }

            var errors = new List<string>();
            var system = ReadSystem(root);
            var generated = ReadTime(root, "generated");
            var environments = ReadEnvironments(root, errors);
            return errors.Count > 0
                ? new TopologyParseResult(null, errors)
                : new TopologyParseResult(new Topology(system, generated, environments), []);
        }
    }

    private static SystemInfo ReadSystem(JsonElement root)
    {
        var slug = string.Empty;
        string? name = null;
        if (root.TryGetProperty("system", out var system) && system.ValueKind == JsonValueKind.Object)
        {
            slug = ReadText(system, "slug") ?? string.Empty;
            name = ReadText(system, "name");
        }

        return new SystemInfo(slug, name ?? (slug.Length > 0 ? slug : "System"));
    }

    private static List<EnvironmentInfo> ReadEnvironments(JsonElement root, List<string> errors)
    {
        var environments = new List<EnvironmentInfo>();
        if (!root.TryGetProperty("environments", out var array) || array.ValueKind != JsonValueKind.Array)
        {
            errors.Add("environments: missing or not an array.");
            return environments;
        }

        var index = 0;
        foreach (var element in array.EnumerateArray())
        {
            var path = $"environments[{index++}]";
            if (element.ValueKind != JsonValueKind.Object)
            {
                errors.Add($"{path}: not an object.");
                continue;
            }

            var name = ReadText(element, "name");
            if (name is null)
            {
                errors.Add($"{path}.name: missing or empty.");
                continue;
            }

            var deployables = ReadDeployables(element, path, errors);
            environments.Add(new EnvironmentInfo(name, ReadText(element, "tier"), deployables));
        }

        return environments;
    }

    private static List<DeployableInfo> ReadDeployables(JsonElement environment, string environmentPath, List<string> errors)
    {
        var deployables = new List<DeployableInfo>();
        if (!TryReadOptionalArray(environment, "deployables", environmentPath, errors, out var array))
        {
            return deployables;
        }

        var index = 0;
        foreach (var element in array.EnumerateArray())
        {
            var path = $"{environmentPath}.deployables[{index++}]";
            if (element.ValueKind != JsonValueKind.Object)
            {
                errors.Add($"{path}: not an object.");
                continue;
            }

            var frontDoor = ReadFrontDoor(element, path, errors);
            var nodes = ReadNodes(element, path, errors);
            deployables.Add(new DeployableInfo(
                ReadText(element, "name") ?? "app",
                frontDoor,
                ReadPath(element, "healthPath", DeployableInfo.DefaultHealthPath),
                ReadPath(element, "alivePath", DeployableInfo.DefaultAlivePath),
                ReadPath(element, "versionPath", DeployableInfo.DefaultVersionPath),
                nodes));
        }

        return deployables;
    }

    private static Uri? ReadFrontDoor(JsonElement deployable, string deployablePath, List<string> errors)
    {
        if (!deployable.TryGetProperty("frontDoor", out var value) || value.ValueKind == JsonValueKind.Null)
        {
            return null;
        }

        var address = ReadAddress(value);
        if (address is null)
        {
            errors.Add($"{deployablePath}.frontDoor: not an absolute http or https address.");
        }

        return address;
    }

    private static List<NodeInfo> ReadNodes(JsonElement deployable, string deployablePath, List<string> errors)
    {
        var nodes = new List<NodeInfo>();
        if (!TryReadOptionalArray(deployable, "nodes", deployablePath, errors, out var array))
        {
            return nodes;
        }

        var index = 0;
        foreach (var element in array.EnumerateArray())
        {
            var position = index++;
            var path = $"{deployablePath}.nodes[{position}]";
            if (element.ValueKind != JsonValueKind.Object)
            {
                errors.Add($"{path}: not an object.");
                continue;
            }

            var url = element.TryGetProperty("url", out var value) ? ReadAddress(value) : null;
            if (url is null)
            {
                errors.Add($"{path}.url: missing or not an absolute http or https address.");
                continue;
            }

            // Without a role, the order of the file decides: the first node is the primary.
            var role = ReadText(element, "role")?.ToLowerInvariant()
                ?? (position == 0 ? NodeInfo.PrimaryRole : NodeInfo.StandbyRole);
            nodes.Add(new NodeInfo(ReadText(element, "name") ?? url.Host, ReadText(element, "region"), role, url));
        }

        return nodes;
    }

    /// <summary>False when the array is absent (fine: it is empty) or of the wrong type (an error).</summary>
    private static bool TryReadOptionalArray(
        JsonElement parent,
        string property,
        string parentPath,
        List<string> errors,
        out JsonElement array)
    {
        if (!parent.TryGetProperty(property, out array) || array.ValueKind == JsonValueKind.Null)
        {
            return false;
        }

        if (array.ValueKind != JsonValueKind.Array)
        {
            errors.Add($"{parentPath}.{property}: not an array.");
            return false;
        }

        return true;
    }

    private static string? ReadText(JsonElement parent, string property)
    {
        if (!parent.TryGetProperty(property, out var value) || value.ValueKind != JsonValueKind.String)
        {
            return null;
        }

        var text = value.GetString()?.Trim();
        return string.IsNullOrEmpty(text) ? null : text;
    }

    private static string ReadPath(JsonElement parent, string property, string fallback)
    {
        var path = ReadText(parent, property) ?? fallback;
        return path.StartsWith('/') ? path : $"/{path}";
    }

    private static DateTimeOffset? ReadTime(JsonElement parent, string property)
    {
        var text = ReadText(parent, property);
        return DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var time)
            ? time
            : null;
    }

    private static Uri? ReadAddress(JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.String)
        {
            return null;
        }

        return Uri.TryCreate(value.GetString()?.Trim(), UriKind.Absolute, out var address)
            && (address.Scheme == Uri.UriSchemeHttp || address.Scheme == Uri.UriSchemeHttps)
            ? address
            : null;
    }
}
