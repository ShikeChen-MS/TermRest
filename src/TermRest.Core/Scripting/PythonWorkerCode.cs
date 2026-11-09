namespace TermRest.Core.Scripting;

/// <summary>
/// The Python side of <see cref="ScriptWorker"/>, passed to the interpreter with <c>-c</c> (no file is written). It reads
/// one JSON message per line, runs the scripts in it with the <c>tr</c> object, and writes one JSON line back. Whatever
/// scripts print is captured, so it cannot corrupt the protocol.
/// </summary>
internal static class PythonWorkerCode
{
    public const string Source = """
        import base64, contextlib, io, json, linecache, os, re, sys, traceback, types

        # Requests come on a copy of stdin and answers go to a copy of stdout. Stdin itself then reads nothing and stdout
        # goes to stderr, so programs that scripts start can neither take a request nor mix their output into an answer.
        _in = os.fdopen(os.dup(sys.stdin.fileno()), "r", encoding="utf-8")
        _out = os.fdopen(os.dup(sys.stdout.fileno()), "w", encoding="utf-8", newline="\n")
        _null = os.open(os.devnull, os.O_RDONLY)
        os.dup2(_null, sys.stdin.fileno())
        os.close(_null)
        os.dup2(sys.stderr.fileno(), sys.stdout.fileno())
        sys.stdin = io.StringIO()
        sys.stdout = sys.stderr
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
            return json.dumps(value)


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
            # tr.variables reads every scope (this request, environment, collection, globals) and sets this request's.

            def __init__(self, local, changes, scopes):
                super().__init__(local, changes)
                self._scopes = scopes

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
                for _ in range(10):
                    replaced = _reference.sub(lambda m: self.get(m.group(1).strip(), m.group(0)), text)
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
                self.headers = Headers(data.get("headers", []), writable)
                self.body = data.get("body")

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


        def _failure(error):
            message = str(error)
            if isinstance(error, AssertionError):
                if not message:
                    frame = traceback.extract_tb(error.__traceback__)[-1]
                    message = f"assertion failed: {frame.line}" if frame.line else "assertion failed"
                return message
            return f"{type(error).__name__}: {message}"


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
            info = types.SimpleNamespace(phase=phase, script="", **(message.get("info") or {}))
            tests, logs = [], []
            request = Request(message.get("request") or {}, phase == "prerequest")
            tr = types.SimpleNamespace(
                environment=Scope(values["environment"], changes["environment"]),
                collection_variables=Scope(values["collection"], changes["collection"]),
                globals=Scope(values["globals"], changes["globals"]),
                variables=Variables(values["local"], changes["local"], [values["local"], values["environment"], values["collection"], values["globals"]]),
                request=request,
                response=Response(message["response"]) if message.get("response") else None,
                info=info,
            )
            tr.test = _tests(tests, info)
            error = None
            dropped = 0
            for script in message.get("scripts") or []:
                info.script = script["name"]
                output = io.StringIO()
                stdin = sys.stdin
                try:
                    code = _compile(script["name"], script["code"])
                    sys.stdin = io.StringIO()
                    with contextlib.redirect_stdout(output), contextlib.redirect_stderr(output):
                        exec(code, {"__name__": "__termrest__", "tr": tr})
                except SystemExit as stop:
                    if stop.code not in (None, 0):
                        error = {"script": script["name"], "message": f"SystemExit: {stop.code}", "traceback": ""}
                except BaseException as failure:
                    error = {"script": script["name"], "message": f"{type(failure).__name__}: {failure}", "traceback": _traceback(failure, script["name"])}
                finally:
                    sys.stdin = stdin
                lines = output.getvalue().splitlines()
                room = max(_max_lines - len(logs), 0)
                logs.extend({"script": script["name"], "text": line[:4000]} for line in lines[:room])
                dropped += max(len(lines) - room, 0)
                if error:
                    break
            result = {"changes": changes, "tests": tests, "logs": logs, "dropped": dropped, "error": error}
            if phase == "prerequest":
                body = request.body if request.body is None else str(request.body)
                result["request"] = {"method": str(request.method), "url": str(request.url), "headers": request.headers._pairs, "body": body}
            return result


        for _line in _in:
            try:
                _result = _run(json.loads(_line))
            except Exception as _error:
                _result = {"fatal": "".join(traceback.format_exception(type(_error), _error, _error.__traceback__))}
            _out.write(json.dumps(_result) + "\n")
            _out.flush()
        """;
}
