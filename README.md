# TermRest

A lightweight terminal REST client for development and testing:

- **Full-screen, mouse-friendly UI** in the style of Microsoft Edit: menu bar, resizable panes, tables with checkboxes, context menus. Every action also works from the keyboard.
- **Postman-compatible storage.** Collections and environments are Postman v2.1 JSON files, edited in place. TermRest writes them back byte-for-byte as Postman would and keeps everything it doesn't understand (scripts, examples, unknown fields).
- **Client certificates (mutual TLS)** are first-class: PEM (including encrypted keys and chains), PFX/PKCS#12, and OS certificate stores. That includes the Windows certificate store with non-exportable keys and the macOS keychain. Certificates bind to URL patterns via a Newman-compatible registry, and a TLS tab shows exactly what was negotiated and presented.
- **One native executable** per platform: Windows, macOS and Linux on x64 and ARM64. Built with .NET 10 Native AOT: about 17 MB, starts in about 50 ms, no runtime to install.
- **A headless CLI** (`send`, `run`, `validate`, `certs`) for scripts and CI, using the same engine.

> Status: **first prototype** (v0.1). See [Roadmap](#roadmap) and the original [design proposal](docs/design-proposal.md).

```text
 File  Request  Environment  Tools  Help
╭┤Collections├───────────────╮╭┤Request — TermRest Demo / Basics/GET with query├─────────────────────────────╮
│└-TermRest Demo             ││GET      ▼ {{baseUrl}}/get?foo=bar&source=termrest                  ⟦ Send ⟧▖ │
│  ├-Basics                  ││╭──────╮───────╮────╮────╮───────╮────────╮                                   │
│  │ ├─GET  GET with query   │││Params│Headers│Auth│Body│Scripts│Settings│                                   │
│  │ ├─POST POST JSON        │││      ╰───────┴────┴────┴───────┴────────┴──────────────────────────────────╮│
│  │ ├─POST Form URL-encoded │││┌─┬──────┬────────┬────────────────────────────────────────────────────────┐││
│  │ └─GET  Status by path va││││ │Key   │Value   │Description                                             │││
│  ├+Auth                    │││├─┼──────┼────────┼────────────────────────────────────────────────────────┤││
│  └+Client certificate (mTLS││││☑│foo   │bar     │                                                        │││
│                            ││││☑│source│termrest│                                                        │││
│                            ││││☐│debug │1       │Disabled parameters are kept but not sent               │││
│                            │││└─┴──────┴────────┴────────────────────────────────────────────────────────┘││
│                            │││                                                                            ││
│                            │││+ Add  Edit  Remove  Bulk edit                                              ││
│                            ││╰────────────────────────────────────────────────────────────────────────────╯│
│                            │╰──────────────────────────────────────────────────────────────────────────────╯
│                            │╭┤Response├────────────────────────────────────────────────────────────────────╮
│                            ││200 OK · 445 ms · 256 B · HTTP/1.1                                            │
│                            ││╭────╮───────╮────────╮───╮                                                   │
│                            │││Body│Headers│Timeline│TLS│                                                   │
│                            │││    ╰───────┴────────┴───┴──────────────────────────────────────────────────╮│
│                            │││Pretty: on  Wrap: off  Copy  Save as…                                       ││
│                            │││{                                                                          ▲││
│                            │││  "args": {                                                                █││
│                            │││    "foo": "bar",                                                          ░││
│                            │││    "source": "termrest"                                                   ░││
│                            │││  },                                                                       ░││
│                            │││  "headers": {                                                             ▼││
│◄█████████████████████░░░░░►││╰────────────────────────────────────────────────────────────────────────────╯│
╰────────────────────────────╯╰──────────────────────────────────────────────────────────────────────────────╯
 F5  Send │ Ctrl+S  Save │ Ctrl+N  New │ F7  Env: Postman Echo │ F8  Certs │ F10  Menu │ Ctrl+Q  Quit │
```

*(Actual output of `termrest snapshot samples --send`: the real UI rendered headlessly after sending the selected request.)*

## Quick start

Requirements to build: the [.NET 10 SDK](https://dotnet.microsoft.com/download). Users of a published binary need nothing.

```sh
# Open the sample workspace (Postman Echo requests) in the full-screen UI
dotnet run --project src/TermRest.Cli -- samples

# Or send a request headlessly
dotnet run --project src/TermRest.Cli -- send "samples/TermRest Demo.postman_collection.json" "Basics/GET with query" -e "Postman Echo" -v
```

Build a native single-file executable for the current machine (see [Building native binaries](#building-native-binaries)):

```sh
dotnet publish src/TermRest.Cli -c Release -r win-x64      # or win-arm64, linux-x64, linux-arm64, osx-x64, osx-arm64
./src/TermRest.Cli/bin/Release/net10.0/win-x64/publish/termrest.exe samples
```

`termrest` with no command opens the UI in the current directory. You can also pass a workspace folder, or a single `*.postman_collection.json`, in which case its folder becomes the workspace.

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
   ├─ settings.json                      ← active environment, TLS verification, CA bundle, timeout, proxy…
   └─ certificates.json                  ← client-certificate registry (Newman --ssl-client-cert-list format)
```

Variables resolve Postman-style. The highest precedence comes first: `--var`/local, then environment, then collection, then globals. Values may reference other variables, and Postman dynamic variables work: `{{$guid}}`, `{{$timestamp}}`, `{{$isoTimestamp}}`, `{{$randomInt}}`, and others. Unresolved `{{names}}` are kept and reported as warnings.

## Using the UI

Everything is clickable: menus, tree items, tabs, buttons, table rows, and checkboxes. Drag the right edge of the Collections pane, or the bottom edge of the Request pane, to resize. Right-click a tree item for actions. Double-click a table row to edit it, and click its checkbox to enable or disable it.

| Key | Action | Key | Action |
|---|---|---|---|
| F5 / Enter in URL bar / Ctrl+Enter | Send (press again to cancel) | Esc | Cancel a running request |
| Ctrl+S | Save all changed files | Ctrl+N | New request |
| F2 | Rename tree item | Del | Delete tree item / table row |
| F7 | Switch environment | F8 | Client certificates |
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
termrest validate <files…>                     check files round-trip byte-for-byte through TermRest
termrest certs list|store|match                client-certificate helpers
termrest snapshot [<workspace>] [--send]       render the UI headlessly (smoke test; --send includes a live response)

common options: -e/--env <name|file>  --var key=value  --timeout <ms>  -k/--insecure
send options:   -i/--include  -v/--verbose  -o/--output <file>  --raw  -f/--fail  --curl
run options:    --bail  --allow-http-errors  --report-json <file>
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
| Collection v3.0.0 YAML (Postman v12 "Native Git") | Planned (phase 2) |
| Environments / globals | Read/write (`*.postman_environment.json`, `*.postman_globals.json`) |
| Folders, descriptions, saved examples, `protocolProfileBehavior`, unknown fields | Kept; edited fields are rewritten surgically |
| Body modes | raw, urlencoded, formdata (text + files), file, graphql |
| Auth | inherit, noauth, basic, bearer, apikey, digest, ntlm, oauth2 (access token) — others are kept, not applied |
| `pm.*` scripts | Kept and editable, **not executed yet** |
| Client certificates | `request.certificate` honoured; Newman certificate-list format for the registry |

Run `termrest validate <your exports>` to check that TermRest would save your files without changing them.

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
src/TermRest.Core   UI-free engine: Postman model + mapper, variables, HTTP executor, certificates, workspace
src/TermRest.Tui    Terminal.Gui v2 views and dialogs
src/TermRest.Cli    `termrest` executable (System.CommandLine): TUI by default + headless commands
tests/…Core.Tests   xUnit v3: fidelity, mapper, URLs, variables, certificates, in-process Kestrel HTTP/TLS/mTLS tests
tests/…Tui.Tests    headless UI tests: renders the real window, injects mouse/keyboard input, asserts on screen text
```

```sh
dotnet build
dotnet test                                    # Microsoft.Testing.Platform runner (see global.json)
TERMREST_STORE_TESTS=1 dotnet test             # Windows: also run the non-exportable cert-store mTLS test
```

Notes:

- Terminal.Gui 2.5's `TextView` is marked obsolete in favour of the separate `Terminal.Gui.Editor` package. That package has not shipped a build for 2.5 yet, so TermRest uses `TextView` behind a small `TextArea` wrapper, which makes the later switch (syntax highlighting, folding) a one-file change.
- Each request uses a fresh connection, so timing and TLS details are exact. Cookies persist for the session (**Tools › Clear cookies**).

## Roadmap

- **Next:** request history; cURL import; OAuth 2.0 token acquisition, including Entra ID client credentials with a certificate; response search and JSON path filtering; saving responses as Postman examples.
- **Phase 2:**
  - Postman v3 YAML collections.
  - Script execution: generated assertions first, then a `pm.*` sandbox.
  - A collection runner with tests.
  - Syntax-highlighted editor.
  - Proxy authentication.
  - Code snippets.
- **Packaging:** winget, Homebrew, `.deb`/`.rpm`, and signed binaries.
