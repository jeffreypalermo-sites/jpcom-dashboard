# Health dashboard

A static web page that shows the health of every node of a multi-region system. It is a Blazor WebAssembly
application (.NET 10, standalone): the browser itself calls the health endpoint of every node, so the dashboard needs
no server of its own and shows what a client on the internet sees.

The page has two views of the same checks, switched by the tab bar under the header: **Health** (the default, the
tiles below) and **Runtime** (the C4 deployment diagram of one environment, updated live; see "The runtime view"). The
address keeps the choice: `#runtime` opens the runtime view, `#runtime/uat` the diagram of uat, so a link opens it.
The header's controls (pause, interval, probe, check now) and its summary apply to both views: one set of checks, two
renderings.

A system that runs in a Kubernetes cluster has a third view, **Cluster** (`#cluster`): the AKS service as Azure
reports it next to the cluster's own nodes, namespaces and pods (see "The cluster view"). The tab is there only when
`topology.json` has `cluster`; without it the page is as it was: no tab, no request, no word about a cluster.

For each environment (tdd, uat, prod) and each deployable in it, the health view shows:

- one tile for the Azure Front Door endpoint (the public address) and one tile per regional node (web app), with its
  region and role (primary or standby);
- per tile: the state, the HTTP status, the latency, the app version, the time of the last check and a strip with the
  last 30 checks; for a web app that reports them, its calls of the last minute and its process's vitals (CPU, memory,
  requests in flight, exceptions, uptime), with a small trend line next to the requests and the CPU; and for a web
  app that answers a detailed health check, one mark per thing it checks (its database, a gateway it calls), so an
  unhealthy tile says which of them failed (see "Health checks, entry by entry");
- per deployable, a "Code" card (the build its primary node runs: commit, lines of code by language, tests, coverage,
  complexity, CRAP, Qodana) and a "Delivery" card (deployed when, signed off by whom, lead time, how far behind the
  first environment), each only when its source answers; and after the environments the "Code" card of the dashboard
  itself, the build that serves the page (see "The dashboard's own build");
- under each environment's name, what it cost in Azure (the last complete day, seven days, the month so far, and the
  services that cost most), after the environments the same for what they share, and in the header for the whole
  system: a day old, and said so (see "Cost");
- under each environment's name, on the tiles of the deployable and on its nodes in the runtime diagram, a mark for
  every deployment that is queued, running, waiting for a sign-off or just ended, whatever the environment: a dot
  and a sentence, minutes old, and said so (see "Deployments in flight");
- under each environment's name, in how many of the pipeline's hourly health reports it was healthy, in 24 hours and
  in seven days, and when one last failed: hourly checks, not continuous monitoring, and said so (see "Availability");
- under every view, "What just happened": the last 50 events this page observed (state changes, entries of a
  health check that changed, restarts, deployments, failovers, pins, traffic, and for a system with a cluster what
  changed in it);
- where the topology has a link for it, every number and name leads to its place in the Azure portal or in Octopus
  Deploy;
- which region is expected to serve the traffic, a "Failed over to <region>" banner when the primary is not healthy
  but a standby is, and whether the Front Door endpoint agrees;
- the version the last deployment pinned in Git next to the versions the nodes run ("Pinned 2.4.7. In sync: all 2
  nodes run 2.4.7." or "Differs: eastus2 runs 2.4.6."), with a link to the Octopus Deploy project that deploys the
  deployable and to the history of the pins on GitHub;
- in the header: the overall summary ("All 7 nodes healthy", "2 of 7 nodes not healthy"), the time of the last
  refresh, pause and resume, the interval (10 s, 30 s, 60 s) and the probe (health check or liveness), and a second
  line only while versions differ somewhere ("Versions differ in 1 environment").

The browser holds no secret: it reads public addresses only (the nodes' health, detailed health, version, telemetry
and build endpoints and public files on GitHub), and the links to the Azure portal, Octopus Deploy and GitHub are
plain links that ask the viewer to sign in there.

Everything beyond the health checks is optional. A source that is absent or cannot be read (an older app without the
endpoint, a file that is not published yet, a topology without the field) is never an error: its element is not
shown, or shows a dash.

## Run it locally

```
dotnet run --project src/Dashboard
```

Then open http://localhost:5210 (http://localhost:5210/#runtime for the runtime view). The repository ships a sample
`src/Dashboard/wwwroot/topology.json` and, rendered from it, a sample `src/Dashboard/wwwroot/runtime/` (tdd and uat);
their hosts do not exist, so every tile and every web app of the diagram shows Unreachable and no pinned version is
found. Point the sample at real nodes and at a real
system repository to see them (and allow `http://localhost:5210` in the nodes' CORS settings, see below). The sample
names no cluster, so it has no Cluster tab: a topology takes absolute addresses only, and the sample would have none
to give for the cluster's two files. To see the view, add a `cluster` to the sample with the addresses of a real
cluster's files, or of two files a local server serves (the tests' copies are `src/Dashboard.Tests/Samples/`).

```
dotnet build -c Release     # warnings are errors
dotnet test -c Release      # the unit tests of the health logic
dotnet publish src/Dashboard -c Release -o publish    # the site is publish/wwwroot
```

The SDK version is pinned in `global.json` (10.0.100, rolling forward to the latest 10.0 feature band).

## The topology: `topology.json`

The dashboard loads `topology.json` from its own address (next to `index.html`) when it starts and when "Reload
topology" is pressed. The deployment writes the real file; the build does not know the system.

```json
{
  "system": { "slug": "cmdemo2", "name": "CM demo 2 multi-region", "repository": "https://github.com/example-org/cmdemo2-system",
              "deliveryUrl": "https://raw.githubusercontent.com/example-org/cmdemo2-system/status/delivery.json",
              "costUrl": "https://raw.githubusercontent.com/example-org/cmdemo2-system/status/cost.json",
              "deploymentsUrl": "https://raw.githubusercontent.com/example-org/cmdemo2-system/deployments/deployments.json",
              "dashboard": { "name": "dashboard", "buildPath": "/build-facts.json" } },
  "generated": "2026-10-04T22:00:00Z",
  "environments": [
    {
      "name": "uat", "tier": "nonprod",
      "versionsUrl": "https://raw.githubusercontent.com/example-org/cmdemo2-system/main/environments/uat/versions.json",
      "versionsHistoryUrl": "https://github.com/example-org/cmdemo2-system/commits/main/environments/uat/versions.json",
      "links": { "applicationInsights": "https://portal.azure.com/#@<tenant>/resource/<id of appi-cmdemo2-uat>/overview",
                 "applicationMap": "...", "database": "...", "resourceGroup": "..." },
      "deployables": [
        {
          "name": "ui",
          "projectUrl": "https://example.octopus.app/app#/Spaces-1/projects/cmdemo2-ui",
          "frontDoor": "https://cmdemo2-uat-def456.z01.azurefd.net",
          "healthPath": "/_healthcheck", "alivePath": "/alive", "versionPath": "/_version",
          "telemetryPath": "/_telemetry", "trafficPaths": [ "/" ], "buildPath": "/_build",
          "healthDetailPath": "/_healthcheck/detailed",
          "links": { "frontDoor": "...", "logs": "..." },
          "nodes": [
            { "name": "app-cmdemo2-uat-ui", "region": "westus3", "role": "primary", "url": "https://app-cmdemo2-uat-ui.azurewebsites.net",
              "links": { "portal": "...", "liveMetrics": "...", "performance": "...", "failures": "...", "dependencies": "..." } },
            { "name": "app-cmdemo2-uat-ui-eastus2", "region": "eastus2", "role": "standby", "url": "https://app-cmdemo2-uat-ui-eastus2.azurewebsites.net" }
          ]
        }
      ]
    }
  ]
}
```

| Field | Required | When it is missing |
|---|---|---|
| `system.slug`, `system.name` | no | The name falls back to the slug, then to "System". |
| `system.repository` | no, may be `null` | The footer names the system without a link to its repository. |
| `system.deliveryUrl` | no, may be `null` | No "Delivery" cards and no last failover test: nothing is read (see "Delivery"). |
| `system.costUrl` | no, may be `null` | No cost anywhere on the page: nothing is read (see "Cost"). |
| `system.deploymentsUrl` | no, may be `null` | No mark of a deployment anywhere on the page: nothing is read, and the page is as it was before the key existed (see "Deployments in flight"). |
| `system.dashboard` | no, may be `null`; an object | No "Code" card of the dashboard itself: nothing is read from the page's own address but the topology and `runtime/` (see "The dashboard's own build"). Anything but an object is read as absent. |
| `system.dashboard.buildPath` | no, may be `null` | The same: without the path there is no `system.dashboard`. A path of the page's own site, read from its root whatever it starts with (`/build-facts.json`). |
| `system.dashboard.name` | no | `dashboard`. The name the card and its heading show. |
| `generated` | no | The footer does not show when the topology was generated. |
| `environments` | yes, an array | Error. |
| `environments[].name` | yes | Error. |
| `environments[].tier` | no | No tier label. |
| `environments[].versionsUrl` | no, may be `null` | No pinned versions for this environment: nothing is read and nothing is compared (but for a deployable with `pinUrl`). |
| `environments[].versionsHistoryUrl` | no, may be `null` | No "Pin history" link (but for a deployable with `pinHistoryUrl`). |
| `environments[].links` | no | No links to the environment's resources (see "Links"). Keys: `applicationInsights`, `applicationMap`, `database`, `resourceGroup`. |
| `environments[].namespace` | no, may be `null` | The cluster view has no group for this environment: the pods of its namespace are listed with the platform's. |
| `environments[].deployables` | no | The environment is shown without tiles. |
| `deployables[].name` | no | `app`. It is also the deployable's key in `versions.json`. |
| `deployables[].projectUrl` | no, may be `null` | No "Octopus project" link. |
| `deployables[].pinUrl` | no, may be `null` | The deployable's pinned version is its entry in the environment's `versions.json` (`versionsUrl`). With it, the pinned version is read from this address instead: a Kustomize file (see "Pinned versions"). |
| `deployables[].pinHistoryUrl` | no, may be `null` | "Pin history" leads to `versionsHistoryUrl`, where the environment has one. |
| `deployables[].frontDoor` | no, may be `null` | No Front Door tile, and no "Front Door" row on the nodes' tiles. With exactly one node, the deployable is shown as a single node that serves (see "How the states are decided"). |
| `deployables[].healthPath` | no | `/_healthcheck`. |
| `deployables[].alivePath` | no | `/alive`. |
| `deployables[].versionPath` | no | `/_version`. |
| `deployables[].telemetryPath` | no, may be `null` | No calls per minute: the arrows show "–"; no process vitals and no trend lines. The nodes' own counts of the last minute (`/_telemetry`, see "Calls per minute"). |
| `deployables[].trafficPaths` | no, may be `null` | The traffic button calls `/` only. An empty list says the deployable takes no generated traffic (a dashboard listed as a node). |
| `deployables[].buildPath` | no, may be `null` | No "Code" card: nothing is read (see "Code"). |
| `deployables[].healthDetailPath` | no, may be `null` | No marks of the health check's entries on the tiles, and a dependency in the runtime diagram is "Not known": nothing is read (see "Health checks, entry by entry"). |
| `deployables[].links` | no | The Front Door tile's name is no link, and there is no "Requests in Logs" link. Keys: `frontDoor`, `logs`. |
| `deployables[].nodes` | no | No node tiles. |
| `nodes[].url` | yes, an absolute http(s) address | Error. |
| `nodes[].name` | no | The host of `url`. |
| `nodes[].region` | no | The tile is titled with the node's name. |
| `nodes[].role` | no | `primary` for the first node of the deployable, `standby` for the others. |
| `nodes[].links` | no | The node's numbers and its name are plain text. Keys: `portal`, `liveMetrics`, `performance`, `failures`, `dependencies`. |
| `cluster` | no, may be `null`; an object | No cluster view: no tab, and nothing is read (see "The cluster view"). |
| `cluster.name` | no | The view is titled "Cluster", and the links' titles do not name the cluster. |
| `cluster.statusUrl` | no, may be `null` | No "Cluster" card, no nodes and no pods: the cluster's own status is not read. |
| `cluster.serviceUrl` | no, may be `null` | No "AKS service" card: Azure's facts are not read, and an unreachable cluster is not compared with them. |
| `cluster.links` | no | No link to the cluster in the Azure portal. Keys: `portal`, `workloads`. |

An address that is present (`system.repository`, `system.deliveryUrl`, `system.costUrl`, `system.deploymentsUrl`,
`versionsUrl`, `versionsHistoryUrl`, `projectUrl`, `pinUrl`, `pinHistoryUrl`, `frontDoor`, `nodes[].url`, `cluster.statusUrl`, `cluster.serviceUrl`) must be
an absolute http(s) address: anything else is an error. A topology without `repository`, `versionsUrl`, `versionsHistoryUrl`, `projectUrl`, `pinUrl` and
`pinHistoryUrl` is shown as before these fields existed: no line about versions, no request to GitHub. A `links`
object is more forgiving, because a link is a courtesy: an entry whose value is not an absolute http(s) address is
left out (so is a `links` that is not an object), an unknown key is ignored, and the number it would have belonged to
stays plain text. The two links of `cluster.links` the page knows (`portal`, `workloads`) are held to the rule of the
addresses instead: one that is present and not an absolute http(s) address is an error
(`cluster.links.portal: not an absolute http or https address.`); any other key of `cluster.links` is ignored.

Unknown fields are ignored. A file that is missing, is not JSON or breaks a rule above is not shown in part: the
dashboard shows "The topology could not be read" with every reason and its place in the file (for example
`environments[1].deployables[0].nodes[0].url: missing or not an absolute http or https address.`).

The version endpoint (`versionPath`) answers JSON such as `{"version":"2.4.21+0a1b2c3"}`; the tile shows the part
before `+`. A node that stops answering keeps the last version it reported.

### A dashboard as a node

A system can have this page in more than one home (in its cluster, and outside it on a static-site platform that
answers while the cluster does not). The topology may then list each dashboard as a deployable with one node: its
address, `healthPath` and `alivePath` `/`, `versionPath` `/version.json` (the Build writes that file next to
`index.html`), and an empty `trafficPaths`, so the traffic button leaves it alone. Each dashboard then checks the
others like any node, with their version and pin; in the runtime view the site that serves the page you are looking
at says "This page", and every other one shows its state, with the arrow from the browser carrying it. The topology
may also name `buildPath` `/build-facts.json` for a dashboard: the Build writes that file next to `index.html` too,
and the dashboard then has a "Code" card like an app (see "Code"). The checked site must let other origins read `/`,
`/version.json` and `/build-facts.json`.

### An application that brings its own runtime

A deployable does not have to be an App Service app. Where the system creates nothing for an application (hosting
`own` in the demo-environment kit), the application itself reports what it runs on, and the deployment writes that
into the deployable's entry: `nodes` as reported (each with its `url`; `name`, `region` and `role` where the
application named them), `frontDoor` when it has a public address of its own in front of them, and `healthPath`,
`alivePath` and `versionPath` where it named them. A path it did not name is left out, and the page asks its default
(`/_healthcheck`, `/alive`, `/_version`). `projectUrl`, `telemetryPath`, `trafficPaths`, `buildPath` and
`healthDetailPath` come from the system as for every deployable. There are no `links`: the deployment does not know
the application's resources, so its numbers and names are plain text.

