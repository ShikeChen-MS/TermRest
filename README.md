# TermRest

A lightweight terminal REST client for development and testing:

- **Full-screen, mouse-friendly UI** in the style of Microsoft Edit: menu bar, resizable panes, tables with checkboxes, context menus. Every action also works from the keyboard. Color themes are built in (Dracula, Nord, Solarized, and more), or you can create your own.
- **Postman-compatible storage.** Collections and environments are Postman files, edited in place: v2.1 JSON, or the v3 YAML folders that Postman v12 keeps in Git. TermRest writes them back as Postman would (untouched JSON byte-for-byte; in YAML only the lines you changed) and keeps everything it doesn't understand (examples, unknown fields).
- **Scripts in Python, JavaScript or C#.** Pre-request and test scripts for collections, folders and requests, with variables, request changes and tests. JavaScript scripts use Postman's `pm` object, so Postman collections' scripts run too. Each language runs in the runtime you choose: your project's Python, Node.js or Bun, and the .NET SDK, with the packages you installed (NuGet packages for C#). TermRest carries no script engine: a runtime starts for its first script and stops when idle. See [Scripts](#scripts). **Scripts are not sandboxed:** they run with your permissions, so turn them on only for collections you trust (see [Script safety](#script-safety)).
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
   ├─ settings.json                      ← active environment, TLS verification, CA bundle, timeout, proxy, history, YAML wrapping, color theme, recording redaction…
   ├─ certificates.json                  ← client-certificate registry (Newman --ssl-client-cert-list format)
   └─ themes/                            ← optional: color themes shared with the workspace
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
| F12 | Start / stop recording (see [Recording requests](#recording-requests)) | ← / → on a tab row | Previous / next tab (Home / End: first / last; ↓ or Enter: into the page) |

**Request editor tabs**

- **Params:** query parameters (kept in sync with the URL) and path variables (`:id` segments).
- **Headers**
- **Auth:** inherit, none, Basic, Bearer, API key, Digest, NTLM, or an OAuth 2.0 access token. Other Postman auth types are kept but not yet applied.
- **Body:** none, raw (JSON, XML, text, HTML, JavaScript, with Beautify), form-data with files, x-www-form-urlencoded, a file (see [Sending a file as the body](#sending-a-file-as-the-body)), or GraphQL.
- **Scripts:** pre-request and post-response (test) scripts, in Python, JavaScript or C# (see [Scripts](#scripts)).
- **Settings:** per-request SSL verification and redirects, which client certificate applies, and the description.

**Response tabs**

- **Body:** pretty or raw view, wrap, copy, save.
- **Headers**
- **Timeline:** the exact request sent, DNS/connect/TLS/TTFB/download timing, and warnings.
- **TLS:** protocol and cipher, the client certificate sent and whether the server asked for one, and the server chain with validation details.
- **Scripts:** what the scripts did: tests, printed output, errors with their traceback, and the variables they set.

### Sending a file as the body

For a large payload, choose **file** in the Body tab instead of pasting it into the editor. TermRest keeps only the file's path and reads the file each time the request is sent, streaming it: it is never loaded into memory, and you can keep editing it in another program.

- **Type:** *Binary* sends the file as it is (`application/octet-stream`). *JSON*, *XML*, *Text*, *HTML* and *JavaScript* set the matching Content-Type and check the file first:
  - **JSON** must be valid, UTF-8 JSON. A byte order mark is fine; UTF-16 is not, and is named as the problem.
  - **XML** must be well-formed. Its DTD is never processed, so entities never read other files.
  - **Text, HTML and JavaScript** must look like text (no NUL bytes, no UTF-16).
- **When it is checked:** when you choose the file (a file that does not fit the type is turned down, with the line and column of the problem) and again each time the request is sent, since the file may have changed. A file that no longer fits is not sent. Checking streams through a 64 KB buffer: only a single value larger than that, such as a long base64 string, needs more.
- **Content-Type:** a `Content-Type` header on the request wins, for example `application/merge-patch+json` with the JSON type. To send deliberately broken JSON, choose *Binary* and set the header yourself.
- **Paths:** a relative path starts at the collection's folder, and `{{variables}}` work. The file is sent byte for byte: `{{variables}}` inside it are not replaced.
- **Postman:** the body is Postman's binary body (`"mode": "file"`); the type is stored as `"language"` in its `file` object, which Postman ignores.
- **Recordings** keep the file's path, not its content (see [Recording requests](#recording-requests)). While recording, the Body tab says so.

## Scripts

Scripts run around each request, as in Postman: **pre-request** scripts before it is sent (set variables, sign it, fetch a token), **post-response** scripts once the response is in (tests, keep a value for the next request). Write them in **Python**, in **JavaScript** with Postman's `pm` object (so the scripts of Postman collections run too), or in **C#**. Each language runs in **a runtime you choose**: your project's Python (a virtual environment, so its packages are there to use), Node.js or Bun, and the .NET SDK (10 or later).

> [!CAUTION]
> **Scripts are programs, and TermRest does not sandbox them.** Choosing a runtime for a workspace lets every script of its collections run as you, with your permissions. They can read, change, delete or upload your files, keys and credentials, see your variables and secrets, start programs, download packages, and send anything anywhere. Postman runs its scripts in a restricted sandbox; TermRest hands them the full Python, Node.js, Bun or .NET you chose, with nothing to contain a malicious one. **Treat a collection with scripts like a program you download and run:** choose runtimes only for workspaces whose collections you wrote or have read. See [Script safety](#script-safety).

TermRest itself carries no script engine. Until a request has a script, nothing extra runs. Then one process per runtime runs every script of the session, and it stops after 5 idle minutes. Measured on the same machine (memory while scripting; TermRest starts Node.js with V8 options that halve its memory):

| Option | Added to TermRest | Memory while scripting |
|---|---|---|
| **Your Python, one process per session (TermRest)** | nothing | about 23 MB, only while scripts are in use |
| **Your Node.js, one process per session (TermRest)** | nothing | about 68 MB (35 MB private), only while scripts are in use |
| **Your Bun, one process per session (TermRest)** | nothing | about 46 MB, only while scripts are in use |
| **Your .NET SDK, a program built from the C# scripts (TermRest)** | nothing | about 26 MB (7 MB private), only while scripts are in use; building them also keeps the SDK's compiler server (about 160 MB) for 10 minutes |
| Jint (JavaScript interpreter, embedded) | +7.6 MB per binary | +16–19 MB, always |
| V8 (ClearScript, embedded) | +28.5 MB native library per platform | +50 MB, always |
| Roslyn (C# scripting, embedded) | +23 MB, and no Native AOT | +190 MB, always |

A runtime answers in about a millisecond once started; starting takes 0.1–0.3 s. C# scripts are built first (see *C# scripts* below).

**Choosing the runtimes**

- **In the UI:** *Tools › Scripts…*, one row for each language. Type a command (`python3`, `node`, `bun`, `dotnet`) or a path, which may be relative to the workspace (`.venv/bin/python`, `.venv\Scripts\python.exe`). Or press **Find**: for Python, it looks for `.venv`, `venv` and `env` in the workspace, then `python3` and `python` on PATH; for JavaScript, `node` and `bun` on PATH; for C#, `dotnet` on PATH, then `~/.dotnet`. **Check** shows the version. Python 3.8, Node.js 18 or Bun 1.1, and the .NET 10 SDK or later are needed (for C#, the SDK: a .NET runtime alone cannot build scripts).
- **Your choices are kept per workspace folder in your user settings**, never in the workspace. A cloned repository cannot choose a program for TermRest to run.
- **On the command line:** `--python <path>`, `--javascript <path>` and `--csharp <path>` on `send` and `run`; without them, the UI's choices for that workspace apply. `--no-scripts` turns scripts off.
- **No runtime chosen for a language:** in the UI, its scripts are skipped and the request is sent; the status line says `⚠ scripts not run` and the *Scripts* tab says which. On the command line, that is an error (so a CI run never passes without its tests): the request is not sent, and the message says to pass `--python`, `--javascript`, `--csharp` or `--no-scripts`.

**Writing scripts**

- **A request's scripts** are in its *Scripts* tab; choose *Python*, *JavaScript* or *C#* there.
- **A collection's or folder's scripts:** right-click it in the tree › *Scripts…* (or *Request › Collection or folder scripts…*). They run for every request inside.
- **Order:** each phase runs the scripts of the collection, then the folders (outermost first), then the request. The languages can be mixed: each script sees the variables and request changes of the ones before it.
- **A failing pre-request script stops the request.** This covers an exception, a syntax or compile error, and a timeout. The request is not sent, nor added to the history or a recording, and the error is shown with the script's line.
- **Time limit:** each phase may take 30 seconds (building C# scripts does not count); the runtime is then stopped and started again for the next script.
- **Working folder:** the runtimes run in the workspace folder, so scripts can import your own modules from it (`import helpers`, `require('./helpers')`) and read its files. Python modules load once per process; *Tools › Scripts › Stop scripts* reloads them.
- **Values are text:** a variable set to `42` reads back as `"42"` in the next request or phase, and lists and objects become JSON.

| Python: the `tr` object | |
|---|---|
| `tr.environment`, `tr.collection_variables`, `tr.globals` | `.get(name, default)`, `.set(name, value)`, `.unset(name)`, `.has(name)`, `.clear()`, `.to_dict()`, and `tr.environment["name"]`. Values set are kept in memory until TermRest closes (Postman's "current values"); files are never changed. *Tools › Scripts* shows how many there are and clears them. |
| `tr.variables` | Reads every scope (this request, the environment, the collection, the globals: the first that has it). `.set()` is for this request only. `.replace_in("{{base}}/x")` replaces `{{variables}}` in a text. |
| `tr.request` | `.method`, `.url`, `.headers` (`.get`, `.get_all`, `.set`, `.add`, `.remove`, `.items()`), `.body` (raw bodies). Pre-request scripts can change them; `{{variables}}` they contain are replaced afterwards. Test scripts see the request as it was sent. |
| `tr.response` | In test scripts: `.status` (or `.code`), `.reason`, `.headers`, `.time` (ms), `.size`, `.text()`, `.json()`, `.content` (bytes). |
| `tr.test(name, fn)`, `@tr.test(name)` | Records a test that fails when `fn` raises, as `assert` does. |
| `tr.info` | `.phase` (`prerequest` or `test`), `.script`, `.request_name`, `.collection`, `.environment`. |
| `print(…)` | Goes to the response's *Scripts* tab, and to stderr on the command line. |

| JavaScript: Postman's `pm` object | |
|---|---|
| `pm.environment`, `pm.collectionVariables`, `pm.globals`, `pm.variables`, `pm.iterationData` | `.get`, `.set`, `.unset`, `.has`, `.clear`, `.toObject`, `.replaceIn`, as in Postman. |
| `pm.request` | `.method`, `.url` (`.query.add`, `.toString()`, `.getHost()` …), `.headers` (`.add`, `.upsert`, `.remove`, `.get`, `.has`, `.each` …), `.body.raw`. Pre-request scripts can change them, as with `tr.request`. |
| `pm.response` | `.code`, `.status`, `.headers`, `.responseTime`, `.responseSize`, `.json()`, `.text()`, `.stream`, `.cookies`, and Postman's assertions: `.to.have.status(…)`, `.to.have.header(…)`, `.to.have.body(…)`, `.to.have.jsonBody(…)`, `.to.be.ok`, `.success`, `.clientError`, `.json` … |
| `pm.test(name, fn)`, `pm.expect(value)` | Tests (also asynchronous: `async` functions, or a `done` callback) and chai's assertions: `equal`, `eql`, `include`, `a`/`an`, `property`, `lengthOf`, `above`/`below`, `oneOf`, `members`, `keys`, `match`, `throw`, `not`, `deep`, `nested` …, with chai's messages. |
| `pm.sendRequest(request, callback)` | With a callback, or `await` it. It uses the runtime's own `fetch`: TermRest's certificates and proxy settings do not apply. |
| `pm.info`, `pm.cookies`, `console.log` … | As in Postman; `console` output goes where `print` does. |
| The API from before `pm` | `tests["name"] = …`, `postman.setEnvironmentVariable(…)`, `responseBody`, `responseCode`, `responseHeaders`, `responseTime`. |
| `require(…)` | Node.js modules, your own files, and the packages installed in the workspace (`npm install lodash crypto-js moment`). Postman's globals `_`, `CryptoJS`, `cheerio` and `tv4` load those packages when first used; `xml2Json` uses `xml2js`. Bun installs packages it cannot find by itself. |
| Not supported | `pm.execution.skipRequest()` (the request is not sent, with an error), `setNextRequest` (requests run in order), `pm.vault`, `pm.require` (Postman's package library), `pm.cookies.jar()`, `pm.visualizer` (ignored). Each script phase runs in a fresh context, as in Postman. |

**C# scripts**

- **Built, then run.** TermRest writes the C# scripts of a collection into one small program, each script the body of a method, and your .NET SDK builds it as a C# file without a project (`dotnet run --file`, new in .NET 10). The first build takes a few seconds (more when it restores NuGet packages); a change, about a second. Built programs are kept in TermRest's cache folder, so unchanged scripts start at once, in the next TermRest too: `%LOCALAPPDATA%\TermRest\csharp`, `~/Library/Caches/TermRest/csharp` or `~/.cache/termrest/csharp` (`TERMREST_CACHE_DIR` moves it). The 16 most recently used are kept, a few hundred KB each.
- **A script is the body of an async method:** statements, local functions, `await`, `return`. It may start with `using` directives and the directives of C# files: `#:package Name@1.2.3` (NuGet packages), `#:project path`, `#:property Name=Value`, and `#:include helpers.cs` (SDK 10.0.300 or later) for a file of your own types, as scripts cannot declare classes. Relative paths are relative to the workspace. Besides .NET's usual namespaces (`System`, `System.IO`, `System.Linq`, `System.Net.Http` …), `System.Text`, `System.Text.Json.Nodes` and `System.Text.RegularExpressions` are imported.
- **Compile errors name the script and its line**, as the compiler reports them. The scripts before it still run, and a script that does not compile does not keep the collection's other requests from running theirs.
- **Building uses the SDK's compiler server**, which stays for 10 minutes after a build (about 160 MB), as with any `dotnet build`: it makes the next build quick. *Tools › Scripts › Stop scripts* stops it.

| C#: the `tr` object | |
|---|---|
| `tr.Environment`, `tr.CollectionVariables`, `tr.Globals` | `["name"]` (null when not set; setting null unsets), `.Get(name, fallback)`, `.Set(name, value)`, `.Unset(name)`, `.Has(name)`, `.Clear()`, `.ToDictionary()`. Kept as in Python. |
| `tr.Variables` | Reads every scope (this request, the environment, the collection, the globals). `.Set()` is for this request only. `.ReplaceIn("{{base}}/x")` replaces `{{variables}}` in a text. |
| `tr.Request` | `.Method`, `.Url`, `.Headers` (`["name"]`, `.Get`, `.GetAll`, `.Set`, `.Add`, `.Remove`, `.Has`), `.Body` (raw bodies). Pre-request scripts can change them. |
| `tr.Response` | In test scripts: `.Status` (or `.Code`), `.Reason`, `.Headers`, `.Time` (ms), `.Size`, `.Text()`, `.Json()` (a `JsonNode`: `json["name"]`, `(int)json["id"]`), `.Content` (bytes). |
| `tr.Test(name, () => …)` | Records a test that fails when it throws; `async () => …` tests are awaited. Assertion libraries from NuGet work too. |
| `tr.Assert(condition)`, `tr.AssertEqual(expected, actual)` | Fail with the code that failed: `assertion failed: tr.Response.Status == 201`, `tr.Response.Code: expected 201, got 200`. `AssertEqual` compares numbers as numbers and JSON as JSON: `tr.AssertEqual("Ada", json["name"])`. |
| `tr.Info` | `.Phase`, `.Script`, `.RequestName`, `.Collection`, `.Environment`. |
| `Console.WriteLine(…)` | Goes where `print` does. `HttpClient` sends requests of the script's own (TermRest's certificates and proxy settings do not apply). |

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

**Where the results show**

- **In the UI**, the response's status line says `tests 3/3 passed`, `✗ 1 of 3 tests failed`, `✗ script error` or `⚠ scripts not run`. Its *Scripts* tab lists the tests, the output, errors with their line, the variables set, and the scripts that did not run. *Help › Scripting* summarizes the `tr` and `pm` objects.
- **`termrest send`** prints the output and tests to stderr. It exits with 1 when a test fails or a script fails.
- **`termrest run`** prints them under each request, and a request whose tests fail counts as failed. `--report-json` includes `tests` and `scriptError`.
- **Recordings** include `_termrest.scripts`: tests, the error and traceback, the names (not the values) of the variables set, and what the scripts printed, all redacted like the rest.

**Compatibility**

- Scripts are stored in Postman's `event` entries. JavaScript ones stay as Postman wrote them (`"type": "text/javascript"`), so Postman runs them too. Python ones are marked `"type": "text/x-python"` and C# ones `"type": "text/x-csharp"` (in v3 YAML: `language: text/x-python`, `language: text/x-csharp`); Postman keeps them, but doesn't run them.

### Script safety

TermRest runs scripts the way a terminal runs commands: it starts the runtime you chose and lets the scripts do anything that runtime can. That makes them as capable as any program you write, and as dangerous as any program you run without reading it. You choose the runtimes, so the responsibility for what runs is yours.

**What a script can do, with nothing to stop it**

- Read, change, delete or upload any file you can: source code, SSH keys, cloud credentials (`~/.azure`, `~/.aws`, `~/.kube`), browser profiles.
- See every variable, secret ones included, the requests with their `Authorization` headers, the responses, and TermRest's environment variables (tokens, in CI). It can send them anywhere: `fetch`, `HttpClient`, `urllib` and `pm.sendRequest` do not go through TermRest.
- Start programs, which may keep running after TermRest stops the runtime, install software, and change your system's settings.
- Run code it brings in:
  - **C#:** `#:package` restores NuGet packages, and their build steps (MSBuild targets, analyzers) run while the scripts are built, before any script does. `#:sdk`, `#:project` and `#:include` bring in more.
  - **JavaScript:** `require` loads npm packages from the workspace's `node_modules`, and Bun downloads missing ones by itself.
  - **Python:** scripts import whatever the chosen environment has.
  - A repository can ship such code itself, for example a `node_modules` folder or a `.venv`.

**What TermRest does, and does not do**

- It never chooses a runtime by itself. Runtimes are chosen per workspace folder and kept in your user settings, never in the workspace, so a cloned repository cannot turn its scripts on. *Find* only looks when you press it.
- Without a runtime, the UI sends requests and skips their scripts (`⚠ scripts not run`). On the command line, scripts without a runtime are an error until one is chosen (in the UI, or with `--python`, `--javascript` or `--csharp`), or until you pass `--no-scripts`.
- `tr` and `pm` cannot change collection, environment or settings files: the values scripts set stay in memory until TermRest closes. Recordings redact secrets, in what scripts print too.
- It does **not** sandbox, review, sign or limit scripts.
  - JavaScript gets a fresh context per phase for Postman compatibility, not for isolation: Node.js's `vm` module is not a security boundary.
  - A timeout stops the runtime, not what a script already did.
- Once a runtime is chosen for a workspace, **every** script of its collections runs on the next send, without asking. That includes scripts that arrive with a `git pull`, an import or a teammate's change, and the scripts of the collection and its folders, which a request's *Scripts* tab does not show.

**Before you turn scripts on**

- **Read the scripts first:** the request's *Scripts* tab, and right-click › *Scripts…* on the collection and on each folder. In the files, they are the `event` entries (`script.exec`) of JSON collections and the `scripts` of v3 YAML files. Review changes to them as you review code.
- **Keep collections you did not write, or have not read, in a workspace folder with no runtime chosen.** On the command line, use `--no-scripts` for them.
- **Choose runtimes you installed yourself.** *Find* suggests the workspace's own `.venv`: in a repository you did not create, make sure it is one you made.
- **Pin package versions** (`#:package Name@1.2.3`, lock files), and prefer package feeds you trust.
- **Run collections from unknown sources in a container, a VM or a separate account.** In CI, give the job only the secrets its requests need: scripts see the job's environment.
- **To turn scripts off again,** clear the runtime in *Tools › Scripts…* and save, or remove it from `pythonInterpreters`, `javaScriptRuntimes` or `csharpRuntimes` in your `settings.json`.

## Color themes

Choose a theme in *Tools › Color theme…*. Moving through the list previews each theme live on the whole UI; *Use theme* keeps it and *Cancel* restores the previous one. The choice applies to all your workspaces. Tick *Use only in this workspace* to store it in the workspace's `.termrest/settings.json` instead, for example to give a production workspace a red menu bar.

- **Built in:** Terminal.Gui's themes (Default uses your terminal's own colors; Dark, Light, TurboPascal 5, Anders, 8-Bit, Green Phosphor, Amber Phosphor) and TermRest's Dracula, Gruvbox Dark, High Contrast, Monokai, Nord, Solarized Dark and Solarized Light.
- **Method and status colors:** every theme colors the request methods in the tree (GET, POST, PUT, PATCH, DELETE, others) and the response status (2xx/3xx, 4xx, 5xx).
- **Custom themes** are JSON files. *Copy as custom theme…* (or `termrest themes copy`) writes a copy of any theme for you to edit, and *Reload theme files* picks up your edits without a restart. A theme file names a `base` theme and changes some of its palette colors. Every color of the UI is derived from the palette's 20 colors. For full control, `schemes` sets single roles of Terminal.Gui's color schemes:

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

  Palette colors: `background`, `text`, `muted`, `accent` (hot keys), `selection`, `selectionText`, `input` (fields and tables), `bar`, `barText`, `dialog`, `dialogText`, `success`, `warning`, `error`, `get`, `post`, `put`, `patch`, `delete`, `otherMethods`. Schemes: `Base`, `Menu`, `Dialog`, `Error`, `Accent`, `Success`, `Warning`. Roles: `Normal`, `HotNormal`, `Focus`, `HotFocus`, `Active`, `HotActive`, `Highlight`, `Editable`, `ReadOnly`, `Disabled`. Mistakes such as an unknown key or an invalid color are listed in the dialog and by `termrest themes`, and the rest of the file still applies.
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

## Recording requests

Click **● Record** at the right end of the menu bar (or press **F12**, or choose *Tools › Start recording*). Every request you send after that is recorded with its response:

- **The request:** method, URL, headers, cookies, query parameters, and the body: text and form fields as sent; files by reference only (see below).
- **The response:** status, headers, cookies and the full body.
- **Connection details:** timing, the server address, and TLS (protocol, cipher, server certificate chain, the client certificate sent).
- **Failures:** the error of requests that failed.

While recording, the button turns into a red badge with a blinking dot, the time since recording started and the number of requests recorded: **● REC 01:23 · 3 requests · Stop F12**. The Response pane's title shows which request a response was recorded as (**Response · ● recorded #3**).

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
  - `Authorization` and `Proxy-Authorization` credentials. The scheme stays: `Bearer [REDACTED]`.
  - Cookie values in `Cookie` and `Set-Cookie`. Names and attributes stay.
  - Headers, URL parameters (query and fragment), and JSON and form fields named like credentials: password, secret, token, API key, signature, session id, private key, connection string, client assertion …
  - Passwords in URLs (`https://user:[REDACTED]@host`).
  - The values of auth credentials, secret variables and variables named like credentials, wherever they appear: URLs, headers, bodies (also URL-encoded or JSON-escaped) and error messages.
  - Every value redacted by name, wherever it appears later in the recording. For example, a token from a login response is redacted when a later request sends it.

  Add your own names in *Tools › Settings › Recordings redact* (`recordingRedact` in `.termrest/settings.json`). Names are compared without case and separators, at the end of a name: `Tenant-Key` covers `X-Tenant-Key` and `tenant_key`.
- **Memory:** a recording does not grow in memory.
  - Each request is written to a temporary file as soon as its response arrives, on a background thread.
  - Bodies are written in 64 KB pieces, and saving copies the temporary file into the HAR file through a small buffer.
  - Responses are included up to the workspace's maximum response size (*Tools › Settings*, 50 MB). Files sent with a request are never read for the recording.
- **Temporary files** are private to you (on Unix, a folder only you can read) and are deleted after saving or discarding:
  - Windows: `%TEMP%`.
  - macOS: `$TMPDIR`, which is per user.
  - Linux: `$TMPDIR` if it is set, otherwise `/tmp`. When `/tmp` is a RAM file system (tmpfs), `/var/tmp` is used instead, so a large recording doesn't take memory after all.

  Set `TERMREST_TEMP_DIR` to use another folder. On Unix, the saved HAR file is also readable only by you (mode 600).
- **Recovery:** if TermRest closes while recording (a closed terminal, a crash), nothing is lost. The next start says so, and *Tools › Recover unsaved recordings* saves the recording.
- **Command line:** `--record <file>` on `send` and `run` records the same way, for example as evidence from a CI run:

```sh
termrest run "samples/TermRest Demo.postman_collection.json" -e "Postman Echo" --record run.har
termrest send api.postman_collection.json "Users/Get user" --record get-user.har
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
- **Wrapping long lines:** some earlier Postman versions wrapped long values at 80 columns; current versions write each value on one line. TermRest writes wrapped values exactly as Postman did: text breaks at spaces, URLs break with an escaped `\` line break, and multi-line text becomes a folded `>` block. Choose in *Tools › Settings › Wrap YAML lines*, or set `yamlLineWidth` in `.termrest/settings.json`:
  - *Like the files* (the default, no setting): files that are wrapped stay wrapped, and new files are not wrapped.
  - *Never* (`0`) or *At column* (for example `80`; 20 to 1000).

  Only new and changed values are wrapped: lines you don't change keep their layout, so a different setting never rewrites a whole file. *Help › Workspace info* and `termrest validate` show which collections are wrapped.
- **Conversion and validation:**

  ```sh
  termrest convert "Contoso API.postman_collection.json"     # → postman/collections/Contoso API/
  termrest convert "Contoso API.postman_collection.json" --line-width 80   # wrapped like older Postman versions
  termrest convert "postman/collections/Contoso API" --to v2.1 -o contoso.postman_collection.json
  termrest convert dev.postman_environment.json              # → postman/environments/dev.environment.yaml
  termrest validate "postman/collections/Contoso API"        # checks that every file converts losslessly
  ```

- **Tested on real files:** 31 collections written by Postman, from public GitHub repositories. Editing and reverting every part of every request leaves all files byte-for-byte unchanged. Every corpus file that Postman wrapped comes out byte-for-byte the same when TermRest writes its content from scratch (files with comments are skipped).

Not yet supported in v3 collections:

- Postman's GraphQL, gRPC and WebSocket request types are not shown; their files are left untouched. HTTP requests with a GraphQL body work.
- Saved examples (`*.example.yaml`) are kept and move with their request, but are not shown, and `convert --to v2.1` leaves them out.
- A client certificate pinned on a request (v2.1 `certificate`) has no v3 equivalent; use the certificate registry.
- If a collection or folder defines several auth methods, only the first one can be edited; the others are kept.

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
termrest [<workspace>] [--theme <name>]        open the UI (default: current directory)
termrest send <collection> <request> [opts]    send one request; body to stdout, status/diagnostics to stderr
termrest run <collection> [--folder F] [opts]  run all requests in order, with their scripts; exit code 1 if any fail
termrest validate <files…>                     check that files (JSON, YAML, v3 folders) round-trip unchanged through TermRest
termrest convert <input> [--to v3|v2.1] [-o O] convert a collection or environment between v2.1 JSON and v3 YAML
termrest history [<workspace>] [opts]          requests sent from a workspace, newest first
termrest import-curl <collection> [<command>]  add a request from a curl command (read from stdin when omitted)
termrest certs list|store|match                client-certificate helpers
termrest themes [use <theme>|copy <theme> <name>]  list, choose or copy color themes of the UI
termrest snapshot [<workspace>] [--send]       render the UI headlessly (smoke test; --send includes a live response)

<collection>: a *.postman_collection.json file or a v3 collection folder
common options:      -e/--env <name|file>  --var key=value  --timeout <ms>  -k/--insecure  --record <file.har>
                     --python <path>  --javascript <path>  --csharp <path>  --no-scripts
send options:        -i/--include  -v/--verbose  -o/--output <file>  --raw  -f/--fail  --curl  --no-history
run options:         --bail  --allow-http-errors  --report-json <file>
history options:     -n/--limit <n>  --show <id>  --json  --clear
import-curl options: --folder <path>  --name <name>  --dry-run
convert options:     --line-width <n>  (wrap YAML output at column n; 0: never)
themes options:      -w/--workspace <folder>  --only-here (use)  --in-workspace (copy)
snapshot options:    --send  --request <path>  --width <n>  --height <n>  --theme <name>
```

```sh
termrest run "samples/TermRest Demo.postman_collection.json" -e "Postman Echo" --report-json report.json
termrest send api.postman_collection.json "Users/Get user" --var userId=42 -i
termrest send api.postman_collection.json "Users/Get user" --curl     # print an equivalent curl command (scripts do not run)
termrest run api.postman_collection.json --python .venv/bin/python   # run the collection's Python scripts and tests
termrest run postman-export.json --javascript node                   # run a Postman collection's pm.* scripts and tests
termrest run api.postman_collection.json --csharp dotnet             # build and run the collection's C# scripts and tests
```

## Postman compatibility

| Area | Status |
|---|---|
| Collection v2.1.0 JSON | Read/write; untouched content round-trips byte-for-byte (tabs, key order, escaping, BOM/CRLF preserved) |
| Collection v2.0.0 JSON | Read/write (v2.0 auth shape kept) |
| Collection v3.0.0 YAML (Postman v12 "Native Git") | Read/write: `postman/collections/<name>/` folders with `*.request.yaml` and `definition.yaml`; edits rewrite only the changed lines. See [Postman v3 (YAML) collections](#postman-v3-yaml-collections) |
| Environments / globals | Read/write: JSON (`*.postman_environment.json`, `*.postman_globals.json`) and YAML (`postman/environments/*.environment.yaml`, `postman/globals/workspace.globals.yaml`) |
| Folders, descriptions, saved examples, `protocolProfileBehavior`, unknown fields | Kept; edited fields are rewritten surgically |
| Body modes | raw, urlencoded, formdata (text + files), file (binary, or typed and checked: `file.language`), graphql |
| Auth | inherit, noauth, basic, bearer, apikey, digest, ntlm, oauth2 (access token) — others are kept, not applied |
| Scripts | JavaScript (`pm.*`, and the older `postman.*` and `tests[]`) runs in Node.js or Bun, Python (`"type": "text/x-python"`; v3 `language: text/x-python`) in Python, C# (`"type": "text/x-csharp"`) with the .NET SDK; see [Scripts](#scripts) for the `pm` API supported |
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
                    HTTP executor, certificates, history, recording (HAR, redaction), scripts (Python and
                    JavaScript workers, C# programs built by the user's .NET SDK),
                    cURL import, workspace
src/TermRest.Tui    Terminal.Gui v2 views and dialogs
src/TermRest.Cli    `termrest` executable (System.CommandLine): TUI by default + headless commands
tests/…Core.Tests   xUnit v3: fidelity, mapper, URLs, variables, certificates, YAML, Postman v3, history, cURL,
                    recording and redaction, in-process Kestrel HTTP/TLS/mTLS tests
tests/…Tui.Tests    headless UI tests: renders the real window, injects mouse/keyboard input, asserts on screen text
```

```sh
dotnet build
dotnet test                                    # Microsoft.Testing.Platform runner (see global.json)
TERMREST_STORE_TESTS=1 dotnet test             # Windows: also run the non-exportable cert-store mTLS test
TERMREST_V3_CORPUS=<folder> dotnet test        # also edit and revert every Postman v3 collection found under <folder>
TERMREST_TEST_PYTHON=<python> dotnet test      # the Python for script tests (default: python3 or python on PATH; skipped without)
TERMREST_TEST_JAVASCRIPT=<runtime> dotnet test # the Node.js or Bun for script tests (default: node or bun on PATH; skipped without)
TERMREST_TEST_DOTNET=<dotnet> dotnet test      # the .NET SDK (10+) for C# script tests (default: dotnet on PATH; skipped without)
```

Notes:

- Terminal.Gui 2.5's `TextView` is marked obsolete in favour of the separate `Terminal.Gui.Editor` package. That package has not shipped a build for 2.5 yet, so TermRest uses `TextView` behind a small `TextArea` wrapper, which makes the later switch (syntax highlighting, folding) a one-file change.
- Mouse responsiveness: the UI runs Terminal.Gui's main loop at 60 passes per second (its default is 25) and, on Windows, asks for 1 ms timer resolution while it runs (`TimerResolution`), because Windows rounds the loop's and the input poll's sleeps up to 15.6 ms ticks. The request and response tabs use `TabPanel` instead of Terminal.Gui's `Tabs`, which lays out and draws every page on each redraw; it looks the same and makes clicks, drags and resizes about twice as fast to repaint.
- Each request uses a fresh connection, so timing and TLS details are exact. Cookies persist for the session (**Tools › Clear cookies**).

## Roadmap

- **Next:** OAuth 2.0 token acquisition, including Entra ID client credentials with a certificate; response search and JSON path filtering; saving responses as Postman examples, and showing them in v3 collections.
- **Phase 2:**
  - Syntax-highlighted editor, for bodies and scripts.
  - Proxy authentication.
  - Code snippets.
- **Packaging:** winget, Homebrew, `.deb`/`.rpm`, and signed binaries.
