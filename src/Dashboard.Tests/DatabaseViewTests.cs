using System.Net;

namespace Dashboard.Tests;

public class DatabaseViewTests
{
    private const string ManifestJson = """
        {
          "environment": "tdd",
          "database": "sqldb-cmdemo1-tdd",
          "readAt": "2026-10-12T10:00:00Z",
          "source": "https://raw.githubusercontent.com/org/repo/main/environments/tdd/database.json",
          "truncated": false,
          "plantuml": "1.2026.8",
          "tables": [
            { "alias": "t_dbo_Employee", "qualifiedName": "db.t_dbo_Employee", "schema": "dbo", "name": "Employee", "rows": 123, "columns": 7 },
            { "alias": "t_dbo_WorkOrder", "qualifiedName": "db.t_dbo_WorkOrder", "schema": "dbo", "name": "WorkOrder", "rows": 1234, "columns": 12 }
          ],
          "references": [ { "id": "t_dbo_WorkOrder-to-t_dbo_Employee", "from": "t_dbo_WorkOrder", "to": "t_dbo_Employee", "name": "FK_WorkOrder_Employee", "columns": ["CreatorId"] } ]
        }
        """;

    private const string LiveJson = """
        {
          "generated": "2026-10-12T11:00:00Z",
          "database": "sqldb-cmdemo1-tdd",
          "tables": [
            { "schema": "dbo", "name": "Employee", "rows": 130,
              "columns": [ { "name": "Id", "type": "uniqueidentifier", "nullable": false, "key": "pk" }, { "name": "ManagerId", "type": "uniqueidentifier", "nullable": true, "key": "pk,fk" } ] },
            { "schema": "dbo", "name": "WorkOrder", "rows": 1, "columns": [ { "name": "Id", "type": "uniqueidentifier", "nullable": false, "key": "pk" } ] }
          ],
          "references": [ { "name": "FK_WorkOrder_Employee", "from": "dbo.WorkOrder", "columns": ["CreatorId"], "to": "dbo.Employee", "toColumns": ["Id"] } ],
          "truncated": false
        }
        """;

    private static readonly DateTimeOffset ReadAt = new(2026, 10, 12, 10, 0, 0, TimeSpan.Zero);

    private static DatabaseManifest Manifest(string json = ManifestJson) => DatabaseParser.ParseManifest(json).Value!;

    private static DatabaseLive Live(string json = LiveJson) => DatabaseParser.ParseLive(json)!;

    private static DatabaseLive LiveOf(params string[] tables) =>
        new(ReadAt.AddHours(1), [.. tables.Select(name => new DatabaseLiveTable("dbo", name, 5, []))], false);

    [Fact]
    public void AnEnvironmentOfTheIndexMayNameTheFilesOfItsDatabaseView()
    {
        var index = RuntimeManifestParser.ParseIndex("""
            { "environments": [
              { "name": "tdd", "manifest": "tdd.json", "svg": "tdd.svg", "database": { "svg": "tdd.database.svg", "manifest": "tdd.database.json", "later": 1 } },
              { "name": "uat", "manifest": "uat.json", "svg": "uat.svg" } ] }
            """).Value!;

        Assert.Equal(new RuntimeDatabaseFiles("tdd.database.svg", "tdd.database.json"), index.Environments[0].Database);
        Assert.Null(index.Environments[1].Database);
    }

    [Theory]
    [InlineData("null")]
    [InlineData("\"tdd.database.svg\"")]
    [InlineData("{ }")]
    [InlineData("{ \"svg\": \"tdd.database.svg\" }")]
    [InlineData("{ \"svg\": \"tdd.database.svg\", \"manifest\": 4 }")]
    [InlineData("{ \"svg\": \"../tdd.database.svg\", \"manifest\": \"tdd.database.json\" }")]
    [InlineData("{ \"svg\": \"tdd.database.svg\", \"manifest\": \"https://elsewhere.example.net/tdd.database.json\" }")]
    public void ADatabaseEntryThatNamesNoTwoFilesOfRuntimeIsLeftOutAndTheDiagramStillShows(string database)
    {
        var index = RuntimeManifestParser.ParseIndex($$"""{ "environments": [ { "name": "tdd", "database": {{database}} } ] }""");

        Assert.True(index.IsValid);
        Assert.Equal(new RuntimeIndexEntry("tdd", "tdd.json", "tdd.svg"), Assert.Single(index.Value!.Environments));
    }

