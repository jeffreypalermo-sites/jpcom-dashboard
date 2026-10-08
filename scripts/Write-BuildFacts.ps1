#Requires -Version 7.4

<#
.SYNOPSIS
  Writes build-facts.json: what the dashboard answers at /build-facts.json about the build it runs.

.DESCRIPTION
  The Build workflow calls this after it has published the site and written version.json, and the file lands next to
  index.html, in the artifact dashboard-site: the zip release and the image release both carry it. A dashboard reads
  it as it reads an app's /_build (buildPath of the topology) and shows it as the "Code" card.

  Facts and where they come from:
    version     the parameter (the Build passes MAJOR.MINOR.run_number, the number version.json holds)
    commit, commitUrl, buildUrl   the parameters, then the environment of GitHub Actions (GITHUB_SHA,
                GITHUB_SERVER_URL, GITHUB_REPOSITORY, GITHUB_RUN_ID); null without them, as in a local run
    builtAt     the parameter, then now
    code        tracked source files of this checkout (git ls-files), non-blank lines per language
    tests       trx files of the test run (dotnet test --logger trx): the tests that passed, as "unit". The
                dashboard has unit tests only, so the file names no other kind
    coverage    Cobertura files of the same run (--collect:"XPlat Code Coverage"), merged per line
    complexity  the complexity attribute coverlet writes on every method of those Cobertura files
    crap        null: the build has no CRAP report and no threshold to hold a score against
    analysis    null: the build does not run Qodana

  Coverage and complexity leave out what the build generates (files under obj/, *.g.cs), as the lines of code do.

  Every section is optional: an input that is missing or unreadable makes its section null, with a SKIP line that
  says why, and the script goes on. It fails only when the checkout is not a Git repository or the file cannot be
  written.

.PARAMETER OutputPath
  The file to write. Default: publish/wwwroot/build-facts.json under RepoRoot. Its folder must exist: the site is
  published first.

.PARAMETER RepoRoot
  The checkout to count the code of. Default: the parent directory of this script's directory.

.PARAMETER Version
  The version of the build (MAJOR.MINOR.run_number). Null without one.

.PARAMETER Commit
  The full SHA the build was made from. Default: $env:GITHUB_SHA.

.PARAMETER ServerUrl
  The address of GitHub, for the links. Default: $env:GITHUB_SERVER_URL, then https://github.com.

.PARAMETER Repository
  owner/name on GitHub, for the links. Default: $env:GITHUB_REPOSITORY.

.PARAMETER RunId
  The ID of the Build workflow run, for buildUrl. Default: $env:GITHUB_RUN_ID.

.PARAMETER BuiltAt
  When the build finished (any format [datetimeoffset] parses). Default: now.

.PARAMETER TestResultsPath
  The directory with the test run's trx files, at any depth. Default: TestResults under RepoRoot.

.PARAMETER CoveragePath
  The directory with the test run's Cobertura files (*.cobertura.xml), at any depth. Default: TestResultsPath.

.EXAMPLE
  pwsh -NoProfile -File scripts/Write-BuildFacts.ps1
  The facts of the working copy, after dotnet publish: the code, and the tests and coverage when TestResults has them.

.EXAMPLE
  pwsh -NoProfile -File scripts/Write-BuildFacts.ps1 -Version 1.0.42 -TestResultsPath TestResults -OutputPath publish/wwwroot/build-facts.json
  What the Build workflow runs.
