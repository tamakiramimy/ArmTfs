# arm-tfs

A cross-platform .NET 8 CLI tool for TFVC (Team Foundation Version Control) operations, designed to run natively on **ARM64 macOS** (Apple Silicon / M-series) and **Windows 11 ARM** — where the official `tf.exe` is unavailable.

## Why

### The `tf.exe` problem

Microsoft's `tf.exe` is an x86/x64 binary only. It does not run on:

- macOS (any architecture)
- Windows 11 ARM (no Rosetta-style emulation for TFVC tooling)
- Linux

### Why TEE-CLC also doesn't work on ARM64

[JetBrains TEE-CLC](https://github.com/microsoft/team-explorer-everywhere) is a Java-based cross-platform TF client that looks like a promising alternative — but it fails on ARM64 for a deeper reason:

**The shell script (`tf`) has no `aarch64` mapping:**

```bash
# TEE-CLC tf script architecture detection (simplified)
case "`uname -m`" in
  i386|i686)  ARCH="x86"   ;;
  x86_64)     ARCH="x86_64" ;;
  ppc64)      ARCH="ppc"    ;;
  ia64)       ARCH="ia64_32" ;;
  # aarch64 → NOT HANDLED → JNI native library lookup fails
esac
```

ARM64 Linux reports `aarch64` from `uname -m`, which has no mapping — the `ARCH` variable keeps the raw value and the JNI native library directory is not found. **macOS Apple Silicon is worse**: the Darwin branch hard-codes `ARCH=""` (written when macOS was Intel-only).

**The real blocker: `com.microsoft.tfs.jni.jar` has no ARM64 native libraries.**

TEE-CLC relies on JNI (Java Native Interface) for critical features: NTLM authentication, credential storage, and file locking. The bundled native `.so`/`.dylib`/`.dll` files only exist for x86/x86_64/PPC/IA64. No ARM64 build exists and the project is archived (no longer maintained).

**Windows 11 ARM special case**: if you run an x64 JDK (via WoW64 emulation), the x64 JNI `.dll` may load. However:
- Native ARM64 JDK → JNI load fails with `UnsatisfiedLinkError`
- Even with x64 JDK, NTLM authentication via JNI may fail silently

### The arm-tfs approach

`arm-tfs` uses cross-platform **TFS/Azure DevOps REST APIs** for regular item/history/workspace operations and the legacy **TFVC SOAP Repository.asmx protocol** for operations REST cannot model correctly, such as branch, merge, label, lock, and shelveset workflows. It still uses no JNI, no native TF client, no COM, and no P/Invoke, so it runs anywhere .NET 8 runs.

## Requirements

- .NET 8 SDK or Runtime
- A TFS 2015+ or Azure DevOps Server with REST API enabled
- A Personal Access Token (PAT) — strongly recommended

## Installation

### From source

```bash
git clone https://github.com/yourname/arm-tfs
cd arm-tfs
dotnet pack src/ArmTfs.Cli/ArmTfs.Cli.csproj -c Release
dotnet tool install --global --add-source src/ArmTfs.Cli/bin/Release arm-tfs
```

### Run without installing

```bash
cd src/ArmTfs.Cli
dotnet run -- <command> [options]
```

## Authentication

PAT (Personal Access Token) is the recommended method and works on all platforms.

```bash
arm-tfs configure --url https://tfs.example.com/DefaultCollection --pat YOUR_TOKEN
```

Username/password (Basic Auth) also works if your server supports it:

```bash
arm-tfs configure --url https://tfs.example.com/DefaultCollection \
                  --username DOMAIN\user --password yourpassword
```

Credentials are saved to `~/.arm-tfs/config.json`.  
Environment variables override the saved config at runtime:

| Variable         | Description           |
|------------------|-----------------------|
| `ARM_TFS_URL`    | Server collection URL |
| `ARM_TFS_PAT`    | Personal Access Token |
| `ARM_TFS_USER`   | Username              |
| `ARM_TFS_PASSWORD` | Password            |

## Quick Start

```bash
# 1. Configure server and credentials
arm-tfs configure --url https://tfs.example.com/DefaultCollection --pat TOKEN

# 2. Create a workspace and map a server folder to a local folder
arm-tfs workspace new --name MyWorkspace
arm-tfs workspace map --server "$/MyProject/Main" --local ~/code/myproject

# 3. Get the latest files
cd ~/code/myproject
arm-tfs get

# 4. Edit a file and mark it checked out
arm-tfs checkout MyFile.cs

# 5. Check in
arm-tfs checkin -c "Fix bug in MyFile"
```

## Commands

### `configure`

Configure the TFS server connection.

```
arm-tfs configure [options]

Options:
  --url <url>            Server collection URL (e.g. https://tfs/DefaultCollection)
  --pat <token>          Personal Access Token
  --username <user>      Username (DOMAIN\user or user@domain)
  --password <pass>      Password
  --display-name <name>  Your display name (for commit attribution)
  --show                 Print the current configuration (PAT is masked)
```

---

### `workspace`

Manage local workspaces. Workspace metadata is stored in `.tf/workspace.json`.

```
arm-tfs workspace new [options]
  --name <name>          Workspace name (default: machine hostname)
  --server <url>         Override server URL for this workspace

arm-tfs workspace show
  (prints workspace metadata from the nearest .tf/ directory)

arm-tfs workspace map --server <serverPath> --local <localPath>
  (adds a server→local path mapping)
```

---

### `get`

Download files from the server.

```
arm-tfs get [path] [options]

Arguments:
  path                   Local path or server path ($/...) [default: .]

Options:
  --version/-v <n>       Download a specific changeset version
  --force/-f             Overwrite even if local file is up-to-date
  --recursive/-r         Get files recursively [default: true]
  --dry-run              Show what would be downloaded, without writing
```

---

### `status`

Show pending changes and locally modified tracked files.

```
arm-tfs status [path] [options]

Options:
  --all                  Show all tracked files, not just modified ones
```

---

### `checkout` / `co` / `edit`

Mark files as checked out (Edit). This is a **local-only** operation — no server lock is acquired.

```
arm-tfs checkout <paths...> [options]

Options:
  --recursive/-r         Include files in subdirectories
```

---

### `add`

Mark new files as pending Add.

```
arm-tfs add <paths...> [options]

Options:
  --recursive/-r         Include files in subdirectories
```

---

### `undo`

Undo pending changes and optionally restore file contents from the server.

```
arm-tfs undo <paths...> [options]

Arguments:
  paths                  Files to undo, or '.' to undo all pending changes

Options:
  --no-restore           Remove from pending list without restoring file
```

---

### `checkin` / `ci`

Check in pending changes to the server.

```
arm-tfs checkin [paths...] [options]

Options:
  -c, --comment <text>   Changeset comment (required)
  --dry-run              Show what would be checked in without submitting
  --keep-pending         Keep pending changes after a dry-run
```

---

### `history` / `hist`

Show changeset history for a path.

```
arm-tfs history [path] [options]

Options:
  --top/-n <n>           Maximum number of changesets [default: 20]
  --author/-u <name>     Filter by author display name
  --format <fmt>         Output format: table | json [default: table]
```

---

### `shelveset` / `shelve`

List or inspect shelvesets.

```
arm-tfs shelveset list [options]
  --owner <name>         Filter by owner
  --name <name>          Filter by shelveset name

arm-tfs shelveset show <name>
  (name can be "shelvesetname;owner" to specify an owner)
```

### `diff`

Compare a local file with the latest, tracked base, or a specific changeset.

```
arm-tfs diff <path> [options]

Options:
  --base                 Compare against the tracked base version
  --version <n>          Compare against a specific changeset
  --format <fmt>         Output format: text | json [default: text]
```

### `branch`

Inspect TFVC branch topology.

```
arm-tfs branch list [scope] [options]
arm-tfs branch show <path> [options]

Options:
  --format <fmt>         Output format: table | json [default: table]
```

### `changeset`

Inspect a single changeset including file-level detail.

```
arm-tfs changeset show <id> [options]

Options:
  --format <fmt>         Output format: table | json [default: table]
```

### `label`

List or inspect TFVC labels.

```
arm-tfs label list [options]
arm-tfs label show <id> [options]

Options:
  --format <fmt>         Output format: table | json [default: table]
```

### `merge`

Merge analysis and execution built from branch ancestry plus TFVC history. Execution uses TFVC
SOAP `Repository.asmx` by default so the server records real merge history.

```
arm-tfs merge base --source <path> --target <path> [options]
arm-tfs merge candidate --source <path> --target <path> [options]
arm-tfs merge execute --source <path> --target <path> --changeset <id> [options]
arm-tfs merge execute --source <path> --target <path> --from <id> --to <id> [options]
arm-tfs merge preview-conflicts --source <path> --target <path> --from <id> --to <id> [options]

Options:
  --top <n>              Maximum candidate changesets to return [default: 20]
  --scan <n>             Source/target history window to scan [default: 80]
  --dry-run              Build a merge plan without checking in
  --resolution-file <f>  Per-file source/target/manual conflict resolutions
  --mode <mode>          Merge protocol: soap | rest [default: soap]
  --format <fmt>         Output format: table | json [default: table]
```

`merge candidate` output is inferential: it uses branch ancestry, branch creation-time history,
and merge source ranges found in target changesets. `merge execute --from/--to --dry-run` asks
the server for one SOAP merge plan across the whole range, so all server-detected conflicts are
listed up front instead of being discovered one changeset at a time.

Conflict resolution behavior:

- `source` uses SOAP `Resolve(AcceptTheirs)` and keeps TFVC merge history.
- `target` uses SOAP `Resolve(AcceptYours)` and keeps TFVC merge history when the server returns
  merge operations to check in.
- `manual` uses the supplied merged file content and falls back to a REST content check-in because
  SOAP `Resolve(AcceptMerge)` expects a real local workspace file, not raw file bytes.

## JSON Contracts

The CLI now exposes stable JSON envelopes for thin clients and automation.

- `arm-tfs status . --all --format json`
- `arm-tfs history . --top 20 --format json`
- `arm-tfs diff <path> --format json`
- `arm-tfs branch list "$/' --format json`
- `arm-tfs branch show <path> --format json`
- `arm-tfs changeset show <id> --format json`
- `arm-tfs label list --format json`
- `arm-tfs label show <id> --format json`
- `arm-tfs merge base --source <path> --target <path> --format json`
- `arm-tfs merge candidate --source <path> --target <path> --format json`

Each response contains `schemaVersion` and `command` as a stable outer envelope.

## VS Code Thin Adapter

A minimal VS Code extension now lives in [src/ArmTfs.VsCode](src/ArmTfs.VsCode). It does not reimplement TFVC logic; it only:

- resolves an `arm-tfs` CLI invocation
- executes CLI commands with `--format json`
- parses the stable envelopes into typed TypeScript contracts
- exposes commands and an exported client API for tree views/webviews
- uses VS Code's native diff editor for source-vs-target comparisons
- uses VS Code's native conflict marker editor for manual merge resolution

Build it with:

```bash
cd src/ArmTfs.VsCode
npm install
npm run compile
```

## Local Workspace Model

`arm-tfs` uses a **local workspace** model:

- No server-side locks are acquired on checkout.
- Workspace metadata is stored in a `.tf/` directory at the workspace root:
  - `.tf/workspace.json` — workspace definition and path mappings
  - `.tf/pending.json` — pending changes (add / edit / delete / rename)
  - `.tf/versions/<hash>.json` — per-file version tracking (changeset ID + content hash)
- Files are identified as modified by comparing their SHA-256 hash against the stored hash at the time of last `get`.

## Building from Source

```bash
git clone https://github.com/yourname/arm-tfs
cd arm-tfs
dotnet build
dotnet test
```

To publish a self-contained single-file binary for ARM64 macOS:

```bash
dotnet publish src/ArmTfs.Cli -c Release -r osx-arm64 --self-contained true -p:PublishSingleFile=true -o publish/osx-arm64
```

For Windows ARM64:

```bash
dotnet publish src/ArmTfs.Cli -c Release -r win-arm64 --self-contained true -p:PublishSingleFile=true -o publish/win-arm64
```

## Architecture

```
arm-tfs.sln
├── src/
│   ├── ArmTfs.Core/          # TFVC REST/SOAP clients, workspace management, models
│   │   ├── Client/
│   │   │   ├── TfsConnection.cs       # VssConnection wrapper (PAT auth)
│   │   │   └── TfvcClientService.cs   # TFVC operations via TfvcHttpClient
│   │   ├── Config/
│   │   │   └── TfsConfig.cs           # ~/.arm-tfs/config.json
│   │   ├── Models/
│   │   │   ├── PendingChange.cs
│   │   │   ├── MergeQueryModels.cs
│   │   │   ├── TfsServerItem.cs
│   │   │   ├── TrackedFileVersion.cs
│   │   │   └── WorkspaceMetadata.cs
│   │   └── Workspace/
│   │       └── WorkspaceManager.cs    # .tf/ metadata management
│   └── ArmTfs.Cli/           # CLI entry point (System.CommandLine)
│       ├── Program.cs
│       └── Commands/          # One file per command
│   └── ArmTfs.VsCode/        # Thin VS Code adapter over CLI JSON contracts
│       ├── package.json
│       └── src/
│           ├── armTfsCliClient.ts
│           ├── contracts.ts
│           └── extension.ts
└── tests/
    └── ArmTfs.Core.Tests/    # xUnit tests for Core
```

## Known Limitations

- **Manual merge content does not use SOAP Resolve yet** — source/target conflict resolutions use SOAP Resolve, but manual merged content falls back to a REST content check-in.
- **Merge candidate detection is inferential** — results are derived from branch ancestry plus scanned history windows, so old or atypical merge flows may require a larger `--scan` value. Use SOAP range dry-run to validate the final conflict plan before executing.
- **No workspace locking** — concurrent edits by multiple clients on the same workspace folder are not protected.
- **PAT required for macOS** — Windows Integrated Auth (NTLM/Kerberos) is not supported cross-platform.

## License

MIT