    [Fact]
    public void TheSampleIndexHasNoDatabaseView()
    {
        var index = RuntimeManifestParser.ParseIndex(RuntimeManifestParserTests.Sample("index.json")).Value!;

        Assert.All(index.Environments, entry => Assert.Null(entry.Database));
    }

    [Fact]
    public void TheManifestNamesTheDatabaseAndItsDrawnTables()
    {
        var manifest = Manifest();

        Assert.Equal("tdd", manifest.Environment);
        Assert.Equal("sqldb-cmdemo1-tdd", manifest.Database);
        Assert.Equal(ReadAt, manifest.ReadAt);
        Assert.Equal(new Uri("https://raw.githubusercontent.com/org/repo/main/environments/tdd/database.json"), manifest.Source);
        Assert.False(manifest.Truncated);
        Assert.Equal(2, manifest.Drawn);
        Assert.Equal(
            [
                new DatabaseTable("t_dbo_Employee", "db.t_dbo_Employee", "dbo", "Employee", 123, 7),
                new DatabaseTable("t_dbo_WorkOrder", "db.t_dbo_WorkOrder", "dbo", "WorkOrder", 1234, 12),
            ],
            manifest.Tables);
    }

    [Fact]
    public void AManifestWithOnlyWhatIsRequiredIsRead()
    {
        var manifest = Manifest("""{ "environment": "tdd", "database": "db", "tables": [ { "alias": "t_dbo_A", "qualifiedName": "db.t_dbo_A", "name": "A", "rows": "many" } ] }""");

        Assert.Null(manifest.ReadAt);
        Assert.Null(manifest.Source);
        Assert.False(manifest.Truncated);
        Assert.Equal([new DatabaseTable("t_dbo_A", "db.t_dbo_A", string.Empty, "A", null, null)], manifest.Tables);
        Assert.Empty(Manifest("""{ "environment": "tdd", "database": "db" }""").Tables);
    }

    [Theory]
    [InlineData("http://plain.example.net/database.json")]
    [InlineData("database.json")]
    [InlineData("javascript:alert(1)")]
    public void OnlyAnAbsoluteHttpsAddressIsTheLiveFile(string source)
    {
        Assert.Null(Manifest($$"""{ "environment": "tdd", "database": "db", "source": "{{source}}" }""").Source);
    }

    [Fact]
    public void ATruncatedManifestSaysHowManyTablesTheDiagramDraws()
    {
        var manifest = Manifest("""{ "environment": "tdd", "database": "db", "truncated": true, "drawn": 40, "tables": [] }""");

        Assert.Equal("The diagram draws the first 40 tables.", DatabaseText.TruncatedLine(manifest));
        Assert.Null(DatabaseText.TruncatedLine(Manifest()));
        Assert.Equal("The diagram draws the first 2 tables.", DatabaseText.TruncatedLine(Manifest() with { Truncated = true }));
    }

    [Theory]
    [InlineData(null, "The file is empty.")]
    [InlineData("  ", "The file is empty.")]
    [InlineData("[]", "The file must contain a JSON object.")]
    [InlineData("{ \"environment\": ", "The file is not valid JSON (line 1, position 18).")]
    [InlineData("{ \"database\": \"db\" }", "environment: missing or empty.")]
    [InlineData("{ \"environment\": \"tdd\" }", "database: missing or empty.")]
    [InlineData("{ \"environment\": \"tdd\", \"database\": \"db\", \"tables\": 3 }", "tables: not an array.")]
    [InlineData("{ \"environment\": \"tdd\", \"database\": \"db\", \"tables\": [ { \"alias\": \"t_dbo_A\" } ] }", "tables[0]: alias, qualifiedName and name are required.")]
    public void AMalformedManifestIsAShortProblemNeverAnException(string? json, string problem)
    {
        var result = DatabaseParser.ParseManifest(json);

        Assert.False(result.IsValid);
        Assert.Equal([problem], result.Errors);
    }