The page treats such an entry like any other. Three things follow from what it is:

- Several nodes may have the role `primary` (an application that serves from more than one region at once). The page
  still names one node that is expected to serve, the first healthy primary in the order of the file, and shows the
  others as ready ("How the states are decided").
- The node answers the page from another origin by its own means ("CORS").
- Calls per minute, process vitals, the Code card and the marks of the health check show only when the application
  serves them at the paths the topology names, in the formats of this page.

## Pinned versions: what Git says next to what runs

Octopus Deploy deploys every deployable. The first step of a deployment commits the version it deploys to the system
repository: `environments/<environment>/versions.json` on `main`, a JSON object with one entry per deployable.

```json
{ "dashboard": "1.0.1", "ui": "2.4.7" }
```

That entry is the **pinned** version: what Git says the environment runs. When a later step of the deployment fails,
the deployment puts the previous version back, so the file names the last version that deployed.

The dashboard reads the file from `versionsUrl` and shows, in one line under the banner of each deployable, the
pinned version next to what the nodes report at `versionPath`:

| Line | Meaning |
|---|---|
| Pinned 2.4.7. In sync: all 2 nodes run 2.4.7. | Every node whose version is known runs the pinned version. |
| Pinned 2.4.7. Differs: eastus2 runs 2.4.6. | A node runs another version; each such node is named with its version. A deployment is in flight or failed part-way, a node was rolled back or deployed by hand, or the file is ahead of the copy GitHub serves (see below). |
| Pinned 2.4.7. Not compared: westus3 is unreachable. | The pinned version is known, and no node's version is. |
| No pinned version. | The file has no entry for the deployable, or was not found (HTTP 404): nothing was deployed yet. A repository that is not public answers HTTP 404 too. |
| Pinned version not known. | The file could not be read: no answer, another HTTP status or not a JSON object. The reason is shown. |
| Reading the pinned version | Before the first answer. |

- Only the nodes are compared, not the Front Door endpoint: it answers with the version of whichever node served the
  request.
- Versions are compared as the tiles show them: without build metadata (`2.4.7+0a1b2c3` is `2.4.7`), and without
  regard to case.
- A node whose version is not known (unreachable, not checked yet, no answer from its version endpoint) is left out
  of the comparison and named ("eastus2 is unreachable"). It never makes the versions differ, although its tile
  still shows the last version it reported.
- A node that answers its probe with an error still runs a version, and that version is compared.
- "Differs" is a warning, never an error of the dashboard: the state has its own sign (≠, against = for "in sync" and
  dots for "not known") and its words, and the header gets a second line, "Versions differ in 1 environment". The
  summary above it stays about health.

The file is read once per round of checks and per environment (not per node), at the same time as the nodes and with
the same rules: a `GET` with `cache: no-store` and no header of its own (so the browser sends no CORS preflight), and
the same timeout. `raw.githubusercontent.com` allows every origin. A file that cannot be read changes no tile and
fails no check; the next round reads it again, and a failed reading replaces a good one (the dashboard does not
compare with a pinned version it can no longer read).

`raw.githubusercontent.com` may serve a copy that is a few minutes old (it caches for about five minutes, whatever
the browser asks for). Right after a deployment the nodes can therefore already run the new version while the
dashboard still reads the old pin, and shows "Differs" until GitHub serves the new file.

The links open in a new tab: "Octopus project" (`projectUrl`: the releases and deployments of the deployable) and
"Pin history" (`versionsHistoryUrl`: one commit per pin, each naming the deployment). Octopus Deploy asks the viewer
to sign in; the dashboard itself never calls Octopus Deploy.

### A pin in a kustomization: `pinUrl`

Where Argo CD deploys from Git (one Kubernetes cluster, every app one node), the version pinned in Git is not an
entry of `versions.json`: it is the image tag in the deployable's Kustomize file, one file per deployable and
environment, for example `gitops/environments/uat/ui/kustomization.yaml`:

```yaml
images:
  - name: example.azurecr.io/cmdemo3/ui
    newTag: "2.4.15"
```

A deployable with `pinUrl` reads its pinned version from that address instead of from `versionsUrl`: the value of the
file's first `newTag:` entry, in double or single quotes or without, a trailing comment left out (the page reads the
lines of a file in block style; it does not parse YAML). The line under the banner and its states are the same, with
that file's name in the reason:

| The address answers | Line |
|---|---|
| A file with a `newTag` | Pinned 2.4.15, compared with the nodes as above. |
| HTTP 404 | No pinned version. |
| A file without `newTag`, or with an empty one | Pinned version not known. The reason is shown ("kustomization.yaml could not be read: the file has no newTag entry."). |
| No answer, or another HTTP status | Pinned version not known. The reason is shown. |

The file is read by the rules of `versions.json` (a `GET` with `cache: no-store` and no header of its own, the same
timeout, a failed reading replaces a good one), once per round of checks and per deployable. "Pin history" leads to
`pinHistoryUrl` when the deployable has one, otherwise to `versionsHistoryUrl`. A deployable without `pinUrl` is read
from `versions.json` as before, also next to one with it; what `versions.json` says about a deployable with `pinUrl` is
not its pin and is ignored.

## The runtime view

The runtime view shows one environment as a C4 deployment diagram: the Azure subscription, the resource groups (the
tier's, and the Front Door's), the regions (primary, standby, and the region of the database and of the static
sites), the App Service plans with their size, the web apps, the Front Door endpoint, the Azure SQL database, the
dashboard's Static Web App and the browser, with the relationships between them. One button per environment selects
the diagram, which opens fitted to the page (down to 70 % of its size, where the tiles' words are 8 px high; below
that, and at its actual size, it scrolls sideways inside its own frame). The button at the right names what a press
does: "Actual size" while the diagram is fitted, "Fit to width" while it is not. The diagram is a light sheet in the
dark theme too. A system whose deployable declares what it depends on also has, outside the subscription, one box
per dependency, with an arrow from each of the deployable's web apps. The database is drawn in an environment that
has one. An application that brings its own runtime ("An application that brings its own runtime", above) is drawn
outside the subscription too, in a boundary of its own, because the deployment does not know where it runs: its
public address when it has one, and its nodes in one frame per region they name, each with the tile of a web app and
no plan around it. The browser's arrow goes to the public address and from there to every node, or to each node
where there is no public address.

The widths of the diagram are set by the deployment (the slots below, and the names of the resources): an
environment with a standby region is the browser, three columns of boxes and three columns of number lines wide,
about 1740 px for cmdemo2. It fits the page without scrolling sideways in a window from about 1270 px on.

**Drawn when the dashboard was deployed** (static): the resources, their names, sizes, regions and relationships. The
deployment renders the diagram with PlantUML from `system.json` and the topology, so a change to the environments, a
standby region, a plan's size or the Front Door endpoint shows after the dashboard is deployed again (as in
`topology.json`).

**Live, from the checks of this page** (every round, and when another environment is selected), drawn into the
diagram in place:

| Element | What it shows |
|---|---|
| Web app, Front Door endpoint | The box's colour and border by state (Healthy, Unhealthy, Unreachable, Checking), and a tile: the state's badge with its word, HTTP status and latency, the version, the last 30 checks. A web app also shows its version next to the pinned one ("pinned 2.4.21: in sync", "differs from pinned 2.4.21"), its own numbers where it reports them ("12 req/min · p95 85 ms" with the trend of the requests, "0 errors · 0 exceptions/min", "CPU 3.2 % · 412 MB · 2 in flight" with the trend of the CPU, "up 2 h" or "restarted 3 min ago") and its role ("primary: serves traffic", "standby: ready, no traffic"; the only node of a deployable without a Front Door endpoint has no role: "serves traffic", "not serving"), and last, where it answers a detailed health check, one small mark per entry with the summary in words ("8 checks healthy", "LlmGateway degraded", "2 of 8 checks not healthy"; see "Health checks, entry by entry"); the endpoint shows where it routes ("routes to eastus2 (failed over)") and whether it agrees with the web apps. |
| Region of web apps | A mark with words: "serving traffic" (green frame), "standby: ready", "not serving" (red frame), by the same serving decision as the health view's banner. A region of the database and the static sites has a note in slate, in italics ("database: reachable; static sites: not probed"). |
| Front Door to an origin | Solid and green while it carries the traffic, dotted grey while idle, dashed red when the origin is not healthy: a failover is the green line moving from priority 1 to priority 2. |
| Web app to the database | Green from the web app that serves, dotted from the others. |
| Number line of a relationship | Calls per minute of the last minute, in a solid frame, as the web apps count them: browser to Front Door (the sum of its origins' requests from Front Door), Front Door to an origin (its requests from Front Door; Front Door's health probes next to the role), web app to database (the SQL commands of its requests, with the background ones next to the role, "app queries · 55 background"; all SQL commands for an app that does not tell them apart), web app to dependency (the web app's outgoing HTTP calls, `http.perMinute`, when the web app has exactly one dependency; a dash, "not counted apart", when it has more, because the app counts them as one number). Next to the number, its trend over the last checks of this page. A dashed frame with "–" where no web app reports a number (no `telemetryPath`, or an app without the endpoint). |
| Database | The browser cannot ask the database, but a web app's health check connects to it: "Reachable" (healthy) when the health check of a web app that uses it passes, with the queries per minute its web apps' traffic causes (the background ones are in the tooltip); "Not confirmed" (neutral) when none passes, since the web app may be the cause; "Not probed" (neutral) with the Liveness probe, which leaves the database alone. |
| Dependency (a box outside the subscription) | The browser does not call it; the entry of the web apps' detailed health check that the system names for it says its state. "Reachable" (healthy) when that entry is Healthy on a web app that passes its health check, with the entry's own words as the line ("Chat client is not configured"), or "westus3 reports it degraded" when another web app says otherwise; "Degraded" or "Unhealthy" (drawn as unhealthy) with the entry's words when no web app reaches it; and neutral with the reason otherwise: "Not known" (no web app answers the detailed health check, or it has no entry of that name), "Not confirmed" (the entry is healthy only on a web app that fails its own health check), "Not probed" (the system names no entry for it, or the probe is Liveness, which reads no entries). |
| Web app to a dependency | Green from the web app that serves, dotted from the others, like the line to the database. |
| Links | Where the topology has a link (see "Links"): a web app's badge (Live Metrics), its name (the web app in the portal), its version (the release in Octopus Deploy, or else the commit of its build), its requests (Performance) and its errors (Failures); the Front Door endpoint's and the database's name; the numbers on the arrows (Performance, the dependency calls, the requests in Logs). They are real `a` elements: underlined, in the order of the keyboard, each with a title that says where it goes. |
| Static site | Neutral, "Not probed": the dashboard does not check itself. The static site that serves the page says "This page". |
| Mark of a deployment | With `system.deploymentsUrl`: a small dot in the corner of the tile of every node whose deployable is being deployed to the shown environment (its web apps, its Front Door endpoint, the dashboard's static site), on a white disc so it reads on a box of any state. Its shape says the state, its tooltip the sentence, and it is a link to the task in Octopus Deploy where the file gives its address (see "Deployments in flight"). The node keeps its state: a deployment is no state of health. |

A state is never colour alone: the badge has an icon and a word, the regions a word, the lines differ in dash and
width. Hover a node or a line for its details.

Above the diagram, under the title: what is being deployed to the shown environment, one line per deployment, and a
dot on the button of every environment something is being deployed to (see "Deployments in flight").

Under the diagram, for the environment it shows: the links to its resources in the Azure portal, what the pipeline's
hourly health reports found (see "Availability"), what it cost and what the resources it shares with the others cost
(see "Cost"), per deployable the "Code" and "Delivery" cards of
the health view (see "Code" and "Delivery"), and last the "Code" card of the dashboard itself (see "The dashboard's
own build"); then the legend.

A deployment from before the runtime view has no `runtime/`: the tab then says that the diagram is not available for
this deployment, and the health view works as before.

### The files: `runtime/`

The deployment (`deploy-staticwebapp.ps1` of the system repository) writes, next to `topology.json`:

| File | Content |
|---|---|
| `runtime/index.json` | `{ "generated": "...", "plantuml": "1.2026.8", "environments": [ { "name": "uat", "manifest": "uat.json", "svg": "uat.svg" } ] }`, in the order of `system.json`. A file name is a plain name in `runtime/`. |
| `runtime/<env>.svg` | The diagram, rendered by PlantUML (the pinned version, layout engine smetana). |
| `runtime/<env>.json` | The manifest: which drawn element is which (below). |
| `runtime/<env>.puml` | The PlantUML source, for reading; the page does not load it. |

The manifest maps each element's alias to what the browser knows, so the page never reads names out of the SVG:

```json
{
  "environment": "uat",
  "svg": "uat.svg",
  "nodes": [
    { "alias": "browser", "qualifiedName": "browser", "kind": "person", "name": "Browser" },
    { "alias": "fd_ui", "qualifiedName": "sub.rg_edge.afd.fd_ui", "kind": "frontdoor", "deployable": "ui",
      "name": "cmdemo2-uat-ui", "url": "https://cmdemo2-uat-def456.z01.azurefd.net" },
    { "alias": "app_ui_primary", "qualifiedName": "sub.rg_tier.region_primary.plan_primary.app_ui_primary",
      "kind": "webapp", "deployable": "ui", "name": "app-cmdemo2-uat-ui", "role": "primary", "region": "westus3",
      "regionAlias": "region_primary", "url": "https://app-cmdemo2-uat-ui.azurewebsites.net" },
    { "alias": "sqldb", "qualifiedName": "sub.rg_tier.region_data.sqldb", "kind": "sql", "name": "sqldb-cmdemo2-uat",
      "region": "centralus", "regionAlias": "region_data", "url": null },
    { "alias": "swa_dashboard", "qualifiedName": "sub.rg_tier.region_data.swa_dashboard", "kind": "staticsite",
      "deployable": "dashboard", "name": "swa-cmdemo2-uat-dashboard", "region": "centralus",
      "regionAlias": "region_data", "url": null },
    { "alias": "dep_ui_LLM_gateway", "qualifiedName": "dep_ui_LLM_gateway", "kind": "dependency", "deployable": "ui",
      "name": "LLM gateway", "healthCheck": "LlmGateway", "dependencyKind": "external", "url": null }
  ],
  "regions": [ { "alias": "region_primary", "qualifiedName": "sub.rg_tier.region_primary", "name": "westus3", "roles": [ "primary" ] } ],
  "edges": [
    { "id": "fd_ui-to-app_ui_primary", "from": "fd_ui", "to": "app_ui_primary", "kind": "origin", "priority": 1 },
    { "id": "app_ui_primary-to-sqldb", "from": "app_ui_primary", "to": "sqldb", "kind": "sql" },
    { "id": "dep_ui_LLM_gateway-to-app_ui_primary", "from": "app_ui_primary", "to": "dep_ui_LLM_gateway", "kind": "dependency" }
  ],
  "generated": "2026-10-04T22:00:00Z",
  "plantuml": "1.2026.8"
}
```

- **Aliases** (`<d>` is the deployable's name with every character but a letter or a digit as `_`): `browser`; the
  boundaries `sub`, `rg_edge`, `rg_tier`, `afd`, `region_primary`, `region_standby`, `region_data`, `region_static`
  (a region with two roles is one boundary, named after its first role), `plan_primary`, `plan_standby`; the nodes
  `fd_<d>`, `app_<d>_primary`, `app_<d>_standby`, `sqldb`, `swa_<d>`, and `dep_<d>_<n>` for a dependency (`<n>` is
  its name, written as `<d>` is). A relationship's id is `<from>-to-<to>`: the name the SVG gives the drawn link.
  A web app's relationship to a dependency is drawn from the dependency's side (which puts its box under or above
  the subscription and its number line into a free column), so its id is `dep_<d>_<n>-to-app_<d>_<role>`, while
  its `from` is the web app and its `to` the dependency.
  An application that brings its own runtime: the boundary `own_<d>`; in it `fd_<d>` (its public address, kind
  `frontdoor`), the frames `region_own_<d>_<n>` (one per region its nodes name, numbered in the order of the
  topology; nodes without a region share one, named "region not reported") and the nodes `app_<d>_<n>` (kind
  `webapp`, numbered in the order of the topology). Its relationships are `browser-to-fd_<d>` and
  `fd_<d>-to-app_<d>_<n>` (kind `origin`, `priority` 1 for a node with the role `primary` and 2 for any other,
  since the deployment does not know how the application's public address routes), or `browser-to-app_<d>_<n>`
  without a public address. It has no relationship to the database.
- **Kinds**: nodes `person`, `frontdoor`, `webapp`, `sql`, `staticsite`, `dependency`; relationships `public` (the
  browser to a public address), `origin` (with its `priority`), `sql`, `dashboard`, `dependency`. An unknown kind is
  drawn and not updated.
- **A dependency** (`kind` `dependency`) also has `healthCheck`, the name of the entry of its web apps' detailed
  health check that tells its state (`null` when the system names none), and `dependencyKind`, what it is in the
  system's own words (`external`). It comes from `deployables[].dependencies` of `system.json`, a list of
  `{ "name": "LLM gateway", "healthCheck": "LlmGateway", "kind": "external" }`; a system without it has no such node
  and no such relationship, and its diagram is as before.
- **Addresses**: `url` is the address the page checks (web app, Front Door endpoint: the same as in `topology.json`,
  which is how a node finds its checks) or, for a static site, the dashboard's address where the deployment knows it
  (its own environment's); `null` for the database, which the browser cannot probe, for a Front Door endpoint that
  is not deployed yet, and for another environment's dashboard.
- A node whose address `topology.json` does not have is drawn neutral, "Not checked: not in topology.json".

The sample `runtime/` was made by the same functions as a deployment (`ConvertTo-Topology` and `Write-RuntimeDiagram`
of `deploy-staticwebapp.ps1`, with PlantUML 1.2026.8) from a `system.json` whose topology is the sample's: cmdemo2
with tdd and uat (both with capability "telemetry"), uat with a standby in eastus2, Front Door, plans B1, the database
in centralus, `telemetryPath`, `buildPath` and `trafficPaths` for ui, a subscription and tenant of zeros for the links,
and the dashboard at http://localhost:5210 in tdd. Render it again when the diagram or the sample topology changes; the tests read it.

**What the page relies on in the SVG.** PlantUML writes these attributes, and they are not a documented contract (they
changed in PlantUML 1.2026.3 and 1.2026.4), so the deployment pins one version and checks every render for them; a
missing one fails the deployment:

| Element | In the SVG |
|---|---|
| Node | `<g class="entity" data-qualified-name="<alias path through the boundaries>">`; its `<rect>` (a database: its two `<path>`) is the box; its `<image>` is the slot. |
| Region | `<g class="cluster" data-qualified-name="...">`; its first `<rect>` is the frame; its `<image>` is the slot. |
| Relationship | `<g class="link" data-entity-1="<id of the from node's g>" data-entity-2="<id of the to node's g>">`, with its `<path>`, `<polygon>` (the head) and `<image>` (the slot). PlantUML's own layout engine (smetana, which the deployment uses: the worker has no Graphviz) gives the `<path>` no id; a Graphviz layout names it `<from>-to-<to>`, and the page reads that too. |

**Slots.** Text in PlantUML's SVG has a fixed `textLength`, and the layout depends on the length of every text, so
live values cannot be written into the rendered text. Instead every node, every region of web apps and every Front
Door and database relationship carries a transparent image of a fixed size in its description: PlantUML lays it out
like any image, the page hides it and draws into its rectangle (`js/runtime.js`). The diagram's look before an update
(and when opened on its own) is neutral. The sizes are set in `deploy-staticwebapp.ps1`: a web app's slot is 232 by
146 (the badge, seven lines 15 px apart and the history strip), and 232 by 161 for a deployable with
`healthDetailPath` (an eighth line: the marks of its health check); a Front Door endpoint's 232 by 98; a database's,
a static site's and a dependency's 232 by 46; a region's 190 by 22; a relationship's 144 by 34. The script draws as
many lines as a slot holds, so a newer page on an older diagram loses lines, never its place (the marks are the last
line: a diagram from before them loses only them), and it spreads the 30 bars of the history strip over the slot's
width. The widths are what the widest line needs: every pixel of them is paid for by the scale of the whole diagram
(a slot of 250 and a number line of 160 made cmdemo2's diagram wider than the page at its smallest scale).

**The update.** `RuntimePayloadBuilder` (plain C#, unit-tested) maps the monitor's state and the manifest to a
payload, and `js/runtime.js` draws it. Every word and state is decided in C#; the script sets `data-rt-state` on the
elements and draws text and small shapes with classes, and `css/app.css` ("Runtime view") gives them their colours.
The payload, as JSON:

```json
{
  "nodes": [ { "alias": "app_ui_primary", "state": "healthy", "label": "Healthy", "facts": "HTTP 200 · 41 ms",
               "lines": [ { "text": "version 2.4.21", "tone": "strong",
                            "parts": [ { "text": "version " }, { "text": "2.4.21", "link": { "href": "https://...", "title": "Release 2.4.21 of ui in Octopus Deploy (...)" } } ] },
                          { "text": "pinned 2.4.21: in sync", "tone": "insync" },
                          { "text": "12 req/min · p95 85 ms", "tone": "plain", "parts": [ "..." ],
                            "trend": { "points": [ 0.25, 1, 0.5 ], "title": "Requests per minute, last 3 checks: 10 to 40, now 20" } },
                          { "text": "primary: serves traffic", "tone": "serving" },
                          { "text": "LlmGateway degraded", "tone": "warn",
                            "marks": [ { "state": "healthy", "title": "API: Healthy. API layer is healthy. Took under 0.1 ms." },
                                       { "state": "degraded", "title": "LlmGateway: Degraded. ..." } ] } ],
               "history": [ "healthy", "unhealthy", "healthy" ], "title": "app-cmdemo2-uat-ui: Healthy\n...",
               "link": { "href": "https://...", "title": "Live Metrics ..." }, "nameLink": { "href": "https://...", "title": "The web app ..." },
               "deployment": { "state": "executing", "title": "deploying cmdemo2-ui 2.4.43 to uat (3 min so far)",
                               "link": { "href": "https://...", "title": "..." } } } ],
  "regions": [ { "alias": "region_primary", "state": "serving", "label": "serving traffic" } ],
  "edges": [ { "id": "fd_ui-to-app_ui_primary", "state": "active", "number": "10", "unit": "calls/min",
               "text": "first, while healthy", "title": "...",
               "link": { "href": "https://...", "title": "Performance ..." }, "trend": { "points": [ 0, 1 ], "title": "..." } } ]
}
```

Node states `healthy`, `unhealthy`, `unreachable`, `checking`, `neutral`; region states `serving`, `standby`, `down`,
`checking`, `neutral`; relationship states `active`, `idle`, `down`, `checking`, `neutral`; line tones `strong`,
`plain`, `muted`, `serving`, `ok`, `warn`, `insync`, `differs`, `unknown`. `number` is absent for a relationship
without a number line, and "–" where no node reports calls per minute. The script reports every alias or id of the
payload that the SVG lacks, and the view names them.

Optional in the payload, and absent where there is nothing to say: `link` (a tile's badge, a relationship's number)
and `nameLink` (the node's name, which PlantUML drew: the script wraps it), each `{ href, title }`; a line's `parts`
(the same words as `text` in pieces, present only when a piece has a `link`); `trend` on a line or a relationship
(`points` are heights from 0 to 1, oldest first, on a scale from zero to the largest reading; `title` is the same in
words); a line's `marks`, one per entry of the node's detailed health check (`state` `healthy`, `degraded`, `failed`
or `unknown`, drawn before the line's words as a check, a warning triangle, a cross or dots, each with its `title`
as tooltip and accessible name; eight at most, those that are not healthy first). The script draws a link as an `a`
element (new tab, `rel="noopener"`, its own `title`) and gives the focus back to the link that had it when an update
redraws the tile. A tile's `deployment` is the mark of a deployment of the node's deployable to the shown environment:
`state` `executing`, `queued`, `waiting` or `ended` (the dot's shape), `title` (the sentence; one line per deployment
when the deployable has more than one, and the first gives the shape) and `link` (the task in Octopus Deploy; absent
when the file gives no address). The script draws it in the corner of the node's slot, or of its box for a node
without one.

## The cluster view

For a system that runs in a Kubernetes cluster (runtime aks-argocd of the demo-environment kit: one AKS cluster for
every environment), the topology names the cluster, and the page gets a third tab:

```json
"cluster": {
  "name": "aks-cmdemo3",
  "statusUrl": "https://cmdemo3-cluster.20-225-155-175.sslip.io/cluster.json",
  "serviceUrl": "https://raw.githubusercontent.com/example-org/cmdemo3-system/cluster-status/aks.json",
  "links": { "portal": "https://portal.azure.com/...", "workloads": "https://portal.azure.com/..." }
}
```

and each environment the namespace that holds its pods, `"namespace": "cmdemo3-prod"`.

The view has two sources, and it shows them next to each other because they can differ: Azure can report a service
available whose pods do not answer, and a stopped cluster reports nothing at all.

| Source | Written by | How fresh |
|---|---|---|
| `statusUrl`, **live** | A collector inside the cluster: its nodes, namespaces, pods and volume claims. | Every `intervalSeconds` (15 s). |
| `serviceUrl`, **slow** | A scheduled workflow: what Azure itself says about the AKS service. | Several times an hour (a ten-minute schedule, which GitHub starts every 10 to 45 minutes), and GitHub serves a copy that may be five minutes older. |

Both are read with every round of the page's checks (the interval of the header, and "Check now"): a `GET` with
`cache: no-store`, no header of its own and the timeout of a node; there is no second timer. Both must allow every
origin. A reading that fails replaces a good one, and the next round reads again. Nothing is kept beyond the page.

### The live file: `cluster.json`

```json
{ "generated": "2026-10-06T20:15:30Z", "intervalSeconds": 15, "kubernetesVersion": "v1.33.3",
  "nodes": [
    { "name": "aks-system-12148983-vmss000000", "ready": true, "pressures": [], "unschedulable": false,
      "pool": "system", "size": "Standard_D4as_v6", "zone": null, "kubeletVersion": "v1.33.3", "createdAt": "2026-10-03T14:02:11Z",
      "cpu": { "usage": 812, "allocatable": 3860, "requests": 2450, "limits": 9100 },
      "memory": { "usage": 9876543210, "allocatable": 13400000000, "requests": 6200000000, "limits": 11800000000 },
      "pods": { "count": 63, "capacity": 110 } } ],
  "namespaces": [
    { "name": "cmdemo3-prod",
      "pods": [
        { "name": "ui-6d5f7c9b8-x2k4q", "workload": "ui", "kind": "Deployment", "node": "aks-system-12148983-vmss000000",
          "phase": "Running", "ready": true, "containers": 1, "containersReady": 1, "restarts": 0, "reason": null,
          "startedAt": "2026-10-06T19:28:40Z",
          "cpu": { "usage": 14, "requests": 100, "limits": 500 },
          "memory": { "usage": 271000000, "requests": 268435456, "limits": 536870912 } } ],
      "volumes": [ { "name": "data-db-0", "capacity": 8589934592, "phase": "Bound" } ] } ] }
```

CPU is in millicores; memory and a volume's capacity are in bytes. `usage` may be `null` (no metrics yet, or a
finished pod), and so may `limits` and `requests` (none set). `phase` is the pod phase of Kubernetes; `reason` is why
a container that is not ready waits or ended, or the pod's own reason (`CrashLoopBackOff`, `ImagePullBackOff`,
`Evicted`, `Completed`). `kind` is the kind of the workload that owns the pod. `pressures` lists the node conditions
that are true among `MemoryPressure`, `DiskPressure`, `PIDPressure` and `NetworkUnavailable`.

The file must be a JSON object with `nodes` or `namespaces`. Everything else is optional: a node, a namespace, a pod
or a volume without a `name` is left out; a number that is absent is a dash; a `ready` that is absent is not ready;
a `phase` that is absent is `Unknown`; unknown fields are ignored.

### Azure's facts: `aks.json`

```json
{ "generated": "2026-10-06T20:10:04Z", "name": "aks-cmdemo3", "resourceGroup": "rg-cmdemo3-cluster", "location": "southcentralus",
  "availability": { "state": "Available", "summary": "There are no known issues affecting this kubernetes cluster.", "reason": null, "occurredAt": "2026-10-03T14:05:00Z" },
  "powerState": "Running", "provisioningState": "Succeeded", "kubernetesVersion": "1.33.3", "tier": "Free",
  "pools": [ { "name": "system", "mode": "System", "count": 1, "size": "Standard_D4as_v6", "osDiskGb": 128,
               "powerState": "Running", "provisioningState": "Succeeded", "kubernetesVersion": "1.33.3" } ],
  "metrics": { "windowMinutes": 15, "nodeCpuPercent": 21.4, "nodeMemoryPercent": 63.0, "nodeDiskPercent": null,
               "apiServerCpuPercent": 4.6, "apiServerMemoryPercent": 31.5 } }
```

`availability.state` is the verdict of Azure Resource Health: `Available`, `Unavailable`, `Degraded` or `Unknown`.
`powerState` is `Running` or `Stopped`. Each number of `metrics` may be `null` or absent (Azure does not emit every
metric for every cluster): its meter is then a dash, which is no problem. The file must be a JSON object that says
something about the service (`availability`, `powerState` or `provisioningState`); everything else is optional.

### What the view shows

1. **AKS service** (Azure's facts): the verdict as the state with Azure's sentence, the power state, the provisioning
   state, the Kubernetes version, the tier, the region and the node pools (name, mode, count × size, OS disk, state);
   Azure Monitor's numbers as small meters (node CPU, memory and disk, API server CPU and memory) with the window they
   are an average of; "as of 15:10:04 (5 min ago)" from `generated`, and a note when the facts are older than 90
   minutes ("Azure's facts are 2 h old: the workflow that publishes them may not be running."). `links.portal` is
   the link "AKS cluster in the Azure portal".
2. **Cluster** (the live file): the state with what is wrong, then the sums: nodes ready, pods ready (finished jobs
   are in neither number and counted on their own), the pods the nodes run of how many they can take, all restarts,
   the Kubernetes version; CPU and memory used of what the nodes offer ("0.81 of 3.86 cores", "9.2 of 12.5 GiB") as a
   meter each, with a thinner one for what the pods request ("63 % requested") and the trend of the value over the
   last checks of this page (see "Trends": the last 60 readings, a check without a live status is a gap); "as of"
   and the collector's interval.
3. **Nodes**: one row per node: its name, Ready or NotReady, each pressure as a warning chip, "cordoned" when it is
   unschedulable, the pool, the size, the zone where it has one, the kubelet's version, its age, and CPU, memory and
   pods against what it offers.
4. **Pods**, by namespace: first the namespaces of the topology's environments, in the topology's order ("tdd ·
   namespace cmdemo3-tdd", with the tier), then every other namespace by name as **Platform**. Per namespace a
   summary ("3 of 3 ready · 1 finished · 1 restart · CPU 53 m · memory 1.5 GiB"), a table (the workload's name with
   the pod's under it, kind, state, restarts, CPU, memory, age; the pods that are not ready and not finished first,
   the finished last and muted) and its volume claims in one line ("data-db-0 8 GiB, Bound"). CPU and memory are
   measured against the pod's limit where it has one ("14 of 500 m" with a meter), the plain number otherwise, a
   dash without a measurement. The platform is one line ("Platform: 9 namespaces, 54 of 54 pods ready") that opens,
   and it is open by itself while one of its pods is unhealthy. `links.workloads` is the link "Workloads in the Azure
   portal". An environment whose namespace the file does not list says so.

CPU is shown in cores for the nodes and in millicores for a pod below one core ("14 m": a thousandth of a core);
memory in KiB, MiB and GiB. A meter is ink, not a state colour: how much, not how good. From 90 % of its whole on it
is marked: the warning colour, and the warning shape and bold words next to it. Ages ("3 d", "47 min") and the rules
below are counted to the file's `generated`, not to the browser's clock.

### How the cluster's states are decided

The states are the page's (Healthy, Unhealthy, Unreachable, Checking, with their shapes), and two more: a warning
(the triangle in a frame) and a neutral state (a bar: stopped, finished, not known). `ClusterAssessment` and
`PodRules` decide them, in plain C#.

**A pod.**

| Pod | State |
|---|---|
| Phase `Succeeded` | Finished: a job that ran to its end. Not a problem, not counted, muted. |
| Phase `Failed` | Unhealthy. |
| Phase `Pending` | Starting; unhealthy when it has been pending for more than 5 minutes (by `startedAt`; starting when the file does not say since when). |
| Phase `Running`, ready | Ready. |
| Phase `Running`, not ready | Unhealthy; starting while it is younger than 2 minutes. |
| Another phase | Ready when it says so, unhealthy otherwise. |

**The cluster** (the live file).

| The live file | State | Words |
|---|---|---|
| Not read yet | Checking | "Reading the cluster's status file" |
| Read, fresh: every node ready under no pressure, every pod that is not finished ready | Healthy | "Every node is ready and under no pressure, and every pod is ready" |
| Read, fresh: something is wrong | Unhealthy | What is wrong, most severe first (nodes that are not ready, nodes under pressure, failed pods, running pods that are not ready by their restarts, pods pending too long), three at most, then the count of the rest: "Node aks-…000000 is not ready; ui in cmdemo3-tdd: CrashLoopBackOff, 7 restarts; and 2 more". |
| Read, fresh: nothing is wrong, and some pods are starting | Checking, "Starting" | "1 pod is starting: ui in cmdemo3-tdd" |
| Read, stale: `generated` is older than four intervals of the collector, and a minute at least | Unreachable, "Stale" | "The collector in the cluster last wrote 15:15:30 (12 min ago)", and what Azure reports. The nodes and pods below are as of then. |
| No answer, or HTTP 404, while Azure's facts say `powerState` `Stopped` | Neutral, "Stopped" | "The cluster is stopped", and that the pods and the pages served from inside the cluster do not run while it is. |
| No answer, or another HTTP status | Unreachable | "The cluster's status file does not answer", the reason, and what Azure reports next to it: "Azure reports the AKS service Available and Running, as of 15:10:04 (5 min ago)." |
| An answer that is not the file | Unhealthy, "Unreadable" | "The cluster's status file could not be read", and why ("The file is not valid JSON."). |

A cordoned node is still a healthy one. A stale file is judged by the browser's clock against the cluster's: a
browser whose clock is more than a minute ahead sees a fresh file as stale.

**The AKS service** (Azure's facts), the first row that applies.

| Azure's facts | State | Words |
|---|---|---|
| Not read yet | Checking | "Reading Azure's facts about the AKS service" |
| HTTP 404 | Neutral, "Not published" | "Azure's facts about the AKS service are not published yet": the workflow has not run. It is no error of the page. |
| No answer, or another HTTP status | Neutral, "Not known" | "Azure's facts about the AKS service could not be read", and why. |
| An answer that is not the file | Warning, "Unreadable" | The same, and why. |
| `powerState` `Stopped` | Neutral, "Stopped" | "The AKS service is stopped" |
| `availability.state` `Unavailable` | Unhealthy | "Azure reports the AKS service unavailable" |
| `Degraded` | Warning | "Azure reports the AKS service degraded" |
| A `provisioningState` other than `Succeeded` | Warning, with the state as its word | "The provisioning state of the AKS service is Updating, not Succeeded" |
| `Unknown`, or no verdict | Neutral, "Unknown" | "Azure Resource Health has no verdict on the AKS service at the moment" |
| `Available` (and running, and succeeded) | Healthy, "Available" | "Azure reports the AKS service available and running" |

Under the words stands Azure's own sentence (`availability.summary`).

## Links: where a number leads

The deployment writes optional `links` maps into `topology.json`, and the page turns every number or name that has a
matching link into one. A link opens a new tab (`rel="noopener"`), and its title says where it goes and that the
destination asks for a sign-in: the page holds no credential and calls none of these places itself.

| Where | Key | It leads to | In the page |
|---|---|---|---|
| `nodes[].links` | `portal` | The web app in the Azure portal. | The web app's name. |
| | `liveMetrics` | Live Metrics of the environment's Application Insights. | The tile's state badge. |
| | `performance` | The Performance blade. | Requests per minute; the number on the Front Door to origin arrow. |
| | `failures` | The Failures blade. | Errors, exceptions. |
| | `dependencies` | A Logs query: the dependency calls (SQL, HTTP) of the app's role, by target. | SQL per minute; the number on the web app to database arrow. |
| `deployables[].links` | `frontDoor` | The Front Door profile in the Azure portal. | The Front Door endpoint's name. |
| | `logs` | A Logs query: the requests of the app's role, by five minutes and instance. | "Requests in Logs" in the versions line; the number on the browser to Front Door arrow. |
| `environments[].links` | `applicationInsights`, `applicationMap`, `database`, `resourceGroup` | The environment's Application Insights, its application map, its database, its resource group. | The links at the end of the environment's heading and under the runtime diagram; the database's name in the diagram. |
| `cluster.links` | `portal` | The AKS cluster in the Azure portal. | "AKS cluster in the Azure portal" in the cluster view's "AKS service" card. |
| | `workloads` | The cluster's workloads in the Azure portal. | "Workloads in the Azure portal" next to the cluster view's "Pods". |

Two links need no entry, because the page builds them from what it reads: a web app's **version** leads to its
release in Octopus Deploy (`projectUrl` + `/deployments/releases/<version>`), or else, when its build reports that
version, to the commit (`commitUrl` of the build endpoint); a Front Door endpoint's version is no link, since it
answers with the version of whichever node served. The "Delivery" card's version leads to `releaseUrl` of the
delivery facts.

`deploy-staticwebapp.ps1` builds the addresses from `system.json` by the naming convention of the stack:
`https://portal.azure.com/#@<tenantId>/resource<resource id>/<blade>`, with the web app
`app-<slug>-<env>-<deployable>[-<region>]` (blade `appServices`), the component `appi-<slug>-<env>` when the
environment has capability "telemetry" (blades `overview`, `applicationMap`, `quickPulse`, `performance`, `failures`),
the database `sqldb-<slug>-<env>` on the environment's SQL server (the one name it reads from Azure, because it ends
in a generated suffix), the Front Door profile `azure.frontDoor` and the tier's resource group. The Logs links are the
portal's "share a link to the query" form (the KQL gzipped and base64-encoded), filtered to
`cloud_RoleName == "<slug>-<deployable>"`: Application Insights is one component per environment, and both regions'
web apps report under that role. A link the deployment cannot build (no subscription in `system.json`, no telemetry,
a SQL server it may not list) is left out.

## How the states are decided

Every check is a `GET` from the browser with `cache: no-store` and a timeout of 10 seconds. All nodes are checked at
the same time, and every result is shown as it arrives: a node that hangs delays no other node. The pinned versions
are read alongside (see above).

| State | Meaning |
|---|---|
| Healthy | The endpoint answered HTTP 200. |
| Unhealthy | The endpoint answered with any other HTTP status (for example 503 from a failing health check). |
| Unreachable | The browser got no answer it may read: a network failure, a refused CORS request or no answer within 10 seconds. |
| Checking | Not checked yet. |

A state is never shown by colour alone: every state has its own icon shape and its text label, and the bars of the
history strip differ in height (tall: healthy, half: unhealthy, stub: unreachable).

**Expected to serve traffic.** Per deployable, the dashboard takes the nodes in priority order (primary nodes first,
then the others, each in the order of the topology) and names the first healthy one. When that node is not a primary,
the deployable has failed over, and the banner says to which region and why. When no node is healthy, nothing can
serve. The Front Door endpoint agrees when it is healthy exactly when a node is; a disagreement is flagged (Front
Door down although a node is healthy, or Front Door healthy although no node is).

**A single node.** A deployable without a Front Door endpoint and with exactly one node (one app at one public
address, as in a Kubernetes cluster) is shown as a single node that serves: nothing can take over from it, so the
banner says "Serves traffic: <node>", "Not serving: <node>" or "Checking <node>", the runtime view's tile "serves
traffic", "not serving" or "checking", and neither names a primary, a standby, a region or a failover. The states and
their colours are the same. Where the topology has no Front Door endpoint at all, the page's help texts do not name
Front Door either.

**Summary.** Every tile counts as one node: the Front Door endpoints and the web apps. Unhealthy and Unreachable both
count as not healthy.

**Probe.** "Health check" calls `healthPath`: the full check also connects to the database, which keeps a database
that pauses when idle (a serverless one) awake. "Liveness" calls `alivePath`: it only asks whether the web app is
running and leaves the database alone, so such a database can pause while the dashboard is open.

**Polling.** A round of checks runs every interval (30 seconds unless changed). Polling stops while the dashboard is
paused and while its browser tab is hidden (Page Visibility API), and a round runs at once when it resumes or the tab
is shown again. "Check now", changing the probe and reloading the topology run one round at once, also while paused.

## CORS: every node must allow the dashboard's origin

The browser reads an answer from another origin only when that origin allows it. Each node must therefore allow the
dashboard's origin in its CORS settings, on App Service for example:

```
az webapp cors add --resource-group <group> --name <web app> --allowed-origins https://<dashboard host>
```

Without it the browser blocks the answer and the dashboard reports the node as **Unreachable**, although the node may
be healthy: from inside the page, a refused CORS request and a network failure look the same. The same holds for an
answer that does not come from the app, such as the platform's own page for a stopped web app: it carries no CORS
header, so the tile shows Unreachable, not Unhealthy. The browser's console names the cause.

The Front Door endpoint forwards the request to a node, and the node's CORS header comes back through it, so the same
setting covers the Front Door tile.

In a cluster the ingress can answer for the node: runtime aks-argocd of the demo-environment kit puts an Envoy Gateway
`SecurityPolicy` (every origin, GET, no credentials) on each app's route while the system has a dashboard.

A node that is not on App Service allows the origin in its own way: an application that runs on infrastructure of its
own answers its health, liveness and version paths with the header `Access-Control-Allow-Origin` itself, also when the
status is not 200.

## The repository

```
Dashboard.sln
Directory.Build.props        warnings as errors, nullable, analyzers; shared by both projects
global.json                  the SDK
src/Dashboard                the Blazor WebAssembly app
  App.razor                  the page: header, view tabs, environments, footer, polling
  Components/                tile, history strip, trend line, state badge, deployable section, version line, code and
                             delivery cards, the dashboard's own code, cost line, availability line, the marks of
                             deployments and their dot, events strip, links, runtime view, legend, cluster view (its
                             cards, tables, meter and badge)
  Health/                    the health logic, plain C# without a browser
  Runtime/                   the runtime view's files, payload and address, plain C# without a browser
  Cluster/                   the cluster view's files, states, grouping and words, plain C# without a browser
  wwwroot/                   index.html, css/app.css, js/visibility.js, js/location.js, js/runtime.js, the sample
                             topology.json and runtime/
src/Dashboard.Tests          xUnit tests of the health logic; Samples/ holds a cluster.json and an aks.json
scripts/Write-BuildFacts.ps1 the Build's step that writes build-facts.json, the dashboard's own build facts
.github/workflows            build.yml, release.yml, secret-scan.yml
```

The health logic is in `src/Dashboard/Health` and has no dependency on the browser: reading the topology
(`TopologyParser`), classifying an answer (`HealthClassifier`, `NodeProber`), the serving and failover decision
(`ServingAssessment`), the summary (`HealthSummary`), the history (`HistoryBuffer`), the polling loop (`Poller`),
reading the pinned versions (`PinnedVersions`, `PinnedVersionsReader`) and comparing them with the nodes
(`VersionAssessment`, `VersionSummary`), a node's telemetry with its process (`TelemetrySnapshot`, `ProcessVitals`),
the trends (`Trend`, `Trends`), the events (`EventDetector`, `EventLog`), the build facts (`BuildInfo`, `BuildText`;
the dashboard's own are `DashboardInfo` of the topology and `DashboardMonitor.DashboardBuild`), the traffic button's
requests and its choice of environment (`TrafficPlan`),
the entries of a detailed health check (`HealthDetail`, `HealthDetailText`),
the delivery facts (`DeliveryReport`, `DeliveryText`) with the hourly health reports (`HealthReports`,
`AvailabilityText`), the cost (`CostReport`, `CostText`), the deployments in flight (`DeploymentsReport`,
`DeploymentMark`, `DeploymentText`) and the links (`LinkSet`, `LinkText`). The runtime view's logic is in `src/Dashboard/Runtime`: reading `runtime/`
(`RuntimeManifestParser`, `RuntimeLoader`), the update of the diagram (`RuntimePayloadBuilder`) and the view in the
address (`ViewAddress`). The cluster view's logic is in `src/Dashboard/Cluster`: the two files (`ClusterStatus`,
`AksService`) and their reading (`ClusterReader`, `ClusterMonitor`), the states (`PodRules`, `ClusterAssessment`), the
grouping and the sums (`ClusterGroups`, `ClusterTotals`), the words and units (`ClusterText`) and the events
(`ClusterEventDetector`). `HttpClient` and `TimeProvider` are injected, so the tests run them with a stub handler and
fake time.

There is no external dependency at run time: no CDN, no web font, no CSS framework. The style sheet is
`wwwroot/css/app.css` and follows the viewer's light or dark preference.

The site is static and expects to be served from the root of its host (`<base href="/">`); the host must serve
`.wasm` files as `application/wasm`.

## Build and release

- **Build** (`.github/workflows/build.yml`): on every pull request and every push to `master`. It builds with
  warnings as errors, runs the tests (their results as a trx file and their coverage as a Cobertura file, in
  `TestResults/`), publishes the site, writes `version.json` and `build-facts.json` next to `index.html` and uploads
  the content of the published `wwwroot` folder as the artifact `dashboard-site`. The job `Build result` is the check
  the default-branch ruleset requires.
- **Release** (`.github/workflows/release.yml`): after a green Build of `master`. It zips the content of
  `dashboard-site` (`index.html` at the root of the zip) as `<SYSTEM_SLUG>-<DEPLOYABLE_NAME>.<version>.zip`, pushes
  it to the Octopus built-in feed and creates the release `<version>` of the Octopus project
  `<SYSTEM_SLUG>-<DEPLOYABLE_NAME>`. It signs in to Octopus with GitHub OIDC (environment `release`) and reads the
  repository variables `SYSTEM_SLUG`, `DEPLOYABLE_NAME`, `OCTOPUS_URL`, `OCTOPUS_SPACE_NAME` and
  `OCTOPUS_SERVICE_ACCOUNT_ID`.
- **secret-scan** (`.github/workflows/secret-scan.yml`): gitleaks over the history of the commit every pull request and push checks out.

For a system that serves the dashboard from its cluster (runtime aks-argocd), the release is an image instead:
`.github/release-image.yml` of the template, which `add-demo-deployable.ps1` puts in place as
`.github/workflows/release.yml`. It builds `Dockerfile` (the site behind nginx, without root, port 8080;
`nginx.conf`), pushes `<ACR_LOGIN_SERVER>/<SYSTEM_SLUG>/<DEPLOYABLE_NAME>:<version>` as the registry's push identity
(`AZURE_CLIENT_ID_ACR_PUSH`, `AZURE_TENANT_ID`, `AZURE_SUBSCRIPTION_ID`, `ACR_NAME`), locks the tag and creates the
Octopus release. The image carries the sample `topology.json` and `runtime/` too, but nginx serves those two paths
from `/content`, where the cluster mounts what the deployment committed for the environment (a ConfigMap with the
flat keys `topology.json` and `runtime-<file>`). Both releases take the artifact `dashboard-site` whole, so both serve
`/version.json` and `/build-facts.json` as the Build wrote them: the zip at its root, the image from nginx's root.

The version is `MAJOR_VERSION.MINOR_VERSION.<run number of the Build run>`; the two numbers are in `build.yml`. The
build passes it as `-p:Version=...`, and the dashboard shows it in its footer. A local build shows `0.0.0-local`.

Every release says what it was built from and how good that build is, in two files the Build writes into the site:
`/version.json` (`{ "version": "1.0.42" }`) and `/build-facts.json` (`scripts/Write-BuildFacts.ps1`: the version, the
commit and the Build's run, the lines of code, the tests, the coverage and the complexity; see "Code"). Both are part
of the artifact `dashboard-site`, so the zip and the image carry them without a step of their own, and a deployment
does not change them. In the image, nginx answers them, like `index.html`, with `Cache-Control: no-cache`: their
names stay the same from one release to the next. To write the facts of a working copy:

```
dotnet test -c Release --logger trx --collect:"XPlat Code Coverage" --results-directory TestResults
dotnet publish src/Dashboard -c Release -o publish
pwsh -NoProfile -File scripts/Write-BuildFacts.ps1    # publish/wwwroot/build-facts.json
```

Without the environment of GitHub Actions the commit and the two links are `null`, and without `TestResults/` the
tests, the coverage and the complexity are; the script says each as a `SKIP` line.

The zip carries the sample `topology.json` and `runtime/`; the deployment replaces them with the real ones (it removes
the sample's `runtime/` first). The build writes no precompressed copy of those files (`topology.json.br`,
`runtime/*.gz`, ...), so no stale copy can be served.

## Calls per minute and the traffic button

A node with `telemetryPath` answers it with its own counts over the last minute (any origin may read them; numbers
only):

```json
{
  "windowSeconds": 60,
  "startedAt": "2026-10-05T23:00:00Z",
  "requests": { "perMinute": 12, "frontDoor": 10, "direct": 2, "errors": 0, "p95Ms": 85 },
  "probes": { "perMinute": 4, "frontDoor": 6 },
  "sql": { "perMinute": 117, "requests": 62, "background": 55, "p95Ms": 12 },
  "http": { "perMinute": 0 },
  "process": { "cpuPercent": 3.2, "workingSetMb": 412, "gcHeapMb": 96, "threads": 41, "inFlight": 2, "exceptionsPerMinute": 0, "uptimeSeconds": 5321 }
}
```

`requests` is traffic (not the checks); `probes.perMinute` the dashboards' and diagnostics' checks, `probes.frontDoor`
Front Door's health probes. The health view's tile shows `requests.frontDoor` as "Front Door" only for a deployable
with a Front Door endpoint. Every round reads it from each regional node next to the health check (never through Front
Door, which would answer for one node only). An answer that is not this JSON is no numbers, not a failure.

`sql.perMinute` is every SQL command the process ran. An app may tell them apart: `sql.requests` (run while handling
an HTTP request) and `sql.background` (all others, mostly the message bus polling the database). When both are there,
the number the page shows for the web app's queries is `requests`, what traffic causes: on the web app to database
arrow (with the background next to the role, "app queries · 55 background", and both in the tooltip), in the
database's tile (the sum over its web apps, the background in the tooltip) and as "SQL" on the health view's tile. An
app that reports `perMinute` only (an older one) keeps the total everywhere. The arrow's trend follows the number
shown.

`http.perMinute` is every outgoing HTTP call of the process. The runtime diagram shows it on the arrow from a web
app to its dependency when the deployable declares exactly one (see "The runtime view"); with more than one the
arrows show a dash, because the app does not tell its calls apart.

### Process vitals

`process` is optional (an older app omits it), and so is each of its values. A web app's tile shows, in both views:

| Value | Shown as |
|---|---|
| `cpuPercent` | "CPU 3.2 %" (one decimal below ten), with its trend. |
| `workingSetMb` | "412 MB", from 1024 on "1.4 GB"; the tooltip adds the managed heap (`gcHeapMb`) and the `threads`. |
| `inFlight` | Requests being answered at this moment. |
| `exceptionsPerMinute` | "0 exceptions/min"; a warning (the warning shape and bold words) when above zero, like errors. |
| `uptimeSeconds` | "up 3 min", "up 2 h", "up 4 d" (minutes up to two hours, hours up to two days). Under five minutes it is a restart worth a look, "restarted 3 min ago", marked as a warning. |

### Trends

The page keeps the last 60 telemetry readings of every node in memory (half an hour at the default interval; nothing
is stored) and draws a small line next to a number: requests per minute and CPU on the tiles, calls per minute on
the arrows of the runtime diagram (for the browser to Front Door arrow, the origins' readings of the same check added
up). Oldest on the left, the last reading as a dot, on a scale from zero to the largest reading, so a line that stays
low is a number that stays small. Its title says the same in words ("Requests per minute, last 12 checks: 0 to 45,
now 12"). A line needs two readings; a check without a reading is left out.

The panel at the bottom of the page sends two requests a second for a minute (`TrafficPlan`) from the browser to the
chosen environment's public addresses, the Front Door endpoint or else the primary node, round-robin over
`trafficPaths`. They are plain GETs in mode `no-cors` (`js/traffic.js`): the browser needs no CORS answer, and the
response stays opaque. While it runs, the page checks every 10 s, and says so next to the "Interval" control
("every 10 s while traffic runs") while that control names a longer interval; the control keeps the viewer's choice,
which applies again when the traffic ends.

The panel's "Environment" starts on the environment the runtime view shows (`#runtime/uat`: uat) and follows it when
the viewer selects another diagram; where the page has shown none (the health view), it is the first environment
that has something to call. Once the viewer picks an environment in the panel, that choice stays, and nothing
changes it while traffic runs (`TrafficPlan.Choose`).

## Health checks, entry by entry

The health check a tile is about answers with a status only. An app may also answer a detailed health check, one
entry per thing it depends on and checks; with `healthDetailPath` (for example `/_healthcheck/detailed`) the page
reads it and shows which entry is the reason:

```json
{ "overallStatus": "Healthy", "totalDurationMs": 42.6,
  "entries": [
    { "name": "API", "status": "Healthy", "description": "API layer is healthy", "durationMs": 0.001 },
    { "name": "DataAccess", "status": "Healthy", "description": "Database connection successful (Provider: ...)", "durationMs": 42.07 },
    { "name": "LlmGateway", "status": "Degraded", "description": "Chat client answered slowly: 4.2 s", "durationMs": 4211.9 } ] }
```

`status` is `Healthy`, `Degraded` or `Unhealthy` (the states of ASP.NET Core's health checks); anything else, or
none, is shown as "Not known". `description` and `durationMs` are optional. An entry without a `name` is left out,
and an answer without a single named entry is no answer.

- **When it is read.** With every round of checks, from each regional node, next to its health check, and only while
  the probe is "Health check": the detailed check connects to the database like the health check itself, and
  "Liveness" is there to leave the database alone. A Front Door endpoint is not asked (it would answer for one
  node). The body is read whatever the HTTP status, because a failing health check answers 503 with the same JSON.
  Any origin must be allowed to read it, like the telemetry.
- **What is kept.** The answer of the last check only: no answer, an answer that is not this JSON, or the probe
  Liveness leaves a tile without marks, never with the marks of an earlier check, and fails no check.
- **Health view.** A block "Health checks" on the web app's tile: one mark per entry, with the entry's name and its
  state as a shape (check, warning triangle, cross, dots) and, when it is not healthy, as a bold word on a wash; for
  a screen reader every mark has its state in words. The tooltip has the check's own words and how long it took
  ("DataAccess: Healthy. Database connection successful. Took 42.1 ms."). A tile that is unhealthy or unreachable
  says under the marks which entries are the reason: "Not healthy: DataAccess (Unhealthy: Database connection
  failed: login timeout expired)".
- **Runtime view.** The last line of the web app's tile: the same marks (eight at most, those that are not healthy
  first), each with its tooltip, and a summary in words: "8 checks healthy", the one entry that is not ("LlmGateway
  degraded"), or how many are not ("2 of 8 checks not healthy"). The tooltip of an unhealthy web app ends with the
  same "Not healthy: ..." sentence. A dependency outside the subscription takes its state from the entry the
  deployment named for it (see "The runtime view").

The health view's state of a tile is still the health check's HTTP status and nothing else: a `Degraded` entry
changes no state and no summary. An entry that changes its state is an event (see "What just happened").

## What just happened

Above the traffic panel, in every view: the last 50 events this page observed since it was opened, newest first,
each with its time, its environment and node, and words. The page finds them itself, by comparing every check with
the one before (`EventDetector`); nothing comes from a server's log, and a reload of the page starts an empty list
(a reload of the topology keeps it).

| Event | When | Example |
|---|---|---|
| Health | An endpoint's state changed; at the first check only when it is not healthy. | "ui: Healthy → Unreachable: No answer within 10 s" |
| Health check | An entry of a web app's detailed health check has another state than in the last answer that had entries (so a check that read none, or the probe Liveness, hides no change); at the first answer, and for an entry the answer before did not have, only when it is not healthy. With the entry's own words. Its level is the entry's new state: Healthy good news, Degraded a warning, Unhealthy a problem. An entry that is gone is no event, and neither is the same state in other words. It follows the node's own state change of the same check, which it explains. | "ui: LlmGateway Healthy → Degraded: Chat client answered slowly: 4.2 s", "ui: DataAccess Unhealthy at the first check: Database connection failed" |
| Restart | A web app's `uptimeSeconds` went down, or (without it) its `startedAt` is later. Compared with its last reading, also across checks it did not answer. | "ui restarted, up 12 s" |
| Deployment | A web app reports another version. Not for a Front Door endpoint, which answers for whichever node served. | "ui: 2.4.14 → 2.4.15, deployed" |
| Serving region | At the end of a round, the node expected to serve changed: a failover, a failback, nothing serves, serves again. | "Failover: westus3 → eastus2. Primary westus3 is unreachable; eastus2 is expected to serve traffic." |
| Pin | The version pinned in Git changed between two readings of `versions.json`, or of the deployable's own file (`pinUrl`). | "ui: pinned 2.4.14 → 2.4.15 in Git" |
| Traffic | The traffic button was started, stopped or ran out. | "Traffic started: 2 requests a second for 60 s to ui at cmdemo2-uat-def456.z01.azurefd.net" |
| Cluster | Only with `cluster` in the topology, between two readings of its files and never at the first: the status file stopped answering or answers again; the collector stopped writing (the file became stale) or writes again; a node is no longer ready or is ready again; per pod one event a round at most: its restart count rose, or else it became unhealthy, or else it is ready again; Azure's verdict on the AKS service or its power state changed. A round names ten pods and counts the rest. | "ui in cmdemo3-tdd restarted (7 restarts): CrashLoopBackOff", "Node aks-…000000 is not ready", "The cluster's status file stopped answering: no answer within 10 s", "Azure's verdict on the AKS service: Available → Degraded" |

The list is a `role="log"` region: additions are announced politely, and it scrolls inside its own frame. An event's
level has a shape (check, warning triangle, cross, dots) next to its words.

## Code: the build a deployable runs

With `buildPath` (for example `/_build`), the page asks the deployable's primary node for the build it runs: once
per load of the topology, and again when the node reports another version; not every round. A node that is
unreachable is asked once it answers. Any origin must be allowed to read it, like the telemetry. The answer, every
part optional and any section possibly `null`:

```json
{ "version": "2.4.15", "commit": "<sha>", "commitUrl": "https://github.com/o/r/commit/<sha>", "builtAt": "2026-10-06T05:00:00Z",
  "buildUrl": "https://github.com/o/r/actions/runs/1",
  "code": { "linesOfCode": 84210, "files": 1203, "languages": [ { "name": "C#", "lines": 61234, "files": 800 } ] },
  "tests": { "unit": 1009, "integration": 240, "acceptance": 168 },
  "coverage": { "linePercent": 81.2, "branchPercent": 70.1 },
  "complexity": { "average": 1.9, "max": 34, "methods": 5210 },
  "crap": { "max": 28.5, "threshold": 30, "overThreshold": 0 },
  "analysis": { "qodanaProblems": 0 } }
```

The "Code" card (in the health view inside the deployable's section, in the runtime view under the diagram) shows
what is there: the build's version, the commit (its first seven characters, linked to `commitUrl`), when it was built
(relative, the exact time in the title) and the build's run; the lines of code and files, with one bar divided by
language (the five largest by name, the rest as "Other") and under it the same as a list in words, so no colour has
to be told apart; the tests by kind with their sum; the coverage of lines and branches; the average and the worst
cyclomatic complexity; the worst CRAP score and how many methods are over the threshold (a warning when one is); the
problems Qodana reports (a warning when there are any). Each environment's card is its own primary node's answer, so
two environments that run different builds show different cards. An answer that is not this JSON, or no answer, is
no card.

### The dashboard's own facts: `/build-facts.json`

The dashboard publishes the facts of its own build as well: the file `/build-facts.json` next to `index.html`, in
the shape above. Its Build writes it (`scripts/Write-BuildFacts.ps1`, see "Build and release"), so a release carries
the facts of the build it was made from, and a topology that lists a dashboard as a node may name it as that node's
`buildPath` (see "A dashboard as a node"). What each part holds:

| Part | What it holds |
|---|---|
| `version` | The version of the build, `MAJOR.MINOR.<run number>`: the number `/version.json` holds. |
| `commit`, `commitUrl` | The commit the Build checked out, and its page on GitHub. |
| `builtAt` | When the Build wrote the file (UTC). |
| `buildUrl` | The Build's run on GitHub. |
| `code` | The non-blank lines and the files of the repository, by language, over the files Git tracks: C#, Razor, CSS, Markdown, JavaScript, PowerShell, YAML and HTML. Not counted: what nobody wrote by hand (`bin/`, `obj/`, `*.g.cs`, `*.min.*`) and every file of another kind: data and pictures (JSON, SVG, PlantUML), the project files, `Dockerfile` and `nginx.conf`. |
| `tests` | `unit`: the tests that passed in the Build's test run (its trx file). The dashboard has unit tests only, so the file names no other kind and the card says the count as one kind, for example "741 unit": an `integration` or `acceptance` of 0 would read as tests that exist and found nothing to run. |
| `coverage` | `linePercent` and `branchPercent` of that test run, from the Cobertura file coverlet writes. The components (`.razor`) count, and no unit test renders one; the code the build generates under `obj/` does not count. |
| `complexity` | The cyclomatic complexity coverlet writes for every method in the same file: the `average`, the `max` and the number of `methods`, generated code left out. |
| `crap` | `null`: the Build makes no CRAP report and has no threshold to hold a score against. |
| `analysis` | `null`: the Build does not run Qodana. |

A part whose input is missing is `null` too, and the Build's log says why in a `SKIP` line: the commit and the two
links outside GitHub Actions, the tests without a trx file, the coverage and the complexity without a Cobertura
file. The card leaves a `null` part out, so a dashboard's "Code" card has no CRAP line and no Qodana line.

On a system whose topology does not list the dashboard as a node (the App Service runtime: `deploy-staticwebapp.ps1`
lists the web apps only), the deployment writes `system.dashboard = { "name": "<deployable>", "buildPath":
"/build-facts.json" }` into `topology.json` from the static deployable's `buildPath` in `system.json`, and only when
the release carries the file. The page then reads the file from its own origin (never from another site) and shows it
as the Code card "dashboard (this page)": after the environments in the Health view, last under the diagram in the
Runtime view. Without the key the page asks for nothing.

## Delivery

With `system.deliveryUrl`, the page reads the system's delivery facts: with the first round of checks and then every
five minutes. The deployment writes the address
`https://raw.githubusercontent.com/<githubOrg>/<repository>/status/delivery.json`; a workflow of the system
repository publishes the file to its branch `status`. Until it has, the address answers 404 and nothing is shown. A
reading that fails later keeps the last good one.

```json
{ "generated": "2026-10-06T05:00:00Z",
  "environments": [ { "name": "prod", "deployables": [
    { "name": "ui", "version": "2.4.14", "deployedAt": "2026-10-06T03:37:00Z", "signedOffBy": "cm-ai-ops", "reason": "...",
      "commit": "<sha>", "commitAt": "...", "leadTimeHours": 5.2, "behindFirst": { "versions": 0, "days": 0 },
      "deploymentsLast7Days": 4, "failedLast7Days": 0, "releaseUrl": "https://..." } ] } ],
  "failover": { "environment": "uat", "at": "2026-10-05T12:00:00Z", "seconds": 44 } }
```

An environment of the file may also have `health`, what its hourly health reports found (see "Availability").

The "Delivery" card of a deployable in an environment shows what is there:

| Line | From |
|---|---|
| Deployed: the version (linked to `releaseUrl`) and how long ago, the exact time in the title. | `version`, `deployedAt` |
| Signed off: by whom, and the reason. The first environment has no sign-off step, so its card has no such line. | `signedOffBy`, `reason` |
| Lead time: from the commit to this deployment ("5.2 h from commit 0a1b2c3 to this deployment"). | `leadTimeHours`, `commit`, `commitAt` |
| Compared: with the first environment of the topology: "same as tdd", "2 versions, 3 days behind tdd". Versions are the distance in the project's list of releases; days are since both last ran the same release (absent when they never did). Not shown in the first environment. | `behindFirst` |
| Last 7 days: "4 deployments, none failed"; a warning when one failed. | `deploymentsLast7Days`, `failedLast7Days` |

`generated` is when the file's content last changed, not the time of a check: the card's title has it as "Delivery
facts as of the last change". Entries of the file that belong to no deployable of the topology get a card of their
own after the environment's deployables: first the one named `system`, the system project itself, labelled "the
system (infrastructure and pipeline)", then the others (such as the dashboard). The last failover test of the system
(`failover`: environment, when, the seconds until the standby answered) is a line above the events.

## Availability

The system's pipeline has an hourly runbook "Health report" in Octopus Deploy: per environment it asks every node
and the public address, and the run fails when one is not healthy. The workflow that publishes the delivery facts
counts those runs (`scripts/write-delivery.ps1` of the system repository: one read of Octopus per environment), and
an environment of `delivery.json` then has:

```json
{ "name": "prod", "deployables": [ ],
  "health": { "last24Hours": { "reports": 24, "healthy": 23 }, "last7Days": { "reports": 166, "healthy": 164 },
              "lastFailure": "2026-10-06T16:13:00Z" } }
```

| Field | What it is |
|---|---|
| `last24Hours`, `last7Days` | `reports`: the health reports of the environment that ended in that time (succeeded, failed or timed out; a cancelled one is in neither number). `healthy`: those of them that succeeded. |
| `lastFailure` | When the last report that did not succeed ended; `null` when none did in seven days. |
| `health` itself | `null` or absent when the system has no such runbook or its runs could not be read. |

Under the environment's name in the health view ("Availability") and under the diagram in the runtime view
("Availability of prod"), the page shows:

> Healthy in **23 of 24** hourly checks (95.8 %) in 24 hours · **164 of 166** in 7 days · last failure **9 h ago**
> Hourly checks by the pipeline, not continuous monitoring.

- The second line is part of the element, not a tooltip: nobody should read the numbers as monitoring. An outage
  between two reports is not counted, and a report that fails says a node did not answer at that moment, not for how
  long. The tooltip says who counts and how, and when the delivery facts last changed (the counts are as of then).
- The percentage has one decimal and is never rounded to 100 % while a report failed. The seven days' percentage is
  the tooltip of that part.
- "last failure" is relative, with the exact time in its tooltip; without a failure in seven days the line ends "no
  failure in 7 days". A window without a single report says so ("No hourly check ended in 24 hours": a system that
  is dormant, or younger than the window) and claims nothing.
- A part the file does not have is left out; an environment without `health` (an older file, a `null`, counts that
  cannot be: more healthy reports than reports) has no line. It is history, not the state of this moment: no colour,
  no icon, no event, and it does not change the summary.

## Cost

With `system.costUrl`, the page reads what the system cost in Azure: with the first round of checks and then every
five minutes, as it reads the delivery facts. The deployment writes the address
`https://raw.githubusercontent.com/<githubOrg>/<repository>/status/cost.json`; the workflow that publishes the
delivery facts publishes this file next to them from Azure Cost Management (asked hourly until the last complete
day is there in full, then every six hours; `scripts/write-cost.ps1` of the
system repository). Until it has, the address answers 404 and no cost is shown. A reading that fails later keeps the
last good one.

```json
{ "generated": "2026-10-07T05:00:00Z", "currency": "USD", "asOf": "2026-10-06",
  "system": { "yesterday": 3.41, "last7Days": 22.10, "monthToDate": 24.80 },
  "environments": [
    { "name": "prod", "yesterday": 1.52, "last7Days": 9.80, "monthToDate": 11.02,
      "topServices": [ { "name": "Azure App Service", "monthToDate": 6.10 } ] },
    { "name": "shared", "yesterday": 1.10, "last7Days": 7.70, "monthToDate": 8.20, "topServices": [ ] } ] }
```

| Field | What it is |
|---|---|
| `asOf` | The last complete UTC day the numbers include. Every number is a sum of complete UTC days: `yesterday` is that day, `last7Days` the seven days that end with it, `monthToDate` the first of its month up to it. |
| `currency` | The currency Azure bills in. `USD` is shown as `$1.52`, any other by its code (`1.52 EUR`). |
| `system` | The whole system: everything in its resource groups. |
| `environments[]` | One entry per environment, by the tag `environment` of the resources, and `shared`: what carries no such tag (the Front Door profile, the registry, the Terraform state) and what Azure bills without tags. |
| `topServices` | Up to three services (as Cost Management names them) that cost most in `monthToDate`, the most expensive first. |
| `environments[].estimate` | Optional, for a system whose environments share one cluster: `{ "share": 0.2, "yesterday": …, "last7Days": …, "monthToDate": … }`, the environment's estimated part of the shared cost (the cluster's cost that no tag claims, times the share of CPU and memory the environment's pods request of what all pods request). The line of the environment then ends "plus about $2.69 this month of what the environments share (20 % of what all pods request)". An estimate, not a bill: it stays part of `shared`. |
| `generated` | When the file's content last changed, not the time of a check. |

Where it is shown:

| Where | What |
|---|---|
| The header, under every view | "Cost of the system": the three numbers of `system`. |
| Health, under an environment's name | "Cost": the three numbers of the environment's entry, and under them the services that cost most ("Most this month: Azure App Service $6.10, SQL Database $2.00"). An environment without an entry has no line. |
| Health, after the environments | A heading per entry that is no environment of the topology: first those Azure still bills under another name (a removed environment, marked "not in the topology"), then `shared` ("no environment"), each with its line and a sentence that says what it holds. |
| Runtime, under the diagram | "Cost of <environment>" and "Cost of what the environments share". |

A line reads "Cost $1.52 yesterday · $9.80 in 7 days · $11.02 this month · as of 2026-10-06". The numbers are never
presented as live:

- every line ends with the day its numbers are of ("as of"), and its tooltip says that Azure's cost arrives hours late
  and is amended for a day or two;
- "yesterday" is said only while `asOf` is the UTC day before now: a file that was not renewed reads "on 2026-10-06"
  instead. "This month" is said only while now is in the month of `asOf`: on the first of a month, and for an old
  file, it reads "in September";
- a number that is `null` in the file (Azure throttled or refused that reading) is a dash;
- cost has no state: no colour, no icon, no event, and it does not change the summary.

Attribution is by tag, so it is as good as the tags: a resource that several environments use is counted for the one
whose tag it carries (the App Service plan a tier's environments share carries the tag of the tier's first
environment), and a resource created without the tag counts as shared.

## Deployments in flight

With `system.deploymentsUrl`, the page marks what is being deployed, in whatever environment. The system reads
Octopus Deploy once for all of it and publishes one file, `deployments.json`; no application reports its own
deployment, and the page itself never calls Octopus Deploy. The address is
`https://raw.githubusercontent.com/<githubOrg>/<repository>/deployments/deployments.json`: workflow `deployments` of
the system repository publishes the file to its branch `deployments` when a deployment pins its version, every five
minutes and on demand (`scripts/write-deployments.ps1` of the system repository). Until it has, the address answers
404 and nothing is marked.

```json
{ "generated": "2026-10-08T04:22:24Z", "system": "cmdemo2", "octopus": "https://example.octopus.app/app#/Spaces-1",
  "deployments": [
    { "project": "cmdemo2-ui", "environment": "uat", "release": "2.4.43", "state": "executing",
      "since": "2026-10-08T04:22:07Z", "url": "https://example.octopus.app/app#/Spaces-1/tasks/ServerTasks-1" },
    { "project": "cmdemo2-ui", "environment": "tdd", "release": "2.4.43", "state": "succeeded",
      "since": "2026-10-08T04:15:00Z", "finished": "2026-10-08T04:20:00Z", "url": "https://..." } ] }
```

| Field | What it is |
|---|---|
| `generated` | When the system read Octopus Deploy. It is not the time of a check. |
| `deployments[]` | One entry per deployment task that has not ended, or ended in the last half hour: in flight first. An empty list says that nothing is being deployed. |
| `project` | The Octopus project: `<slug>-<deployable>` (with `system.slug` of the topology), and `<slug>-system` for the system's own release, its infrastructure and configuration. |
| `environment`, `release` | Where it deploys to, and what. |
| `state` | `queued` (behind another task), `executing`, `waiting` (stopped for a person: the sign-off), and ended: `succeeded`, `failed`, `canceled`. |
| `since` | When it started, or when it was queued while it has not started. |
| `finished` | When it ended; absent while it has not. |
| `url` | The task in Octopus Deploy. |

The file must be a JSON object with a `deployments` list. An entry without `project`, `environment`, `release` or
`state` is left out; a time that does not parse is no time; an address that is not an absolute http(s) address is no
link; unknown fields are ignored.

**What is marked.** A deployment in flight (`queued`, `executing`, `waiting`), for as long as the file has it; one
that ended, for ten minutes after `finished` (by the browser's clock) and then no more, so a deployment of a few
minutes that ended before the page saw it start still shows. One that ended at a time the page cannot read is not
marked. A state the page does not know is marked like one in flight while the file gives it no `finished`, and like
one that ended otherwise. The marks of an environment are in this order: `waiting` first (a person has to act), then
`executing`, `queued`, and what ended: `failed`, `canceled`, `succeeded`.

**The words** are those of the fleet's dashboard, so both walls read alike; they name the project and the
environment in full:

| State | Sentence |
|---|---|
| `queued` | cmdemo2-ui 2.4.43 is queued for uat |
| `executing` | deploying cmdemo2-ui 2.4.43 to uat |
| `waiting` | cmdemo2-ui 2.4.43 waits for a sign-off in uat |
| `succeeded` | cmdemo2-ui 2.4.43 reached uat 5 min ago |
| `failed` | cmdemo2-ui 2.4.43 failed in uat 5 min ago |
| `canceled` | cmdemo2-ui 2.4.43 was canceled in uat 5 min ago |
| another | cmdemo2-ui 2.4.43 in uat: paused (the file's own word) |

The age is minutes under an hour ("5 min", and "1 min" at least), hours under two days ("5 h"), then days ("3 d").
The tooltip of a mark in flight adds for how long it has been so ("deploying cmdemo2-ui 2.4.43 to uat (3 min so
far)").

**The dot.** A mark is a dot and its sentence. The dot's colour is the marker's own, a blue that is none of the
states of health, and it is never the only sign: the shape says the state, and the sentence says it in words.

| Shape | State |
|---|---|
| Filled, with a ring that leaves it (the pulse; still where the viewer asks for less motion, `prefers-reduced-motion`) | `executing` |
| Hollow | `queued` |
| A dot in a ring | `waiting`: a person has to act |
| Small and still | Ended in the last ten minutes (`succeeded`, `failed`, `canceled`), and a state the page does not know |

**Where it shows.**

| Where | What |
|---|---|
| Health, under an environment's name | Every mark of the environment, one line each: the dot and the sentence, the sentence a link to the task (`url`). First under the name, above the cost and the availability. |
| Health, on a tile | The marks of the tile's deployable in its environment, the same lines, under the tile's chips: on the Front Door tile and on every node's. The project `<slug>-<deployable>` is the deployable `<deployable>` of the topology; the system's own project and a project the topology does not list are on no tile, only under the environment's name. |
| Runtime, under the title | Every mark of the shown environment, as in the health view: the system's own release marks the environment here, and so does a project the diagram draws no node of. |
| Runtime, the environments' buttons | A dot (the shape of its first mark) on every environment with a mark in flight, with the sentences as its tooltip: uat shows as deploying while prod is looked at. What only ended puts no dot there. |
| Runtime, on a node | A dot in the corner of the tile of every node the manifest draws for the deployable (`deployable` of a node of `runtime/<env>.json`), also one the topology does not list, such as the dashboard's static site or a node an application recorded for itself. A dependency is not marked: nobody deploys it here. With more than one mark the first gives the shape, and the tooltip has all. |
| Runtime, the legend | The four shapes, only with `system.deploymentsUrl`. |

**How fresh.** Minutes, not seconds, and the tooltip of every list says so, with the time of `generated`: a
deployment that started shows within about one to six minutes and its end as soon, one that is only queued within
half an hour. The workflow runs at the pin of a deployment and stays while it is executing, and on a five-minute
schedule that GitHub starts when it has room (every twenty to thirty minutes on 2026-10-08),
`raw.githubusercontent.com` may serve the file a few minutes old, and the page reads it with the first round of
checks and then every 60 seconds, with the rounds: more often gains nothing. Like the other files it is read only
while the page checks (not while it is paused or its tab is hidden).

A reading that fails (no answer, HTTP 404 while the system does not publish the file yet, anything but the file)
marks nothing, and unlike the cost it does not keep the last good one: a deployment that was executing in a file the
page can no longer read would stay marked for ever. The next reading marks again. A mark is no state: it changes no
tile's state, no summary and no banner, and it is no event in "What just happened" (a web app that reports another
version is, see there).
