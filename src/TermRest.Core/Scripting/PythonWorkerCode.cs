using System.Text;

namespace TermRest.Core.Scripting;

/// <summary>
/// The Python side of <see cref="ScriptWorker"/>. No file is written: the command line (<c>-c</c>) holds a short bootstrap
/// (<see cref="Bootstrap"/>) that reads the worker's code, hex-encoded, from the first line of input (<see cref="Preamble"/>)
/// and runs it. It reads one JSON message per line, runs the scripts in it with the <c>tr</c> object, and writes one JSON
/// line back. Whatever scripts print is captured, so it cannot corrupt the protocol. A request a script sends
/// (tr.send_request) goes out as a line of its own, and its answer comes in as a line of input.
/// </summary>
internal static class PythonWorkerCode
{
    /// <summary>
    /// The command (python -c): reads the worker from the first line of a copy of stdin and runs it. The worker reads its
    /// messages from the same copy (<c>_in</c>), whose buffer may hold the start of the first one already. A runtime that is
    /// a batch file (a pyenv-win shim, python.bat) gets the command through cmd.exe, which ends a command line at its first
    /// line break, refuses one over 8,191 characters, and takes " % ! ^ &amp; | &lt; &gt; as its own: the command is a short
    /// line without them, with a space so that it is passed in quotes (where cmd.exe leaves parentheses alone). Hex needs no
    /// module: one imported here (os is loaded already) could come from the workspace, which the worker leaves out of
    /// sys.path while it imports its own.
    /// </summary>
    public const string Bootstrap = "import os;_in=open(os.dup(0),encoding='utf-8');exec(bytes.fromhex(_in.readline()).decode())";

    /// <summary>The first line of input: the worker's code, hex-encoded (UTF-8).</summary>
    public static string Preamble { get; } = Convert.ToHexString(Encoding.UTF8.GetBytes(Source));