    [Fact]
    public void TheLiveFileHasEveryTableWithItsColumns()
    {
        var live = Live();

        Assert.Equal(ReadAt.AddHours(1), live.Generated);
        Assert.False(live.Truncated);
        Assert.Equal(["Employee", "WorkOrder"], live.Tables.Select(table => table.Name));
        Assert.Equal(130, live.Tables[0].Rows);
        Assert.Equal(
            [new DatabaseColumn("Id", "uniqueidentifier", false, "pk"), new DatabaseColumn("ManagerId", "uniqueidentifier", true, "pk,fk")],
            live.Tables[0].Columns);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("404: Not Found")]
    [InlineData("[]")]
    [InlineData("{ }")]
    [InlineData("{ \"tables\": { } }")]
    public void ALiveFileThatCannotBeReadIsNoFile(string? json)
    {
        Assert.Null(DatabaseParser.ParseLive(json));
    }

    [Fact]
    public void WhatALiveFileLacksIsLeftOutNotRefused()
    {
        var live = Live("""{ "tables": [ { "name": "A", "rows": -1, "columns": [ { "type": "int" }, { "name": "Id" } ] }, { "schema": "dbo" }, 7 ] }""");

        Assert.Null(live.Generated);
        var table = Assert.Single(live.Tables);
        Assert.Equal(new DatabaseLiveTable(string.Empty, "A", null, table.Columns), table);
        Assert.Equal([new DatabaseColumn("Id", string.Empty, null, string.Empty)], table.Columns);
    }

    [Theory]
    [InlineData(0, "0 rows")]
    [InlineData(1, "1 row")]
    [InlineData(2, "2 rows")]
    [InlineData(999, "999 rows")]
    [InlineData(1234, "1,234 rows")]
    [InlineData(12345678, "12,345,678 rows")]
    public void ARowCountIsWrittenAsTheDiagramWritesIt(long rows, string text)
    {
        var culture = Thread.CurrentThread.CurrentCulture;
        try
        {
            // The diagram's separators are the invariant culture's, whatever the viewer's is.
            Thread.CurrentThread.CurrentCulture = new System.Globalization.CultureInfo("de-DE");
            Assert.Equal(text, DatabaseText.Rows(rows));
        }
        finally
        {
            Thread.CurrentThread.CurrentCulture = culture;
        }
    }

    [Theory]
    [InlineData("dbo", "Employee", "t_dbo_Employee")]
    [InlineData("sales", "Order Line", "t_sales_Order_Line")]
    [InlineData("dbo", "Größe-2", "t_dbo_Gr__e_2")]
    public void ATablesAliasIsItsSchemaAndNameInPlainCharacters(string schema, string name, string alias)
    {
        Assert.Equal(alias, DatabaseText.Alias(schema, name));
    }

    [Fact]
    public void WithoutALiveFileEveryTableKeepsTheCountThatWasDrawn()
    {
        Assert.Equal(
            [new DatabaseCount("db.t_dbo_Employee", "123 rows"), new DatabaseCount("db.t_dbo_WorkOrder", "1,234 rows")],
            DatabaseText.Counts(Manifest(), null));
    }

    [Fact]
    public void TheLiveFilesCountReplacesTheDrawnOne()
    {
        Assert.Equal(
            [new DatabaseCount("db.t_dbo_Employee", "130 rows"), new DatabaseCount("db.t_dbo_WorkOrder", "1 row")],
            DatabaseText.Counts(Manifest(), Live()));
    }

    [Fact]
    public void ATableTheLiveFileDoesNotCountKeepsItsDrawnCountAndOneNobodyCountedIsLeftAlone()
    {
        var manifest = Manifest() with
        {
            Tables = [.. Manifest().Tables, new DatabaseTable("t_dbo_Audit", "db.t_dbo_Audit", "dbo", "Audit", null, 3)],
        };
        var live = new DatabaseLive(null, [new DatabaseLiveTable("dbo", "Employee", null, []), new DatabaseLiveTable("dbo", "Audit", null, [])], false);

        Assert.Equal(
            [new DatabaseCount("db.t_dbo_Employee", "123 rows"), new DatabaseCount("db.t_dbo_WorkOrder", "1,234 rows")],
            DatabaseText.Counts(manifest, live));
    }

