using System.Globalization;
using System.Text.Json;
using Dashboard.Health;

namespace Dashboard.Runtime;

/// <summary>
/// <c>runtime/&lt;env&gt;.database.json</c>: the schema of the environment's database as the deployment read it, and
/// which drawn table of <c>runtime/&lt;env&gt;.database.svg</c> is which.
/// </summary>
/// <param name="Environment">The environment's name.</param>
/// <param name="Database">The database's name.</param>
/// <param name="ReadAt">When the deployment read the schema; null when the file does not say.</param>
/// <param name="Source">
/// The live file's address (<see cref="DatabaseLive"/>), on another origin; null without one, and for anything but an
/// absolute https address.
/// </param>
/// <param name="Truncated">The database has more tables than the diagram draws.</param>
/// <param name="Tables">The tables the diagram draws.</param>
/// <param name="Drawn">How many tables the diagram draws; as many as <paramref name="Tables"/> when the file does not say.</param>
public sealed record DatabaseManifest(
    string Environment,
    string Database,
    DateTimeOffset? ReadAt,
    Uri? Source,
    bool Truncated,
    IReadOnlyList<DatabaseTable> Tables,
    int Drawn);

/// <param name="Alias">The table's name in the PlantUML source (<see cref="DatabaseText.Alias"/>).</param>
/// <param name="QualifiedName">The <c>data-qualified-name</c> of the table's group in the SVG.</param>
/// <param name="Rows">The rows counted when the schema was read; null when the file has no count.</param>
/// <param name="Columns">How many columns the table has; null when the file does not say.</param>
public sealed record DatabaseTable(string Alias, string QualifiedName, string Schema, string Name, long? Rows, int? Columns);

/// <summary>
/// The live file (<see cref="DatabaseManifest.Source"/>): the same schema read again later, with every column. It is
/// not the dashboard's own file, so every part of it is optional.
/// </summary>
/// <param name="Generated">When it was written; null when the file does not say.</param>
public sealed record DatabaseLive(DateTimeOffset? Generated, IReadOnlyList<DatabaseLiveTable> Tables, bool Truncated);

public sealed record DatabaseLiveTable(string Schema, string Name, long? Rows, IReadOnlyList<DatabaseColumn> Columns);

/// <param name="Key">Empty, <c>pk</c>, <c>fk</c> or <c>pk,fk</c>.</param>
public sealed record DatabaseColumn(string Name, string Type, bool? Nullable, string Key);

/// <summary>The runtime view asks for the database view of an environment, or for its diagram again.</summary>
/// <param name="Shown">The database view is to be shown.</param>
/// <param name="Step">The change is a step of the browser's history: its Back undoes it.</param>
public sealed record RuntimeDatabaseChange(string Environment, bool Shown, bool Step);

/// <summary>A drawn table and the words of its row count: what <c>js/database.js</c> writes into the diagram.</summary>
public sealed record DatabaseCount(string QualifiedName, string Text);

/// <summary>One line of "Tables as a list".</summary>
public sealed record DatabaseListRow(string Name, int? Columns, long? Rows);

/// <summary>
/// Reads the database view's two files. The manifest is the deployment's (<c>deploy-staticwebapp.ps1</c>). Required:
/// its environment, its database and, of every table, alias, qualified name and name. Unknown fields are ignored. The
/// live file is somebody else's: a file that cannot be read is no file, never an error.
/// </summary>
public static class DatabaseParser
{
    /// <summary>The most tables and the most columns of one table that are kept.</summary>
    private const int MostTables = 2000;
    private const int MostColumns = 1000;

    private static readonly JsonDocumentOptions Options = new()
    {
        AllowTrailingCommas = true,
        CommentHandling = JsonCommentHandling.Skip,
    };