    private const string Source = """
        import sys

        # With -c, Python looks for modules in the current folder (the workspace) first. The worker's own modules come from
        # Python itself (a types.py in the workspace must not replace them); the scripts then import the workspace's.
        _workspace = sys.path.pop(0) if sys.path and sys.path[0] == "" else None
        import base64, datetime, decimal, io, json, linecache, os, re, secrets, threading, traceback, types, uuid
        if _workspace is not None:
            sys.path.insert(0, _workspace)

        # Requests come on a copy of stdin (_in, which the bootstrap opened) and answers go to a copy of stdout. Stdin itself
        # then reads nothing and stdout goes to stderr, so programs that scripts start can neither take a request nor mix
        # their output into an answer.
        _out = os.fdopen(os.dup(sys.stdout.fileno()), "w", encoding="utf-8", newline="\n")
        _null = os.open(os.devnull, os.O_RDONLY)
        os.dup2(_null, sys.stdin.fileno())
        os.close(_null)
        os.dup2(sys.stderr.fileno(), sys.stdout.fileno())
        sys.stdin = io.StringIO()
        sys.stdout = sys.stderr

        # On Linux and macOS, a session of its own, without TermRest's terminal: programs that scripts start (ssh, sudo, gpg)
        # cannot open /dev/tty to ask over TermRest's screen, and fail at once, as they do without a terminal.
        if hasattr(os, "setsid"):
            try:
                os.setsid()
            except OSError:
                pass
        _compiled = {}
        _reference = re.compile(r"\{\{([^{}]+)\}\}")
        _max_lines = 1000


        def _text(value):
            if isinstance(value, str):
                return value
            if isinstance(value, bool):
                return "true" if value else "false"
            if value is None:
                return ""
            if isinstance(value, (int, float)):
                return str(value)
            # Dates and times, UUIDs and decimals are their text, as in the other languages; anything else becomes JSON.
            if isinstance(value, (datetime.date, datetime.time)):
                return value.isoformat()
            if isinstance(value, (uuid.UUID, decimal.Decimal)):
                return str(value)
            return json.dumps(value)


        class _Output(io.TextIOBase):
            # What a script prints, as lines of up to 4,000 characters, as many as there is room for: the others are only
            # counted, so that a script printing without end does not fill the memory.

            def __init__(self, room):
                super().__init__()
                self.lines, self.dropped, self._room, self._line = [], 0, room, ""

            def writable(self):
                return True

            def write(self, text):
                parts = text.split("\n")
                parts[0] = self._line + parts[0]
                for line in parts[:-1]:
                    self._keep(line.splitlines() or [""])
                self._line = parts[-1][:4000]
                return len(text)

            def end(self):
                self._keep(self._line.splitlines())
                self._line = ""

            def _keep(self, lines):
                for line in lines:
                    if len(self.lines) < self._room:
                        self.lines.append(line[:4000])
                    else:
                        self.dropped += 1


        class _Capture(io.TextIOBase):
            # sys.stdout and sys.stderr from the start: what is written goes to the output of the script that runs, also through
            # a stream kept since an earlier script (a logging handler's); between scripts, to stderr.

            def __init__(self, stream):
                super().__init__()
                self._stream, self._output = stream, None

            def writable(self):
                return True

            def begin(self, room):
                self._output = _Output(room)

            def end(self):
                output, self._output = self._output, None
                output.end()
                return output

            def write(self, text):
                output = self._output
                if output is not None:
                    return output.write(text)
                self._stream.write(text)
                return len(text)

            def flush(self):
                if self._output is None:
                    self._stream.flush()


        _capture = _Capture(sys.stderr)
        sys.stdout = sys.stderr = _capture
        _alphanumeric = "abcdefghijklmnopqrstuvwxyz0123456789"


        def _dynamic(samples):
            # Postman's dynamic variables, as requests resolve them; the names and words they pick from come with the message.
            def pick(key):
                values = samples.get(key) or []
                return str(values[secrets.randbelow(len(values))]) if values else ""

            def random_text(length, alphabet=_alphanumeric):
                return "".join(alphabet[secrets.randbelow(len(alphabet))] for _ in range(length))

            def now():
                return datetime.datetime.now(datetime.timezone.utc)

            return {
                "$guid": lambda: str(uuid.uuid4()),
                "$randomUUID": lambda: str(uuid.uuid4()),
                "$timestamp": lambda: str(int(now().timestamp())),
                "$isoTimestamp": lambda: now().strftime("%Y-%m-%dT%H:%M:%S.%f")[:-3] + "Z",
                "$randomInt": lambda: str(secrets.randbelow(1001)),
                "$randomBoolean": lambda: "true" if secrets.randbelow(2) else "false",
                "$randomAlphaNumeric": lambda: random_text(1),
                "$randomHexadecimal": lambda: random_text(1, "0123456789abcdef"),
                "$randomFirstName": lambda: pick("firstNames"),
                "$randomLastName": lambda: pick("lastNames"),
                "$randomFullName": lambda: pick("firstNames") + " " + pick("lastNames"),
                "$randomUserName": lambda: f"{pick('firstNames').lower()}.{pick('lastNames').lower()}{secrets.randbelow(100)}",
                "$randomEmail": lambda: f"{pick('firstNames').lower()}.{pick('lastNames').lower()}@example.com",
                "$randomWord": lambda: pick("words"),
                "$randomPassword": lambda: random_text(15),
            }


        class Scope:
            # The variables of one scope: the environment, the collection, the globals, or this request.

            def __init__(self, values, changes):
                self._values = values
                self._changes = changes

            def get(self, key, default=None):
                return self._values.get(key, default)

            def set(self, key, value):
                self._values[key] = self._changes[key] = _text(value)

            def unset(self, key):
                self._values.pop(key, None)
                self._changes[key] = None

            def has(self, key):
                return key in self._values

            def clear(self):
                for key in list(self._values):
                    self.unset(key)

            def to_dict(self):
                return dict(self._values)

            def __contains__(self, key):
                return self.has(key)

            def __getitem__(self, key):
                return self._values[key]

            def __setitem__(self, key, value):
                self.set(key, value)

            def __delitem__(self, key):
                self.unset(key)

            def __repr__(self):
                return repr(self._values)


        class Variables(Scope):
            # tr.variables reads every scope (this request, the run's iteration data, environment, collection, globals) and sets this request's.

            def __init__(self, local, changes, scopes, dynamic=None):
                super().__init__(local, changes)
                self._scopes = scopes
                self._dynamic = dynamic or {}

            def get(self, key, default=None):
                for values in self._scopes:
                    if key in values:
                        return values[key]
                return default

            def has(self, key):
                return any(key in values for values in self._scopes)

            def to_dict(self):
                merged = {}
                for values in reversed(self._scopes):
                    merged.update(values)
                return merged

            def __getitem__(self, key):
                value = self.get(key)
                if value is None:
                    raise KeyError(key)
                return value

            def replace_in(self, text):
                def value(match):
                    name = match.group(1).strip()
                    found = self.get(name)
                    if found is not None:
                        return found
                    generate = self._dynamic.get(name)
                    return generate() if generate is not None else match.group(0)

                for _ in range(10):
                    replaced = _reference.sub(value, text)
                    if replaced == text:
                        break
                    text = replaced
                return text


        class Headers:
            # Headers in order: names are case-insensitive and may repeat.

            def __init__(self, pairs, writable):
                self._pairs = [[str(name), str(value)] for name, value in pairs]
                self._writable = writable

            def get(self, name, default=None):
                for key, value in self._pairs:
                    if key.lower() == name.lower():
                        return value
                return default

            def get_all(self, name):
                return [value for key, value in self._pairs if key.lower() == name.lower()]

            def set(self, name, value):
                self._check()
                index = next((i for i, pair in enumerate(self._pairs) if pair[0].lower() == name.lower()), len(self._pairs))
                self._pairs = [pair for pair in self._pairs if pair[0].lower() != name.lower()]
                self._pairs.insert(min(index, len(self._pairs)), [name, _text(value)])

            def add(self, name, value):
                self._check()
                self._pairs.append([name, _text(value)])

            def remove(self, name):
                self._check()
                self._pairs = [pair for pair in self._pairs if pair[0].lower() != name.lower()]

            def items(self):
                return [(key, value) for key, value in self._pairs]

            def to_dict(self):
                return {key: value for key, value in self._pairs}

            def _check(self):
                if not self._writable:
                    raise TypeError("these headers cannot be changed")

            def __contains__(self, name):
                return self.get(name) is not None

            def __getitem__(self, name):
                value = self.get(name)
                if value is None:
                    raise KeyError(name)
                return value

            def __setitem__(self, name, value):
                self.set(name, value)

            def __delitem__(self, name):
                self.remove(name)

            def __iter__(self):
                return iter([key for key, _ in self._pairs])

            def __len__(self):
                return len(self._pairs)

            def __repr__(self):
                return repr(self._pairs)


        class Request:
            def __init__(self, data, writable):
                self.name = data.get("name")
                self.method = data.get("method", "GET")
                self.url = data.get("url", "")
                self._writable = writable
                self.headers = Headers(data.get("headers", []), writable)
                self.body = data.get("body")

            @property
            def headers(self):
                return self._headers

            @headers.setter
            def headers(self, value):
                # As with requests: a dict, any other mapping (requests' CaseInsensitiveDict), or (name, value) pairs
                # replaces the headers.
                if not isinstance(value, Headers):
                    pairs = value.items() if callable(getattr(value, "items", None)) else value
                    value = Headers([(name, _text(item)) for name, item in pairs], self._writable)
                self._headers = value

            def __repr__(self):
                return f"<{self.method} {self.url}>"


        class Response:
            def __init__(self, data):
                self.status = self.code = data.get("status", 0)
                self.reason = data.get("reason", "")
                self.headers = Headers(data.get("headers", []), False)
                self.time = data.get("time", 0.0)
                self.size = data.get("size", 0)
                self._text = data.get("text")
                self._base64 = data.get("base64")

            @property
            def content(self):
                if self._base64 is not None:
                    return base64.b64decode(self._base64)
                return (self._text or "").encode("utf-8")

            def text(self):
                if self._text is not None:
                    return self._text
                return self.content.decode("utf-8", "replace")

            def json(self):
                return json.loads(self.text())

            def __repr__(self):
                return f"<{self.status} {self.reason}>"


        _send_lock = threading.Lock()
        _sending = False  # while a message's scripts run: the requests they send go through TermRest then
        _sent = 0


        def _json_default(value):
            if isinstance(value, (bytes, bytearray)):
                return bytes(value).decode("utf-8", "replace")
            if isinstance(value, Headers):
                return value.items()
            return _text(value)


        def _send_request(request, **fields):
            # tr.send_request: a request that TermRest sends, with its certificates, proxy and settings. As Postman's pm.sendRequest
            # takes it: a URL, or a dict with url, method, header (or headers), body and auth; fields given by name are added. Its
            # {{variables}} are sent as written (tr.variables.replace_in replaces them). One at a time: TermRest answers each with a
            # line of input, which only the thread that asked reads.
            global _sent
            data = {"url": request} if isinstance(request, str) else dict(request or {})
            data.update(fields)
            with _send_lock:
                if not _sending:
                    raise RuntimeError("the scripts already finished: TermRest sends no more requests for them")
                _sent += 1
                _out.write(json.dumps({"send": {"id": _sent, "request": data}}, default=_json_default, separators=(",", ":")) + "\n")
                _out.flush()
                line = _in.readline()
            answer = (json.loads(line) if line else {}).get("sent") or {"error": "TermRest did not answer"}
            if answer.get("log"):
                _capture.write(answer["log"] + "\n")
            if "error" in answer:
                raise ConnectionError(answer["error"])
            return Response(answer.get("response") or {})


        def _failure(error):
            message = _message(error)
            if isinstance(error, AssertionError):
                if not message:
                    frame = traceback.extract_tb(error.__traceback__)[-1]
                    message = f"assertion failed: {frame.line}" if frame.line else "assertion failed"
                return message
            return f"{type(error).__name__}: {message}"


        def _describe(error):
            return f"{type(error).__name__}: {_message(error)}"


        def _message(error):
            # An exception's own text, also of one whose __str__ fails (it would fail the whole answer).
            try:
                return str(error)
            except Exception:
                return "(its message could not be read)"


        def _tests(results, info):
            def test(name, fn=None):
                if fn is None:
                    def decorator(function):
                        test(name, function)
                        return function
                    return decorator
                try:
                    fn()
                except Exception as error:
                    results.append({"name": str(name), "passed": False, "error": _failure(error), "script": info.script})
                else:
                    results.append({"name": str(name), "passed": True, "script": info.script})
            return test


        def _compile(name, code):
            # Tracebacks show lines from linecache: set it for each run, as two folders may share a name.
            linecache.cache[name] = (len(code), None, code.splitlines(True), name)
            compiled = _compiled.get((name, code))
            if compiled is None:
                if len(_compiled) > 500:
                    _compiled.clear()
                compiled = _compiled[(name, code)] = compile(code, name, "exec")
            return compiled


        def _traceback(error, name):
            frames = [frame for frame in traceback.extract_tb(error.__traceback__) if frame.filename == name]
            return "".join(traceback.format_list(frames)) + "".join(traceback.format_exception_only(type(error), error))


        def _run(message):
            phase = message.get("phase", "")
            variables = message.get("variables") or {}
            changes = {"environment": {}, "collection": {}, "globals": {}, "local": {}}
            values = {scope: dict(variables.get(scope) or {}) for scope in changes}
            data = dict(variables.get("data") or {})
            info = types.SimpleNamespace(phase=phase, script="", **(message.get("info") or {}))
            tests, logs = [], []
            request = Request(message.get("request") or {}, phase == "prerequest")
            tr = types.SimpleNamespace(
                environment=Scope(values["environment"], changes["environment"]),
                collection_variables=Scope(values["collection"], changes["collection"]),
                globals=Scope(values["globals"], changes["globals"]),
                variables=Variables(values["local"], changes["local"], [values["local"], data, values["environment"], values["collection"], values["globals"]], _dynamic(message.get("dynamic") or {})),
                iteration_data=Scope(data, {}),
                request=request,
                response=Response(message["response"]) if message.get("response") else None,
                info=info,
                send_request=_send_request,
            )
            tr.test = _tests(tests, info)
            error = None
            dropped = 0
            for script in message.get("scripts") or []:
                info.script = script["name"]
                _capture.begin(max(_max_lines - len(logs), 0))
                stdin = sys.stdin
                try:
                    code = _compile(script["name"], script["code"])
                    sys.stdin = io.StringIO()
                    sys.stdout = sys.stderr = _capture
                    exec(code, {"__name__": "__termrest__", "tr": tr})
                except SystemExit as stop:
                    if stop.code not in (None, 0):
                        error = {"script": script["name"], "message": f"SystemExit: {stop.code}", "traceback": ""}
                except BaseException as failure:
                    error = {"script": script["name"], "message": _describe(failure), "traceback": _traceback(failure, script["name"])}
                finally:
                    sys.stdin = stdin
                    sys.stdout = sys.stderr = _capture
                    output = _capture.end()
                logs.extend({"script": script["name"], "text": line} for line in output.lines)
                dropped += output.dropped
                if error:
                    break
            result = {"changes": changes, "tests": tests, "logs": logs, "dropped": dropped, "error": error}
            if phase == "prerequest":
                try:
                    body = request.body
                    if isinstance(body, (bytes, bytearray)):
                        body = bytes(body).decode("utf-8", "replace")  # json.dumps(…).encode(): the text, not "b'…'"
                    elif isinstance(body, (dict, list, tuple, bool)):
                        try:
                            body = _text(body)  # JSON, as variables get it, not Python's text ({'a': 1}, True)
                        except (TypeError, ValueError) as failure:
                            result["error"] = error or {"script": "", "message": f"The pre-request scripts set the request body to a {type(body).__name__} that cannot be sent as JSON: {failure}", "traceback": ""}
                            body = None
                    elif body is not None:
                        body = str(body)
                    headers = [[str(name), str(value)] for name, value in request.headers._pairs]
                    result["request"] = {"method": str(request.method), "url": str(request.url), "headers": headers, "body": body}
                except Exception as failure:
                    # What the scripts left in tr.request can't be read (headers that are not a Headers object …): the
                    # request isn't sent, and what the scripts printed, tested and set is kept.
                    result["error"] = error or {"script": "", "message": f"The pre-request scripts left a request that cannot be sent: {_describe(failure)}", "traceback": ""}
            return result


        for _line in _in:
            _sending = True
            try:
                _result = _run(json.loads(_line))
            except Exception as _error:
                _result = {"fatal": "".join(traceback.format_exception(type(_error), _error, _error.__traceback__))}
            with _send_lock:  # after a request a thread still waits for: its answer comes before the next message
                _sending = False
            _out.write(json.dumps(_result) + "\n")
            _out.flush()
        """;
}