    [Fact]
    public void TheSameTablesAreNoChangeOfTheSchema()
    {
        Assert.False(DatabaseText.HasSchemaChanged(Manifest(), null));
        Assert.False(DatabaseText.HasSchemaChanged(Manifest(), Live()));
        Assert.False(DatabaseText.HasSchemaChanged(Manifest(), LiveOf("WorkOrder", "Employee")));
    }

    [Fact]
    public void ATableAddedOrRemovedIsAChangeOfTheSchema()
    {
        Assert.True(DatabaseText.HasSchemaChanged(Manifest(), LiveOf("Employee", "WorkOrder", "Audit")));
        Assert.True(DatabaseText.HasSchemaChanged(Manifest(), LiveOf("Employee")));
        Assert.True(DatabaseText.HasSchemaChanged(Manifest(), LiveOf()));
    }

    [Fact]
    public void ADiagramOfTheFirstTablesLacksTablesByDesign()
    {
        var first = Manifest() with { Truncated = true };

        Assert.False(DatabaseText.HasSchemaChanged(first, LiveOf("Employee", "WorkOrder", "Audit")));
        Assert.True(DatabaseText.HasSchemaChanged(first, LiveOf("Employee", "Audit")));
    }

    [Fact]
    public void TheSchemaIsAsOldAsTheDeploymentUntilANewerLiveFileIsRead()
    {
        var now = ReadAt.AddHours(3);

        Assert.Equal("Schema read 3 h ago (at the environment's last deployment)", DatabaseText.ReadLine(Manifest(), null, now));
        Assert.Equal("Schema read 2 h ago", DatabaseText.ReadLine(Manifest(), Live(), now));

        // A live file from before the deployment, and one without its time, do not replace the deployment's.
        Assert.Equal((ReadAt, false), DatabaseText.ReadAt(Manifest(), Live() with { Generated = ReadAt.AddMinutes(-5) }));
        Assert.Equal((ReadAt, false), DatabaseText.ReadAt(Manifest(), Live() with { Generated = null }));
        Assert.Null(DatabaseText.ReadLine(Manifest() with { ReadAt = null }, null, now));
        Assert.Equal("Schema read 2 h ago", DatabaseText.ReadLine(Manifest() with { ReadAt = null }, Live(), now));
    }

    [Fact]
    public void TheListIsTheLiveFilesWhenItIsThereElseTheDiagrams()
    {
        Assert.Equal(
            [new DatabaseListRow("dbo.Employee", 7, 123), new DatabaseListRow("dbo.WorkOrder", 12, 1234)],
            DatabaseText.List(Manifest(), null));
        Assert.Equal(
            [new DatabaseListRow("dbo.Employee", 2, 130), new DatabaseListRow("dbo.WorkOrder", 1, 1)],
            DatabaseText.List(Manifest(), Live()));
        Assert.Equal("1,234", DatabaseText.Number(1234));
        Assert.Equal("–", DatabaseText.Number(null));
    }

    [Theory]
    [InlineData("", "")]
    [InlineData("pk", "primary key")]
    [InlineData("fk", "foreign key")]
    [InlineData("pk,fk", "primary key, foreign key")]
    [InlineData("unique", "unique")]
    public void AColumnsKeyIsSaidInWords(string key, string words)
    {
        Assert.Equal(words, DatabaseText.Key(key));
    }

    [Fact]
    public void ANullableColumnIsSaidInAWord()
    {
        Assert.Equal(["yes", "no", "–"], new bool?[] { true, false, null }.Select(DatabaseText.Nullable));
    }

    [Fact]
    public void TheLiveFileIsReadWithACheckRoundAndNeverMoreOftenThanOnceAMinute()
    {
        var read = ReadAt;

        Assert.True(DatabaseRefresh.IsDue(active: true, null, read));
        Assert.False(DatabaseRefresh.IsDue(active: true, read, read.AddSeconds(10)));
        Assert.False(DatabaseRefresh.IsDue(active: true, read, read.AddSeconds(59)));
        Assert.True(DatabaseRefresh.IsDue(active: true, read, read.AddSeconds(60)));
        Assert.True(DatabaseRefresh.IsDue(active: true, read, read.AddMinutes(30)));
    }