    public static RuntimeParseResult<DatabaseManifest> ParseManifest(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return Failed("The file is empty.");
        }

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(json, Options);
        }
        catch (JsonException exception)
        {
            return Failed($"The file is not valid JSON (line {exception.LineNumber + 1}, position {exception.BytePositionInLine + 1}).");
        }

        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return Failed("The file must contain a JSON object.");
            }

            var errors = new List<string>();
            var environment = JsonRead.Text(root, "environment");
            var database = JsonRead.Text(root, "database");
            if (environment is null)
            {
                errors.Add("environment: missing or empty.");
            }

            if (database is null)
            {
                errors.Add("database: missing or empty.");
            }

            if (root.TryGetProperty("tables", out var array) && array.ValueKind is not (JsonValueKind.Array or JsonValueKind.Null))
            {
                errors.Add("tables: not an array.");
            }

            var tables = new List<DatabaseTable>();
            var index = 0;
            foreach (var element in JsonRead.Items(root, "tables").Take(MostTables))
            {
                var path = $"tables[{index++}]";
                var alias = JsonRead.Text(element, "alias");
                var qualifiedName = JsonRead.Text(element, "qualifiedName");
                var name = JsonRead.Text(element, "name");
                if (alias is null || qualifiedName is null || name is null)
                {
                    errors.Add($"{path}: alias, qualifiedName and name are required.");
                    continue;
                }

                tables.Add(new DatabaseTable(alias, qualifiedName, JsonRead.Text(element, "schema") ?? string.Empty, name, JsonRead.Long(element, "rows"), JsonRead.Count(element, "columns")));
            }

            return errors.Count > 0 || environment is null || database is null
                ? new RuntimeParseResult<DatabaseManifest>(null, errors)
                : new RuntimeParseResult<DatabaseManifest>(
                    new DatabaseManifest(
                        environment,
                        database,
                        JsonRead.Time(root, "readAt"),
                        Source(root),
                        JsonRead.Flag(root, "truncated") ?? false,
                        tables,
                        JsonRead.Count(root, "drawn") ?? tables.Count),
                    []);
        }
    }

    /// <summary>The live file; null for anything that is no JSON object with a list of tables.</summary>
    public static DatabaseLive? ParseLive(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(json, Options);
        }
        catch (JsonException)
        {
            return null;
        }

        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("tables", out var array) || array.ValueKind != JsonValueKind.Array)
            {
                return null;
            }

            var tables = new List<DatabaseLiveTable>();
            foreach (var element in JsonRead.Items(root, "tables").Take(MostTables))
            {
                if (JsonRead.Text(element, "name") is not { } name)
                {
                    continue;
                }

                var columns = JsonRead.Items(element, "columns")
                    .Take(MostColumns)
                    .Where(column => JsonRead.Text(column, "name") is not null)
                    .Select(column => new DatabaseColumn(
                        JsonRead.Text(column, "name")!,
                        JsonRead.Text(column, "type") ?? string.Empty,
                        JsonRead.Flag(column, "nullable"),
                        JsonRead.Text(column, "key")?.ToLowerInvariant() ?? string.Empty))
                    .ToList();
                tables.Add(new DatabaseLiveTable(JsonRead.Text(element, "schema") ?? string.Empty, name, JsonRead.Long(element, "rows"), columns));
            }

            return new DatabaseLive(JsonRead.Time(root, "generated"), tables, JsonRead.Flag(root, "truncated") ?? false);
        }
    }

    private static RuntimeParseResult<DatabaseManifest> Failed(string problem) => new(null, [problem]);

    /// <summary>The live file is fetched by the page itself: only an absolute https address is kept.</summary>
    private static Uri? Source(JsonElement root) =>
        JsonRead.Address(root, "source") is { } address && address.Scheme == Uri.UriSchemeHttps ? address : null;
}

/// <summary>The words of the database view, and what it decides: which count a table shows, whether the schema changed.</summary>
public static class DatabaseText
{
    /// <summary>The line the view shows when the live file's tables are not the diagram's.</summary>
    public const string SchemaChanged = "The schema changed since this diagram was drawn: deploy the dashboard again.";

    /// <summary>
    /// A row count as the diagram writes it: <c>1,234 rows</c>, <c>1 row</c>. (In the SVG the space is a no-break
    /// space, which <c>js/database.js</c> reads and writes.)
    /// </summary>
    public static string Rows(long rows) =>
        rows == 1 ? "1 row" : string.Create(CultureInfo.InvariantCulture, $"{rows:#,0} rows");

    /// <summary>
    /// A table's alias: <c>t_</c>, the schema, <c>_</c> and the name, with every character outside
    /// <c>[A-Za-z0-9_]</c> replaced by <c>_</c>.
    /// </summary>
    public static string Alias(string schema, string name) =>
        string.Concat($"t_{schema}_{name}".Select(character => char.IsAsciiLetterOrDigit(character) || character == '_' ? character : '_'));

    /// <summary>A table as the list names it: <c>dbo.Employee</c>.</summary>
    public static string QualifiedTable(string schema, string name) => schema.Length == 0 ? name : $"{schema}.{name}";

