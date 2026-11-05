# TermRest

A lightweight terminal REST client for development and testing:

- **Full-screen, mouse-friendly UI** in the style of Microsoft Edit: menu bar, resizable panes, tables with checkboxes, context menus. Every action also works from the keyboard.
- **Postman-compatible storage.** Collections and environments are Postman files, edited in place: v2.1 JSON, or the v3 YAML folders that Postman v12 keeps in Git. TermRest writes them back as Postman would (untouched JSON byte-for-byte; in YAML only the lines you changed) and keeps everything it doesn't understand (scripts, examples, unknown fields).
- **Request history and cURL import.** Every request you send is kept per workspace, with credentials masked, ready to reopen, send again or save. Paste a curl command (for example from a browser's *Copy as cURL*) into the URL bar to import it.
- **Client certificates (mutual TLS)** are first-class: PEM (including encrypted keys and chains), PFX/PKCS#12, and OS certificate stores. That includes the Windows certificate store with non-exportable keys and the macOS keychain. Certificates bind to URL patterns via a Newman-compatible registry, and a TLS tab shows exactly what was negotiated and presented.
- **One native executable** per platform: Windows, macOS and Linux on x64 and ARM64. Built with .NET 10 Native AOT: under 20 MB, starts in about 50 ms, no runtime to install.
- **A headless CLI** (`send`, `run`, `validate`, `convert`, `history`, `import-curl`, `certs`) for scripts and CI, using the same engine.

> Status: **first prototype** (v0.1). See [Roadmap](#roadmap) and the original [design proposal](docs/design-proposal.md).

```text
 File  Request  Environment  Tools  Help
╭┤Collections├──────────────────╮╭┤Request — TermRest Demo / Basics/GET with query├────────────────────────────────────╮
│└-TermRest Demo                ││GET      ▼ {{baseUrl}}/get?foo=bar&source=termrest                         ⟦ Send ⟧▖ │
│  ├-Basics                     ││╭──────╮───────╮────╮────╮───────╮────────╮                                          │
│  │ ├─GET  GET with query      │││Params│Headers│Auth│Body│Scripts│Settings│                                          │
│  │ ├─POST POST JSON           │││      ╰───────┴────┴────┴───────┴────────┴─────────────────────────────────────────╮│
│  │ ├─POST Form URL-encoded    │││┌─┬──────┬────────┬───────────────────────────────────────────────────────────────┐││
│  │ └─GET  Status by path varia││││ │Key   │Value   │Description                                                    │││
│  ├+Auth                       │││├─┼──────┼────────┼───────────────────────────────────────────────────────────────┤││
│  └+Client certificate (mTLS)  ││││☑│foo   │bar     │                                                               │││
│                               ││││☑│source│termrest│                                                               │││
│                               ││││☐│debug │1       │Disabled parameters are kept but not sent                      │││
│                               │││└─┴──────┴────────┴───────────────────────────────────────────────────────────────┘││
│                               │││                                                                                   ││
│                               │││+ Add  Edit  Remove  Bulk edit                                                     ││
│                               ││╰───────────────────────────────────────────────────────────────────────────────────╯│
│                               │╰─────────────────────────────────────────────────────────────────────────────────────╯
│                               │╭┤Response├───────────────────────────────────────────────────────────────────────────╮
│                               ││200 OK · 264 ms · 256 B · HTTP/1.1                                                   │
│                               ││╭────╮───────╮────────╮───╮                                                          │
│                               │││Body│Headers│Timeline│TLS│                                                          │
│                               │││    ╰───────┴────────┴───┴─────────────────────────────────────────────────────────╮│
│                               │││Pretty: on  Wrap: off  Copy  Save as…                                              ││
│                               │││{                                                                                 ▲││
│                               │││  "args": {                                                                       █││
│                               │││    "foo": "bar",                                                                 ░││
│                               │││    "source": "termrest"                                                          ░││
│                               │││  },                                                                              ░││
│                               │││  "headers": {                                                                    ▼││
│◄██████████████████████████░░░►││╰───────────────────────────────────────────────────────────────────────────────────╯│
╰───────────────────────────────╯╰─────────────────────────────────────────────────────────────────────────────────────╯
 F5  Send │ Ctrl+S  Save │ Ctrl+N  New │ F7  Env: Postman Echo │ F8  Certs │ F9  History │ F10  Menu │ Ctrl+Q  Quit │
```

*(Actual output of `termrest snapshot samples --send`: the real UI rendered headlessly after sending the selected request.)*

## Quick start

Requirements to build: the [.NET 10 SDK](https://dotnet.microsoft.com/download). Users of a published binary need nothing.

```sh
# Open the sample workspace (Postman Echo requests) in the full-screen UI
dotnet run --project src/TermRest.Cli -- samples

# The same requests as a Postman v3 (YAML) workspace
dotnet run --project src/TermRest.Cli -- samples/v3

# Or send a request headlessly
dotnet run --project src/TermRest.Cli -- send "samples/TermRest Demo.postman_collection.json" "Basics/GET with query" -e "Postman Echo" -v
```

Build a native single-file executable for the current machine (see [Building native binaries](#building-native-binaries)):

```sh
dotnet publish src/TermRest.Cli -c Release -r win-x64      # or win-arm64, linux-x64, linux-arm64, osx-x64, osx-arm64
./src/TermRest.Cli/bin/Release/net10.0/win-x64/publish/termrest.exe samples
```

`termrest` with no command opens the UI in the current directory. You can also pass a workspace folder, a single `*.postman_collection.json` (its folder becomes the workspace), or a Postman v3 collection folder.

## Workspace layout

A workspace is just a folder. Put it in git and share it.

```text
my-api/
├─ Contoso API.postman_collection.json   ← Postman v2.1 (v2.0 can be read); import/export with Postman as-is
├─ dev.postman_environment.json
├─ prod.postman_environment.json
├─ workspace.postman_globals.json        ← optional
├─ certs/                                ← your certificate files (don't commit private keys)
└─ .termrest/
   ├─ settings.json                      ← active environment, TLS verification, CA bundle, timeout, proxy, history…
   └─ certificates.json                  ← client-certificate registry (Newman --ssl-client-cert-list format)
```

The collections and environments can also be in Postman's v3 layout (`postman/collections/…`, YAML); see [Postman v3 (YAML) collections](#postman-v3-yaml-collections).

Variables resolve Postman-style. The highest precedence comes first: `--var`/local, then environment, then collection, then globals. Values may reference other variables, and Postman dynamic variables work: `{{$guid}}`, `{{$timestamp}}`, `{{$isoTimestamp}}`, `{{$randomInt}}`, and others. Unresolved `{{names}}` are kept and reported as warnings.

## Using the UI

Everything is clickable: menus, tree items, tabs, buttons, table rows, and checkboxes. Drag the right edge of the Collections pane, or the bottom edge of the Request pane, to resize. Right-click a tree item for actions. Double-click a table row to edit it, and click its checkbox to enable or disable it.

| Key | Action | Key | Action |
|---|---|---|---|
| F5 / Enter in URL bar / Ctrl+Enter | Send (press again to cancel) | Esc | Cancel a running request |
| Ctrl+S | Save all changed files | Ctrl+N | New request; adds an imported or history request to a collection |
| F2 | Rename tree item | Del | Delete tree item / table row / history entry |
| F7 | Switch environment | F8 | Client certificates |
| F9 | Switch the sidebar: Collections ⇄ History | Paste into the URL bar | Import a curl command |
| F6 / Shift+F6 | Next / previous pane | Tab / Shift+Tab | Next / previous field |
| F10 | Menu | Shift+F10 | Context menu |
| Ctrl+L | Focus the URL bar | Ctrl+Q | Quit (asks to save) |

**Request editor tabs**

- **Params:** query parameters (kept in sync with the URL) and path variables (`:id` segments).
- **Headers**
- **Auth:** inherit, none, Basic, Bearer, API key, Digest, NTLM, or an OAuth 2.0 access token. Other Postman auth types are kept but not yet applied.
- **Body:** none, raw (JSON, XML, text, HTML, JavaScript, with Beautify), form-data with files, x-www-form-urlencoded, binary file, or GraphQL.
- **Scripts:** pre-request and test scripts are kept and editable.
- **Settings:** per-request SSL verification and redirects, which client certificate applies, and the description.

**Response tabs**

- **Body:** pretty or raw view, wrap, copy, save.
- **Headers**
- **Timeline:** the exact request sent, DNS/connect/TLS/TTFB/download timing, and warnings.
- **TLS:** protocol and cipher, the client certificate sent and whether the server asked for one, and the server chain with validation details.

## Request history

Every request you send from the UI or with `termrest send` is recorded with its response: status, headers, timing and the body (up to 256 KB by default). Collection runs and `snapshot` are not recorded.

- **Storage:** history is kept per user, outside the workspace, so it never ends up in git. The location is `%LOCALAPPDATA%\TermRest\history` on Windows, `~/Library/Application Support/TermRest/history` on macOS, and `$XDG_STATE_HOME/termrest/history` (default `~/.local/state/termrest/history`) on Linux, with one folder per workspace. Set `TERMREST_STATE_DIR` to move it. On Unix only you can read the files.
- **Masking:** secrets are masked before anything is written to disk:
  - `Authorization`, `Cookie`, and headers whose names contain token, secret, password or api-key;
  - the value of every `Set-Cookie`;
  - the request's auth credentials and the values of variables marked secret, including where a server echoes them back in a response body.
- **In the UI:** press **F9** (or click *History* in the status bar) to switch the sidebar to the history list, and type in *Filter* to narrow it.
  - Click an entry to open the request exactly as it was sent, together with its recorded response.
  - Right-click an entry for *Send again*, *Save to collection…*, *Copy as cURL*, *Go to request in collection*, *Delete entry* and *Clear history…*.
  - **Ctrl+N** adds the opened entry to a collection.
- **Settings:** *Tools › Settings › History* turns recording off and sets how many entries (500) and how many KB of each response body (256) are kept. In `.termrest/settings.json` these are `historyEnabled`, `historyLimit` and `historyResponseKb`.

```sh
termrest history samples                     # newest first: id, time, status, duration, method, URL, request
termrest history samples --show c637d3a5     # one entry in full: request, response headers and body
termrest history samples --json -n 100       # machine-readable
termrest history samples --clear
termrest send … --no-history                 # send without recording
```

## Importing cURL commands

- **Paste** a curl command into the URL bar. It replaces the current request (method, URL, headers, body, auth, client certificate), as in Postman; **Ctrl+S** saves it. Typing a curl command and pressing Enter does the same.
- **File › Import cURL command…** (also in the *Request* menu) opens the command as a new request instead. **Ctrl+N** then adds it to a collection.
- **Syntax:** bash, cmd and PowerShell syntax are all understood, so every flavor of the browsers' *Copy as cURL* works, including line continuations, `$'…'` strings and `^` escapes.
- **Options imported:**
  - request: `-X`, `-H`, `-A`, `-e`, `-b name=value`, `-r`, `-I`;
  - body: `-d`/`--data-*`, `--json`, `-F` (text and files), `-T`, `-G`/`--url-query`;
  - auth: `-u` (Basic, or with `--digest`/`--ntlm`/`--negotiate`) and `--oauth2-bearer`;
  - client certificates: `--cert`/`--key`/`--pass`/`--cert-type`;
  - TLS and redirects: `-k` and `--max-redirs` (TermRest follows redirects by default, as with `-L`).
- **Notes:** options that are workspace settings in TermRest (proxy, timeouts, CA files, HTTP version) and unsupported options are listed as notes after the import. Options that only affect curl's own output, such as `-s`, `-v`, `-o` or `--compressed`, are skipped without a note.

```sh
termrest import-curl api.postman_collection.json "curl -X POST https://api.example.com/v1/items -H 'Content-Type: application/json' -d '{\"name\":\"x\"}'"
pbpaste | termrest import-curl postman/collections/Contoso --folder Imported --name "Create item"   # or read from stdin
termrest import-curl api.postman_collection.json "curl …" --dry-run                                 # show what would be imported
```

The other direction: `termrest send … --curl`, *Request › Copy as cURL*, and *Copy as cURL* in the history.

## Postman v3 (YAML) collections

Postman v12 can keep a workspace's collections and environments in a Git repository as YAML ("Native Git", collection format v3), with one folder per collection and one file per request. TermRest reads and writes that layout directly, so the same repository works in both tools.

```text
my-api/
├─ postman/
│  ├─ collections/
│  │  └─ Contoso API/
│  │     ├─ .resources/definition.yaml     ← collection description, auth, variables, scripts
│  │     ├─ Get status.request.yaml        ← one file per request
│  │     └─ Users/                         ← a folder
│  │        ├─ .resources/definition.yaml  ← folder auth, scripts and position
│  │        └─ Get user.request.yaml
│  ├─ environments/dev.environment.yaml
│  └─ globals/workspace.globals.yaml
└─ .termrest/                              ← TermRest settings and certificate registry, as above
```

- **Opening:** open the repository folder (`termrest my-api`) or a collection folder directly. A workspace may mix JSON and YAML collections.
- **Saving changes only what changed.** An edit rewrites the affected lines of one file and keeps comments, key order, quoting and everything TermRest doesn't use. Renaming, moving, reordering and deleting requests and folders renames, moves and deletes their files (with their saved examples) the way Postman does, including the `order` values. Files TermRest doesn't manage are never deleted.
- **New and imported collections:** in a workspace that already uses the v3 layout, new collections and environments are created as YAML, and *File › Import Postman collection* converts JSON collections. The choice is in *Tools › Settings › New collections* (`collectionFormat`: `v3` or `v2.1`).
- **Conversion and validation:**

  ```sh
  termrest convert "Contoso API.postman_collection.json"     # → postman/collections/Contoso API/
  termrest convert "postman/collections/Contoso API" --to v2.1 -o contoso.postman_collection.json
  termrest convert dev.postman_environment.json              # → postman/environments/dev.environment.yaml
  termrest validate "postman/collections/Contoso API"        # checks that every file converts losslessly
  ```

- **Tested on real files:** 31 collections written by Postman, from public GitHub repositories. Editing and reverting every part of every request leaves all files byte-for-byte unchanged.

Not yet supported in v3 collections:

- Postman's GraphQL, gRPC and WebSocket request types are not shown; their files are left untouched. HTTP requests with a GraphQL body work.
- Saved examples (`*.example.yaml`) are kept and move with their request, but are not shown, and `convert --to v2.1` leaves them out.
- A client certificate pinned on a request (v2.1 `certificate`) has no v3 equivalent; use the certificate registry.
- If a collection or folder defines several auth methods, only the first one can be edited; the others are kept.
- Long lines are not folded at 80 columns as some Postman versions do, but lines you don't change keep their folding.

## Client certificates (mutual TLS)

Open **Tools › Client certificates** (F8). From there you can add a PEM pair, a PFX, or pick a certificate from the OS store. Then bind it to URL patterns and press **Test** to confirm it loads with its private key. Entries are stored in `.termrest/certificates.json`:

```json
[
  {
    "name": "contoso-dev",
    "matches": ["https://*.contoso-dev.example/*"],
    "cert": { "src": "certs/client.pem" },
    "key": { "src": "certs/client.key" },
    "passphrase": "env:CONTOSO_KEY_PASSPHRASE"
  },
  {
    "name": "partner",
    "matches": ["https://api.partner.example:8443/*"],
    "pfx": { "src": "certs/partner.pfx" },
    "passphrase": "{{partnerPfxPassphrase}}"
  },
  {
    "name": "service identity",
    "matches": ["https://*.internal.example/*"],
    "x-store": { "location": "CurrentUser", "store": "My", "thumbprint": "4F2A…C91B" }
  }
]
```

- **File format:** the same as Newman's `--ssl-client-cert-list` (`name`, `matches`, `cert`/`key`/`pfx` `src`, `passphrase`). `x-store` is a TermRest extension. Relative paths resolve against the workspace folder.
- **Matching:** patterns follow Postman's rules: `scheme://host[:port]/path`, where `*.domain` matches the domain and its subdomains, and a pattern without a port matches any port. The most specific match wins. A `certificate` set on an individual request in the collection (a Postman v2.1 field) takes precedence over the registry.
- **Passphrases:** avoid storing them in the file. Use `env:NAME` to read an environment variable, or `{{variable}}` to read a (secret) environment value.
- **OS stores**
  - *Windows:* `CurrentUser\My` or `LocalMachine\My`. Keys marked non-exportable, and smart-card or TPM-backed keys, work because the TLS handshake signs inside CNG/SChannel.
  - *macOS:* `CurrentUser\My` is your login keychain (identities with private keys).
  - *Linux:* use PEM/PFX files. The .NET user store under `~/.dotnet/corefx/cryptography/x509stores/my` also works.
- **Server trust:** the OS trust store by default. Add private or corporate CAs with **Tools › Settings › Extra CA bundle** (PEM). Turn verification off per request in **Settings**, or globally (`--insecure` on the CLI).
- **Accuracy:** TLS session resumption is disabled and client-certificate selection is always explicit, so every request does a full handshake with exactly the certificate shown. Cached credentials from earlier requests are never reused, which is important when testing certificate changes.

```sh
termrest certs list samples                       # load every registry entry and show subject, expiry, key status
termrest certs store                              # OS-store certificates with private keys
termrest certs match https://client.badssl.com/ samples
```

## Command line

```text
termrest [<workspace>]                         open the UI (default: current directory)
termrest send <collection> <request> [opts]    send one request; body to stdout, status/diagnostics to stderr
termrest run <collection> [--folder F] [opts]  run all requests in order; exit code 1 if any fail
termrest validate <files…>                     check that files (JSON, YAML, v3 folders) round-trip unchanged through TermRest
termrest convert <input> [--to v3|v2.1] [-o O] convert a collection or environment between v2.1 JSON and v3 YAML
termrest history [<workspace>] [opts]          requests sent from a workspace, newest first
termrest import-curl <collection> [<command>]  add a request from a curl command (read from stdin when omitted)
termrest certs list|store|match                client-certificate helpers
termrest snapshot [<workspace>] [--send]       render the UI headlessly (smoke test; --send includes a live response)

<collection>: a *.postman_collection.json file or a v3 collection folder
common options:      -e/--env <name|file>  --var key=value  --timeout <ms>  -k/--insecure
send options:        -i/--include  -v/--verbose  -o/--output <file>  --raw  -f/--fail  --curl  --no-history
run options:         --bail  --allow-http-errors  --report-json <file>
history options:     -n/--limit <n>  --show <id>  --json  --clear
import-curl options: --folder <path>  --name <name>  --dry-run
```

```sh
termrest run "samples/TermRest Demo.postman_collection.json" -e "Postman Echo" --report-json report.json
termrest send api.postman_collection.json "Users/Get user" --var userId=42 -i
termrest send api.postman_collection.json "Users/Get user" --curl     # print an equivalent curl command
```

## Postman compatibility

| Area | Status |
|---|---|
| Collection v2.1.0 JSON | Read/write; untouched content round-trips byte-for-byte (tabs, key order, escaping, BOM/CRLF preserved) |
| Collection v2.0.0 JSON | Read/write (v2.0 auth shape kept) |
| Collection v3.0.0 YAML (Postman v12 "Native Git") | Read/write: `postman/collections/<name>/` folders with `*.request.yaml` and `definition.yaml`; edits rewrite only the changed lines. See [Postman v3 (YAML) collections](#postman-v3-yaml-collections) |
| Environments / globals | Read/write: JSON (`*.postman_environment.json`, `*.postman_globals.json`) and YAML (`postman/environments/*.environment.yaml`, `postman/globals/workspace.globals.yaml`) |
| Folders, descriptions, saved examples, `protocolProfileBehavior`, unknown fields | Kept; edited fields are rewritten surgically |
| Body modes | raw, urlencoded, formdata (text + files), file, graphql |
| Auth | inherit, noauth, basic, bearer, apikey, digest, ntlm, oauth2 (access token) — others are kept, not applied |
| `pm.*` scripts | Kept and editable, **not executed yet** |
| Client certificates | `request.certificate` honoured; Newman certificate-list format for the registry |
| cURL | Import (bash, cmd and PowerShell syntax) and export |

Run `termrest validate <your exports>` to check that TermRest would save your files without changing them. Saves are atomic, write through symbolic links, and keep file permissions (Unix mode / Windows ACL), so a `chmod 600` environment holding secrets stays private. Importing a collection that is already open reloads it from disk, and asks first if it has unsaved changes.

## Building native binaries

Native AOT compiles per OS. Cross-architecture builds work (x64 ↔ arm64); cross-OS builds don't, so CI builds each OS on its own runner.

| RID | Toolchain on the build machine |
|---|---|
| `win-x64`, `win-arm64` | Visual Studio 2022+ "Desktop development with C++" (+ "C++ ARM64 build tools" for win-arm64) |
| `linux-x64`, `linux-arm64` | `clang` and `zlib1g-dev` (plus an arm64 sysroot when cross-compiling) |
| `osx-x64`, `osx-arm64` | Xcode command-line tools (both architectures from either Mac) |

The [CI workflow](.github/workflows/ci.yml) tests on Windows, Linux and macOS. It then publishes and smoke-tests all six RIDs, using `termrest snapshot` to prove the UI renders in each AOT binary. Pushing a `v*` tag attaches the binaries to a GitHub release.

## Development

```text
src/TermRest.Core   UI-free engine: Postman model + mapper (v2.1 JSON, v3 YAML), YAML reader/writer, variables,
                    HTTP executor, certificates, history, cURL import, workspace
src/TermRest.Tui    Terminal.Gui v2 views and dialogs
src/TermRest.Cli    `termrest` executable (System.CommandLine): TUI by default + headless commands
tests/…Core.Tests   xUnit v3: fidelity, mapper, URLs, variables, certificates, YAML, Postman v3, history, cURL,
                    in-process Kestrel HTTP/TLS/mTLS tests
tests/…Tui.Tests    headless UI tests: renders the real window, injects mouse/keyboard input, asserts on screen text
```

```sh
dotnet build
dotnet test                                    # Microsoft.Testing.Platform runner (see global.json)
TERMREST_STORE_TESTS=1 dotnet test             # Windows: also run the non-exportable cert-store mTLS test
TERMREST_V3_CORPUS=<folder> dotnet test        # also edit and revert every Postman v3 collection found under <folder>
```

Notes:

- Terminal.Gui 2.5's `TextView` is marked obsolete in favour of the separate `Terminal.Gui.Editor` package. That package has not shipped a build for 2.5 yet, so TermRest uses `TextView` behind a small `TextArea` wrapper, which makes the later switch (syntax highlighting, folding) a one-file change.
- Each request uses a fresh connection, so timing and TLS details are exact. Cookies persist for the session (**Tools › Clear cookies**).

## Roadmap

- **Next:** OAuth 2.0 token acquisition, including Entra ID client credentials with a certificate; response search and JSON path filtering; saving responses as Postman examples, and showing them in v3 collections.
- **Phase 2:**
  - Script execution: generated assertions first, then a `pm.*` sandbox.
  - A collection runner with tests.
  - Syntax-highlighted editor.
  - Proxy authentication.
  - Code snippets.
- **Packaging:** winget, Homebrew, `.deb`/`.rpm`, and signed binaries.
