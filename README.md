# TpsReader for .NET

`TpsReader` is a simple, read-only .NET library for opening TopSpeed (`.TPS`)
files and reading their tables and records. `TpsReader.Tool` supplies the `tps`
command for schema discovery, filtering, JSON/JSONL output, and CSV export.

Both packages target .NET 8 and support .NET 8 or later. The tool automatically
rolls forward to a newer major runtime when .NET 8 is not installed. Version
0.3.0 is a breaking rename from `TpsParser` and `TpsInspector`; no compatibility
namespace or shim package is provided.

## Library usage

```powershell
dotnet add package TpsReader --version 0.3.6
```

### Basic usage

Open a file and iterate through its tables and records:

```csharp
using TpsReader;

var file = TpsFile.Open(@"C:\data\CUSTOMER.TPS");

foreach (var table in file.Tables)
{
    Console.WriteLine($"{table.Name}: {table.Records.Count} records");

    foreach (var record in table.Records)
    {
        var customerNumber = record.GetInt32("CUS:CUSTNUMBER");
        var company = record.GetString("COMPANY");

        Console.WriteLine($"{customerNumber}: {company}");
    }
}
```

### LINQ filtering and projection

`TpsTable.Records` is LINQ-ready, so consumers can use normal LINQ without a
package-specific query API:

```csharp
using TpsReader;

var file = TpsFile.Open(@"C:\data\CUSTOMER.TPS");
var customers = file.GetTable();

var companies = customers.Records
    .Where(record => record.GetString("STATE") == "AZ")
    .Select(record => new
    {
        Number = record.Get<int>("CUSTNUMBER"),
        Company = record.GetString("COMPANY")
    })
    .ToArray();
```

Use `GetTable(string)` for a case-insensitive table name or `GetTable(int)` for
a table number. Parameterless `GetTable()` succeeds only when the file contains
exactly one table.

Table names prefer meaningful TPS metadata. For an unnamed single-table file
opened from a path, the name is the source filename without its path or final
extension (`CUSTOMER.TPS` becomes `CUSTOMER`). Unnamed tables from streams,
byte arrays, and superfiles fall back to their field prefix, then their table
number.

Records support an indexer, generic conversion, and familiar typed helpers:

```csharp
var record = customers.Records[0];

var company = (string?)record["COMPANY"];
var customerNumber = record.Get<int>("CUSTNUMBER");
var phones = record.Get<string[]>("PHONES");

if (record.TryGet<decimal>("BALANCE", out var balance))
{
    Console.WriteLine(balance);
}
```

`GetValue`, the indexer, `Get<T>`, and `TryGet<T>` resolve ordinary fields,
MEMOs, and BLOBs through the same case-insensitive name rules. Full schema names
and unambiguous short names are accepted. BLOB and other byte-array results are
defensive copies.

Numeric `Get<T>` conversions are checked. A DECIMAL value converts exactly to
`decimal` when it is representable; otherwise it remains available losslessly
through `GetDecimalString`. Arbitrary strings are not parsed as numbers.
Ordinary fixed-width STRING values have trailing NUL and space padding removed.

### GROUP values

GROUP fields default to a fixed-width string projection. Only schema-declared
STRING, CSTRING, and PSTRING leaves are placed at their byte offsets; binary
members and gaps become spaces. Internal and trailing spaces are preserved.

```csharp
string groupText = record.Get<string>("ADDRESS_GROUP")!;
byte[] originalGroupBytes = record.Get<byte[]>("ADDRESS_GROUP")!;

string[] groupArray = record.Get<string[]>("LINES")!;
byte[][] originalArrayBytes = record.Get<byte[][]>("LINES")!;
```

GROUP width is measured in bytes. Array elements repeat the first element's
child layout, including nested string leaves and string arrays. Raw byte results
are cloned.

### Other inputs and options

`Open` and `TryOpen` accept a path, readable `Stream`, or complete `byte[]`.
Streams are consumed from their current position and are never disposed or
rewound by the library. Input arrays are treated as read-only.

```csharp
var encrypted = TpsFile.Open(
    @"C:\data\encrypted.tps",
    new TpsOpenOptions { Owner = "owner-password" });

if (!TpsFile.TryOpen(tpsBytes, out var parsed, out var error))
{
    Console.Error.WriteLine(error!.Message);
}
```

Set `IgnoreErrors = true` only for intentional partial recovery. A malformed
data page is discarded atomically; no partial record from that page is returned.
`StringEncoding` controls schema names, string fields, GROUP text, and MEMO text.

Use `OpenMetadata` with the same path, stream, or byte-array inputs when only
table definitions are needed:

```csharp
var metadata = TpsFile.OpenMetadata(@"C:\data\CUSTOMER.TPS");
Console.WriteLine(metadata.IsMetadataOnly);       // true
Console.WriteLine(metadata.GetTable().RecordLength);
```

Metadata-only tables have empty `Records` collections and do not scan record or
MEMO/BLOB content. `TpsFile.IsEncrypted` reports whether owner-based decryption
was actually used, and `RecoveryIssueCount` reports malformed block/page
incidents skipped during recovery.

Set `TpsOpenOptions.Progress` to receive byte-based `TpsReadProgress` updates for
source loading, decryption, definition scanning, and content scanning. Metadata-
only opens omit the content stage.

### Bounded-memory streaming

Use `OpenStreaming` when a file is too large to retain all records in memory.
The returned object owns its path handle and must be disposed. Its tables contain
schema only; call `ReadRecords` to decode one record at a time in file order:

```csharp
using var file = TpsFile.OpenStreaming(@"C:\data\CUSTOMER.TPS");
var table = file.GetTable();

foreach (var record in file.ReadRecords(table))
{
    Console.WriteLine(record.GetString("COMPANY"));
}

long exactCount = file.CountRecords(table); // scans only when not already known
```

`OpenStreaming` accepts paths, complete byte arrays, and readable seekable
streams. It does not accept forward-only streams such as the stream returned by
`ZipArchiveEntry.Open()`: extract or spool one TPS entry to a temporary file,
open that path, dispose the streaming file, and then delete the temporary file.
Processing archive entries sequentially keeps both memory and temporary storage
bounded.

Only one enumeration or count may be active for a `TpsStreamingFile`, although
sequential repeat scans are supported. Caller-provided streams remain open and
must not be accessed concurrently. Records already returned are self-contained
and remain usable after the streaming file is disposed. MEMO/BLOB payloads are
fully assembled for the current record; peak memory can therefore include the
largest current payload plus a lightweight fragment-location index.

Pass a `CancellationToken` to `ReadRecords` or `CountRecords` for cooperative
cancellation. Streaming progress uses `IndexingMemos`, `StreamingRecords`, and
`CountingRecords` stages. Errors found after enumeration begins are reported as
`TpsParseException`.

Schema objects preserve raw TPS details in addition to their existing logical
properties. Tables expose `RecordLength`; fields expose raw type, flags, index,
and string-mask metadata; MEMO/BLOB definitions expose declared length and
external name; and indexes expose flags, external name, and ordered components.
Each record exposes its `SourcePageOffset`. `GetMemoState` distinguishes absent,
complete, and damaged MEMO/BLOB values recovered with `IgnoreErrors`.

## Command-line tool

Install the tool package while keeping the short `tps` command:

```powershell
dotnet tool install --global TpsReader.Tool --version 0.3.6
tps --help
```