    /// <summary>
    /// The node of the runtime diagram that opens the database view: the environment's database, the first node of
    /// kind <c>sql</c>; null in a diagram without one.
    /// </summary>
    public static RuntimeNode? Box(RuntimeManifest manifest)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        return manifest.Nodes.FirstOrDefault(node => node.Kind == RuntimeNodeKind.Sql);
    }

    /// <summary>The accessible name of the database's box in the runtime diagram.</summary>
    public static string BoxLabel(RuntimeNode node)
    {
        ArgumentNullException.ThrowIfNull(node);
        return $"Database {node.Name}: open its tables";
    }

    /// <summary>
    /// The count every drawn table shows: the live file's where it has the table with a count, else the one the
    /// deployment drew. A table neither counted is left as drawn.
    /// </summary>
    public static IReadOnlyList<DatabaseCount> Counts(DatabaseManifest manifest, DatabaseLive? live)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        var counted = LiveByAlias(live);
        return
        [
            .. manifest.Tables
                .Select(table => (table.QualifiedName, Rows: counted.TryGetValue(table.Alias, out var found) && found.Rows is { } rows ? rows : table.Rows))
                .Where(count => count.Rows is not null)
                .Select(count => new DatabaseCount(count.QualifiedName, Rows(count.Rows!.Value))),
        ];
    }

    /// <summary>
    /// Whether the live file's tables are other tables than the diagram's: one the diagram draws is gone, or there is
    /// one it does not draw. A diagram that draws only the first tables (<see cref="DatabaseManifest.Truncated"/>)
    /// lacks tables by design, so there only a drawn table that is gone counts.
    /// </summary>
    public static bool HasSchemaChanged(DatabaseManifest manifest, DatabaseLive? live)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        if (live is null)
        {
            return false;
        }

        var drawn = manifest.Tables.Select(table => table.Alias).ToHashSet(StringComparer.Ordinal);
        var counted = LiveByAlias(live);
        return drawn.Any(alias => !counted.ContainsKey(alias))
            || (!manifest.Truncated && counted.Keys.Any(alias => !drawn.Contains(alias)));
    }

    /// <summary>
    /// The moment the view's facts are of: when the deployment read the schema, or the live file's when that is
    /// newer; and whether it is the live file's.
    /// </summary>
    public static (DateTimeOffset? At, bool Live) ReadAt(DatabaseManifest manifest, DatabaseLive? live)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        return live?.Generated is { } generated && (manifest.ReadAt is not { } readAt || generated > readAt)
            ? (generated, true)
            : (manifest.ReadAt, false);
    }

    /// <summary>The line under the heading: <c>Schema read 2 h ago (at the environment's last deployment)</c>.</summary>
    public static string? ReadLine(DatabaseManifest manifest, DatabaseLive? live, DateTimeOffset now) =>
        ReadAt(manifest, live) switch
        {
            ({ } at, true) => $"Schema read {TimeText.Ago(at, now)}",
            ({ } at, false) => $"Schema read {TimeText.Ago(at, now)} (at the environment's last deployment)",
            _ => null,
        };

    /// <summary>The line of a diagram that draws only the first tables; null for one that draws all.</summary>
    public static string? TruncatedLine(DatabaseManifest manifest)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        return manifest.Truncated
            ? string.Create(CultureInfo.InvariantCulture, $"The diagram draws the first {manifest.Drawn} tables.")
            : null;
    }

    /// <summary>"Tables as a list": the live file's tables when it is there, else the diagram's.</summary>
    public static IReadOnlyList<DatabaseListRow> List(DatabaseManifest manifest, DatabaseLive? live)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        return live is not null
            ? [.. live.Tables.Select(table => new DatabaseListRow(QualifiedTable(table.Schema, table.Name), table.Columns.Count, table.Rows))]
            : [.. manifest.Tables.Select(table => new DatabaseListRow(QualifiedTable(table.Schema, table.Name), table.Columns, table.Rows))];
    }

    /// <summary>A column's key in words: <c>primary key</c>, <c>foreign key</c>, both, or nothing.</summary>
    public static string Key(string key)
    {
        ArgumentNullException.ThrowIfNull(key);
        var parts = key.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return string.Join(", ", parts.Select(part => part switch
        {
            "pk" => "primary key",
            "fk" => "foreign key",
            _ => part,
        }));
    }

    /// <summary>Whether a column takes null, in a word; a dash where the live file does not say.</summary>
    public static string Nullable(bool? nullable) => nullable switch
    {
        true => "yes",
        false => "no",
        null => "–",
    };

    /// <summary>A count for the list: <c>1,234</c>; a dash for one nobody counted.</summary>
    public static string Number(long? number) =>
        number is { } value ? value.ToString("#,0", CultureInfo.InvariantCulture) : "–";

    private static Dictionary<string, DatabaseLiveTable> LiveByAlias(DatabaseLive? live)
    {
        var tables = new Dictionary<string, DatabaseLiveTable>(StringComparer.Ordinal);
        foreach (var table in live?.Tables ?? [])
        {
            tables.TryAdd(Alias(table.Schema, table.Name), table);
        }

        return tables;
    }
}

/// <summary>
/// When the live file is read again: with a check round of the page, so only while the page is neither paused nor
/// hidden (and only while the database view is shown: nothing else asks), and never more often than once a minute.
/// </summary>
public static class DatabaseRefresh
{
    public static readonly TimeSpan Interval = TimeSpan.FromMinutes(1);

    /// <param name="active">The page checks: it is neither paused nor hidden.</param>
    /// <param name="lastRead">When the live file was last asked for; null when it never was.</param>
    public static bool IsDue(bool active, DateTimeOffset? lastRead, DateTimeOffset now) =>
        active && (lastRead is not { } last || now - last >= Interval);
}