    [Fact]
    public void APausedOrHiddenPageDoesNotReadTheLiveFile()
    {
        Assert.False(DatabaseRefresh.IsDue(active: false, null, ReadAt));
        Assert.False(DatabaseRefresh.IsDue(active: false, ReadAt, ReadAt.AddHours(1)));
    }

    [Fact]
    public void TheDatabasesBoxOfTheRuntimeDiagramOpensItsTables()
    {
        var manifest = RuntimeManifestParser.ParseManifest(RuntimeManifestParserTests.Sample("uat.json")).Value!;

        var box = DatabaseText.Box(manifest)!;

        Assert.Equal("sqldb", box.Alias);
        Assert.Equal($"Database {box.Name}: open its tables", DatabaseText.BoxLabel(box));
        Assert.Null(DatabaseText.Box(manifest with { Nodes = [.. manifest.Nodes.Where(node => node.Kind != RuntimeNodeKind.Sql)] }));
    }

    private static RuntimeLoader Loader(StubHandler handler) =>
        new(new HttpClient(handler) { BaseAddress = new Uri("https://dashboard.example.net/") });

    [Fact]
    public async Task TheViewsFilesAreLoadedFromRuntime()
    {
        var handler = new StubHandler(request => request.RequestUri!.AbsolutePath switch
        {
            "/runtime/tdd.database.json" => StubHandler.Answer(HttpStatusCode.OK, ManifestJson),
            "/runtime/tdd.database.svg" => StubHandler.Answer(HttpStatusCode.OK, "<svg xmlns=\"http://www.w3.org/2000/svg\"></svg>"),
            _ => StubHandler.Answer(HttpStatusCode.NotFound),
        });

        var diagram = await Loader(handler).LoadDatabaseAsync(new RuntimeDatabaseFiles("tdd.database.svg", "tdd.database.json"), CancellationToken.None);

        Assert.True(diagram.IsValid);
        Assert.Equal("sqldb-cmdemo1-tdd", diagram.Manifest!.Database);
        Assert.Empty(diagram.Errors);
    }

    [Fact]
    public async Task FilesThatCannotBeReadAreReportedWithTheirReasons()
    {
        var handler = new StubHandler(request => request.RequestUri!.AbsolutePath.EndsWith(".json", StringComparison.Ordinal)
            ? StubHandler.Answer(HttpStatusCode.OK, "{ }")
            : StubHandler.Answer(HttpStatusCode.OK, "<!DOCTYPE html><html></html>"));

        var diagram = await Loader(handler).LoadDatabaseAsync(new RuntimeDatabaseFiles("tdd.database.svg", "tdd.database.json"), CancellationToken.None);

        Assert.False(diagram.IsValid);
        Assert.Equal(
            [
                "runtime/tdd.database.json: environment: missing or empty.",
                "runtime/tdd.database.json: database: missing or empty.",
                "runtime/tdd.database.svg: not an SVG.",
            ],
            diagram.Errors);
    }

    [Fact]
    public async Task TheLiveFileIsReadFromItsOwnAddress()
    {
        var source = new Uri("https://raw.githubusercontent.com/org/repo/main/environments/tdd/database.json");
        var handler = new StubHandler(_ => StubHandler.Answer(HttpStatusCode.OK, LiveJson));

        var live = await Loader(handler).ReadLiveDatabaseAsync(source, CancellationToken.None);

        Assert.Equal(130, live!.Tables[0].Rows);
        Assert.Equal([source.AbsoluteUri], handler.RequestedUrls);
    }

    [Fact]
    public async Task ALiveFileThatIsAbsentRefusedOrBrokenIsNoneAndNoError()
    {
        var source = new Uri("https://raw.githubusercontent.com/org/repo/main/environments/tdd/database.json");

        Assert.Null(await Loader(new StubHandler(_ => StubHandler.Answer(HttpStatusCode.NotFound, "404: Not Found"))).ReadLiveDatabaseAsync(source, CancellationToken.None));
        Assert.Null(await Loader(new StubHandler(_ => throw new HttpRequestException("CORS"))).ReadLiveDatabaseAsync(source, CancellationToken.None));
        Assert.Null(await Loader(new StubHandler(_ => StubHandler.Answer(HttpStatusCode.OK, "<html>"))).ReadLiveDatabaseAsync(source, CancellationToken.None));
    }
}