Each [GitHub release](https://github.com/CarlosGtrz/TpsReader/releases) also
includes self-contained native AOT archives:

- `tps-v<version>-win-x64.zip` contains `tps.exe` for 64-bit Windows.
- `tps-v<version>-linux-x64.zip` contains `tps` for 64-bit glibc-based Linux.
- `tps-v<version>-osx-arm64.zip` contains `tps` for Apple silicon macOS.

None of these executables require a separate .NET installation.

Build or run it from this repository:

```powershell
dotnet build TpsReader.sln -c Release
dotnet test TpsReader.sln -c Release
dotnet run --project src\TpsReader.Tool -c Release -- schema C:\data\CUSTOMER.TPS
```

To create local packages without publishing them:

```powershell
dotnet pack src\TpsReader -c Release -o artifacts\packages
dotnet pack src\TpsReader.Tool -c Release -o artifacts\packages
```

### Discover and read

Inspect structure before requesting records:

```powershell
tps inspect C:\data --recursive
tps schema C:\data\CUSTOMER.TPS
tps schema C:\data\CUSTOMER.TPS --table CUSTOMER
tps rows C:\data\CUSTOMER.TPS --table CUSTOMER --fields CUSTNUMBER,COMPANY --limit 20
```

`tps --version` prints the CLI package version as a machine-readable
`MAJOR.MINOR.PATCH` value.

`schema` and JSON row documents retain `formatVersion: 1`. `rows` returns at
most 100 records by default; use `--limit`, `--skip`, or explicit `--all`.
JSONL emits one versioned record per line.

### CLI filters

The library does not define a query model: application consumers use LINQ. The
tool keeps its dynamic field selection and `--where` compiler internal.
Repeated CLI predicates are combined with AND.

| Value type | Operators |
| --- | --- |
| Number, DECIMAL, DATE, TIME | `eq`, `ne`, `lt`, `le`, `gt`, `ge`, `is-null`, `is-not-null` |
| STRING, CSTRING, PSTRING, GROUP, MEMO | `eq`, `ne`, `contains`, `starts-with`, `ends-with`, `is-null`, `is-not-null` |
| BLOB | `is-null`, `is-not-null` |

```powershell
tps rows CUSTOMER.TPS --where STATE eq AZ --where CUSTNUMBER ge 100
tps rows DATA.TPS --where CONTACT_GROUP contains Smith
tps rows DATA.TPS --where GROUP_ARRAY[2] starts-with West
```

Use `@recordNumber` as a numeric pseudo-field. Array selectors are one-based.
Text matching is case-insensitive unless `--case-sensitive` is supplied. GROUP
operators ignore trailing NUL/space padding while preserving internal spacing.
DATE literals use `yyyy-MM-dd`; TIME accepts `HH:mm:ss`, `HH:mm:ss.f`, or
`HH:mm:ss.ff`. DECIMAL comparisons remain lossless.

JSON and JSONL emit GROUP projections as full fixed-width strings. DECIMAL
values remain strings, dates and times use ISO text, and BLOBs default to length
plus SHA-256 metadata. Add `--blob-mode base64` when complete BLOB content is
required.

### CSV export

```powershell
tps export CUSTOMER.TPS --output C:\export
tps export CUSTOMER.TPS --fields CUSTNUMBER,COMPANY --where STATE eq AZ --output C:\export
```

GROUP cells are always quoted and preserve their full width, including internal
and trailing spaces. Text MEMOs are columns. BLOBs are separate `.blob` files
referenced by their CSV cells. Writes are atomic and overwrite existing export
files; source TPS files are never modified.

### Encrypted and damaged files

Prefer an environment variable over placing an owner value in command history:

```powershell
$env:TPS_OWNER = 'secret'
tps rows encrypted.tps --owner-env TPS_OWNER --limit 10
```

Use `--ignore-errors` only when incomplete recovery is acceptable. Exit codes
are `0` for success, `1` for invalid path/arguments/query, and `2` for parsing or
export failures. Structured commands keep diagnostics on stderr.

## Agent skill

The repository includes the portable [`read-tps-files`](plugins/tpsreader/skills/read-tps-files/SKILL.md)
Agent Skill, packaged as the `tpsreader` plugin for Codex and Claude Code and as
`@carlosgtrz/tpsreader-agent-skill` for Pi.

```text
codex plugin marketplace add CarlosGtrz/TpsReader --sparse .agents/plugins --sparse plugins
codex plugin add tpsreader@tpsreader
```

```text
claude plugin marketplace add CarlosGtrz/TpsReader --sparse .claude-plugin plugins
claude plugin install tpsreader@tpsreader
```

```text
pi install npm:@carlosgtrz/tpsreader-agent-skill@1.0.2
```

See the [agent-skill package documentation](plugins/tpsreader/README.md) for
prerequisites, supported platforms, security behavior, examples, and update or
removal commands.

Maintainers can validate the exact release tarball and perform the interactive
npm/2FA publication with
[`scripts/publish-agent-skill.ps1`](scripts/publish-agent-skill.ps1). Run it
with `-WhatIf` first to complete every pre-publication check without logging in
or publishing.

```powershell
.\scripts\publish-agent-skill.ps1 -WhatIf
.\scripts\publish-agent-skill.ps1
```

## Migrating to 0.3.0

- Replace the `TpsParser` package, project, assembly, and namespace with
  `TpsReader`.
- Replace `TpsInspector` with `TpsReader.Tool`; the executable command remains
  `tps`.
- Keep existing `TpsFile`, `TpsTable`, `TpsRecord`, and other `Tps*` class names.
- Ordinary fixed-width STRING values are now returned without trailing padding.
- GROUP values now default to fixed-width text rather than raw bytes or JSON hex.
  Request `byte[]` or `byte[][]` explicitly when raw GROUP storage is needed.
- CLI JSON keeps `formatVersion: 1` despite the deliberate GROUP row-value
  semantic change.

## Attribution and license

This parser adapts logic from
[`ctrl-alt-dev/tps-parse`](https://github.com/ctrl-alt-dev/tps-parse), copyright
2012-2021 Erik Hooijmeijer, under the Apache License 2.0. See
[`Apache-2.0.txt`](Apache-2.0.txt).

TPS parsing is based on reverse engineering and may be incomplete. Verify
output before relying on it for critical work.