#>
[CmdletBinding()]
param(
    [string]$OutputPath = '',
    [string]$RepoRoot = '',
    [string]$Version = '',
    [string]$Commit = '',
    [string]$ServerUrl = '',
    [string]$Repository = '',
    [string]$RunId = '',
    [string]$BuiltAt = '',
    [string]$TestResultsPath = '',
    [string]$CoveragePath = ''
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $true

# Languages counted as code, by file extension.
$languageByExtension = @{
    '.cs' = 'C#'; '.csx' = 'C#'
    '.razor' = 'Razor'; '.cshtml' = 'Razor'
    '.ts' = 'TypeScript'; '.tsx' = 'TypeScript'
    '.js' = 'JavaScript'; '.jsx' = 'JavaScript'; '.mjs' = 'JavaScript'; '.cjs' = 'JavaScript'
    '.css' = 'CSS'; '.scss' = 'CSS'; '.sass' = 'CSS'; '.less' = 'CSS'
    '.html' = 'HTML'; '.htm' = 'HTML'
    '.sql' = 'SQL'
    '.ps1' = 'PowerShell'; '.psm1' = 'PowerShell'; '.psd1' = 'PowerShell'
    '.sh' = 'Shell'; '.bash' = 'Shell'
    '.py' = 'Python'
    '.yml' = 'YAML'; '.yaml' = 'YAML'
    '.bicep' = 'Bicep'
    '.md' = 'Markdown'
}

# Not written by hand: build output, packages, vendored libraries, minified and generated files.
$generatedOrVendored = [regex]::new(
    '(^|/)(bin|obj|node_modules|generated)/|(^|/)wwwroot/lib/|\.min\.[^/]+$|\.designer\.cs$|\.g(\.i)?\.cs$|modelsnapshot\.cs$',
    [System.Text.RegularExpressions.RegexOptions]'IgnoreCase, CultureInvariant')

# One match per line that has anything but white space.
$nonBlankLine = [regex]::new('(?m)^[^\S\n]*\S', [System.Text.RegularExpressions.RegexOptions]::CultureInvariant)

# A line of the log, for the person who reads the step's output; the facts themselves go to the file.
function Write-LogLine {
    param([string]$Message)

    Write-Information -MessageData $Message -InformationAction Continue
}

function Read-XmlFile {
    param([string]$Path)

    # No DTD is fetched or expanded, whatever the file declares.
    $settings = [System.Xml.XmlReaderSettings]::new()
    $settings.DtdProcessing = [System.Xml.DtdProcessing]::Ignore
    $settings.XmlResolver = $null
    $reader = [System.Xml.XmlReader]::Create($Path, $settings)
    try {
        $document = [System.Xml.XmlDocument]::new()
        $document.XmlResolver = $null
        $document.Load($reader)
        return $document
    }
    finally {
        $reader.Dispose()
    }
}

function Get-Number {
    param([string]$Text)

    return [double]::Parse($Text, [System.Globalization.CultureInfo]::InvariantCulture)
}

function Get-CodeFact {
    param([string]$Root)

    $tracked = git -C $Root -c core.quotepath=false ls-files
    $byLanguage = @{}
    foreach ($relativePath in $tracked) {
        if ($generatedOrVendored.IsMatch($relativePath)) { continue }
        $language = $languageByExtension[[System.IO.Path]::GetExtension($relativePath)]
        if (-not $language) { continue }
        $path = Join-Path $Root $relativePath
        if (-not (Test-Path -LiteralPath $path -PathType Leaf)) { continue }

        if (-not $byLanguage.ContainsKey($language)) { $byLanguage[$language] = @{ lines = 0; files = 0 } }
        $byLanguage[$language].lines += $nonBlankLine.Matches([System.IO.File]::ReadAllText($path)).Count
        $byLanguage[$language].files += 1
    }

    if ($byLanguage.Count -eq 0) { return $null }

    # Largest first; the name decides between equals, so the order never depends on the machine.
    $languages = @(
        $byLanguage.GetEnumerator() |
            Sort-Object -Property @{ Expression = { $_.Value.lines }; Descending = $true }, @{ Expression = { $_.Key } } |
            ForEach-Object { [ordered]@{ name = $_.Key; lines = $_.Value.lines; files = $_.Value.files } }
    )
    return [ordered]@{
        linesOfCode = [int]($languages | ForEach-Object { $_.lines } | Measure-Object -Sum).Sum
        files       = [int]($languages | ForEach-Object { $_.files } | Measure-Object -Sum).Sum
        languages   = $languages
    }
}

function Read-TestResultFile {
    param([System.IO.FileInfo]$File)

    $document = Read-XmlFile -Path $File.FullName
    $run = "/*[local-name()='TestRun']"
    $counters = $document.SelectSingleNode("$run/*[local-name()='ResultSummary']/*[local-name()='Counters']")
    if ($null -eq $counters) { return $null }

    # The assembly the tests ran from names the run: a folder that holds two runs of it counts the later one.
    $testMethod = $document.SelectSingleNode("//*[local-name()='UnitTest']/*[local-name()='TestMethod']")
    $assembly = if ($null -ne $testMethod) { [System.IO.Path]::GetFileName($testMethod.GetAttribute('codeBase')) } else { '' }
    $times = $document.SelectSingleNode("$run/*[local-name()='Times']")
    $finished = [datetimeoffset]$File.LastWriteTimeUtc
    if ($null -ne $times -and $times.GetAttribute('finish')) {
        $finished = [datetimeoffset]::Parse($times.GetAttribute('finish'), [System.Globalization.CultureInfo]::InvariantCulture)
    }

    return @{ assembly = $assembly; finished = $finished; passed = [int]$counters.GetAttribute('passed') }
}

function Get-TestFact {
    param([string]$Directory)

    if (-not (Test-Path -LiteralPath $Directory -PathType Container)) { return $null }

    $latest = @{}
    foreach ($file in Get-ChildItem -LiteralPath $Directory -Recurse -File -Filter '*.trx' | Sort-Object -Property FullName) {
        $result = Read-TestResultFile -File $file
        if ($null -eq $result) { continue }
        if (-not $latest.ContainsKey($result.assembly) -or $result.finished -gt $latest[$result.assembly].finished) {
            $latest[$result.assembly] = $result
        }
    }

    if ($latest.Count -eq 0) { return $null }

    # The dashboard's tests are unit tests, all of them: no browser, no network, no database. The kinds an app
    # also has (integration, acceptance) are left out, not zero: the card then says "<n> unit" and claims no more.
    return [ordered]@{
        unit = [int]($latest.Values | ForEach-Object { $_.passed } | Measure-Object -Sum).Sum
    }
}

function Read-CoberturaFile {
    param([string]$Directory)

    if (-not (Test-Path -LiteralPath $Directory -PathType Container)) { return $null }
    $files = @(Get-ChildItem -LiteralPath $Directory -Recurse -File -Filter '*.cobertura.xml' | Sort-Object -Property FullName)
    if ($files.Count -eq 0) { return $null }

    # The test run leaves the same report twice (the collector's file and the copy attached to the trx), and a
    # second test project would instrument the same assembly again. A line counts once, covered when any report
    # hit it; of a line's branches, as many count as the report that covered most of them.
    $lineHits = [System.Collections.Generic.Dictionary[string, bool]]::new()
    $branchesCovered = [System.Collections.Generic.Dictionary[string, int]]::new()
    $branchesTotal = [System.Collections.Generic.Dictionary[string, int]]::new()
    $complexity = [System.Collections.Generic.Dictionary[string, int]]::new()
    $conditions = [regex]::new('\((\d+)/(\d+)\)')

    foreach ($file in $files) {
        $document = Read-XmlFile -Path $file.FullName
        foreach ($class in $document.SelectNodes('//class')) {
            # What the build generates (the JSON source generator's files under obj/) is nobody's code to test.
            $fileName = $class.GetAttribute('filename').Replace('\', '/')
            if ($generatedOrVendored.IsMatch($fileName)) { continue }
            $classKey = "$fileName|$($class.GetAttribute('name'))"

            foreach ($line in $class.SelectNodes('lines/line')) {
                $key = "$classKey|$($line.GetAttribute('number'))"
                $hit = $line.GetAttribute('hits') -ne '0'
                $known = $false
                if (-not $lineHits.TryGetValue($key, [ref]$known) -or ($hit -and -not $known)) { $lineHits[$key] = $hit }

                $match = $conditions.Match($line.GetAttribute('condition-coverage'))
                if (-not $match.Success) { continue }
                $covered = [int]$match.Groups[1].Value
                $total = [int]$match.Groups[2].Value
                $knownCount = 0
                if (-not $branchesCovered.TryGetValue($key, [ref]$knownCount) -or $covered -gt $knownCount) { $branchesCovered[$key] = $covered }
                if (-not $branchesTotal.TryGetValue($key, [ref]$knownCount) -or $total -gt $knownCount) { $branchesTotal[$key] = $total }
            }

            foreach ($method in $class.SelectNodes('methods/method')) {
                $value = $method.GetAttribute('complexity')
                if (-not $value) { continue }
                $complexity["$classKey|$($method.GetAttribute('name'))|$($method.GetAttribute('signature'))"] = [int](Get-Number -Text $value)
            }
        }
    }

    return @{
        lines           = $lineHits.Count
        linesCovered    = @($lineHits.Values | Where-Object { $_ }).Count
        branches        = [int]($branchesTotal.Values | Measure-Object -Sum).Sum
        branchesCovered = [int]($branchesCovered.Values | Measure-Object -Sum).Sum
        complexity      = @($complexity.Values)
    }
}

function Get-CoverageFact {
    param($Cobertura)

    if ($null -eq $Cobertura -or $Cobertura.lines -eq 0) { return $null }
    return [ordered]@{
        linePercent   = [Math]::Round(100.0 * $Cobertura.linesCovered / $Cobertura.lines, 1)
        branchPercent = if ($Cobertura.branches -gt 0) { [Math]::Round(100.0 * $Cobertura.branchesCovered / $Cobertura.branches, 1) } else { $null }
    }
}

function Get-ComplexityFact {
    param($Cobertura)

    if ($null -eq $Cobertura -or $Cobertura.complexity.Count -eq 0) { return $null }
    $measured = $Cobertura.complexity | Measure-Object -Average -Maximum
    return [ordered]@{
        average = [Math]::Round([double]$measured.Average, 1)
        max     = [int]$measured.Maximum
        methods = [int]$measured.Count
    }
}

# A section without its input is null in the file and a SKIP line in the log that says why; the build goes on.
function Get-Section {
    param([string]$Name, [string]$Missing, [scriptblock]$Read)

    try {
        $value = & $Read
    }
    catch {
        if ($env:GITHUB_ACTIONS -eq 'true') {
            Write-LogLine "::warning title=Build facts::The section '$Name' could not be read: $($_.Exception.Message)"
        }

        Write-LogLine "SKIP ${Name}: $($_.Exception.Message)"
        return $null
    }

    if ($null -eq $value) {
        Write-LogLine "SKIP ${Name}: $Missing"
        return $null
    }

    Write-LogLine "PASS $Name"
    return $value
}

# The same for a fact that is one text: null and a SKIP line when it is empty.
function Get-Text {
    param([string]$Name, [string]$Missing, [string]$Value)

    if (-not $Value) {
        Write-LogLine "SKIP ${Name}: $Missing"
        return $null
    }

    Write-LogLine "PASS $Name"
    return $Value
}

Write-LogLine '==> Build facts'

$RepoRoot = if ($RepoRoot) { (Resolve-Path -LiteralPath $RepoRoot).Path } else { (Resolve-Path -LiteralPath (Join-Path $PSScriptRoot '..')).Path }
if (-not $Commit) { $Commit = $env:GITHUB_SHA }
if (-not $ServerUrl) { $ServerUrl = if ($env:GITHUB_SERVER_URL) { $env:GITHUB_SERVER_URL } else { 'https://github.com' } }
if (-not $Repository) { $Repository = $env:GITHUB_REPOSITORY }
if (-not $RunId) { $RunId = $env:GITHUB_RUN_ID }

$resolve = { param([string]$Path) $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($Path) }
$OutputPath = if ($OutputPath) { & $resolve $OutputPath } else { Join-Path -Path $RepoRoot -ChildPath 'publish/wwwroot/build-facts.json' }
$TestResultsPath = if ($TestResultsPath) { & $resolve $TestResultsPath } else { Join-Path $RepoRoot 'TestResults' }
$CoveragePath = if ($CoveragePath) { & $resolve $CoveragePath } else { $TestResultsPath }

# The code is counted over the files Git tracks: without a repository there is nothing to say what the code is.
$PSNativeCommandUseErrorActionPreference = $false
$inside = if (Get-Command git -ErrorAction SilentlyContinue) { git -C $RepoRoot rev-parse --is-inside-work-tree 2>$null }
$PSNativeCommandUseErrorActionPreference = $true
if ("$inside".Trim() -ne 'true') {
    Write-LogLine "FAIL $RepoRoot is not a checkout of a Git repository (or git is not installed)"
    exit 1
}

$outputDirectory = Split-Path -Parent $OutputPath
if (-not (Test-Path -LiteralPath $outputDirectory -PathType Container)) {
    Write-LogLine "FAIL the folder $outputDirectory does not exist: publish the site first (dotnet publish src/Dashboard -c Release -o publish)"
    exit 1
}

$builtAtTime = if ($BuiltAt) {
    [datetimeoffset]::Parse($BuiltAt, [System.Globalization.CultureInfo]::InvariantCulture, [System.Globalization.DateTimeStyles]::AssumeUniversal)
}
else {
    [datetimeoffset]::UtcNow
}

Write-LogLine '==> Sections'
$github = if ($Repository) { "$($ServerUrl.TrimEnd('/'))/$Repository" } else { '' }
$commitUrl = if ($github -and $Commit) { "$github/commit/$Commit" } else { '' }
$buildUrl = if ($github -and $RunId) { "$github/actions/runs/$RunId" } else { '' }
$cobertura = Get-Section -Name 'coverage reports' -Missing "no *.cobertura.xml under $CoveragePath (dotnet test --collect:`"XPlat Code Coverage`" writes it)" -Read {
    Read-CoberturaFile -Directory $CoveragePath
}
$facts = [ordered]@{
    version    = Get-Text -Name 'version' -Missing 'no -Version' -Value $Version
    commit     = Get-Text -Name 'commit' -Missing 'no -Commit and no GITHUB_SHA' -Value $Commit
    commitUrl  = Get-Text -Name 'commitUrl' -Missing 'no commit, or no -Repository and no GITHUB_REPOSITORY' -Value $commitUrl
    builtAt    = $builtAtTime.UtcDateTime.ToString('yyyy-MM-ddTHH:mm:ssZ', [System.Globalization.CultureInfo]::InvariantCulture)
    buildUrl   = Get-Text -Name 'buildUrl' -Missing 'no -RunId and no GITHUB_RUN_ID, or no -Repository and no GITHUB_REPOSITORY' -Value $buildUrl
    code       = Get-Section -Name 'code' -Missing "no tracked file of a counted language under $RepoRoot" -Read { Get-CodeFact -Root $RepoRoot }
    tests      = Get-Section -Name 'tests' -Missing "no *.trx under $TestResultsPath (dotnet test --logger trx writes it)" -Read {
        Get-TestFact -Directory $TestResultsPath
    }
    coverage   = Get-Section -Name 'coverage' -Missing 'no coverage report' -Read { Get-CoverageFact -Cobertura $cobertura }
    complexity = Get-Section -Name 'complexity' -Missing 'no coverage report: coverlet writes the complexity of every method there' -Read {
        Get-ComplexityFact -Cobertura $cobertura
    }
    crap       = $null
    analysis   = $null
}
Write-LogLine 'SKIP crap: the build has no CRAP report and no threshold'
Write-LogLine 'SKIP analysis: the build does not run Qodana'

$json = $facts | ConvertTo-Json -Depth 8
try {
    [System.IO.File]::WriteAllText($OutputPath, "$json`n", [System.Text.UTF8Encoding]::new($false))
}
catch {
    Write-LogLine "FAIL $OutputPath could not be written: $($_.Exception.Message)"
    exit 1
}

Write-LogLine "==> $OutputPath"
Write-LogLine $json
