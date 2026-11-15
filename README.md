# TermRest

A lightweight terminal REST client for development and testing:

- **Full-screen, mouse-friendly UI** in the style of Microsoft Edit: menu bar, resizable panes, tables with checkboxes, context menus. Every action also works from the keyboard. Color themes are built in (Dracula, Nord, Solarized, and more), or you can create your own.
- **Postman-compatible storage.** Collections and environments are Postman files, edited in place: v2.1 JSON, or the v3 YAML folders that Postman v12 keeps in Git. TermRest writes them back as Postman would (untouched JSON byte-for-byte; in YAML only the lines you changed) and keeps everything it doesn't understand (examples, unknown fields).
- **Scripts in Python, JavaScript, C# or PowerShell.** Pre-request and test scripts for collections, folders and requests, with variables, request changes and tests. JavaScript scripts use Postman's `pm` object, so Postman collections' scripts run too. Each language runs in the runtime you choose: your project's Python, Node.js or Bun, the .NET SDK, and PowerShell 7, with the packages and modules you installed (NuGet packages for C#). A script can have a version in each language (Postman's JavaScript next to a PowerShell one, say): each request runs one language only, the first of your order that has all its scripts, or the one it chooses. TermRest carries no script engine: a runtime starts for its first script and stops when idle. See [Scripts](#scripts). **Scripts are not sandboxed:** they run with your permissions, so turn them on only for collections you trust (see [Script safety](#script-safety)).
- **Request history and cURL import.** Every request you send is kept per workspace, with credentials masked, ready to reopen, send again or save. Paste a curl command (for example from a browser's *Copy as cURL*) into the URL bar to import it.
- **Recording.** Press Record (F12) and every request and response you send is recorded. On stop, TermRest asks where to save it as a HAR file (JSON, opens in browser dev tools). Credentials, cookies, tokens and secret variables are redacted, files sent with a request are recorded by path only, and a recording streams to a private temporary file instead of growing in memory.
- **Large bodies from files.** Send a file as the body without loading it: it is streamed when the request is sent, and a JSON or XML file is checked first.
- **Client certificates (mutual TLS)** are first-class: PEM (including encrypted keys and chains), PFX/PKCS#12, and OS certificate stores. That includes the Windows certificate store with non-exportable keys and the macOS keychain. Certificates bind to URL patterns via a Newman-compatible registry, and a TLS tab shows exactly what was negotiated and presented.
- **One native executable** per platform: Windows, macOS and Linux on x64 and ARM64. Built with .NET 10 Native AOT: under 20 MB, starts in about 50 ms, no runtime to install.
- **A headless CLI** (`send`, `run`, `validate`, `convert`, `history`, `import-curl`, `certs`) for scripts and CI, using the same engine.

> Status: **first prototype** (v0.1). See [Roadmap](#roadmap) and the original [design proposal](docs/design-proposal.md).

```text
 File  Request  Environment  Tools  Help                                                                   ● Record F12
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

`termrest` with no command opens the UI in the current directory. You can also pass a workspace folder, a single `*.postman_collection.json` (its folder becomes the workspace, or for one in `postman/collections` the folder that holds `postman`), or a Postman v3 collection folder.

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
   ├─ settings.json                      ← active environment, TLS verification, CA bundle, timeout, proxy (used once you allow it), history, YAML wrapping, color theme, recording redaction…
   ├─ certificates.json                  ← client-certificate registry (Newman --ssl-client-cert-list format)
   └─ themes/                            ← optional: color themes shared with the workspace
```

The collections and environments can also be in Postman's v3 layout (`postman/collections/…`, YAML); see [Postman v3 (YAML) collections](#postman-v3-yaml-collections). A folder of the workspace that can't be listed (one you may not read, say), or a link to a folder that is gone (a collection linked from a repository that was moved), is reported when the workspace opens; the rest of the workspace loads.

Environments can share a name (an updated export imported next to the old one, or a JSON environment and the YAML copy `termrest convert` made of it). TermRest remembers the active one by its file (`activeEnvironmentFile` in `.termrest/settings.json`, next to its name in `activeEnvironment`), so it stays the one you picked, also after a restart; the environment menu, the status bar and *Manage environments* show such environments with their file names, or with their paths in the workspace where the file names are the same too (`Local.postman_environment.json` in the workspace folder and in `postman/environments`). When that file is gone, or the settings were written by an earlier version, the first environment with that name is active.

Collections can share a name the same way (an updated export imported next to the old one is copied in as `Orders API (2).postman_collection.json`; `termrest convert` puts a v3 copy in `postman/collections` next to the JSON one). The tree and the request pane's title show such collections with their file (or v3 folder) names, or with their paths in the workspace where those are the same too, and the history keeps the file each request was sent from (see [Request history](#request-history)).

The files TermRest creates for new and imported collections, environments and theme copies are named for every OS, as a workspace is shared through Git: characters Windows doesn't allow in a file name (`/ \ : * ? " < > |`, control characters) become `-`, also on Linux and macOS, and so does a leading `.`; a reserved name (`CON`, `NUL`, `COM1` …) gets a `-`, also before an extension (`con.example.org` becomes `con-.example.org`, as Windows reserves `CON` with any extension), and a long name is cut to 64 characters (the name inside the file stays whole). A name that differs only in case from a file already there gets a number, as Windows and macOS don't tell such names apart (`orders API (2).postman_collection.json` next to `Orders API.postman_collection.json`).

*File › Import Postman environment…* also takes a globals export (`*.postman_globals.json`): it becomes the workspace's globals, saved as `workspace.postman_globals.json` (`postman/globals/workspace.globals.yaml` in the v3 layout). A workspace has one set of globals: while it has some, such an import is refused, and they stay as they are.

A `settings.json` or `certificates.json` that can't be read (invalid JSON, a merge conflict) is reported when the workspace opens and is never saved over; fix or delete it. The same goes for your user settings (theme, script runtimes, the proxies you allowed); `send` and `run` warn when those can't be read, as the script runtimes and language order chosen in them aren't used then, nor a proxy you allowed. While `.termrest/settings.json` can't be read, `send` and `run` send nothing and exit with code 1 (the file may set the proxy, TLS verification, the history and what recordings redact), and nothing goes into the history. TermRest writes only the settings you change into `.termrest/settings.json` (the active environment's name and file together), into the file as it is then: settings a `git pull` changed meanwhile and ones TermRest doesn't know are kept (comments are not). Nothing is written when nothing changed.

A proxy URL in `.termrest/settings.json` (`proxy`) is used only once you allow it for that workspace folder: the file comes with the repository you clone, and a proxy sees every request, with its credentials. Until then, requests use the system's proxy settings (the OS's, `HTTPS_PROXY`), as with `system`; the status bar and *Help › Workspace info* say so, and `send` and `run` print a warning (the proxy's password masked). Saving in *Tools › Settings* allows the proxy shown there; the dialog shows its password masked too, and keeps it while the mask is left in place (also when only the host or port changes). The approval is kept per workspace folder in your user settings (`allowedProxies`) as a SHA-256 hash, never the proxy's URL, which can hold a password; when the file names another proxy (after a `git pull`, say), that one must be allowed again. `system` and `none` need no approval, and the file's other settings, TLS verification and the CA bundle included, apply as they are.

Requests send body and form-data files only from the workspace folder, also once links are resolved: a collection that comes with a repository could otherwise send any file you can read (SSH keys, cloud credentials) to a server of its choosing, with no script needed. A file outside the folder (an absolute path, a `../` path, or a link in the repository that leads out) stops its request with an error that names it; a path outside the folder as written is not even looked at, by the request or by its Body tab, so a Windows `\\server\share` path doesn't reach that server. For the same reason, on Windows, client certificates and the CA bundle (named by a request, `.termrest/certificates.json` or `.termrest/settings.json`) are not opened from a network share outside the folder, nor through a link that leads there: the request is sent without them, with a warning. To send such files and use such certificates, check *Outside files: Allow files from outside this folder* in *Tools › Settings* (kept per workspace folder in your user settings, `outsideFilesAllowed`), or pass `--allow-outside-files` to `send`, `run` or `certs list`.

Variables resolve Postman-style. The highest precedence comes first: `--var`/local, then environment, then collection, then globals. Values may reference other variables, and Postman dynamic variables work, with a new value each time: `{{$guid}}` (also `$randomUUID`), `{{$timestamp}}`, `{{$isoTimestamp}}`, `{{$randomInt}}` (0 to 1000), `$randomBoolean`, `$randomAlphaNumeric` and `$randomHexadecimal` (one character), `$randomFirstName`, `$randomLastName`, `$randomFullName`, `$randomUserName`, `$randomEmail`, `$randomWord` and `$randomPassword`. Scripts resolve them too (`.replaceIn`, `.replace_in`, `.ReplaceIn`): `pm.environment.set('email', pm.variables.replaceIn('{{$randomEmail}}'))` keeps one address for the requests that use `{{email}}`. A variable of the same name takes the place of a dynamic one (`$timestamp` set by a script, say), in requests and scripts alike, as in Postman. Unresolved `{{names}}` are kept and reported as warnings. Replacing variables adds at most 4,194,304 characters to a text: a reference whose value would go past that (a value that references itself several times, say) stays unresolved and is reported.

## Using the UI

Everything is clickable: menus, tree items, tabs, buttons, table rows, and checkboxes. Drag the right edge of the Collections pane, or the bottom edge of the Request pane, to resize. The tree and the lists (environments, color themes, history) show a character outside Unicode's Basic Multilingual Plane (most emoji) as `�`, as Terminal.Gui can't draw one there; names keep it. Help texts and *Workspace info* scroll when taller than the screen (mouse wheel, arrow keys, PgDn). Right-click a tree item for actions (*Send* and *Copy as cURL* act on that request). Double-click a table row to edit it, and click its checkbox to enable or disable it (every click counts, even two quick ones, and moves the cursor to that row; the header's checkbox does every row): a value or description with line breaks (a PEM key, a JSON part) is edited in a text area, and what you leave alone keeps its value. *Bulk edit* shows a table as `key:value` lines: line breaks in values show as `\n`, and secret values are masked (leave the mask to keep the value). Keys may contain `:` themselves (`Api:BaseUrl:https://api.example.com`): a line is read as the row it shows as written (also when rows share a key), else as the row whose key it starts with (the longest such key), else split at its first `:`. A renamed line is the row that was in its place; when it isn't clear which secret a mask stands for, TermRest asks for the value.

A request that is not in a collection (a new one, an imported curl command, or one from the history) lives in the editor only. If you changed it, opening another request, quitting or switching workspaces asks first; *Add to collection…* keeps it. **Ctrl+N** adds it to a collection too; either way it stays open there, with its response, also one still on its way. A request still on its way when you open another one finishes: its result goes to the history and the status bar instead of under the request now shown. A request runs in the environment that was active when you sent it: switching (F7) while it is on its way changes later requests only, and the variables its scripts set, and the environment its history entry and recording name, are that one's. Edits you make while its scripts run apply to the next send; the history keeps the request as it was sent.

| Key | Action | Key | Action |
|---|---|---|---|
| F5 / Enter in URL bar / Ctrl+Enter | Send (press again to cancel) | Esc | Cancel a running request |
| Ctrl+S | Save all changed files | Ctrl+N | New request; adds an imported or history request to a collection |
| F2 | Rename tree item | Del / Insert | Delete tree item, table row or history entry / add a table row |
| F7 | Switch environment | F8 | Client certificates |
| F9 | Switch the sidebar: Collections ⇄ History | Paste into the URL bar | Import a curl command |
| F6 / Shift+F6 | Next / previous pane | Tab / Shift+Tab | Next / previous field |
| F10 | Menu | Shift+F10 | Context menu |
| Ctrl+L | Focus the URL bar | Ctrl+Q | Quit (asks to save) |
| F12 | Start / stop recording (see [Recording requests](#recording-requests)) | ← / → on a tab row | Previous / next tab (Home / End: first / last; ↓ or Enter: into the page) |

**Request editor tabs**

- **Params:** query parameters (kept in sync with the URL) and path variables (`:id` segments). Values are sent as typed, except that a `#` in a query parameter, a path variable or a `{{variable}}` in the path, and a `?` in a path variable, are sent percent-encoded (`%23`, `%3F`): they stay part of the value instead of ending the query or path, where the rest would not be sent.
- **Headers:** values are sent as typed, in UTF-8 as curl sends them (`X-User: José` works), and cookies a server set go back in the bytes it sent them in; a value with a line break or NUL is refused (a trailing line break is dropped), so a value can never add a header of its own. Headers that describe a body (`Content-Type`, `Content-Encoding` …) are left out of a request without one, with a warning.
- **Auth:** inherit, none, Basic, Bearer, API key, Digest, NTLM, or an OAuth 2.0 access token. Other Postman auth types are kept but not yet applied. Passwords, tokens and API key values are masked. An empty Bearer token (as a login request's script fills in later) adds no header, as in Postman, and a warning says so: an `Authorization` header the request has stays. Digest and NTLM credentials answer only their own scheme's challenges (NTLM also Negotiate) from the request's origin, never a Basic one. Credentials in the URL (`https://user:password@host`) are sent as Basic auth, as curl and Postman send them, unless the request has an `Authorization` header or an auth helper; as in Postman, a `{{variable}}` there may hold any character: `@ / ? # \` in its value are encoded, so a password with them neither ends the user info nor makes the URL invalid. The history masks the password, or the user name of a URL without one (`https://***@host`), which is then the credential. An API key or OAuth 2.0 token sent in the query is URL-encoded and replaces a parameter of the same name.
- **Body:** none, raw (JSON, XML, text, HTML, JavaScript, with Beautify), form-data with files, x-www-form-urlencoded, a file (see [Sending a file as the body](#sending-a-file-as-the-body)), or GraphQL. Form-data is sent as browsers, curl and Postman send it: field and file names in UTF-8, a field's content type as typed (one with a line break or NUL is refused), and a file without one with the type of its extension (`.gif`, `.jpg`, `.jpeg`, `.png`, `.svg`, `.txt`, `.htm`, `.html`, `.pdf`, `.xml`, `.json`; any other gets `application/octet-stream`). An empty body is no body, sent without a `Content-Type`, as in Postman: raw text that is empty once its `{{variables}}` are replaced, a form with no enabled field, or a file body without a file (a warning says so).
- **Scripts:** pre-request and post-response (test) scripts, in Python, JavaScript, C# or PowerShell (see [Scripts](#scripts)).
- **Settings:** per-request SSL verification and redirects, which client certificate applies, and the description. Redirects are followed like `curl -L`: at most *max redirects* (0 follows none; a negative `maxRedirects` on a request, as earlier imports of `--max-redirs -1` saved it, uses the workspace's), never from https to http; 301 and 302 turn a POST into a GET, 303 any method but HEAD, and 307 and 308 send the method and body again. The `Authorization`, `Proxy-Authorization`, `Cookie` and `Host` headers and the auth helper's (such as an API key) go only to the request's origin (scheme, host and port); other headers follow the redirect. The URL a redirect leads to never takes the user and password of the request's URL (they are sent as its `Authorization` header), so it is shown, kept in the history and recorded without them.

Text editors show tabs 4 columns wide (the Tab key moves to the next field). Every line break, whether CRLF, LF or a lone CR, shows as one, and an edited body, script or description is saved with LF line breaks.

**Response tabs**

- **Body:** pretty or raw view, wrap, copy (the whole body as shown, with its own line breaks), save (as received). *Save as…* asks before replacing a file (*Cancel* is the default), and its message says when that is not the body as received: only the start of one the maximum response size cut, or the copy a history entry keeps (secrets masked as `***`, and cut at the history's size limit). A body over 1 MB shows its first 1 MB with a note; Copy and Save as… still give all of it. A large body shows *Formatting…* while it is prepared. XML with a document type (`<!DOCTYPE …>`) is shown as received: its entities are never expanded.
- **Headers:** as the server sent them, `Content-Encoding` and `Content-Length` included. A value too long for the pane ends with `…`: Enter or a double-click on its row shows the whole header, wrapped, to read or select, and *Copy* (the default button) copies its value. gzip, deflate and br bodies are decoded; a body that can't be decoded, or uses another coding, is kept as received, with a warning. The maximum response size applies to the body as received and once decoded.
- **Timeline:** the exact request sent (with the generated `Accept-Encoding: gzip, deflate, br`, unless you typed one, such as `identity`, which is sent instead), DNS/connect/TLS/TTFB/download timing, and warnings.
- **TLS:** protocol and cipher, the client certificate sent and whether the server asked for one, and the server chain with validation details.
- **Scripts:** what the scripts did: tests, printed output, errors with their traceback, and the variables they set. Like a body, what is over 1 MB shows its first 1 MB with a note.

### Sending a file as the body

For a large payload, choose **file** in the Body tab instead of pasting it into the editor. TermRest keeps only the file's path and reads the file each time the request is sent, streaming it: it is never loaded into memory, and you can keep editing it in another program, even one that keeps it open for writing (Excel, a log writer). Whatever such a program adds while the file is being sent is left out: it is sent as long as it was when sending started (form-data files too).

- **Type:** *Binary* sends the file as it is (`application/octet-stream`). *JSON*, *XML*, *Text*, *HTML* and *JavaScript* set the matching Content-Type and check the file first:
  - **JSON** must be valid, UTF-8 JSON. A byte order mark is fine; UTF-16 is not, and is named as the problem.
  - **XML** must be well-formed, in the encoding its declaration names (legacy ones such as windows-1252, ISO-8859-15 or Shift_JIS included). Its DTD is never processed, so entities never read other files.
  - **Text, HTML and JavaScript** must look like text (no NUL bytes, no UTF-16).
- **When it is checked:** when you choose the file (a file that does not fit the type is turned down, with the line and column of the problem) and again each time the request is sent, since the file may have changed. A file that no longer fits is not sent. Checking streams through a 64 KB buffer: only a single value larger than that, such as a long base64 string, needs more.
- **Content-Type:** a `Content-Type` header on the request wins, for example `application/merge-patch+json` with the JSON type. To send deliberately broken JSON, choose *Binary* and set the header yourself.
- **Paths:** a relative path starts at the collection's folder, and `{{variables}}` work. The file must be in the workspace folder, unless you allow files from outside it (see [Workspace layout](#workspace-layout)); until then, the Body tab doesn't look up a path outside the folder either, and says so. The file is sent byte for byte: `{{variables}}` inside it are not replaced. A typed path is looked up in the background once you pause typing, so a slow or disconnected network drive never holds up the UI.
- **Postman:** the body is Postman's binary body (`"mode": "file"`); the type is stored as `"language"` in its `file` object, which Postman ignores.
- **Recordings** keep the file's path, not its content (see [Recording requests](#recording-requests)). While recording, the Body tab says so.

## Scripts

Scripts run around each request, as in Postman: **pre-request** scripts before it is sent (set variables, sign it, fetch a token), **post-response** scripts once the response is in (tests, keep a value for the next request). Write them in **Python**, in **JavaScript** with Postman's `pm` object (so the scripts of Postman collections run too), in **C#**, or in **PowerShell**, and keep a version in several languages if you like: each request runs its scripts in **one language** (see [Languages](#languages)). Each language runs in **a runtime you choose**: your project's Python (a virtual environment, so its packages are there to use), Node.js or Bun, the .NET SDK (10 or later), and PowerShell (7 or later).

> [!CAUTION]
> **Scripts are programs, and TermRest does not sandbox them.** Choosing a runtime for a workspace lets every script of its collections run as you, with your permissions. They can read, change, delete or upload your files, keys and credentials, see your variables and secrets, start programs, download packages, and send anything anywhere. Postman runs its scripts in a restricted sandbox; TermRest hands them the full Python, Node.js, Bun, .NET or PowerShell you chose, with nothing to contain a malicious one. **Treat a collection with scripts like a program you download and run:** choose runtimes only for workspaces whose collections you wrote or have read. See [Script safety](#script-safety).

TermRest itself carries no script engine. Until a request has a script, nothing extra runs. Then one process per runtime runs every script of the session, and it stops after 5 idle minutes. Measured on the same machine (memory while scripting; TermRest starts Node.js with V8 options that halve its memory):

| Option | Added to TermRest | Memory while scripting |
|---|---|---|
| **Your Python, one process per session (TermRest)** | nothing | about 23 MB, only while scripts are in use |
| **Your Node.js, one process per session (TermRest)** | nothing | about 68 MB (35 MB private), only while scripts are in use |
| **Your Bun, one process per session (TermRest)** | nothing | about 46 MB, only while scripts are in use |
| **Your .NET SDK, a program built from the C# scripts (TermRest)** | nothing | about 26 MB (7 MB private), only while scripts are in use; building them also keeps the SDK's compiler server (about 160 MB) for 10 minutes |
| **Your PowerShell 7, one process per session (TermRest)** | nothing | about 130 MB (70 MB private), only while scripts are in use |
| Jint (JavaScript interpreter, embedded) | +7.6 MB per binary | +16–19 MB, always |
| V8 (ClearScript, embedded) | +28.5 MB native library per platform | +50 MB, always |
| Roslyn (C# scripting, embedded) | +23 MB, and no Native AOT | +190 MB, always |

A runtime answers in about a millisecond once started; starting takes 0.1–0.3 s. PowerShell is the heaviest: it starts in about 0.9 s and answers in about 6 ms. C# scripts are built first (see *C# scripts* below).

**Choosing the runtimes**

- **In the UI:** *Tools › Scripts…*, one row for each language. Type a command (`python3`, `node`, `bun`, `dotnet`, `pwsh`) or a path, which may be relative to the workspace (`.venv/bin/python`, `.venv\Scripts\python.exe`). A command is looked up in the folders of PATH only (on Windows as `name.exe`, unless the name already ends with a program extension from PATHEXT, such as `.cmd`: `python3.12` is `python3.12.exe`; for pyenv-win's shim, type `python.bat`), never in the workspace or TermRest's own folder. A file picked with *Browse…* in the workspace's own folder is kept as `./name`, a path. Or press **Find**: for Python, it looks for `.venv`, `venv` and `env` in the workspace, then `python3` and `python` on PATH; for JavaScript, `node` and `bun` on PATH; for C#, `dotnet` on PATH, then `~/.dotnet`; for PowerShell, `pwsh` on PATH, then `C:\Program Files\PowerShell\7` on Windows. A Python found in the workspace is filled in without being run, as it may have come with the repository: **Check** runs it and shows the version. Python 3.8, Node.js 18 or Bun 1.1, the .NET 10 SDK, and PowerShell 7.0 or later are needed (for C#, the SDK: a .NET runtime alone cannot build scripts; for PowerShell, `pwsh`: Windows PowerShell 5.1, `powershell.exe`, is too old).
- **Your choices are kept per workspace folder in your user settings**, never in the workspace. A cloned repository cannot choose a program for TermRest to run. Outside Windows, a folder is known by its path exactly as written: a path in another case may be another folder (on Linux, and on a macOS volume formatted case-sensitive), so neither these choices nor the proxy and files from outside the folder you allowed carry over to it.
- **On the command line:** `--python <path>`, `--javascript <path>`, `--csharp <path>` and `--powershell <path>` on `send` and `run`; without them, the UI's choices for that workspace apply. A path there is relative to the current folder, as the command line's other paths are (from a repository's root: `termrest run postman/api.postman_collection.json --python .venv/bin/python`); a command such as `node` is looked up on PATH. `--script-order <languages>` changes the [language order](#languages) for the run. `--no-scripts` turns scripts off.
- **No runtime chosen for a language:** in the UI, its scripts are skipped and the request is sent; the status line says `⚠ scripts not run` and the *Scripts* tab says which. On the command line, that is an error (so a CI run never passes without its tests): the request is not sent, and the message says to pass `--python`, `--javascript`, `--csharp`, `--powershell` or `--no-scripts`. A missing runtime does not make a request choose another language: the language depends on the scripts only (see below).

**Writing scripts**

- **A request's scripts** are in its *Scripts* tab: *Language* shows the scripts of a language, and a `•` marks the languages that have some.
- **A collection's or folder's scripts:** right-click it in the tree › *Scripts…* (or *Request › Collection or folder scripts…*). They run for every request inside.
- **Syntax colors:** the script editors color the syntax of the language shown: comments, strings, numbers and constants, keywords, function calls, types, and PowerShell's variables and parameters, in the [color theme](#color-themes)'s script colors. This is plain highlighting, with no completion or error checking.
- **Order:** each phase runs the scripts of the collection, then the folders (outermost first), then the request, one at a time: each script sees the variables and request changes of the ones before it.
- **A failing pre-request script stops the request.** This covers an exception, a syntax or compile error, and a timeout. The request is not sent, nor added to the history or a recording, and the error is shown with the script's line.
- **Time limit:** each phase may take 30 seconds (building C# scripts does not count); the runtime is then stopped and started again for the next script. *Tools › Scripts › Stop scripts* stops a script that runs at once; its request says it was stopped (`Python was stopped while the scripts ran.`).
- **Working folder:** the runtimes run in the workspace folder, so scripts can import your own modules from it (`import helpers`, `require('./helpers')`, `Import-Module ./Helpers.psm1`) and read its files. Python and PowerShell modules load once per process; *Tools › Scripts › Stop scripts* reloads them.
- **Values are text:** a variable or header set to `42` reads back as `"42"` in the next request or phase; a date or time as ISO 8601 text (`2024-01-02T03:04:05+00:00`, `2024-01-02`), and a GUID, URL or decimal as its text; lists and objects as JSON.

**<a id="languages"></a>Languages: one per request**

A collection, a folder and a request can each have a pre-request and a post-response script **in each language**: a version of the same script in several languages, such as Postman's JavaScript test and a PowerShell one. A request runs the scripts of **one language only**, in one runtime, one script at a time: TermRest never runs two scripts, or two runtimes, at once for a request. (A script may still start other programs, another runtime included: that is up to the script.) Scripts in the other languages are kept, and don't run.

- **The language order.** *Tools › Scripts…* lists the languages from first to last; **▲ ▼** change the order, for all your workspaces. By default: Python, JavaScript, C#, PowerShell. On the command line, `--script-order powershell,javascript` puts those first for the run (the others follow in your order).
- **Which language runs.** A request's scripts run in the first language of the order in which **none of them is missing**: each script that runs, the collection's, its folders' and its own, has a version in that language. If one is missing in a language, the next is tried. A request whose test exists only in JavaScript therefore runs its collection's JavaScript scripts too, even when the collection also has PowerShell ones and PowerShell comes first.
- **Choosing a language for a request.** *Run in* in its *Scripts* tab: *Auto* follows the order; *Python*, *JavaScript*, *C#* or *PowerShell* runs that language only. Next to it, the tab says what runs (`→ runs PowerShell`), or why nothing can (`✗ 1 script lacks PowerShell`).
- **Whether pre-request and post-response scripts run.** The boxes above a request's two scripts, *Pre-request* and *Post-response*, say whether the scripts of that phase run for it, its collection's and folders' included. They are ticked when there are some. Untick one to skip them (a login request that must not run the collection's authentication script, say); tick one where there are none and the request needs one. Only the phases that run need a script in the chosen language.
- **When no language has them all,** the request is not sent, in the UI too, and the message says which scripts each language lacks. Write the missing ones, choose a language, or turn a phase off.
- **What ran is in the response's *Scripts* tab:** when other languages have scripts too, it says which language ran (`Language: JavaScript, the first of your language order with all the scripts of this request (PowerShell lacks the post-response script of request 'Users/List')`).

| Python: the `tr` object | |
|---|---|
| `tr.environment`, `tr.collection_variables`, `tr.globals` | `.get(name, default)`, `.set(name, value)`, `.unset(name)`, `.has(name)`, `.clear()`, `.to_dict()`, and `tr.environment["name"]`. Values set are kept in memory until TermRest closes (Postman's "current values"); files are never changed. *Tools › Scripts* shows how many there are and clears them. |
| `tr.variables` | Reads every scope (this request, the environment, the collection, the globals: the first that has it). `.set()` is for this request only. `.replace_in("{{base}}/x")` replaces `{{variables}}` and dynamic variables (`{{$guid}}` …) in a text. |
| `tr.request` | `.method`, `.url`, `.headers` (`.get`, `.get_all`, `.set`, `.add`, `.remove`, `.items()`; as with requests, a dict, another mapping or a list of pairs can replace them), `.body` (raw bodies; bytes are sent as their UTF-8 text, a dict, list, tuple or bool as JSON). Pre-request scripts can change them; `{{variables}}` they contain are replaced afterwards. A request they leave in a state that can't be sent isn't sent, and what they printed, tested and set is kept. Test scripts see the request as it was sent. |
| `tr.response` | In test scripts: `.status` (or `.code`), `.reason`, `.headers`, `.time` (ms), `.size`, `.text()`, `.json()`, `.content` (bytes). |
| `tr.test(name, fn)`, `@tr.test(name)` | Records a test that fails when `fn` raises, as `assert` does. |
| `tr.info` | `.phase` (`prerequest` or `test`), `.script`, `.request_name`, `.collection`, `.environment`. |
| `print(…)` | Goes to the response's *Scripts* tab, and to stderr on the command line. So does `logging`, also through a handler an earlier script set up (`logging.basicConfig()`). |

| JavaScript: Postman's `pm` object | |
|---|---|
| `pm.environment`, `pm.collectionVariables`, `pm.globals`, `pm.variables`, `pm.iterationData` | `.get`, `.set`, `.unset`, `.has`, `.clear`, `.toObject`, `.replaceIn`, as in Postman. |
| `pm.request` | `.method`, `.url` (`.query.add`, `.toString()`, `.getHost()` …; as in Postman, its `host` and `path` may be set as text), `.headers` (`.add`, `.upsert`, `.remove`, `.get`, `.has`, `.each` …), `.body.raw`. Pre-request scripts can change them, as with `tr.request`; a request they leave in a state that can't be sent (a `query` that is no longer a list) isn't sent, and what they printed, tested and set is kept. |
| `pm.response` | `.code`, `.status`, `.headers`, `.responseTime`, `.responseSize`, `.json()`, `.text()`, `.stream`, `.cookies`, and Postman's assertions: `.to.have.status(…)`, `.to.have.header(…)`, `.to.have.body(…)`, `.to.have.jsonBody(…)`, `.to.be.ok`, `.success`, `.clientError`, `.withoutContent`, `.json` …, and `.to.have.jsonSchema(schema)` (with `ajv` installed in the workspace: ajv 6, Postman's, checks formats such as `email` or `date-time` itself; ajv 8 needs `ajv-formats` for them, and a format it doesn't know fails the test, saying so), which also checks any value: `pm.expect(data.items[0]).to.have.jsonSchema(itemSchema)`. |
| `pm.test(name, fn)`, `pm.expect(value)` | Tests (also asynchronous: `async` functions, or a `done` callback) and chai's assertions: `equal`, `eql`, `include`, `a`/`an`, `property`, `lengthOf`, `above`/`below`, `oneOf`, `members`, `keys`, `match`, `throw`, `not`, `deep`, `nested` …, with chai's messages. As in Postman, a property no assertion has, such as a misspelled `.to.be.ture`, fails the test (`Invalid Chai property: ture. Did you mean "true"?`) instead of checking nothing. |
| `pm.sendRequest(request, callback)` | With a callback, or `await` it. It uses the runtime's own `fetch`: TermRest's certificates and proxy settings do not apply. |
| `pm.info`, `pm.cookies`, `console.log` … | As in Postman; `console` output goes where `print` does, that of the modules and packages scripts `require` too (Node.js's own warnings included). |
| The API from before `pm` | `tests["name"] = …`, `postman.setEnvironmentVariable(…)`, `responseBody`, `responseCode`, `responseHeaders`, `responseTime`. |
| `require(…)` | Node.js modules, your own files, and the packages installed in the workspace (`npm install lodash crypto-js moment`). Postman's globals `_`, `CryptoJS`, `cheerio` and `tv4` load those packages when first used; `xml2Json` uses `xml2js`. Bun installs packages it cannot find by itself. |
| Not supported | `pm.execution.skipRequest()` (the request is not sent, with an error), `setNextRequest` (requests run in order), `pm.vault`, `pm.require` (Postman's package library), `pm.cookies.jar()`, `pm.visualizer` (ignored). Each script phase runs in a fresh context, as in Postman. |

**C# scripts**

- **Built, then run.** TermRest writes the C# scripts of a collection into one small program, each script the body of a method, and your .NET SDK builds it as a C# file without a project (`dotnet run --file`, new in .NET 10). The first build takes a few seconds (more when it restores NuGet packages); a change, about a second. Built programs are kept in TermRest's cache folder, so unchanged scripts start at once, in the next TermRest too: `%LOCALAPPDATA%\TermRest\csharp`, `~/Library/Caches/TermRest/csharp` or `~/.cache/termrest/csharp` (`TERMREST_CACHE_DIR` moves it). The 16 most recently used are kept, a few hundred KB each.
- **A script is the body of an async method:** statements, local functions, `await`, `return`. It may start with `using` directives and the directives of C# files: `#:package Name@1.2.3` (NuGet packages), `#:project path`, `#:property Name=Value`, and `#:include helpers.cs` (SDK 10.0.300 or later) for a file of your own types, as scripts cannot declare classes. A `global using` applies to all the collection's C# scripts and the files they include, as the `#:` directives do. Relative paths are relative to the workspace, and a change to what `#:include` or `#:project` brings in builds the scripts again: code, resources and build files (`.cs`, `.resx`, `.razor`, project files, `.props`, `.targets` …) in the project's folder and in the projects it references (`<ProjectReference>`, in turn), and the `Directory.Build.props`/`.targets`, `Directory.Packages.props`, `global.json` and `nuget.config` in the folders above them. Not the other files in those folders, such as the database or logs a program writes, and not references whose paths use MSBuild properties (`$(…)`) or wildcards, or files imported from other folders. Besides .NET's usual namespaces (`System`, `System.IO`, `System.Linq`, `System.Net.Http` …), `System.Text`, `System.Text.Json.Nodes` and `System.Text.RegularExpressions` are imported.
- **Compile errors name the script and its line**, as the compiler reports them; one in a file a script brings in (`#:include`, `#:project`) names that file, its line and column. The scripts before it still run, and a script that does not compile does not keep the collection's other requests from running theirs. Nor does one whose `#:package` can't be restored or whose `#:project` is missing: after its first failure, the other requests leave it out at once until TermRest closes (or *Tools › Scripts › Stop scripts*). The directives of all the scripts apply to the whole program: when another request's script breaks a request's own (its `#:property TreatWarningsAsErrors=true` turns a warning in them into an error, say), they are built without the collection's other scripts and report only their own errors.
- **Building uses the SDK's compiler server**, which stays for 10 minutes after a build (about 160 MB), as with any `dotnet build`: it makes the next build quick. *Tools › Scripts › Stop scripts* stops it.

| C#: the `tr` object | |
|---|---|
| `tr.Environment`, `tr.CollectionVariables`, `tr.Globals` | `["name"]` (null when not set; setting null unsets), `.Get(name, fallback)`, `.Set(name, value)`, `.Unset(name)`, `.Has(name)`, `.Clear()`, `.ToDictionary()`. Kept as in Python. |
| `tr.Variables` | Reads every scope (this request, the environment, the collection, the globals). `.Set()` is for this request only. `.ReplaceIn("{{base}}/x")` replaces `{{variables}}` and dynamic variables (`{{$guid}}` …) in a text. |
| `tr.Request` | `.Method`, `.Url`, `.Headers` (`["name"]`, `.Get`, `.GetAll`, `.Set`, `.Add`, `.Remove`, `.Has`), `.Body` (raw bodies). Pre-request scripts can change them. |
| `tr.Response` | In test scripts: `.Status` (or `.Code`), `.Reason`, `.Headers`, `.Time` (ms), `.Size`, `.Text()`, `.Json()` (a `JsonNode`: `json["name"]`, `(int)json["id"]`), `.Content` (bytes). |
| `tr.Test(name, () => …)` | Records a test that fails when it throws; `async () => …` tests are awaited. Assertion libraries from NuGet work too. |
| `tr.Assert(condition)`, `tr.AssertEqual(expected, actual)` | Fail with the code that failed: `assertion failed: tr.Response.Status == 201`, `tr.Response.Code: expected 201, got 200`. `AssertEqual` compares numbers as numbers and JSON as JSON: `tr.AssertEqual("Ada", json["name"])`. |
| `tr.Info` | `.Phase`, `.Script`, `.RequestName`, `.Collection`, `.Environment`. |
| `Console.WriteLine(…)` | Goes where `print` does. `HttpClient` sends requests of the script's own (TermRest's certificates and proxy settings do not apply). |

**PowerShell scripts**

- **Run by PowerShell 7 (`pwsh`)**, on Windows, Linux and macOS: Windows PowerShell 5.1 does not run them. One `pwsh` runs the scripts of the session, started with `-NoProfile -NonInteractive`: your profile is not loaded, and modules load when a script imports them (`Import-Module`, `using module`, or by using their commands).
- **A script is a script block**, run with `$tr` in its scope. Functions and variables it defines are its own; modules it imports stay loaded for the next scripts. `exit` ends the script, not PowerShell; with a code other than 0 it fails it, as `sys.exit(1)` does in Python. `break` and `continue` outside a loop end it too.
- **Errors stop the script**, as exceptions do in the other languages: `$ErrorActionPreference` is `Stop`, so a command that reports an error (`Get-Item` of a missing file, say) fails it, with the script's line. Set it to `Continue` in a script that should carry on.
- **What a script writes goes to the *Scripts* tab,** in the order it writes it: its output (objects as PowerShell formats them), `Write-Host`, `Out-Host` and `[Console]::WriteLine` (also from `ForEach-Object -Parallel` blocks and thread jobs), warnings, and `Write-Verbose` or `Write-Debug` when their preference lets them through. Tables come without the colors PowerShell 7.2 gives them on a terminal. Output inside `$tr.Test` blocks is dropped; use `Write-Host` there.
- **`$tr` belongs to the script's own runspace:** a `ForEach-Object -Parallel` block or a thread job (`Start-ThreadJob`) runs in another one, where calling `$tr` (`$using:tr`) waits for the script, which waits for the block, until the time limit stops PowerShell. Let such blocks return their results, and use `$tr` after them.
- **PowerShell is the heaviest runtime** (see the table above): about 130 MB while scripts are in use, started in about 0.9 s.

| PowerShell: the `$tr` object | |
|---|---|
| `$tr.Environment`, `$tr.CollectionVariables`, `$tr.Globals` | `.Get(name)`, `.Get(name, default)`, `.Set(name, value)`, `.Unset(name)`, `.Has(name)`, `.Clear()`, `.ToHashtable()`. Names are case-sensitive, as in Postman. Kept as in Python. |
| `$tr.Variables` | Reads every scope (this request, the environment, the collection, the globals). `.Set()` is for this request only. `.ReplaceIn('{{base}}/x')` replaces `{{variables}}` and dynamic variables (`{{$guid}}` …) in a text. |
| `$tr.Request` | `.Method`, `.Url`, `.Headers` (`.Get`, `.GetAll`, `.Set`, `.Add`, `.Remove`, `.Has`, `.Items()`, `.Count`), `.Body` (raw bodies; `$null` for others; bytes are sent as UTF-8 text, a hashtable or object as JSON). Pre-request scripts can change them; a request they leave in a state that can't be sent (`.Headers` set to `$null`) isn't sent, and what they printed, tested and set is kept. |
| `$tr.Response` | In test scripts: `.Status` (or `.Code`), `.Reason`, `.Headers`, `.Time` (ms), `.Size`, `.Text()`, `.Json()` (`ConvertFrom-Json`: `$json.name`), `.Content()` (bytes). |
| `$tr.Test(name, { … })` | Records a test that fails when the block throws or a command in it fails. |
| `$tr.Assert(condition)`, `$tr.AssertEqual(expected, actual)` | Fail with the code that failed: `assertion failed: $tr.Response.Status -eq 201`, `$tr.Response.Code: expected 201, got 200`. `AssertEqual` compares numbers as numbers, text exactly (case included, unlike `-eq`), and JSON objects and hashtables by their members. |
| `$tr.Info` | `.Phase`, `.Script`, `.RequestName`, `.Collection`, `.Environment`. |
| `Write-Host …` | Goes where `print` does. `Invoke-RestMethod` sends requests of the script's own (TermRest's certificates and proxy settings do not apply). |

```python
# Python: a collection pre-request script that logs in once, then sends the token with every request
import json, urllib.request

if not tr.environment.get("token"):
    body = json.dumps({"user": tr.environment.get("user"), "password": tr.environment.get("password")}).encode()
    login = urllib.request.Request(tr.variables.replace_in("{{base}}/login"), body, {"Content-Type": "application/json"})
    with urllib.request.urlopen(login) as reply:
        tr.environment.set("token", json.load(reply)["token"])
tr.request.headers.set("Authorization", "Bearer " + tr.environment.get("token"))
```

```python
# Python: a request's post-response script
@tr.test("creates the item")
def _():
    assert tr.response.status == 201, tr.response.status
    assert tr.response.json()["name"] == "Ada"

tr.collection_variables.set("item_id", tr.response.json()["id"])
```

```js
// JavaScript: the same tests, as Postman writes them
pm.test("creates the item", () => {
    pm.response.to.have.status(201);
    pm.expect(pm.response.json().name).to.eql("Ada");
});
pm.collectionVariables.set("item_id", pm.response.json().id);
```

```csharp
// C#: the same tests
tr.Test("creates the item", () =>
{
    tr.AssertEqual(201, tr.Response.Status);
    tr.AssertEqual("Ada", tr.Response.Json()["name"]);
});
tr.CollectionVariables.Set("item_id", tr.Response.Json()["id"]);
```

```powershell
# PowerShell: the same tests
$tr.Test('creates the item', {
    $tr.AssertEqual(201, $tr.Response.Status)
    $tr.AssertEqual('Ada', $tr.Response.Json().name)
})
$tr.CollectionVariables.Set('item_id', $tr.Response.Json().id)
```

**Where the results show**

- **In the UI**, the response's status line says `tests 3/3 passed`, `✗ 1 of 3 tests failed`, `✗ script error` or `⚠ scripts not run`. Its *Scripts* tab lists the tests, the output, errors with their line, the variables set, and the scripts that did not run. *Help › Scripting* summarizes the `tr` and `pm` objects.
- **`termrest send`** prints the output and tests to stderr. It exits with 1 when a test fails or a script fails.
- **`termrest run`** prints them under each request, and a request whose tests fail counts as failed. `--report-json` includes `tests` and `scriptError`.
- **Recordings** include `_termrest.scripts`: tests, the error and traceback, the names (not the values) of the variables set, and what the scripts printed, all redacted like the rest.

**Compatibility**

- Scripts are stored in Postman's `event` entries, one per phase and language. JavaScript ones stay as Postman wrote them (`"type": "text/javascript"`), so Postman runs them too. The others are marked `"type": "text/x-python"`, `"text/x-csharp"` or `"text/x-powershell"`, and `"disabled": true`: Postman and Newman run every script that is not disabled as JavaScript, so a Python one would fail there. Postman keeps them, and doesn't run them; TermRest runs them though they are marked disabled (a disabled JavaScript script, as Postman does, it doesn't run). TermRest marks the scripts it writes; scripts it doesn't change stay as they are.
- In v3 YAML collections, each script carries its `language` (`language: text/x-python` and so on), Postman's are `text/javascript`.
- What a request's *Scripts* tab chooses (*Run in*, the *Pre-request* and *Post-response* boxes) is kept with the request, only when it is not the default: `"termrest": {"scripts": {"language": "powershell", "preRequest": false}}` in JSON, and the same `termrest:` keys in v3 YAML. Postman ignores it.

### Script safety

TermRest runs scripts the way a terminal runs commands: it starts the runtime you chose and lets the scripts do anything that runtime can. That makes them as capable as any program you write, and as dangerous as any program you run without reading it. You choose the runtimes, so the responsibility for what runs is yours.

**What a script can do, with nothing to stop it**

- Read, change, delete or upload any file you can: source code, SSH keys, cloud credentials (`~/.azure`, `~/.aws`, `~/.kube`), browser profiles.
- See every variable, secret ones included, the requests with their `Authorization` headers, the responses, and TermRest's environment variables (tokens, in CI). It can send them anywhere: `fetch`, `HttpClient`, `urllib`, `Invoke-RestMethod` and `pm.sendRequest` do not go through TermRest.
- Start programs, which may keep running after TermRest stops the runtime, install software, and change your system's settings.
- Run code it brings in:
  - **C#:** `#:package` restores NuGet packages, and their build steps (MSBuild targets, analyzers) run while the scripts are built, before any script does. `#:sdk`, `#:project` and `#:include` bring in more.
  - **JavaScript:** `require` loads npm packages from the workspace's `node_modules`, and Bun downloads missing ones by itself.
  - **Python:** scripts import whatever the chosen environment has.
  - **PowerShell:** scripts load modules from your module path and the workspace (`Import-Module`, `using module`, or just by using their commands), and `Install-Module` downloads more. Your execution policy applies to the script files they load (`.ps1`, `.psm1`), not to the scripts themselves, which TermRest hands to PowerShell as text.
  - A repository can ship such code itself, for example a `node_modules` folder, a `.venv` or a `Modules` folder.

**What TermRest does, and does not do**

- It never chooses a runtime by itself. Runtimes are chosen per workspace folder and kept in your user settings, never in the workspace, so a cloned repository cannot turn its scripts on. *Find* only looks when you press it, and doesn't run a Python it finds in the workspace. A command such as `node` is looked up in the folders of PATH only, so a `node.exe` that a repository ships never runs in its place. *Find* and *Check* run a runtime in TermRest's cache folder, never in the workspace, whose files would otherwise act before you choose it (a `bunfig.toml` that preloads code into Bun, a `global.json` that points `dotnet` to other SDKs).
- It never uses a proxy that a workspace names until you allow it: a proxy URL in `.termrest/settings.json` is allowed per workspace folder in your user settings, never in the workspace (*Tools › Settings › Save*, and again whenever it changes), so a cloned repository cannot send your requests through a proxy of its choosing.
- It never sends a body or form-data file from outside the workspace folder until you allow it for that folder (*Tools › Settings*, or `--allow-outside-files`), so a cloned repository's requests cannot upload your other files. Until then it doesn't open certificates or a CA bundle on a Windows network share outside the folder either, so a cloned repository cannot make Windows sign in to a server of its choosing with your credentials. Scripts are not bound by this: they can read any file.
- Without a runtime, the UI sends requests and skips their scripts (`⚠ scripts not run`). On the command line, scripts without a runtime are an error until one is chosen (in the UI, or with `--python`, `--javascript`, `--csharp` or `--powershell`), or until you pass `--no-scripts`.
- `tr` and `pm` cannot change collection, environment or settings files: the values scripts set stay in memory until TermRest closes. Recordings redact secrets, in what scripts print too.
- Programs that scripts start get no terminal. On Linux and macOS the runtimes run in a session of their own, without TermRest's terminal, so a program that asks on the terminal (ssh or git for a passphrase, sudo) fails at once instead of drawing over TermRest's screen: use an agent, a credential helper or a token. Node.js and Bun get this on Linux when `setsid` (util-linux) is on PATH; on macOS, programs that JavaScript scripts start can still use the terminal. gpg's pinentry may still appear, as gpg-agent shows it on the terminal named by `GPG_TTY`. On Windows, each runtime has a hidden console of its own. If TermRest is killed while a script runs, that script runs to its end.
- It does **not** sandbox, review, sign or limit scripts.
  - JavaScript gets a fresh context per phase for Postman compatibility, not for isolation: Node.js's `vm` module is not a security boundary.
  - A timeout stops the runtime, not what a script already did.
- Once a runtime is chosen for a workspace, **every** script of its collections runs on the next send, without asking. That includes scripts that arrive with a `git pull`, an import or a teammate's change, and the scripts of the collection and its folders, which a request's *Scripts* tab does not show. A script in another language than the one that runs today runs as soon as the language order, a request's *Run in*, or the scripts change.

**Before you turn scripts on**

- **Read the scripts first,** in every language (*Language* in the *Scripts* tab: a `•` marks those with scripts): the request's *Scripts* tab, and right-click › *Scripts…* on the collection and on each folder. In the files, they are the `event` entries (`script.exec`) of JSON collections, disabled ones included, and the `scripts` of v3 YAML files. Review changes to them as you review code.
- **Keep collections you did not write, or have not read, in a workspace folder with no runtime chosen,** and don't allow files from outside it there. On the command line, use `--no-scripts` for them, without `--allow-outside-files`.
- **Choose runtimes you installed yourself.** *Find* suggests the workspace's own `.venv`: in a repository you did not create, make sure it is one you made before you press **Check** or save.
- **Pin package versions** (`#:package Name@1.2.3`, lock files, `Import-Module Name -RequiredVersion 1.2.3`), and prefer package feeds you trust.
- **Run collections from unknown sources in a container, a VM or a separate account.** In CI, give the job only the secrets its requests need: scripts see the job's environment.
- **To turn scripts off again,** clear the runtime in *Tools › Scripts…* and save, or remove it from `pythonInterpreters`, `javaScriptRuntimes`, `csharpRuntimes` or `powerShellRuntimes` in your `settings.json`.

## Color themes

Choose a theme in *Tools › Color theme…*. Moving through the list previews each theme live on the whole UI; *Use theme* keeps it and *Cancel* restores the previous one. The choice applies to all your workspaces. Tick *Use only in this workspace* to store it in the workspace's `.termrest/settings.json` instead, for example to give a production workspace a red menu bar.

- **Built in:** Terminal.Gui's themes (Default uses your terminal's own colors; Dark, Light, TurboPascal 5, Anders, 8-Bit, Green Phosphor, Amber Phosphor) and TermRest's Dracula, Gruvbox Dark, High Contrast, Monokai, Nord, Solarized Dark and Solarized Light.
- **Method, status and script colors:** every theme colors the request methods in the tree (GET, POST, PUT, PATCH, DELETE, others), the response status (2xx/3xx, 4xx, 5xx), and the syntax of scripts.
- **Custom themes** are JSON files. *Copy as custom theme…* (or `termrest themes copy`) writes a copy of any theme for you to edit, and *Reload theme files* picks up your edits without a restart. A copy of a custom theme needs no other theme file, so it looks the same in every workspace: it starts from a built-in theme, and its `schemes` keep the roles the custom theme sets beyond its palette (palette edits don't change those). Its name can't be one another custom theme already has (case aside): only one theme of a name is loaded, so the other would never show. The dialog proposes a free name (`My Dark 2`). A theme file names a `base` theme and changes some of its palette colors. Every color of the UI is derived from the palette's 28 colors. For full control, `schemes` sets single roles of Terminal.Gui's color schemes:

  ```jsonc
  {
    "name": "Production",
    "base": "Nord",                      // any theme, including custom ones (default: Default)
    "palette": {                         // color names (DarkBlue, Gold…), #rrggbb, rgb(r,g,b), or None (the terminal's color)
      "bar": "#5c0000",                  // menu and status bar
      "barText": "White",
      "selection": "DarkRed"
    },
    "schemes": {                         // optional: "<foreground> [on <background>] [bold|italic|underline|faint|reverse|…]"
      "Base": { "Focus": "white on #8b0000 bold" }
    }
  }
  ```

  Palette colors: `background`, `text`, `muted`, `accent` (hot keys), `selection`, `selectionText`, `input` (fields and tables), `bar`, `barText`, `dialog`, `dialogText`, `success`, `warning`, `error`, `get`, `post`, `put`, `patch`, `delete`, `otherMethods`, and for scripts `syntaxComment`, `syntaxKeyword` (with PowerShell's operators such as `-eq`), `syntaxString`, `syntaxNumber` (with constants such as `true` and `None`), `syntaxVariable` (PowerShell's `$name`), `syntaxFunction` (with PowerShell's commands and Python's decorators), `syntaxType` and `syntaxParameter` (PowerShell's `-Name`). A script color set to `None`, or too close to the editor's background to read, is drawn in the editor's text color. Schemes: `Base`, `Menu`, `Dialog`, `Error`, `Accent`, `Success`, `Warning`. Roles: `Normal`, `HotNormal`, `Focus`, `HotFocus`, `Active`, `HotActive`, `Highlight`, `Editable`, `ReadOnly`, `Disabled`. A theme keeps the roles its base sets until it changes a surface color (any but the status, method and script colors), which derives its schemes from its palette again; a status scheme likewise until its color changes. Mistakes such as an unknown key or an invalid color are listed in the dialog and by `termrest themes`, and the rest of the file still applies.
- **Where theme files go:** your own themes are in the `themes` folder of your TermRest configuration folder. That is `%APPDATA%\TermRest` on Windows, `~/Library/Application Support/TermRest` on macOS, and `$XDG_CONFIG_HOME/termrest` (default `~/.config/termrest`) on Linux. Set `TERMREST_CONFIG_DIR` to move it. Themes in a workspace's `.termrest/themes` folder are shared with everyone who uses the workspace. A custom theme with the name of a built-in one replaces it.
- **Which theme is used:** `--theme <name>` for one session, else `$TERMREST_THEME`, else the workspace's choice (`theme` in `.termrest/settings.json`), else yours (`settings.json` in the configuration folder), else Default.

```sh
termrest themes                          # list the themes (● marks the one in use) and the theme folders
termrest themes use Nord                 # for all workspaces (--only-here: this workspace only)
termrest themes copy Nord "My Nord"      # a custom theme to edit (--in-workspace: in .termrest/themes)
termrest --theme "Solarized Light"       # this session only
```

## Request history

Every request you send from the UI or with `termrest send` is recorded with its response: status, headers, timing and the body (up to 256 KB by default). Collection runs and `snapshot` are not recorded.

- **Storage:** history is kept per user, outside the workspace, so it never ends up in git. The location is `%LOCALAPPDATA%\TermRest\history` on Windows, `~/Library/Application Support/TermRest/history` on macOS, and `$XDG_STATE_HOME/termrest/history` (default `~/.local/state/termrest/history`) on Linux, with one folder per workspace. Set `TERMREST_STATE_DIR` to move it. On Unix only you can read the files.
- **Masking:** secrets are masked before anything is written to disk:
  - `Authorization`, `Cookie`, and headers named like credentials (as in recordings: token, secret, password, API key, auth key, subscription key, signature, session id …), sent or received;
  - the value of every `Set-Cookie`;
  - the request's auth credentials and the values of variables marked secret (the value the request actually used, also when it comes from `--var`, a script's `tr.variables`/`pm.variables`, or another variable it references), including where a server echoes them back in a response body: as they are, URL-encoded (fully, or only where a URL needs it) or JSON-escaped (as .NET, JavaScript, Python, PHP or Go write it); in UTF-8, or in UTF-16 or UTF-32 when the body's charset or byte order mark says so; also where the size limit cuts it. This includes values that test scripts store in variables marked secret, in the very response they came from;
  - credentials typed into the request itself (auth fields, such headers, the password in the URL, a client certificate's passphrase), which are stored as `***`; a `Cookie` header keeps its cookie names (`sid=***`). A Basic user name without a password is the credential (an API key, as Stripe takes it: `curl -u sk_test_…:`), and so is the user name of a URL without one: both are stored as `***` (`https://***@host`), and the value is masked wherever it appears, like a password. `{{variable}}` and `env:NAME` references are kept, also when joined by separators (`{{user}}:{{password}}`, `session={{sid}}`). Opening such an entry says so, and sending it or adding it to a collection asks first (*Cancel* is the default): type them again. Auth the request inherited from its folder or collection is stored masked too, but while the request is still in its collection, an entry sent again runs as that request does: with its folders' scripts and the auth it inherits from them. That request is the one at the entry's exact path, never another with the same name (or the same path through slashes in names), in the collection the entry was sent from: the history keeps that collection's file, so another collection of the same name is never taken for it, by *Copy as cURL* and *Go to request in collection* either. Where that file is gone, or for an entry an earlier version recorded, the collection with its name is used while only one has it; while several have that name, the entry is sent on its own, and the status bar says so. Once the request is gone (deleted, or renamed or moved and the collection reloaded), the entry is sent on its own: auth it inherited is put back as it was sent, the status bar says so, and masked credentials ask first;
  - the password of a URL in an error message: the proxy's (from the proxy setting or `HTTPS_PROXY`, spaces and letters that aren't ASCII included) when it refuses a connection, `http://user:***@proxy:3128/`, or that of a URL that isn't valid (`'http://user:***@host:{{port}}/x' is not a valid http(s) URL.`), or there the user name of one without a password, the credential (`'https://***@{{host}}/v1' is not a valid http(s) URL.`). Errors show it masked everywhere, in the UI and on stderr too, and so does the warning about a redirect TermRest doesn't follow (`ftp://user:***@host/`).
- **In the UI:** press **F9** (or click *History* in the status bar) to switch the sidebar to the history list, and type in *Filter* to narrow it.
  - Click an entry to open the request exactly as it was sent, together with its recorded response. The status line gives the size of the body as received, also where the history keeps only its start, or none of it (the Body tab says so). TLS details and script results are not kept, and the TLS and Scripts tabs say so; the Timeline shows the auth, SSL verification and redirects as *(not recorded)*, and the client certificate chosen for the request.
  - Right-click an entry for *Send again*, *Save to collection…*, *Copy as cURL*, *Go to request in collection*, *Delete entry* and *Clear history…*. On the entry that is open, they use your edits.
  - **Ctrl+N** adds the opened entry to a collection.
- **Settings:** *Tools › Settings › History* turns recording off and sets how many entries (500) and how many KB of each response body (256) are kept. In `.termrest/settings.json` these are `historyEnabled`, `historyLimit` and `historyResponseKb`. The History list shows at most that many entries.
- **Clearing:** *Clear history…* and `termrest history … --clear` delete the entries, then their bodies. When another program has the history file open, nothing is deleted; when it has a body open (an antivirus scan), the entries are gone and that body is left for the next clear. Both say so (the command with exit code 1).

```sh
termrest history samples                     # newest first: id, time, status, duration, method, URL, request
termrest history samples --show c637d3a5     # one entry in full: request, response headers and body
termrest history samples --json -n 100       # machine-readable
termrest history samples --clear
termrest send … --no-history                 # send without recording
```

## Recording requests

Click **● Record** at the right end of the menu bar (or press **F12**, or choose *Tools › Start recording*). Every request you send after that is recorded with its response:

- **The request:** method, URL, headers, cookies, query parameters, and the body: text and form fields as sent; files by reference only (see below).
- **The response:** status, headers, cookies and the full body. A text body that isn't valid UTF-8 is recorded as text, with U+FFFD for the bytes that can't be decoded. A body is text when its Content-Type says so: `text/*`, form data, or a subtype with a text format as one of its words (JSON, XML, YAML, CSV, GraphQL, JavaScript …, as in `application/problem+json`); binary JSON such as `application/ubjson` is recorded as base64. A compressed body is recorded decoded: `bodySize` is its size as received, `content.size` once decoded, and `content.compression` the difference.
- **Connection details:** timing, the server address, and TLS (protocol, cipher, server certificate chain, the client certificate sent).
- **Failures:** the error of requests that failed.

While recording, the button turns into a red badge with a blinking dot, the time since recording started and the number of requests recorded: **● REC 01:23 · 3 requests · Stop F12**. The Response pane's title shows which request a response was recorded as (**Response · ● recorded #3**). A request that can't be written to the recording (a full disk, say) is reported once in the status bar; the recording goes on.

**Files are recorded by reference, never their content.** For a [body sent from a file](#sending-a-file-as-the-body) or a form-data file field, the recording keeps the file's path, size and modification time. The file stays where it is, and the recording stays small. You see this in three places: the Body tab says so while recording, the status line after sending says *● recorded with the file's path, not its content*, and the stop dialog counts the requests that sent files. In the HAR file, such a body has no `text`; it has a `_file` entry that says `"contentRecorded": false`, plus a comment that HAR viewers show:

```jsonc
"postData": {
  "mimeType": "application/json",
  "_file": { "path": "C:\\work\\payloads\\orders.json", "size": 48213772, "modified": "2026-11-05T16:58:01.123-07:00", "contentRecorded": false },
  "comment": "Sent from a file. The recording keeps the file's path, not its content."
}
```

Click the badge (or press F12) to stop. TermRest asks where to save the recording. The default is `termrest-recording-<date>-<time>.har` in the workspace folder; type another path or use *Browse…*.

- **The file** is JSON in **HAR 1.2** format (HTTP Archive), the format browser dev tools export. Chrome, Edge and Firefox (*Network › Import HAR*), Fiddler, Charles, Insomnia and HAR viewers open it.
- **TermRest's own details** are under `_termrest` keys, as HAR allows: collection, request, environment, auth type, settings, TLS, warnings, what the [scripts](#scripts) did, and what was redacted.
- **The other choices:** *Keep recording* goes on, and *Discard* throws the recording away. Quitting or switching workspaces while recording asks first.

```jsonc
{
  "startedDateTime": "2026-11-05T13:29:29.512-07:00",
  "time": 272.098,
  "request": {
    "method": "GET",
    "url": "https://postman-echo.com/headers",
    "headers": [{ "name": "Authorization", "value": "Bearer [REDACTED]" }, { "name": "Cookie", "value": "sails.sid=[REDACTED]" }],
    …
  },
  "response": {
    "status": 200,
    "content": { "size": 615, "mimeType": "application/json; charset=utf-8", "text": "{\"headers\":{…\"authorization\":\"[REDACTED]\"…}}" },
    …
  },
  "timings": { "blocked": -1, "dns": 7.344, "connect": 151.33, "ssl": 145.214, "send": 0, "wait": 112.898, "receive": 0.126 },
  "serverIPAddress": "162.159.142.41",
  "_termrest": {
    "collection": "TermRest Demo",
    "request": "Auth/Auth inherited from folder",
    "environment": "Postman Echo",
    "tls": [{ "protocol": "Tls13", "cipherSuite": "TLS_AES_256_GCM_SHA384", "serverCertificates": […], "clientCertificate": null, … }],
    "redacted": [{ "in": "request.headers", "name": "Authorization", "reason": "credential", "count": 1 }, …]
  }
}
```

- **Secrets are redacted** before anything is written, even to the temporary file. The placeholder `[REDACTED]` keeps the shape of the data, and each entry lists what was redacted, where and why (`_termrest.redacted`). Redacted are:
  - `Authorization` and `Proxy-Authorization` credentials. The scheme stays: `Bearer [REDACTED]`. A Basic credential's user name and password are also redacted wherever they appear later (8 characters or more), as the user name can be the key itself (Stripe and Freshdesk take theirs so); `_termrest.auth` gives only `Basic`, never the user name.
  - Cookie values in `Cookie` and `Set-Cookie`. Names and attributes stay.
  - Headers, URL parameters (query and fragment), and JSON, form and form-data fields named like credentials: password, secret, token, API key, auth key (`X-Auth-Key`), signature, session id, private key, connection string, client assertion …; in URLs also `key`, `sig` and `auth` (Google API keys, Azure SAS signatures, Firebase tokens). JSON sent as `text/plain` or `text/html`, bodies that aren't entirely UTF-8, and form-data sent as a raw body (a browser's *Copy as cURL*, imported) are covered too. Such a raw form-data body is recorded as text (as base64 if its files are binary); its file parts are kept as sent.
  - Passwords in URLs (`https://user:[REDACTED]@host`), and the user name of a URL without a password (`https://[REDACTED]@host`), which is then the credential.
  - The values of auth credentials, secret variables (the value the request used, also from `--var` or a script) and variables named like credentials, wherever they appear: URLs, headers, bodies (also URL-encoded, fully or only where a URL needs it, or JSON-escaped as .NET, JavaScript, Python, PHP or Go write it, and in UTF-16 or UTF-32 text, which is recorded as UTF-8) and error messages. Values that test scripts store in such variables are redacted in the response they came from too (a token from a login response).
  - Every value redacted by name, wherever it appears later in the recording. For example, a token from a login response is redacted when a later request sends it. A cookie that is set again (a load balancer's, say) is looked for with its latest value only.

  Add your own names in *Tools › Settings › Recordings redact* (`recordingRedact` in `.termrest/settings.json`). Names are compared without case and separators, at the end of a name: `Tenant-Key` covers `X-Tenant-Key` and `tenant_key`. Names you add while recording are redacted in the requests recorded from then on (requests recorded before stay as they are); names you remove are still redacted until the recording stops. While `.termrest/settings.json` can't be read, recording does not start: fix the file, then open the workspace again.
- **Memory:** a recording does not grow in memory.
  - Each request is written to a temporary file as soon as its response arrives, on a background thread.
  - Bodies are written in 64 KB pieces, and saving copies the temporary file into the HAR file through a small buffer.
  - Responses are included up to the workspace's maximum response size (*Tools › Settings*, 50 MB). Files sent with a request are never read for the recording.
  - If requests come faster than they can be written, sending waits once 4 are waiting.
- **Temporary files** are private to you (on Unix, a folder only you can read) and are deleted after saving or discarding:
  - Windows: `%TEMP%`.
  - macOS: `$TMPDIR`, which is per user.
  - Linux: `$TMPDIR` if it is set, otherwise `/tmp`. When `/tmp` is a RAM file system (tmpfs), `/var/tmp` is used instead, so a large recording doesn't take memory after all.

  Set `TERMREST_TEMP_DIR` to use another folder. On Unix, the saved HAR file is also readable only by you (mode 600).
- **Recovery:** if TermRest closes while recording (a closed terminal, a crash), nothing is lost. The next start says so, and *Tools › Recover unsaved recordings* saves the recording. Only folders private to you are offered (on Unix, mode 700, as TermRest creates them).
- **Command line:** `--record <file>` on `send` and `run` records the same way, for example as evidence from a CI run:

```sh
termrest run "samples/TermRest Demo.postman_collection.json" -e "Postman Echo" --record run.har
termrest send api.postman_collection.json "Users/Get user" --record get-user.har
```

Ctrl+C (or SIGTERM, as a cancelled CI job sends) cancels `send` and `run`: they still save the recording, and `run` still writes its `--report-json` report, however long that takes. A second Ctrl+C ends TermRest at once; *Tools › Recover unsaved recordings* then saves the recording.

## Importing cURL commands

- **Paste** a curl command into the URL bar. It replaces the current request (method, URL, headers, body, auth, client certificate), as in Postman; **Ctrl+S** saves it. Pasted into a request from the history, it becomes a request of its own, as one imported from the menu: it takes neither the auth nor the scripts of the entry's request or its folders, which aren't meant for the host it names. Typing a curl command and pressing Enter does the same. A URL is never taken for a command, even one whose path ends in `/curl`.
- **File › Import cURL command…** (also in the *Request* menu) opens the command as a new request instead. **Ctrl+N** then adds it to a collection.
- **Syntax:** bash, cmd and PowerShell syntax are all understood (PowerShell's `&` call operator too), so every flavor of the browsers' *Copy as cURL* works, including line continuations, `$'…'` strings, escaped emoji and `^` escapes.
- **Options imported:**
  - request: `-X`, `-H` (`-H 'Name:'` drops one of curl's own headers: with `-H 'Content-Type:'`, as TermRest's export writes for a POST without a body, no type is added for `-d` data), `-A`, `-e`, `-b name=value`, `-r`, `-I`;
  - body: `-d`/`--data-*` (`name=value` pairs become form fields; data with an escape that isn't UTF-8, such as `%F6` from a page in windows-1252, stays text and is sent as typed; empty data, as in `-d ''`, gives a request without a body and without curl's default type), `--json` (also `--json @file`), `-F` (text and files), `-T` (a URL without a query whose path is empty or ends with `/` gets the file's name, as curl sends it), `-G`/`--url-query`;
  - auth: `-u` (Basic, or with `--digest`/`--ntlm`/`--negotiate`) and `--oauth2-bearer`;
  - client certificates: `--cert`/`--key`/`--pass`/`--cert-type`;
  - TLS and redirects: `-k` and `--max-redirs` (TermRest follows redirects by default, as with `-L`, keeping credentials for the original origin; `--max-redirs 0` follows none, and `--max-redirs -1` (curl's "no limit") keeps the workspace's limit, with a note). `--proto-redir` is skipped: TermRest never follows a redirect from https to http.
- **Notes:** options that are workspace settings in TermRest (proxy, timeouts, CA files, HTTP version) and unsupported options are listed as notes after the import. Options that only affect curl's own output, such as `-s`, `-v`, `-o` or `--compressed`, are skipped without a note.

```sh
termrest import-curl api.postman_collection.json "curl -X POST https://api.example.com/v1/items -H 'Content-Type: application/json' -d '{\"name\":\"x\"}'"
pbpaste | termrest import-curl postman/collections/Contoso --folder Imported --name "Create item"   # or read from stdin
termrest import-curl api.postman_collection.json "curl …" --dry-run                                 # show what would be imported
```

The other direction: `termrest send … --curl`, *Request › Copy as cURL*, and *Copy as cURL* in the history. The commands are for POSIX shells (bash, zsh), not cmd or PowerShell. A command sends what TermRest sends:

- the URL as it goes out (percent-encoded, the host name in ASCII);
- the method, except for a POST, which curl sends anyway for its data (`-X GET` for a GET with a body; a HEAD request gets `--head`);
- the headers as they go out: no typed `Content-Length`, no `Content-Type` and the like without a body, and an empty value as `-H 'Name;'`;
- a request without a body as TermRest sends it: unless its method is GET, HEAD, DELETE, OPTIONS or CONNECT, it goes with `Content-Length: 0` (without one, IIS and others answer a POST with 411 Length Required), which `--data-raw '' -H 'Content-Type:'` sends (empty data, without curl's default type);
- text form fields with `--form-string`, or `-F 'name="text";type=…'` when they have a content type, and form files as `name=@"path";type=…`, so curl reads only that file and sends the type TermRest sends, as typed or the one of the file's extension (a type without a `/`, such as `jpeg`, needs curl 8.12 or later);
- redirects as TermRest follows them: `--location` with TermRest's limit (`--max-redirs`), `--proto-redir '=https'` for an https URL (TermRest never follows a redirect from https to http), and `--post301 --post302` when a method other than POST has a body (or the empty data above). Where TermRest stops at a redirect and shows it, curl ends with an error, and after a 303 curl keeps a PUT, PATCH or DELETE (without its body) where TermRest sends a GET. A request that follows no redirects (following off, or a limit of 0) gets no `--location`. Neither does one whose auth adds a header such as an API key, or that has a `Proxy-Authorization` header, as curl would send that header to every host it is redirected to, nor one with a client-certificate file, which curl would present to every host it is redirected to (TermRest selects the certificate for each URL); a comment line above the command says so.

A URL with `[]{}` gets `--globoff`, Digest and NTLM credentials keep their scheme (`--digest`, `--ntlm`), client-certificate files are given with full paths (so the command works from any folder), and a certificate from the OS store, which curl can't use, is named in a comment line above the command. A request curl can't send the same way isn't exported, and the reason is shown: a form field whose name contains `=`, or whose content type curl would cut short (at a space, or at a character such as `=` or `(` before the first `;`) or partly read as its own settings, or a HEAD request with a body. Nor is a request TermRest itself wouldn't send, such as one whose body file doesn't exist (a command for it would send no body): its errors are shown instead.

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

- **Opening:** open the repository folder (`termrest my-api`) or a collection folder directly; a JSON collection, environment or globals file in `postman/collections`, `postman/environments` or `postman/globals` opens the repository's workspace too (also for `send`, `run`, `history` and `import-curl`). A workspace may mix JSON and YAML collections. A folder in `postman/collections` is a collection when it or its folders hold request files or definitions: Postman writes no `.resources/definition.yaml` for a collection without settings of its own.
- **Saving changes only what changed.** An edit rewrites the affected lines of one file and keeps comments, key order, quoting and everything TermRest doesn't use, such as a body in a form it can't read (one without a `type`, or of a type it doesn't know: it shows as *none* or empty, and stays as written until you edit the body). Renaming, moving, reordering and deleting requests and folders renames, moves and deletes their files (with their saved examples) the way Postman does, including the `order` values. Files TermRest doesn't manage are never deleted, never overwritten, and keep their names: a folder given the name of a deleted folder that still holds such files is saved as `<name>-1`, with its real name inside. A leading `.` in a name becomes `-` in its file name, so it doesn't hide the file.
- **Lists and maps:** headers, parameters, and collection and folder variables can be a list of entries or, as Postman writes plain ones, a `name: value` map. TermRest reads both and keeps the form a file uses; variables become a list once one of them is disabled, secret or described.
- **Secrets:** a value marked `secret: true` (as Postman marks it, often leaving the value out of the file) or `type: secret` is secret: masked in the UI, the history and recordings. New secrets are marked the way the file marks its others, and a value the file leaves out stays out while it is empty.
- **Disabled values:** an environment value marked `disabled: true`, as Postman marks it, is not used. A value you disable is marked the way the file marks its others: `disabled: true`, or `enabled: false` in a file whose values have `enabled`.
- **Saving safely.** A save that fails part-way is undone. If TermRest is stopped in the middle of one, opening the collection reports the `.termrest-saving-…` folder that holds the files it was moving.
- **Files TermRest couldn't read.** A `definition.yaml` or request file that couldn't be read when the collection was loaded is never saved over, as that would lose what it holds: saving writes everything else and names it, and the edits that belong in it wait until it is fixed (or deleted) and the collection is opened again. A request or folder whose file was deleted meanwhile (a `git checkout`, say) is written anew when you rename or move it, a folder with everything it held. A deleted folder (the collection's own folder too) also comes back whole, with its definition, as soon as something is saved into it (a request you edit, add, move or import there) or the folder itself changes; saving other changes leaves it deleted. Before writing a YAML file TermRest reads it back: a file that wouldn't read back unchanged is not written, and saving names it.
- **New and imported collections:** in a workspace that already uses the v3 layout, new collections and environments are created as YAML, and *File › Import Postman collection* converts JSON collections. The choice is in *Tools › Settings › New collections* (`collectionFormat`: `v3` or `v2.1`). *Import Postman environment…* loads a file from `postman/environments` where it is; one from elsewhere is saved as a new environment in the workspace's format, converted when needed (a globals export becomes the workspace's globals, `postman/globals/workspace.globals.yaml`; see [Workspace layout](#workspace-layout)).
- **Wrapping long lines:** some earlier Postman versions wrapped long values at 80 columns; current versions write each value on one line. TermRest writes wrapped values exactly as Postman did (except in two cases where Postman's output reads back changed: a line of a single space in a quoted value, and a line of only white space after an indented one; those are written to read back exactly): text breaks at spaces, URLs break with an escaped `\` line break, and multi-line text becomes a folded `>` block. Choose in *Tools › Settings › Wrap YAML lines*, or set `yamlLineWidth` in `.termrest/settings.json`:
  - *Like the files* (the default, no setting): files that are wrapped stay wrapped, and new files are not wrapped.
  - *Never* (`0`) or *At column* (for example `80`; 20 to 1000).

  Only new and changed values are wrapped: lines you don't change keep their layout, so a different setting never rewrites a whole file. *Help › Workspace info* and `termrest validate` show which collections are wrapped.
- **Conversion and validation:**

  ```sh
  termrest convert "Contoso API.postman_collection.json"     # → postman/collections/Contoso API/
  termrest convert "Contoso API.postman_collection.json" --line-width 80   # wrapped like older Postman versions
  termrest convert "postman/collections/Contoso API" --to v2.1 -o contoso.postman_collection.json
  termrest convert dev.postman_environment.json              # → postman/environments/dev.environment.yaml
  termrest convert postman/environments/dev.postman_environment.json   # → postman/environments/dev.environment.yaml too: copies go into the input's workspace
  termrest validate "postman/collections/Contoso API"        # checks that every file converts losslessly
  ```

- **Tested on real files:** 31 collections written by Postman, from public GitHub repositories. Editing and reverting every part of every request leaves all files byte-for-byte unchanged. Every corpus file that Postman wrapped comes out byte-for-byte the same when TermRest writes its content from scratch (files with comments are skipped).

Not yet supported in v3 collections:

- Postman's GraphQL, gRPC and WebSocket request types are not shown; their files are left untouched. HTTP requests with a GraphQL body work.
- Saved examples (`*.example.yaml`) are kept and move with their request, but are not shown, and `convert --to v2.1` leaves them out. Converting to v3 writes a request's saved responses as example files, an example's name in its file (`name:`) where its file name can't hold it (two examples whose names differ in case only, a `/` or `:`, or more than 64 characters), as Postman keeps it.
- Converting to v3 writes collection and folder variables as Postman's v3 schema has them: their values as text (`42` becomes `"42"`), without v2.1's other keys (`id`, `name`, `system`), and a secret variable without its value, which Postman keeps out of the collection's files: the value stays in the JSON file it came from, and `convert` and *Import Postman collection* name such variables. Set their values in an environment.
- A client certificate pinned on a request (v2.1 `certificate`) has no v3 equivalent; use the certificate registry.
- A collection's or folder's auth can be a list of credential sets, each with URL `rules` (`rules: {url: api.example.com}`). Sending honors them: a request uses the first set whose rules match its URL (patterns as for client certificates, `api.example.com`, `https://*.example.com/v1/*` or `*`, variables allowed; a set without rules matches any URL), and a request that no set matches is sent without that auth, with a warning, so credentials never reach a host their rules leave out. TermRest doesn't edit the sets, but a copy of a folder (*Duplicate*) keeps them, rules included. `convert --to v2.1` keeps only the first one, which Postman then sends to every URL (v2.1 has one auth per folder); `convert` warns about it.
- A disabled JavaScript script (`"disabled": true` in v2.1) is left out when converting to v3, which can't mark a script disabled; Postman doesn't run it either way.
- A value whose first line is two or more spaces, as Postman writes it (a `|2-` block whose first line holds only spaces), can't be read: the file is reported. TermRest writes such values double-quoted. A value that starts with an empty line, which Postman writes with the block's indentation on that line, reads fine.
- A key that holds a space followed by `*` (`Accept *: v`) reads as text rather than as a key, so TermRest writes such keys double-quoted (`"Accept *": v`).
- Header names over 1,024 characters can't be saved.

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
- **Matching:** patterns follow Postman's rules: `scheme://host[:port]/path`, where `*.domain` matches the domain and its subdomains, and a pattern without a port matches any port. A host can be written in Unicode or in ASCII (punycode): `https://bücher.example/*` and `https://xn--bcher-kva.example/*` match the same URLs. An IPv6 address goes in brackets, in any of its forms: `https://[::1]:8443/*`. The most specific match wins. A `certificate` set on an individual request in the collection (a Postman v2.1 field) takes precedence over the registry; without URL patterns of its own, it goes to the request's own origin only. A redirect target gets the certificate selected for its own URL.
- **Passphrases:** avoid storing them in the file. Use `env:NAME` to read an environment variable, or `{{variable}}` to read a (secret) environment value.
- **A certificate that can't be loaded** (a missing, locked or unreadable file, a wrong passphrase, or on Windows a file on a network share outside the workspace folder, until you allow files from outside it: see [Workspace layout](#workspace-layout)) is not used: the request is sent without it, and a warning says why. Likewise an extra CA bundle that can't be read: only the OS trust store is used then.
- **OS stores**
  - *Windows:* `CurrentUser\My` or `LocalMachine\My`. Keys marked non-exportable, and smart-card or TPM-backed keys, work because the TLS handshake signs inside CNG/SChannel.
  - *macOS:* `CurrentUser\My` is your login keychain (identities with private keys).
  - *Linux:* use PEM/PFX files. The .NET user store under `~/.dotnet/corefx/cryptography/x509stores/my` also works.
- **Server trust:** the OS trust store by default. Add private or corporate CAs with **Tools › Settings › Extra CA bundle** (PEM); it is read once per session and again when the file changes, and a server certificate it vouches for must be issued for server authentication. Turn verification off per request in **Settings**, or globally (`--insecure` on the CLI).
- **Accuracy:** TLS session resumption is disabled and client-certificate selection is always explicit, so every request does a full handshake with exactly the certificate shown. Each request presents only the certificate selected for its URL, never one an earlier request used, which is important when testing certificate changes.
- **Windows intermediate store:** to send a certificate's chain, .NET adds the chain's CA certificates that Windows can't find to your intermediate certificate store (`CurrentUser\CA`).

```sh
termrest certs list samples                       # load every registry entry and show subject, expiry, key status
termrest certs store                              # OS-store certificates with private keys
termrest certs match https://client.badssl.com/ samples
```

## Command line

```text
termrest [<workspace>] [--theme <name>]        open the UI (default: current directory)
termrest send <collection> <request> [opts]    send one request; body to stdout (pipes and files get binary bodies, and any body with --raw, exactly as received), status/diagnostics to stderr
termrest run <collection> [--folder F] [opts]  run all requests in order, with their scripts; exit code 1 if any fail, files of the collection can't be read, or the report or recording can't be written
termrest validate <files…>                     check that files (JSON, YAML, v3 folders) round-trip unchanged through TermRest
termrest convert <input> [--to v3|v2.1] [-o O] convert a collection or environment between v2.1 JSON and v3 YAML; exit code 1, writing nothing, if files of the collection can't be read or the result can't be written. A folder must be a v3 collection (with `.resources/definition.yaml` or `*.request.yaml` files; in `postman/collections`, in its folders too), for `validate` too
termrest history [<workspace>] [opts]          requests sent from a workspace, newest first
termrest import-curl <collection> [<command>]  add a request from a curl command (read from stdin when omitted)
termrest certs list|store|match                client-certificate helpers
termrest themes [use <theme>|copy <theme> <name>]  list, choose or copy color themes of the UI
termrest snapshot [<workspace>] [--send]       render the UI headlessly (smoke test; --send includes a live response)

<collection>: a *.postman_collection.json file or a v3 collection folder
common options:      -e/--env <name|file>  --var key=value  --timeout <ms>  -k/--insecure  --record <file.har>
                     --python <path>  --javascript <path>  --csharp <path>  --powershell <path>
                     --script-order <languages>  --no-scripts  --allow-outside-files
send options:        -i/--include  -v/--verbose  -o/--output <file>  --raw  -f/--fail  --curl  --no-history
run options:         --bail  --allow-http-errors  --report-json <file>
history options:     -n/--limit <n>  --show <id>  --json  --clear
import-curl options: --folder <path>  --name <name>  --dry-run
certs list options:  --allow-outside-files
convert options:     --line-width <n>  (wrap YAML output at column n; 0: never)
themes options:      -w/--workspace <folder>  --only-here (use)  --in-workspace (copy)
snapshot options:    --send  --request <path>  --width <n>  --height <n>  --theme <name>
```

```sh
termrest run "samples/TermRest Demo.postman_collection.json" -e "Postman Echo" --report-json report.json
termrest send api.postman_collection.json "Users/Get user" --var userId=42 -i
termrest send api.postman_collection.json "Users/Get user" --curl     # print an equivalent curl command (scripts do not run; none, and exit code 2, if it can't be sent or curl can't send it the same way)
termrest run api.postman_collection.json --python .venv/bin/python   # run the collection's Python scripts and tests
termrest run postman-export.json --javascript node                   # run a Postman collection's pm.* scripts and tests
termrest run api.postman_collection.json --csharp dotnet             # build and run the collection's C# scripts and tests
termrest run api.postman_collection.json --powershell pwsh           # run the collection's PowerShell scripts and tests
termrest run api.postman_collection.json --powershell pwsh --javascript node --script-order powershell
                                                                     # PowerShell versions of scripts where they all exist, else Postman's
```

Paths on the command line are relative to the current folder, those of `--python`, `--javascript`, `--csharp` and `--powershell` included (a name without a folder, such as `node`, is looked up on PATH); runtimes chosen in the UI (*Tools › Scripts…*) are relative to the workspace.

The `--report-json` report redacts secrets the way [recordings](#recording-requests) do, test names and errors included. Missing folders are created; on Unix only you can read it.

`send` exits with 0 when done; 1 when there was no response, a test or script failed, the body couldn't be saved with `-o`, the recording wasn't saved, or files of the collection or the workspace settings can't be read; 2 for a usage problem (an unknown or ambiguous request or `-e` environment, or one `--curl` can't export); 22 for HTTP status 400 or more with `--fail`; 63 when the response body was cut at the maximum response size (*Tools › Settings*, `maxResponseSizeMb`, 50 MB), so what `-o` or standard output received is only its start; 130 when cancelled. Warnings raised while sending (a redirect not followed, a header left out, a cut body) are printed after the response. A name that matches several requests, in the workspace's collections or in one collection, is reported as ambiguous: give its path (`Folder/Name`) or its collection. So is an `-e` name that several environments of the workspace have: nothing is sent, and the error lists their files; pass one of them with `-e` instead (a JSON or YAML environment file). Without `-e`, the workspace's active environment is used.

A body counts as binary when its Content-Type isn't text (`text/*`; JSON, XML or JavaScript named as a word of the subtype, as in `application/problem+json`, `application/x-ndjson` or `image/svg+xml`; form data), or, with no Content-Type, when it contains a NUL byte. Office documents (`application/vnd.openxmlformats-officedocument.…`) are ZIP files: binary. A console window shows binary content as a hex dump unless `--raw` asks for the bytes. Text bodies are decoded from the charset their Content-Type names (also one with a stray `;` at its end, which .NET's own parser rejects), legacy code pages included (windows-1252, Shift_JIS, GBK, Big5 …), and UTF-16 without a byte order mark in the byte order its text shows, in `send`, `history --show`, the UI, scripts and recordings.

On Windows, commands write UTF-8 without a byte order mark to files and pipes, and read standard input as UTF-8 (a byte order mark is fine). In a console window every character shows, and every character typed or pasted into standard input is read (`import-curl` without a command: end it with a line of Ctrl+Z, then Enter); the code page isn't changed. PowerShell decodes a program's output with `[Console]::OutputEncoding`: before `$u = termrest send …` or `| ConvertFrom-Json`, run `[Console]::OutputEncoding = [Text.UTF8Encoding]::new()`. `>` keeps the bytes in cmd and in PowerShell 7.4 and later.

## Postman compatibility

| Area | Status |
|---|---|
| Collection v2.1.0 JSON | Read/write; untouched content round-trips byte-for-byte (tabs, key order, escaping, BOM/CRLF preserved; a file that mixes CRLF and LF line breaks is saved with the kind most of its lines have) |
| Collection v2.0.0 JSON | Read/write (v2.0 auth shape kept) |
| Collection v3.0.0 YAML (Postman v12 "Native Git") | Read/write: `postman/collections/<name>/` folders with `*.request.yaml` and `definition.yaml`; edits rewrite only the changed lines, and a YAML file without a final line break (an environment's too) is saved without one, unless its last value then ends with a line break (a body or PEM written as a `|` block), which needs one. See [Postman v3 (YAML) collections](#postman-v3-yaml-collections) |
| Environments / globals | Read/write: JSON (`*.postman_environment.json`, `*.postman_globals.json`) and YAML (`postman/environments/*.environment.yaml`, `postman/globals/workspace.globals.yaml`) |
| Folders, descriptions, saved examples, `protocolProfileBehavior`, unknown fields | Kept; edited fields are rewritten surgically |
| Body modes | raw, urlencoded, formdata (text + files), file (binary, or typed and checked: `file.language`), graphql |
| Auth | inherit, noauth, basic, bearer, apikey, digest, ntlm, oauth2 (access token) — others are kept, not applied |
| Scripts | JavaScript (`pm.*`, and the older `postman.*` and `tests[]`) runs in Node.js or Bun, Python (`"type": "text/x-python"`; v3 `language: text/x-python`) in Python, C# (`"type": "text/x-csharp"`) with the .NET SDK, PowerShell (`"type": "text/x-powershell"`) in PowerShell 7; scripts in TermRest's languages are written `"disabled": true` so Postman and Newman skip them; see [Scripts](#scripts) for the `pm` API supported |
| Client certificates | `request.certificate` honoured; Newman certificate-list format for the registry |
| cURL | Import (bash, cmd and PowerShell syntax) and export |

Run `termrest validate <your exports>` to check that TermRest would save your files without changing them. When a file would change, it shows the first line that differs; where only white space differs, it shows `\t` for each tab, `\r` for a carriage return (a CRLF line break in a file whose other lines end in LF, or the reverse) and `·` for each space (Postman indents JSON with tabs, so a file indented with spaces would be rewritten). Saves are atomic, write through symbolic links, and keep file permissions (Unix mode / Windows ACL), so a `chmod 600` environment holding secrets stays private. Imported and converted copies (*Import Postman collection/environment…*, `termrest convert`) are as private as their source from the moment they are created: on Unix they get its read and write permissions, and the folders of a converted collection can be entered only by whoever can read the source. Files must be UTF-8 (a byte order mark is fine); a file in another encoding is reported, never re-encoded. So are a JSON file that repeats a key within an object or holds half of a surrogate pair (`\ud83d`, an emoji cut in two; the error gives the line and column), a file over 256 MB, and anything that isn't a regular file (such as a device a symbolic link points at). Importing a collection that is already open reloads it from disk, and asks first if it has unsaved changes. A collection where the workspace keeps them (a `*.postman_collection.json` file in the workspace folder or in `postman/collections`, or a v3 collection folder there) is loaded where it is. Any other, including a file in the workspace folder under another name such as `collection.json`, is copied in as `<name>.postman_collection.json`, or converted in the v3 layout.

Saving never silently undoes what another program changed since TermRest read a file (a `git pull`, Postman, an editor): *Ctrl+S* lists those files and asks before overwriting them, with Cancel as the default, and saves the other files either way. *Tools › Client certificates* saves `.termrest/certificates.json` with each change and asks the same way when the file changed on disk; there, Cancel loads the file as it is instead, without your change. A request you renamed or moved whose file changed on disk keeps its name and place, with its examples, until you overwrite; then it is moved and written at once. To keep the other version, quit without saving and open the workspace again (or pick a JSON collection again with *File › Import Postman collection…*). A file that was only touched, with the same content, doesn't count as changed.

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
                    HTTP executor, certificates, history, recording (HAR, redaction), scripts (Python, JavaScript
                    and PowerShell workers, C# programs built by the user's .NET SDK),
                    cURL import, workspace
src/TermRest.Tui    Terminal.Gui v2 views and dialogs
src/TermRest.Cli    `termrest` executable (System.CommandLine): TUI by default + headless commands
tests/…Core.Tests   xUnit v3: fidelity, mapper, URLs, variables, certificates, YAML, Postman v3, history, cURL,
                    recording and redaction, in-process Kestrel HTTP/TLS/mTLS tests
tests/…Tui.Tests    headless UI tests: renders the real window, injects mouse/keyboard input, asserts on screen text
tests/…Cli.Tests    termrest commands run as a process: exit codes, output, and the files they write
```

```sh
dotnet build
dotnet test                                    # Microsoft.Testing.Platform runner (see global.json)
TERMREST_STORE_TESTS=1 dotnet test             # Windows: also run the non-exportable cert-store mTLS test
TERMREST_V3_CORPUS=<folder> dotnet test        # also edit and revert every Postman v3 collection found under <folder>
TERMREST_TEST_PYTHON=<python> dotnet test      # the Python for script tests (default: python3 or python on PATH; skipped without)
TERMREST_TEST_JAVASCRIPT=<runtime> dotnet test # the Node.js or Bun for script tests (default: node or bun on PATH; skipped without)
TERMREST_TEST_DOTNET=<dotnet> dotnet test      # the .NET SDK (10+) for C# script tests (default: dotnet on PATH; skipped without)
TERMREST_TEST_POWERSHELL=<pwsh> dotnet test    # the PowerShell (7+) for PowerShell script tests (default: pwsh; skipped without)
```

Notes:

- Terminal.Gui 2.5's `TextView` is marked obsolete in favour of the separate `Terminal.Gui.Editor` package. That package has not shipped a build for 2.5 yet, so TermRest uses `TextView` behind a small `TextArea` wrapper, which makes the later switch (folding, for one) a one-file change. `TextArea` also colors scripts: small hand-written lexers (`src/TermRest.Tui/Syntax`) color one line at a time from what the line above leaves open (a string or a comment), so an edit recolors only the lines down to the last one in view.
- Mouse responsiveness: while the UI is in use (2 seconds after the last key or mouse event, and while a request is on its way), it runs Terminal.Gui's main loop at 60 passes per second and, on Windows, asks for 1 ms timer resolution (`UiPace`, `TimerResolution`), because Windows rounds the loop's and the input poll's sleeps up to 15.6 ms ticks. When idle, it goes back to Terminal.Gui's 25 passes and the system timer, so it costs next to nothing. The request and response tabs use `TabPanel` instead of Terminal.Gui's `Tabs`, which lays out and draws every page on each redraw; it looks the same and makes clicks, drags and resizes about twice as fast to repaint.
- Unexpected errors in the UI are shown and logged to `errors.log` in TermRest's state folder (where the [history](#request-history) is kept), which only you can read; so are files that fail to save for a reason other than a disk or permission problem. The other files are saved anyway.
- Each request uses a fresh connection, so timing and TLS details are exact; the TLS details are read as soon as the handshake completes, so they are there also for a server that closes the connection after its response (`Connection: close`, HTTP/1.0), and again once the response is in, for a server that asks for the client certificate only then (a TLS 1.2 renegotiation, as IIS does without *Negotiate Client Certificate*). Cookies persist for the session (**Tools › Clear cookies**).

## Roadmap

- **Next:** OAuth 2.0 token acquisition, including Entra ID client credentials with a certificate; response search and JSON path filtering; saving responses as Postman examples, and showing them in v3 collections.
- **Phase 2:**
  - Syntax highlighting of request and response bodies (scripts have it).
  - Proxy authentication.
  - Code snippets.
- **Packaging:** winget, Homebrew, `.deb`/`.rpm`, and signed binaries.
