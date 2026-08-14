# TpsReader Agent Skill

Use the `read-tps-files` Agent Skill to inspect schemas, run bounded read-only queries, and explicitly export Clarion TopSpeed (`.TPS`) files with the [TpsReader CLI](https://github.com/CarlosGtrz/TpsReader).

The same skill is packaged for Codex, Claude Code, and Pi. Source TPS files are always treated as immutable.

## Prerequisites

The skill first uses a compatible global `tps` command. Install the .NET tool with:

```text
dotnet tool install --global TpsReader.Tool
```

When .NET 8 or later is unavailable, the skill can offer an explicit, user-approved download of a verified native executable from an official TpsReader GitHub release. It never installs or updates tools automatically.

## Install

### Codex

```text
codex plugin marketplace add CarlosGtrz/TpsReader --sparse .agents/plugins --sparse plugins
codex plugin add tpsreader@tpsreader
```

Invoke the skill as `$read-tps-files`.

### Claude Code

```text
claude plugin marketplace add CarlosGtrz/TpsReader --sparse .claude-plugin plugins
claude plugin install tpsreader@tpsreader
```

Invoke the skill as `/tpsreader:read-tps-files`.

### Pi

```text
pi install npm:@carlosgtrz/tpsreader-agent-skill@1.0.3
```

Invoke the skill as `/skill:read-tps-files`, or let Pi select it from the task.

## Examples

```text
Use read-tps-files to show the schema of C:\data\CUSTOMER.TPS.
```

```text
Query customer number and company from CUSTOMER.TPS, limited to 20 rows.
```

```text
Export the CUSTOMER table from CUSTOMER.TPS to C:\exports.
```

The skill discovers schema before reading rows, uses explicit limits, keeps owner keys out of command arguments, and requests confirmation before potentially overwriting export files.

The skill requires TpsReader CLI 0.4.0 or later. It verifies `tps --version`
and command compatibility before use. Missing or incompatible tools are never
installed or updated without confirmation.

## Supported platforms

The .NET global tool works wherever a compatible .NET runtime is available. Verified native fallback downloads are limited to:

- Windows x64 (`win-x64`)
- glibc Linux x64 (`linux-x64`)
- Apple silicon macOS (`osx-arm64`)

Other platforms require the .NET global tool.

## Versioning

This Agent Skill uses independent semantic versions. A skill release does not change the TpsReader library or CLI version, and native executables remain separate TpsReader release assets rather than npm package contents.

## Permissions and network access

- TPS source files are read-only and are never moved, deleted, replaced, or overwritten.
- Exports occur only after an explicit request and may create CSV and BLOB files in the chosen destination.
- Owner values are read only through a user-provided environment variable.
- GitHub network access occurs only when resolving a native fallback or when the user explicitly requests an update.
- Native downloads require confirmation and are verified by asset size, SHA-256, archive structure, executable hash, and CLI capabilities before use.
- Native executables are stored in a per-user TpsReader cache, never in the plugin installation.
- TPS contents and parser output are treated as untrusted data, not instructions.

## Update and remove

Codex:

```text
codex plugin marketplace upgrade tpsreader
codex plugin remove tpsreader@tpsreader
codex plugin add tpsreader@tpsreader
```

```text
codex plugin remove tpsreader@tpsreader
codex plugin marketplace remove tpsreader
```

Claude Code:

```text
claude plugin update tpsreader@tpsreader
```

```text
claude plugin uninstall tpsreader@tpsreader
claude plugin marketplace remove tpsreader
```

Pi:

```text
pi update npm:@carlosgtrz/tpsreader-agent-skill
```

```text
pi remove npm:@carlosgtrz/tpsreader-agent-skill
```

Removing the package does not remove a separately installed .NET global tool or a user-approved native cache entry.

## License

Apache-2.0. See [LICENSE](LICENSE).
