namespace TermRest.Core.Scripting;

/// <summary>
/// The JavaScript side of <see cref="ScriptWorker"/>, for Node.js (18+) and Bun. The runtime is started with
/// <see cref="Bootstrap"/>, which reads <see cref="Source"/> as the first line of its input and runs it: the source is
/// too long for a command line, and no file is written. The worker then reads one JSON message per line, runs the
/// scripts in it with Postman's <c>pm</c> object in a fresh <c>vm</c> context, and writes one reply line, marked with
/// <see cref="ReplyMarker"/> so that anything else written to stdout (by programs that scripts start) is ignored.
/// Requests the scripts send (pm.sendRequest) go out as marked lines too, and their answers come in as lines of input
/// (see <see cref="ScriptWorker"/>'s RunAsync).
/// </summary>
internal static class JavaScriptWorkerCode
{
    public const string ReplyMarker = "\u001eTR ";

    public const string Bootstrap =
        """let b='';process.stdin.setEncoding('utf8');const f=c=>{b+=c;const i=b.indexOf('\n');if(i<0)return;process.stdin.removeListener('data',f);new Function('require','__input',JSON.parse(b.slice(0,i)))(require,b.slice(i+1))};process.stdin.on('data',f)""";

    public const string Source = """
        'use strict';
        const vm = require('vm');
        const util = require('util');
        const path = require('path');
        const nodeCrypto = require('crypto');
        const { createRequire } = require('module');

        const MARKER = '\u001eTR ';
        const workspaceRequire = createRequire(path.join(process.cwd(), 'termrest-scripts.js'));
        const sandboxScript = new vm.Script('(' + sandbox.toString() + ')', { filename: 'termrest-pm.js' });
        const compiled = new Map();
        let active = null;

        // require() from the workspace folder: scripts use the packages installed there (npm install lodash …).
        function requireFromWorkspace(name) {
          try {
            return workspaceRequire(name);
          } catch (error) {
            const id = String(name);
            if (error && error.code === 'MODULE_NOT_FOUND' && !/^[./]/.test(id) && !id.startsWith('node:') && String(error.message).includes("'" + id + "'")) {
              const parts = id.split('/');
              error.message = "Cannot find module '" + id + "': install it in the workspace folder (npm install " + (id.startsWith('@') ? parts.slice(0, 2).join('/') : parts[0]) + ')';
            }
            throw error;
          }
        }

        function compile(context, name, code) {
          const key = name + '\u0000' + code;
          let script = compiled.get(key);
          if (script === undefined) {
            if (compiled.size >= 500) compiled.clear();
            script = new vm.Script('(async function () {' + code + '\n})', { filename: name });
            compiled.set(key, script);
          }
          return script.runInContext(context);
        }

        const host = {
          format: args => util.formatWithOptions({ depth: 4, breakLength: Infinity }, ...args),
          inspect: value => util.inspect(value, { depth: 3, breakLength: Infinity, maxArrayLength: 20, maxStringLength: 300 }),
          require: requireFromWorkspace,
          uuid: () => nodeCrypto.randomUUID(),
          randomInt: limit => nodeCrypto.randomInt(limit),
          fetch: (...args) => fetch(...args),
          setTimeout, clearTimeout, setInterval, clearInterval, setImmediate, clearImmediate,
          Buffer, URL, URLSearchParams, TextEncoder, TextDecoder, AbortController, FormData, Headers,
          atob, btoa, structuredClone,
          crypto: globalThis.crypto || nodeCrypto.webcrypto,
        };

        async function run(line) {
          const context = vm.createContext({});
          const state = { onError: null, console: null, sends: [] };
          active = state;
          try {
            const start = sandboxScript.runInContext(context);
            return await start(Object.assign({}, host, {
              message: line,
              compile: (name, code) => compile(context, name, code),
              onError: handler => { state.onError = handler; },
              onConsole: target => { state.console = target; },
              send: request => send(state, request),
            }));
          } finally {
            active = null;

            // Requests still waiting when the scripts end (one failed meanwhile) get no answer: TermRest cancels them.
            for (const id of state.sends) {
              const resolve = answers.get(id);
              answers.delete(id);
              if (resolve !== undefined) resolve({ error: 'The scripts finished before the request was answered.' });
            }
          }
        }

        function uncaught(error) {
          if (active !== null && active.onError !== null) {
            active.onError(error);
          } else {
            try { process.stderr.write('Uncaught: ' + (error && error.stack ? error.stack : String(error)) + '\n'); } catch (ignored) { }
          }
        }

        process.on('uncaughtException', uncaught);
        process.on('unhandledRejection', uncaught);

        // Modules and packages that scripts require run outside the scripts' context, where console is the runtime's own (its
        // stdout and stderr, which TermRest does not show): while a script runs, what they log goes to the script's output.
        for (const name of ['log', 'info', 'debug', 'trace', 'warn', 'error', 'dir', 'table', 'assert', 'count', 'countReset', 'time', 'timeLog', 'timeEnd', 'group', 'groupCollapsed', 'groupEnd']) {
          const own = console[name];
          if (typeof own === 'function') {
            console[name] = (...args) => active !== null && active.console !== null ? active.console[name](...args) : own.apply(console, args);
          }
        }

        let queue = Promise.resolve();
        let partial = [];
        let sendCount = 0;
        const answers = new Map(); // what waits for TermRest's answer to a request that scripts sent, by its id

        // Halves of surrogate pairs (a script's substring of an emoji) are not text: JSON.stringify writes them as escapes
        // (\ud83d), which JSON readers may refuse. They become U+FFFD; whole pairs are written as they are.
        function marked(text) {
          return MARKER + text.replace(/\\(\\|ud[89a-f][0-9a-f]{2})/g, (escape, what) => what === '\\' ? escape : '\\ufffd') + '\n';
        }

        function reply(text) {
          process.stdout.write(marked(text));
        }

        // A request that scripts send goes through TermRest (its certificates, proxy and settings): a line asks for it, and
        // TermRest answers with a line of input of its own, {"sent": …}, while the scripts run (see receive).
        function send(state, request) {
          return new Promise(resolve => {
            if (active !== state) {
              resolve({ error: 'The scripts already finished: TermRest sends no more requests for them.' });
              return;
            }

            const id = ++sendCount;
            const line = marked(JSON.stringify({ send: { id, request } }));
            answers.set(id, resolve);
            state.sends.push(id);
            process.stdout.write(line);
          });
        }

        function receive(chunk) {
          let index;
          while ((index = chunk.indexOf('\n')) >= 0) {
            partial.push(chunk.slice(0, index));
            chunk = chunk.slice(index + 1);
            const line = partial.join('');
            partial = [];
            if (line.startsWith('{"sent":')) {
              const answer = JSON.parse(line).sent;
              const resolve = answers.get(answer.id);
              answers.delete(answer.id);
              if (resolve !== undefined) resolve(answer);
            } else if (line.trim().length > 0) {
              queue = queue.then(() => run(line)).then(reply, error => reply(JSON.stringify({ fatal: error && error.stack ? String(error.stack) : String(error) })));
            }
          }

          if (chunk.length > 0) partial.push(chunk);
        }

        process.stdin.on('data', receive);
        process.stdin.on('end', () => { queue.then(() => process.exit(0)); });
        receive(__input);

        // Runs inside each message's vm context (only its source is used): Postman's pm object, a chai-style expect, and
        // the scripts themselves. Everything from outside the context comes through host.
        function sandbox(host) {
          'use strict';
          const PromiseClass = Promise;
          const stringify = JSON.stringify;
          const message = JSON.parse(host.message);
          const phase = message.phase || '';
          const info = message.info || {};
          const MAX_LINES = 1000;
          const BRAND = Symbol('termrest');
          const hasOwn = (object, key) => Object.prototype.hasOwnProperty.call(object, key);
          const inspect = value => host.inspect(value);
          const tests = [];
          const logs = [];
          const notices = new Set();
          const timers = new Map();
          let dropped = 0;
          let current = '';
          let failure = null;
          let pending = 0;
          let waiters = [];

          function text(value) {
            if (typeof value === 'string') return value;
            if (value === null || value === undefined) return '';
            if (typeof value === 'object') {
              let json;
              try { json = stringify(value); } catch (error) { return String(value); }

              // One whose JSON is a string (a Date, a URL …) is that string, without the quotes.
              return typeof json === 'string' && json.startsWith('"') ? JSON.parse(json) : json;
            }
            return String(value);
          }

          function log(line) {
            for (const part of String(line).split(/\r?\n/)) {
              // Lines are cut at 4,000 characters, not inside a surrogate pair (an emoji).
              if (logs.length < MAX_LINES) logs.push({ script: current, text: part.length > 4000 ? part.slice(0, /[\ud800-\udbff]/.test(part[3999]) ? 3999 : 4000) : part });
              else dropped++;
            }
          }

          function notice(line) {
            if (!notices.has(line)) {
              notices.add(line);
              log('note: ' + line);
            }
          }

          // What a script threw may be any value: one without a prototype, a revoked proxy, or getters that throw. Describing
          // it never throws (that would fail the whole run instead of the script).
          function safeString(value) {
            try { return String(value); } catch (ignored) { }
            try { return Object.prototype.toString.call(value); } catch (ignored) { return '(a value that cannot be shown)'; }
          }

          function isError(value) {
            try { return value !== null && typeof value === 'object' && 'message' in value; } catch (ignored) { return false; }
          }

          function nameOf(error) {
            try { return String(error.name || 'Error'); } catch (ignored) { return 'Error'; }
          }

          function messageOf(error) {
            try { return String(error.message); } catch (ignored) { return '(its message could not be read)'; }
          }

          function describe(error) {
            if (!isError(error)) return safeString(error);
            const name = nameOf(error);
            return name === 'AssertionError' ? messageOf(error) : name + ': ' + messageOf(error);
          }

          function fullDescribe(error) {
            return isError(error) ? nameOf(error) + ': ' + messageOf(error) : safeString(error);
          }

          function traceback(error, name, code) {
            const source = String(code).split(/\r?\n/);
            let stack = '';
            try { if (isError(error) && typeof error.stack === 'string') stack = error.stack; } catch (ignored) { }
            const pattern = new RegExp(name.replace(/[.*+?^${}()|[\]\\]/g, '\\$&') + ':(\\d+)', 'g');
            const frames = [];
            let match;
            while ((match = pattern.exec(stack)) !== null && frames.length < 10) {
              const number = Number(match[1]);
              const frame = '  ' + name + ', line ' + number + (source[number - 1] !== undefined ? '\n    ' + source[number - 1].trim() : '');
              if (frames[frames.length - 1] !== frame) frames.push(frame);
            }

            return (frames.length > 0 ? frames.join('\n') + '\n' : '') + fullDescribe(error);
          }

          // ----- waiting for what scripts started (timers, requests, async tests) ---------------------------

          function hold() { pending++; }

          function wake() {
            const list = waiters;
            waiters = [];
            for (const resolve of list) resolve();
          }

          function release() {
            pending = Math.max(0, pending - 1);
            if (pending === 0) wake();
          }

          function settled() {
            return pending === 0 || failure !== null ? PromiseClass.resolve() : new PromiseClass(resolve => { waiters.push(resolve); });
          }

          function fail(error) {
            if (failure === null) failure = { script: current, error };
            wake();
          }

          host.onError(fail);

          function track(promise) {
            hold();
            PromiseClass.resolve(promise).then(release, release);
            return promise;
          }

          function call(fn, args) {
            try {
              const result = fn.apply(undefined, args);
              if (result !== null && typeof result === 'object' && typeof result.then === 'function') {
                hold();
                result.then(release, error => { fail(error); release(); });
              }
            } catch (error) {
              fail(error);
            }
          }

          function stop(id) {
            const clear = timers.get(id);
            if (clear !== undefined) {
              timers.delete(id);
              clear(id);
              release();
            }
          }

          function once(schedule, clear, fn, args, delay) {
            if (typeof fn !== 'function') throw new TypeError('The callback must be a function.');
            hold();
            const id = schedule(() => {
              timers.delete(id);
              try { call(fn, args); } finally { release(); }
            }, delay);
            timers.set(id, clear);
            return id;
          }

          const timerFunctions = {
            setTimeout: (fn, delay, ...args) => once(host.setTimeout, host.clearTimeout, fn, args, delay),
            setImmediate: (fn, ...args) => once((callback) => host.setImmediate(callback), host.clearImmediate, fn, args),
            setInterval(fn, delay, ...args) {
              if (typeof fn !== 'function') throw new TypeError('The callback must be a function.');
              hold();
              const id = host.setInterval(() => call(fn, args), delay);
              timers.set(id, host.clearInterval);
              return id;
            },
            clearTimeout: stop,
            clearInterval: stop,
            clearImmediate: stop,
          };

          // ----- variables -----------------------------------------------------------------------------------

          const scopes = Object.create(null);
          const changes = Object.create(null);
          for (const scope of ['environment', 'collection', 'globals', 'local', 'data']) {
            scopes[scope] = new Map(Object.entries((message.variables || {})[scope] || {}));
            changes[scope] = Object.create(null);
          }

          // Postman's dynamic variables, as requests resolve them; the names and words they pick from come with the message.
          const samples = message.dynamic || {};
          const ALPHANUMERIC = 'abcdefghijklmnopqrstuvwxyz0123456789';
          const pick = list => (Array.isArray(list) && list.length > 0 ? String(list[host.randomInt(list.length)]) : '');
          const randomText = (length, alphabet) => Array.from({ length }, () => alphabet[host.randomInt(alphabet.length)]).join('');
          const dynamic = {
            $guid: () => host.uuid(),
            $randomUUID: () => host.uuid(),
            $timestamp: () => String(Math.floor(Date.now() / 1000)),
            $isoTimestamp: () => new Date().toISOString(),
            $randomInt: () => String(host.randomInt(1001)),
            $randomBoolean: () => String(host.randomInt(2) === 1),
            $randomAlphaNumeric: () => randomText(1, ALPHANUMERIC),
            $randomHexadecimal: () => randomText(1, '0123456789abcdef'),
            $randomFirstName: () => pick(samples.firstNames),
            $randomLastName: () => pick(samples.lastNames),
            $randomFullName: () => pick(samples.firstNames) + ' ' + pick(samples.lastNames),
            $randomUserName: () => pick(samples.firstNames).toLowerCase() + '.' + pick(samples.lastNames).toLowerCase() + host.randomInt(100),
            $randomEmail: () => pick(samples.firstNames).toLowerCase() + '.' + pick(samples.lastNames).toLowerCase() + '@example.com',
            $randomWord: () => pick(samples.words),
            $randomPassword: () => randomText(15, ALPHANUMERIC),
          };

          function replaceIn(template, lookup) {
            if (typeof template !== 'string') return template;
            let result = template;
            for (let depth = 0; depth < 10; depth++) {
              const next = result.replace(/\{\{([^{}]+)\}\}/g, (all, name) => {
                const key = name.trim();
                const value = lookup(key);
                if (value !== undefined) return text(value);
                return hasOwn(dynamic, key) ? dynamic[key]() : all;
              });
              if (next === result) break;
              result = next;
            }

            return result;
          }

          class VariableScope {
            #scope;

            constructor(scope, name) {
              this.#scope = scope;
              if (name !== undefined && name !== null) this.name = name;
            }

            get(key) { return scopes[this.#scope].get(String(key)); }

            set(key, value) {
              key = String(key);
              scopes[this.#scope].set(key, value);
              changes[this.#scope][key] = text(value);
            }

            unset(key) {
              key = String(key);
              scopes[this.#scope].delete(key);
              changes[this.#scope][key] = null;
            }

            has(key) { return scopes[this.#scope].has(String(key)); }

            clear() {
              for (const key of Array.from(scopes[this.#scope].keys())) this.unset(key);
            }

            toObject() {
              const result = {};
              for (const [key, value] of scopes[this.#scope]) result[key] = value;
              return result;
            }

            toJSON() { return this.toObject(); }

            replaceIn(template) { return replaceIn(template, key => this.get(key)); }
          }

          const order = ['local', 'data', 'environment', 'collection', 'globals'];

          // pm.variables: reads every scope (this request first, then data, environment, collection, globals); sets this request's.
          class Variables extends VariableScope {
            constructor() { super('local'); }

            get(key) {
              key = String(key);
              for (const scope of order) if (scopes[scope].has(key)) return scopes[scope].get(key);
              return undefined;
            }

            has(key) {
              key = String(key);
              return order.some(scope => scopes[scope].has(key));
            }

            toObject() {
              const result = {};
              for (const scope of order.slice().reverse()) for (const [key, value] of scopes[scope]) result[key] = value;
              return result;
            }
          }

          // ----- headers, query parameters, URLs, requests and responses ---------------------------------

          class PropertyList {
            #separator;
            #ignoreCase;

            constructor(items, separator, ignoreCase) {
              this.#separator = separator;
              this.#ignoreCase = ignoreCase;
              this.members = [];
              for (const item of items || []) this.members.push(this.#member(item));
            }

            #member(item) {
              const header = this.#separator === ':';
              const value = raw => raw === undefined || raw === null ? (header ? '' : null) : String(raw);
              if (typeof item === 'string') {
                const at = item.indexOf(this.#separator);
                if (header) return at < 0 ? { key: item.trim(), value: '' } : { key: item.slice(0, at).trim(), value: item.slice(at + 1).trim() };
                return at < 0 ? { key: item, value: null } : { key: item.slice(0, at), value: item.slice(at + 1) };
              }

              if (Array.isArray(item)) return { key: String(item[0]), value: value(item[1]) };
              if (item !== null && typeof item === 'object') {
                const member = { key: String(item.key), value: value(item.value) };
                if (item.disabled) member.disabled = true;
                return member;
              }

              throw new TypeError('Expected { key, value }, got ' + inspect(item));
            }

            #same(a, b) {
              return this.#ignoreCase ? String(a).toLowerCase() === String(b).toLowerCase() : String(a) === String(b);
            }

            get(key) {
              const member = this.one(key);
              return member === undefined ? undefined : member.value;
            }

            one(key) { return this.members.find(member => !member.disabled && this.#same(member.key, key)); }

            has(key, value) {
              const checkValue = arguments.length > 1;
              return this.members.some(member => !member.disabled && this.#same(member.key, key) && (!checkValue || member.value === value));
            }

            all() { return this.members.slice(); }

            count() { return this.members.length; }

            idx(index) { return this.members[index]; }

            each(callback, context) { this.members.forEach(member => callback.call(context, member)); }

            map(callback, context) { return this.members.map(member => callback.call(context, member)); }

            filter(callback, context) { return this.members.filter(member => callback.call(context, member)); }

            find(callback, context) { return this.members.find(member => callback.call(context, member)); }

            reduce(callback, initial) { return this.members.reduce(callback, initial); }

            add(item) { this.members.push(this.#member(item)); }

            append(item) { this.add(item); }

            prepend(item) { this.members.unshift(this.#member(item)); }

            upsert(item) {
              const member = this.#member(item);
              const existing = this.one(member.key);
              if (existing === undefined) this.members.push(member);
              else existing.value = member.value;
            }

            remove(predicate) {
              const test = typeof predicate === 'function'
                ? predicate
                : member => this.#same(member.key, predicate !== null && typeof predicate === 'object' ? predicate.key : predicate);
              this.members = this.members.filter(member => !test(member));
            }

            clear() { this.members = []; }

            toObject(excludeDisabled, caseSensitive, multiValue) {
              const result = {};
              for (const member of this.members) {
                if (excludeDisabled && member.disabled) continue;
                const key = this.#ignoreCase && !caseSensitive ? member.key.toLowerCase() : member.key;
                result[key] = multiValue && hasOwn(result, key) ? [].concat(result[key], member.value) : member.value;
              }

              return result;
            }

            toJSON() { return this.members.map(member => Object.assign({}, member)); }

            toString() {
              const members = this.members.filter(member => !member.disabled);
              return this.#separator === ':'
                ? members.map(member => member.key + ': ' + member.value).join('\n')
                : members.map(member => member.value === null ? member.key : member.key + '=' + member.value).join('&');
            }

            [Symbol.iterator]() { return this.members[Symbol.iterator](); }
          }

          // Postman keeps a URL's host and path as lists, but a script may set either as text (pm.request.url.host =
          // 'api.example.com', as Postman allows), and null for any part of it means none.
          function isNone(value) { return value === null || value === undefined; }

          function hostText(host) { return Array.isArray(host) ? host.join('.') : isNone(host) ? '' : String(host); }

          function pathSegments(path) {
            if (Array.isArray(path)) return path;
            const text = isNone(path) ? '' : String(path);
            return text.length === 0 ? [] : text.replace(/^\//, '').split('/');
          }

          class Url {
            constructor(raw) { this.update(raw); }

            update(raw) {
              let rest = raw === null || raw === undefined ? '' : String(raw);
              let hash;
              const hashAt = rest.indexOf('#');
              if (hashAt >= 0) {
                hash = rest.slice(hashAt + 1);
                rest = rest.slice(0, hashAt);
              }

              let query = null;
              const queryAt = rest.indexOf('?');
              if (queryAt >= 0) {
                query = rest.slice(queryAt + 1);
                rest = rest.slice(0, queryAt);
              }

              let protocol;
              const protocolAt = rest.indexOf('://');
              if (protocolAt >= 0) {
                protocol = rest.slice(0, protocolAt);
                rest = rest.slice(protocolAt + 3);
              }

              const slashAt = rest.indexOf('/');
              let authority = slashAt >= 0 ? rest.slice(0, slashAt) : rest;
              let port;
              const portMatch = /:(\d+|\{\{[^{}]+\}\})$/.exec(authority);
              if (portMatch !== null) {
                port = portMatch[1];
                authority = authority.slice(0, portMatch.index);
              }

              this.protocol = protocol;
              this.host = authority.length > 0 ? authority.split('.') : [];
              this.port = port;
              this.path = slashAt >= 0 ? rest.slice(slashAt + 1).split('/') : [];
              this.query = new PropertyList(query === null ? [] : query.split('&'), '=', false);
              this.hash = hash;
              this.variables = new PropertyList([], '=', false);
              return this;
            }

            getHost() { return hostText(this.host); }

            getRemote() { return this.getHost() + (isNone(this.port) ? '' : ':' + this.port); }

            getPath() { return '/' + pathSegments(this.path).join('/'); }

            getQueryString() { return this.query.toString(); }

            getPathWithQuery() {
              const query = this.getQueryString();
              return this.getPath() + (query.length > 0 ? '?' + query : '');
            }

            addQueryParams(params) {
              for (const param of typeof params === 'string' ? params.split('&') : [].concat(params)) this.query.add(param);
            }

            removeQueryParams(params) {
              for (const param of [].concat(params)) this.query.remove(param !== null && typeof param === 'object' ? param.key : param);
            }

            toString() {
              let result = isNone(this.protocol) ? '' : this.protocol + '://';
              result += this.getHost();
              if (!isNone(this.port)) result += ':' + this.port;
              const path = pathSegments(this.path);
              if (path.length > 0) result += '/' + path.join('/');
              if (this.query.count() > 0) result += '?' + this.query.toString();
              if (!isNone(this.hash)) result += '#' + this.hash;
              return result;
            }

            toJSON() { return this.toString(); }
          }

          function makeBody(raw) {
            const body = {
              mode: raw === null || raw === undefined ? undefined : 'raw',
              raw: raw === null || raw === undefined ? undefined : String(raw),
              update(value) {
                if (typeof value === 'string') {
                  body.mode = 'raw';
                  body.raw = value;
                } else if (value !== null && typeof value === 'object' && value.raw !== undefined) {
                  body.mode = 'raw';
                  body.raw = typeof value.raw === 'string' ? value.raw : stringify(value.raw);
                }
              },
              isEmpty() { return body.raw === undefined || body.raw === ''; },
              toString() { return body.raw === undefined ? '' : String(body.raw); },
              toJSON() { return { mode: body.mode, raw: body.raw }; },
            };
            return body;
          }

          function makeRequest(data) {
            let url = new Url(data.url || '');
            let method = String(data.method || 'GET');
            const headers = new PropertyList(data.headers || [], ':', true);
            const body = makeBody(data.body);
            const request = {
              name: data.name === null ? undefined : data.name,
              headers,
              addHeader(header) { headers.add(header); },
              removeHeader(header) { headers.remove(header); },
              upsertHeader(header) { headers.upsert(header); },
              getHeaders() { return headers.toObject(true, true); },
              addQueryParams(params) { url.addQueryParams(params); },
              removeQueryParams(params) { url.removeQueryParams(params); },
              toJSON() { return { name: request.name, method, url: url.toString(), header: headers.toJSON(), body: body.toJSON() }; },
            };
            Object.defineProperty(request, 'url', {
              get() { return url; },
              set(value) { url = value instanceof Url ? value : new Url(value === null || value === undefined ? '' : String(value)); },
              enumerable: true,
            });
            Object.defineProperty(request, 'method', {
              get() { return method; },
              set(value) { method = String(value).toUpperCase(); },
              enumerable: true,
            });
            Object.defineProperty(request, 'body', {
              get() { return body; },
              set(value) {
                if (value === null || value === undefined) {
                  body.mode = undefined;
                  body.raw = undefined;
                } else {
                  body.update(value);
                }
              },
              enumerable: true,
            });
            Object.defineProperty(request, 'to', { get() { return expect(request).to; } });
            Object.defineProperty(request, BRAND, { value: 'request' });
            return request;
          }

          function parseCookies(headers) {
            const cookies = [];
            headers.each(header => {
              if (String(header.key).toLowerCase() !== 'set-cookie') return;
              const first = String(header.value).split(';')[0];
              const at = first.indexOf('=');
              if (at > 0) cookies.push({ name: first.slice(0, at).trim(), value: first.slice(at + 1).trim() });
            });
            return cookies;
          }

          function cookieList(cookies) {
            return {
              get: name => { const cookie = cookies.find(item => item.name === name); return cookie === undefined ? undefined : cookie.value; },
              has: name => cookies.some(item => item.name === name),
              one: name => cookies.find(item => item.name === name),
              all: () => cookies.slice(),
              count: () => cookies.length,
              each: callback => cookies.forEach(callback),
              toObject: () => {
                const result = {};
                for (const cookie of cookies) result[cookie.name] = cookie.value;
                return result;
              },
              jar: () => { throw new Error('pm.cookies.jar() is not available in TermRest.'); },
            };
          }

          function makeResponse(data) {
            const headers = new PropertyList(data.headers || [], ':', true);
            let cache;
            const body = () => {
              if (cache === undefined) {
                cache = data.text !== undefined && data.text !== null
                  ? String(data.text)
                  : data.base64 ? host.Buffer.from(data.base64, 'base64').toString('utf8') : '';
              }

              return cache;
            };
            const result = {
              code: data.status,
              status: data.reason || '',
              headers,
              responseTime: data.time,
              responseSize: data.size,
              cookies: cookieList(parseCookies(headers)),
              text: body,
              json() { return JSON.parse(body()); },
              reason() { return data.reason || ''; },
              size() { return { body: data.size, header: 0, total: data.size }; },
              toJSON() { return { code: data.status, status: data.reason, header: headers.toJSON(), responseTime: data.time, responseSize: data.size }; },
            };
            Object.defineProperty(result, 'stream', {
              get() { return data.base64 ? host.Buffer.from(data.base64, 'base64') : host.Buffer.from(body(), 'utf8'); },
            });
            Object.defineProperty(result, 'to', { get() { return expect(result).to; } });
            Object.defineProperty(result, BRAND, { value: 'response' });
            return result;
          }

          // ----- pm.sendRequest: sent by TermRest, with its certificates, proxy and settings ---------------------

          const list = value => Array.isArray(value) ? value : value !== null && typeof value === 'object' && Array.isArray(value.members) ? value.members : [];

          function headerPairs(source) {
            const pairs = [];
            const fromText = line => {
              const at = line.indexOf(':');
              if (at > 0) pairs.push([line.slice(0, at).trim(), line.slice(at + 1).trim()]);
            };
            if (typeof source === 'string') {
              source.split(/\r?\n/).forEach(fromText);
            } else if (Array.isArray(source) || (source !== null && typeof source === 'object' && Array.isArray(source.members))) {
              for (const item of list(source)) {
                if (typeof item === 'string') fromText(item);
                else if (item !== null && typeof item === 'object' && !item.disabled) pairs.push([String(item.key), item.value === undefined || item.value === null ? '' : String(item.value)]);
              }
            } else if (source !== null && typeof source === 'object') {
              for (const key of Object.keys(source)) pairs.push([key, String(source[key])]);
            }

            return pairs;
          }

          // As Postman's pm.sendRequest takes it (a URL, or { url, method, header, body, auth }); TermRest reads the rest. Its
          // {{variables}} are sent as written, as in Postman: pm.variables.replaceIn replaces them.
          async function send(options) {
            if (typeof options === 'string' || options instanceof Url) options = { url: String(options) };
            options = options || {};
            const url = options.url instanceof Url ? String(options.url) : options.url;
            let body = options.body;
            if (body !== null && typeof body === 'object' && !Array.isArray(body)) {
              body = Object.assign({}, body);
              for (const key of ['urlencoded', 'formdata']) {
                const fields = body[key];
                if (fields !== null && typeof fields === 'object' && !Array.isArray(fields) && Array.isArray(fields.members)) body[key] = fields.members;
              }
            }

            const request = {
              url: url !== null && typeof url === 'object' ? url : url === undefined || url === null ? '' : String(url),
              method: options.method === undefined || options.method === null ? 'GET' : String(options.method),
              header: headerPairs(options.header !== undefined ? options.header : options.headers).map(([key, value]) => ({ key, value })),
              body,
              auth: options.auth,
            };
            const answer = await host.send(request);
            if (answer.log) log(answer.log);
            if (answer.error !== undefined) throw new Error(answer.error);
            return makeResponse(answer.response);
          }

          function sendRequest(options, callback) {
            const promise = send(options);
            hold();
            if (typeof callback === 'function') {
              promise.then(result => call(callback, [null, result]), error => call(callback, [error, null])).then(release, release);
              return undefined;
            }

            promise.then(release, release);
            return promise;
          }

          // ----- expect: the chai assertions Postman scripts use, and Postman's response assertions ------------

          class AssertionError extends Error {
            constructor(text) {
              super(text);
              this.name = 'AssertionError';
            }
          }

          function typeOf(value) {
            if (value === null) return 'null';
            if (Array.isArray(value)) return 'array';
            const type = typeof value;
            return type === 'object' ? Object.prototype.toString.call(value).slice(8, -1).toLowerCase() : type;
          }

          function deepEqual(a, b, seen) {
            if (a === b || (a !== a && b !== b)) return true;
            if (a === null || b === null || typeof a !== 'object' || typeof b !== 'object') return false;
            const kind = typeOf(a);
            if (kind !== typeOf(b)) return false;
            if (kind === 'date') return a.getTime() === b.getTime();
            if (kind === 'regexp') return String(a) === String(b);
            seen = seen || [];
            for (const [x, y] of seen) if (x === a && y === b) return true;
            seen.push([a, b]);
            if (kind === 'set') {
              if (a.size !== b.size) return false;
              const rest = Array.from(b);
              return Array.from(a).every(item => {
                const index = rest.findIndex(other => deepEqual(item, other, seen));
                if (index < 0) return false;
                rest.splice(index, 1);
                return true;
              });
            }

            if (kind === 'map') {
              if (a.size !== b.size) return false;
              for (const [key, value] of a) if (!b.has(key) || !deepEqual(value, b.get(key), seen)) return false;
              return true;
            }

            if (kind === 'array' || ArrayBuffer.isView(a)) {
              if (a.length !== b.length) return false;
              for (let index = 0; index < a.length; index++) if (!deepEqual(a[index], b[index], seen)) return false;
              return true;
            }

            const keys = Object.keys(a);
            return keys.length === Object.keys(b).length && keys.every(key => hasOwn(b, key) && deepEqual(a[key], b[key], seen));
          }

          function readPath(object, path) {
            const parts = String(path).replace(/\[(\d+)\]/g, '.$1').split('.').filter(part => part.length > 0);
            let value = object;
            for (const part of parts) {
              if (value === null || value === undefined || !(part in Object(value))) return { exists: false, value: undefined };
              value = value[part];
            }

            return { exists: true, value };
          }

          // A length as chai finds it: a map's or set's size, else the length property, which the value must have.
          function lengthOf(assertion) {
            const object = assertion._object;
            const kind = typeOf(object);
            if (kind === 'map' || kind === 'set') return object.size;
            if (object === null || object === undefined) assertion._fail('Target cannot be null or undefined.');
            if (!('length' in Object(object))) assertion._fail('expected ' + inspect(object) + " to have property 'length'");
            return object.length;
          }

          class Assertion {
            constructor(object, text) {
              this._object = object;
              this._message = text;
              this._flags = {};
            }

            _msg(text) {
              if (text !== undefined) this._message = text;
            }

            _fail(text) {
              throw new AssertionError((this._message ? this._message + ': ' : '') + text);
            }

            _assert(passed, positive, negative, expected, actual) {
              const negate = this._flags.negate === true;
              if (Boolean(passed) !== negate) return this;
              const shown = arguments.length > 4 ? actual : this._object;
              const fill = template => template
                .replace(/#\{this\}/g, () => inspect(this._object))
                .replace(/#\{exp\}/g, () => inspect(expected))
                .replace(/#\{act\}/g, () => inspect(shown));
              return this._fail(fill(negate ? negative : positive));
            }
          }

          // Reading a property that no assertion has throws, as in chai: a misspelled .to.be.ture would otherwise check
          // nothing. Like chai, it leaves out then, catch, inspect and toJSON, which await and serializers look for. Only
          // the proxies scripts get are checked: chained methods inherit from the objects behind them, which util.inspect
          // (console.log) reads past the proxy.
          const targets = new WeakMap();
          const unchecked = new Set(['then', 'catch', 'inspect', 'toJSON']);
          const checker = {
            get(target, key, receiver) {
              if (typeof key === 'string' && !unchecked.has(key) && !Reflect.has(target, key)) throw new Error('Invalid Chai property: ' + key + suggestion(key));
              return Reflect.get(target, key, receiver);
            },
          };

          function guard(target) {
            const proxy = new Proxy(target, checker);
            targets.set(proxy, target);
            return proxy;
          }

          // chai's hint for a misspelled property: the closest assertion fewer than 4 edits away, the first on a tie.
          function suggestion(name) {
            let best = null;
            let distance = 4;
            for (const candidate of Object.getOwnPropertyNames(Assertion.prototype)) {
              if (candidate === 'constructor' || candidate.startsWith('_') || Math.abs(candidate.length - name.length) >= distance) continue;
              let row = [...Array(candidate.length + 1).keys()];
              for (let i = 1; i <= name.length; i++) {
                const next = [i];
                for (let j = 1; j <= candidate.length; j++) next[j] = Math.min(row[j] + 1, next[j - 1] + 1, row[j - 1] + (name[i - 1] === candidate[j - 1] ? 0 : 1));
                row = next;
              }

              if (row[candidate.length] < distance) {
                best = candidate;
                distance = row[candidate.length];
              }
            }

            return best === null ? '' : '. Did you mean "' + best + '"?';
          }

          function expect(value, text) {
            return guard(new Assertion(value, text));
          }

          expect.fail = text => { throw new AssertionError(text === undefined ? 'expect.fail()' : String(text)); };

          const getter = (names, get) => {
            for (const name of [].concat(names)) Object.defineProperty(Assertion.prototype, name, { get, configurable: true });
          };
          const method = (names, fn) => {
            for (const name of [].concat(names)) Object.defineProperty(Assertion.prototype, name, { value: fn, writable: true, configurable: true });
          };

          // A method that can also be chained, like chai's: expect(x).to.include(1), expect(x).to.include.members([1]).
          function chainable(assertion, fn, chain) {
            if (chain !== undefined) chain.call(assertion);
            const callable = function (...args) { return fn.apply(assertion, args); };
            delete callable.length;
            delete callable.name;
            Object.setPrototypeOf(callable, targets.get(assertion) || assertion);
            return guard(callable);
          }

          // In chai's order, so that a misspelled property gets the hint chai gives (the first of equally close ones).
          getter(['to', 'be', 'been', 'is', 'and', 'has', 'have', 'with', 'that', 'which', 'at', 'of', 'same', 'but', 'does', 'still', 'also'], function () { return this; });
          getter('not', function () { this._flags.negate = true; return this; });
          for (const name of ['deep', 'nested', 'own', 'ordered', 'any', 'all']) getter(name, function () { this._flags[name] = true; return this; });

          const article = type => (/^[aeiou]/i.test(type) ? 'an ' : 'a ') + type;

          function typeAssertion(type, text) {
            this._msg(text);
            const wanted = String(type).toLowerCase();
            return this._assert(typeOf(this._object) === wanted, 'expected #{this} to be ' + article(wanted), 'expected #{this} not to be ' + article(wanted));
          }

          function includeAssertion(value, text) {
            this._msg(text);
            const object = this._object;
            const deep = this._flags.deep === true;
            const same = (a, b) => deep ? deepEqual(a, b) : a === b || (a !== a && b !== b);
            const kind = typeOf(object);
            let found;
            if (kind === 'string') found = object.includes(value);
            else if (kind === 'array' || ArrayBuffer.isView(object)) found = Array.prototype.some.call(object, item => same(item, value));
            else if (kind === 'set') found = Array.from(object).some(item => same(item, value));
            else if (kind === 'map') found = Array.from(object.values()).some(item => same(item, value));
            else if (object !== null && typeof object === 'object' && value !== null && typeof value === 'object') {
              found = Object.keys(value).every(key => key in object && same(object[key], value[key]));
            } else {
              return this._fail('the given combination of arguments (' + kind + ' and ' + typeOf(value) + ') is invalid for this assertion. You can use an array, a map, an object, a set or a string');
            }

            const verb = deep ? 'deep include' : 'include';
            return this._assert(found, 'expected #{this} to ' + verb + ' #{exp}', 'expected #{this} to not ' + verb + ' #{exp}', value);
          }

          function lengthAssertion(expected, text) {
            this._msg(text);
            const length = lengthOf(this);
            // chai compares with ==, so lengthOf('2') passes for two items.
            return this._assert(length == expected, 'expected #{this} to have a length of #{exp} but got #{act}', 'expected #{this} to not have a length of #{act}', expected, length);
          }

          getter(['a', 'an'], function () { return chainable(this, typeAssertion); });
          getter(['include', 'includes', 'contain', 'contains'], function () { return chainable(this, includeAssertion, function () { this._flags.contains = true; }); });
          // As in chai, the comparisons after .length or .lengthOf compare the length: .lengthOf.at.least(1).and.at.most(10).
          getter(['length', 'lengthOf'], function () { return chainable(this, lengthAssertion, function () { this._flags.length = true; }); });

          function eqlAssertion(value, text) {
            this._msg(text);
            return this._assert(deepEqual(this._object, value), 'expected #{this} to deeply equal #{exp}', 'expected #{this} to not deeply equal #{exp}', value);
          }

          method(['equal', 'equals', 'eq'], function (value, text) {
            this._msg(text);
            if (this._flags.deep === true) return eqlAssertion.call(this, value);
            return this._assert(this._object === value, 'expected #{this} to equal #{exp}', 'expected #{this} to not equal #{exp}', value);
          });
          method(['eql', 'eqls'], eqlAssertion);

          // Only numbers and dates are compared: JavaScript would compare null, '' or '42' as numbers.
          function numberOrDate(assertion) {
            const kind = typeOf(assertion._object);
            if (kind !== 'number' && kind !== 'date' && kind !== 'bigint') assertion._fail('expected ' + inspect(assertion._object) + ' to be a number or a date');
          }

          function comparison(name, test, positive, negative) {
            return function (bound, text) {
              this._msg(text);
              const kind = typeOf(bound);
              if (this._flags.length === true) {
                const length = lengthOf(this);
                if (kind !== 'number') return this._fail('the argument to ' + name + ' must be a number');
                return this._assert(test(length, bound), 'expected #{this} to have a length ' + positive + ' #{exp} but got #{act}', 'expected #{this} to have a length ' + negative + ' #{exp}', bound, length);
              }

              if (typeOf(this._object) === 'date' && kind !== 'date') return this._fail('the argument to ' + name + ' must be a date');
              if (typeOf(this._object) === 'number' && kind !== 'number') return this._fail('the argument to ' + name + ' must be a number');
              numberOrDate(this);
              return this._assert(test(this._object, bound), 'expected #{this} to be ' + positive + ' #{exp}', 'expected #{this} to be ' + negative + ' #{exp}', bound);
            };
          }

          method(['above', 'gt', 'greaterThan'], comparison('above', (a, b) => a > b, 'above', 'at most'));
          method(['least', 'gte', 'greaterThanOrEqual'], comparison('least', (a, b) => a >= b, 'at least', 'below'));
          method(['below', 'lt', 'lessThan'], comparison('below', (a, b) => a < b, 'below', 'at least'));
          method(['most', 'lte', 'lessThanOrEqual'], comparison('most', (a, b) => a <= b, 'at most', 'above'));
          method('within', function (start, finish, text) {
            this._msg(text);
            const dates = typeOf(start) === 'date' && typeOf(finish) === 'date';
            const numbers = typeOf(start) === 'number' && typeOf(finish) === 'number';
            const range = dates ? start.toISOString() + '..' + finish.toISOString() : start + '..' + finish;
            if (this._flags.length === true) {
              const length = lengthOf(this);
              if (!numbers) return this._fail('the arguments to within must be numbers');
              return this._assert(length >= start && length <= finish, 'expected #{this} to have a length within ' + range, 'expected #{this} to not have a length within ' + range);
            }

            const kind = typeOf(this._object);
            if (kind === 'date' && !dates) return this._fail('the arguments to within must be dates');
            if (kind === 'number' && !numbers) return this._fail('the arguments to within must be numbers');
            numberOrDate(this);
            return this._assert(this._object >= start && this._object <= finish, 'expected #{this} to be within ' + range, 'expected #{this} to not be within ' + range);
          });
          method(['instanceof', 'instanceOf'], function (constructor, text) {
            this._msg(text);
            const name = constructor && constructor.name ? constructor.name : 'the constructor';
            return this._assert(this._object instanceof constructor, 'expected #{this} to be an instance of ' + name, 'expected #{this} to not be an instance of ' + name);
          });

          function propertyAssertion(name, ...rest) {
            const object = this._object;
            if (object === null || object === undefined) return this._fail('Target cannot be null or undefined.');
            const flags = this._flags;
            let exists = false;
            let value;
            if (flags.nested === true) ({ exists, value } = readPath(object, name));
            else {
              exists = flags.own === true ? hasOwn(object, name) : name in Object(object);
              value = exists ? object[name] : undefined;
            }

            const label = (flags.deep === true ? 'deep ' : '') + (flags.nested === true ? 'nested ' : '') + (flags.own === true ? 'own ' : '') + 'property ' + inspect(name);
            if (rest.length === 0) {
              this._assert(exists, 'expected #{this} to have ' + label, 'expected #{this} to not have ' + label);
            } else {
              const matches = exists && (flags.deep === true ? deepEqual(value, rest[0]) : value === rest[0]);
              this._assert(matches, 'expected #{this} to have ' + label + ' of #{exp}, but got #{act}', 'expected #{this} to not have ' + label + ' of #{act}', rest[0], value);
            }

            this._object = value;
            return this;
          }

          method('property', propertyAssertion);
          method(['ownProperty', 'haveOwnProperty'], function (name, ...rest) {
            this._flags.own = true;
            return propertyAssertion.call(this, name, ...rest);
          });
          method(['match', 'matches'], function (pattern, text) {
            this._msg(text);
            return this._assert(pattern.test(this._object), 'expected #{this} to match ' + pattern, 'expected #{this} not to match ' + pattern);
          });
          method('string', function (part, text) {
            this._msg(text);
            return this._assert(typeof this._object === 'string' && this._object.includes(part), 'expected #{this} to contain #{exp}', 'expected #{this} to not contain #{exp}', part);
          });
          method(['keys', 'key'], function (...keys) {
            if (keys.length === 1 && Array.isArray(keys[0])) keys = keys[0];
            else if (keys.length === 1 && keys[0] !== null && typeof keys[0] === 'object') keys = Object.keys(keys[0]);
            keys = keys.map(String);
            const object = this._object;
            const kind = typeOf(object);
            const actual = kind === 'map' || kind === 'set' ? Array.from(object.keys()).map(String) : Object.keys(Object(object));
            const any = this._flags.any === true;
            const ok = any
              ? keys.some(key => actual.includes(key))
              : keys.every(key => actual.includes(key)) && (this._flags.contains === true || actual.length === keys.length);
            const what = (any ? 'any of ' : '') + (keys.length > 1 ? 'keys ' : 'key ') + keys.map(key => inspect(key)).join(', ');
            return this._assert(ok, 'expected #{this} to have ' + what, 'expected #{this} to not have ' + what);
          });
          method(['throw', 'throws', 'Throw'], function (expected, pattern, text) {
            this._msg(text);
            const fn = this._object;
            if (typeof fn !== 'function') return this._fail('expected ' + inspect(fn) + ' to be a function');
            if (typeof expected === 'string' || typeOf(expected) === 'regexp') {
              pattern = expected;
              expected = undefined;
            }

            let thrown = false;
            let error;
            try { fn(); } catch (caught) { thrown = true; error = caught; }
            let ok = thrown;
            let what = 'to throw an error';
            if (expected !== undefined && expected !== null) {
              what = 'to throw ' + (expected.name || 'an error');
              if (ok) ok = typeof expected === 'function' ? error instanceof expected || (isError(error) && error.name === expected.name) : error === expected;
            }

            if (pattern !== undefined) {
              what += ' matching ' + inspect(pattern);
              const thrownText = isError(error) ? messageOf(error) : safeString(error);
              if (ok) ok = typeOf(pattern) === 'regexp' ? pattern.test(thrownText) : thrownText.includes(String(pattern));
            }

            return this._assert(ok, 'expected #{this} ' + what + (thrown ? ' but ' + fullDescribe(error) + ' was thrown' : ''), 'expected #{this} to not throw an error but ' + fullDescribe(error) + ' was thrown');
          });
          method('oneOf', function (values, text) {
            this._msg(text);
            const same = this._flags.deep === true ? deepEqual : (a, b) => a === b;
            return this._assert(Array.from(values).some(item => same(item, this._object)), 'expected #{this} to be one of #{exp}', 'expected #{this} to not be one of #{exp}', values);
          });
          method('members', function (expected, text) {
            this._msg(text);
            const actual = this._object;
            if (!Array.isArray(actual) || !Array.isArray(expected)) return this._fail('expected ' + inspect(actual) + ' and ' + inspect(expected) + ' to be arrays');
            const same = this._flags.deep === true ? deepEqual : (a, b) => a === b || (a !== a && b !== b);
            const contains = this._flags.contains === true;
            let ok;
            if (this._flags.ordered === true) {
              ok = (contains || actual.length === expected.length) && expected.every((value, index) => same(actual[index], value));
            } else {
              const rest = actual.slice();
              ok = expected.every(value => {
                const index = rest.findIndex(item => same(item, value));
                if (index < 0) return false;
                rest.splice(index, 1);
                return true;
              }) && (contains || rest.length === 0);
            }

            const what = contains ? 'be a superset of ' : 'have the same ' + (this._flags.ordered === true ? 'ordered ' : '') + 'members as ';
            return this._assert(ok, 'expected #{this} to ' + what + '#{exp}', 'expected #{this} to not ' + what + '#{exp}', expected);
          });
          method(['closeTo', 'approximately'], function (expected, delta, text) {
            this._msg(text);
            if (typeOf(this._object) !== 'number') return this._fail('expected ' + inspect(this._object) + ' to be a number');
            if (typeof expected !== 'number' || typeof delta !== 'number') {
              return this._fail('the arguments to closeTo or approximately must be numbers' + (delta === undefined ? ', and a delta is required' : ''));
            }

            return this._assert(Math.abs(this._object - expected) <= delta, 'expected #{this} to be close to ' + expected + ' +/- ' + delta, 'expected #{this} not to be close to ' + expected + ' +/- ' + delta);
          });
          method(['satisfy', 'satisfies'], function (matcher, text) {
            this._msg(text);
            return this._assert(matcher(this._object), 'expected #{this} to satisfy ' + inspect(matcher), 'expected #{this} to not satisfy ' + inspect(matcher));
          });

          getter('true', function () { return this._assert(this._object === true, 'expected #{this} to be true', 'expected #{this} to be false'); });
          getter('false', function () { return this._assert(this._object === false, 'expected #{this} to be false', 'expected #{this} to be true'); });
          getter('null', function () { return this._assert(this._object === null, 'expected #{this} to be null', 'expected #{this} not to be null'); });
          getter('undefined', function () { return this._assert(this._object === undefined, 'expected #{this} to be undefined', 'expected #{this} not to be undefined'); });
          getter('NaN', function () { return this._assert(Number.isNaN(this._object), 'expected #{this} to be NaN', 'expected #{this} not to be NaN'); });
          getter('finite', function () { return this._assert(Number.isFinite(this._object), 'expected #{this} to be a finite number', 'expected #{this} to not be a finite number'); });
          getter(['exist', 'exists'], function () { return this._assert(this._object !== null && this._object !== undefined, 'expected #{this} to exist', 'expected #{this} to not exist'); });
          getter('empty', function () {
            const object = this._object;
            const kind = typeOf(object);
            let empty;
            if (kind === 'string' || kind === 'array' || ArrayBuffer.isView(object)) empty = object.length === 0;
            else if (kind === 'map' || kind === 'set') empty = object.size === 0;
            else if (object !== null && typeof object === 'object') empty = Object.keys(object).length === 0;
            else return this._fail('.empty was passed non-string primitive ' + inspect(object));
            return this._assert(empty, 'expected #{this} to be empty', 'expected #{this} not to be empty');
          });
          getter(['arguments', 'Arguments'], function () {
            const object = this._object;
            const type = object !== null && typeof object === 'object' ? Object.prototype.toString.call(object).slice(8, -1) : typeOf(object);
            return this._assert(type === 'Arguments', 'expected #{this} to be arguments but got ' + type, 'expected #{this} to not be arguments');
          });
          for (const [name, test] of [['extensible', Object.isExtensible], ['sealed', Object.isSealed], ['frozen', Object.isFrozen]]) {
            getter(name, function () { return this._assert(test(this._object), 'expected #{this} to be ' + name, 'expected #{this} to not be ' + name); });
          }

          // Postman's assertions on responses: pm.response.to.have.status(200), .to.be.ok, .to.have.header('Content-Type') …
          const isResponse = value => value !== null && typeof value === 'object' && value[BRAND] === 'response';
          const isRequest = value => value !== null && typeof value === 'object' && value[BRAND] === 'request';
          for (const [name, test, what] of [['postmanRequest', isRequest, 'request'], ['postmanResponse', isResponse, 'response'], ['postmanRequestOrResponse', value => isRequest(value) || isResponse(value), 'request or response']]) {
            getter(name, function () { return this._assert(test(this._object), 'expecting a postman ' + what + ' object but got #{this}', 'not expecting a postman ' + what + ' object'); });
          }

          function responseOf(assertion) {
            if (!isResponse(assertion._object)) assertion._fail('expected ' + inspect(assertion._object) + ' to be a response');
            return assertion._object;
          }

          function statusCode(assertion, code) {
            const actual = responseOf(assertion).code;
            return assertion._assert(actual === code, 'expected response code to be ' + code + ' but found #{act}', 'expected response code to not be ' + code, code, actual);
          }

          function statusClass(digit) {
            return function () {
              const actual = responseOf(this).code;
              return this._assert(Math.floor(actual / 100) === digit, 'expected response code to be ' + digit + 'XX but found #{act}', 'expected response code to not be ' + digit + 'XX but found #{act}', undefined, actual);
            };
          }

          getter('ok', function () {
            if (isResponse(this._object)) return statusCode(this, 200);
            return this._assert(this._object, 'expected #{this} to be truthy', 'expected #{this} to be falsy');
          });
          getter('info', statusClass(1));
          getter('success', statusClass(2));
          getter('redirection', statusClass(3));
          getter('clientError', statusClass(4));
          getter('serverError', statusClass(5));
          getter('error', function () {
            const actual = responseOf(this).code;
            return this._assert(actual >= 400 && actual < 600, 'expected response code to be 4XX or 5XX but found #{act}', 'expected response code to not be 4XX or 5XX but found #{act}', undefined, actual);
          });
          for (const [names, code] of [['accepted', 202], ['withoutContent', 204], ['badRequest', 400], [['unauthorized', 'unauthorised'], 401], ['forbidden', 403], ['notFound', 404], ['notAcceptable', 406], ['rateLimited', 429]]) {
            getter(names, function () { return statusCode(this, code); });
          }

          getter('withBody', function () {
            return this._assert(responseOf(this).text().length > 0, 'expected response to have content in body', 'expected response to not have content in body');
          });
          getter('json', function () {
            let error = null;
            try { JSON.parse(responseOf(this).text()); } catch (caught) { error = caught; }
            return this._assert(error === null, 'expected response body to be a valid json but got error ' + (error === null ? '' : describe(error)), 'expected response body not to be a valid json');
          });
          for (const kind of ['html', 'xml']) {
            getter(kind, function () {
              const type = String(responseOf(this).headers.get('Content-Type') || '');
              return this._assert(type.toLowerCase().includes(kind), 'expected response to be ' + kind + ' but got content type ' + inspect(type), 'expected response not to be ' + kind);
            });
          }

          method('status', function (expected) {
            const response = responseOf(this);
            if (typeof expected === 'number') {
              return this._assert(response.code === expected, 'expected response to have status code #{exp} but got #{act}', 'expected response to not have status code #{exp}', expected, response.code);
            }

            return this._assert(response.status === expected, 'expected response to have status reason #{exp} but got #{act}', 'expected response to not have status reason #{exp}', expected, response.status);
          });
          method('header', function (key, ...rest) {
            const object = this._object;
            if (!isResponse(object) && !isRequest(object)) return this._fail('expected ' + inspect(object) + ' to be a request or a response');
            const kind = isResponse(object) ? 'response' : 'request';
            const exists = object.headers.has(key);
            if (rest.length === 0) {
              return this._assert(exists, 'expected ' + kind + ' to have header with key ' + inspect(key), 'expected ' + kind + ' to not have header with key ' + inspect(key));
            }

            const actual = object.headers.get(key);
            return this._assert(exists && actual === String(rest[0]), 'expected ' + inspect(key) + ' ' + kind + ' header to be #{exp} but got #{act}', 'expected ' + inspect(key) + ' ' + kind + ' header to not be #{exp}', rest[0], actual);
          });
          method('body', function (...args) {
            const body = responseOf(this).text();
            if (args.length === 0) return this._assert(body.length > 0, 'expected response to have content in body', 'expected response to not have content in body');
            const expected = args[0];
            if (typeOf(expected) === 'regexp') return this._assert(expected.test(body), 'expected response body text to match #{exp} but got #{act}', 'expected response body text to not match #{exp}', expected, body);
            if (typeof expected === 'string') return this._assert(body === expected, 'expected response body to equal #{exp} but got #{act}', 'expected response body to not equal #{exp}', expected, body);
            let json;
            try { json = JSON.parse(body); } catch (error) { return this._assert(false, 'expected response body to be a valid json but got error ' + describe(error), 'expected response body to not be a valid json'); }
            return this._assert(deepEqual(json, expected), 'expected response body json to equal #{exp} but got #{act}', 'expected response body json to not equal #{exp}', expected, json);
          });
          method('jsonBody', function (...args) {
            let json;
            try { json = JSON.parse(responseOf(this).text()); } catch (error) { return this._assert(false, 'expected response body to be a valid json but got error ' + describe(error), 'expected response body to not be a valid json'); }
            if (args.length === 0) return this._assert(true, '', 'expected response body to not be a valid json');
            if (typeof args[0] !== 'string') return this._assert(deepEqual(json, args[0]), 'expected response body json to equal #{exp} but got #{act}', 'expected response body json to not equal #{exp}', args[0], json);
            const { exists, value } = readPath(json, args[0]);
            if (args.length === 1) return this._assert(exists, 'expected response body json to have path ' + inspect(args[0]), 'expected response body json to not have path ' + inspect(args[0]));
            return this._assert(exists && deepEqual(value, args[1]), 'expected response body json at ' + inspect(args[0]) + ' to contain #{exp} but got #{act}', 'expected response body json at ' + inspect(args[0]) + ' to not contain #{exp}', args[1], value);
          });
          // As in Postman: a response's (or request's) JSON body, else the value itself (pm.expect(data).to.have.jsonSchema(schema)).
          // Postman's ajv 6 checks formats (email, date-time …) itself, and fails on one it doesn't know. ajv 7 and later check
          // them only with the ajv-formats plugin (for those versions only), taken from the workspace when it is there: a
          // format ajv doesn't know then fails the test rather than going unchecked.
          method('jsonSchema', function (schema, options) {
            if (typeOf(schema) !== 'object') return this._fail('expected ' + inspect(schema) + ' to be an object');
            const object = this._object;
            const data = isResponse(object) || (isRequest(object) && typeof object.json === 'function') ? object.json() : object;
            const ajvModule = host.require('ajv');
            const Ajv = ajvModule.default || ajvModule;
            const unknownFormats = [];
            const logger = { log() {}, warn(...args) { const message = args.join(' '); if (message.includes('unknown format')) unknownFormats.push(message); }, error() {} };
            const ajv = new Ajv(Object.assign({ allErrors: true, strict: false, logger }, options));
            const pluggable = Boolean(ajv.opts && ajv.opts.code); // ajv 7 and later (ajv 6 has _opts)
            let addFormats = null;
            if (pluggable) {
              try {
                addFormats = host.require('ajv-formats');
              } catch (error) {
                if (!error || error.code !== 'MODULE_NOT_FOUND' || !String(error.message).includes('ajv-formats')) throw error;
              }

              if (addFormats !== null) (addFormats.default || addFormats)(ajv);
            }

            const valid = ajv.validate(schema, data);
            if (pluggable && unknownFormats.length > 0) return this._fail(unknownFormats[0] + (addFormats === null ? ': install ajv-formats in the workspace (npm install ajv-formats), as Postman checks formats' : ''));
            return this._assert(valid && !ajv.errors, 'expected data to satisfy schema but found following errors: \n' + ajv.errorsText(), 'expected data to not satisfy schema');
          });

          // ----- pm.test ------------------------------------------------------------------------------------

          function test(name, fn) {
            if (typeof fn !== 'function') {
              log('Skipped test: ' + String(name));
              return pm;
            }

            const entry = { name: String(name), passed: true, script: current };
            tests.push(entry);
            const failed = error => {
              entry.passed = false;
              entry.error = describe(error);
            };
            if (fn.length > 0) {
              // An asynchronous test that calls done() when it has finished.
              hold();
              let finished = false;
              const done = error => {
                if (finished) return;
                finished = true;
                if (error) failed(error);
                release();
              };
              try {
                const result = fn(done);
                if (result !== null && typeof result === 'object' && typeof result.then === 'function') result.then(null, error => { failed(error); done(); });
              } catch (error) {
                failed(error);
                done();
              }
            } else {
              try {
                const result = fn();
                if (result !== null && typeof result === 'object' && typeof result.then === 'function') {
                  hold();
                  result.then(release, error => { failed(error); release(); });
                }
              } catch (error) {
                failed(error);
              }
            }

            return pm;
          }

          test.skip = name => {
            log('Skipped test: ' + String(name));
            return pm;
          };

          // ----- the pm object, console and the other globals of Postman's sandbox -----------------------------

          const pm = {
            info: {
              eventName: phase === 'prerequest' ? 'prerequest' : 'test',
              iteration: typeof info.iteration === 'number' ? info.iteration : 0,
              iterationCount: typeof info.iteration_count === 'number' ? info.iteration_count : 1,
              requestName: info.request_name === null ? undefined : info.request_name,
              requestId: host.uuid(),
            },
            environment: new VariableScope('environment', info.environment),
            collectionVariables: new VariableScope('collection'),
            globals: new VariableScope('globals'),
            variables: new Variables(),
            iterationData: new VariableScope('data'),
            request: makeRequest(message.request || {}),
            response: message.response ? makeResponse(message.response) : undefined,
            expect,
            test,
            sendRequest,
            visualizer: {
              set() { notice('pm.visualizer: TermRest does not show visualizations.'); },
              clear() { },
            },
            execution: {
              location: [],
              setNextRequest() { notice('pm.execution.setNextRequest is not supported: TermRest runs requests in order.'); },
              skipRequest() { throw new Error('pm.execution.skipRequest() is not supported by TermRest, so the request is not sent.'); },
            },
            require() { throw new Error('pm.require() (the Postman package library) is not available in TermRest: use require() with packages installed in the workspace folder.'); },
            vault: {
              get: () => PromiseClass.reject(new Error('pm.vault is not available in TermRest.')),
              set: () => PromiseClass.reject(new Error('pm.vault is not available in TermRest.')),
              unset: () => PromiseClass.reject(new Error('pm.vault is not available in TermRest.')),
            },
          };
          pm.cookies = pm.response !== undefined ? pm.response.cookies : cookieList([]);

          // The API from before pm: postman.setEnvironmentVariable, tests['…'] = …, responseBody …
          const postman = {
            setEnvironmentVariable: (key, value) => pm.environment.set(key, value),
            getEnvironmentVariable: key => pm.environment.get(key),
            clearEnvironmentVariable: key => pm.environment.unset(key),
            clearEnvironmentVariables: () => pm.environment.clear(),
            setGlobalVariable: (key, value) => pm.globals.set(key, value),
            getGlobalVariable: key => pm.globals.get(key),
            clearGlobalVariable: key => pm.globals.unset(key),
            clearGlobalVariables: () => pm.globals.clear(),
            getResponseHeader: name => pm.response !== undefined ? pm.response.headers.get(name) : undefined,
            getResponseCookie: name => pm.cookies.one(name),
            setNextRequest() { notice('postman.setNextRequest is not supported: TermRest runs requests in order.'); },
          };

          function makeConsole() {
            const counts = new Map();
            const started = new Map();
            const write = prefix => (...args) => log(prefix + host.format(args));
            return {
              log: write(''),
              info: write(''),
              debug: write(''),
              trace: write(''),
              warn: write('warn: '),
              error: write('error: '),
              dir: value => log(inspect(value)),
              table: value => log(inspect(value)),
              assert(condition, ...args) { if (!condition) log('Assertion failed' + (args.length > 0 ? ': ' + host.format(args) : '')); },
              count(label = 'default') {
                const count = (counts.get(label) || 0) + 1;
                counts.set(label, count);
                log(label + ': ' + count);
              },
              countReset(label = 'default') { counts.delete(label); },
              time(label = 'default') { started.set(label, Date.now()); },
              timeLog(label = 'default') { if (started.has(label)) log(label + ': ' + (Date.now() - started.get(label)) + ' ms'); },
              timeEnd(label = 'default') {
                if (started.has(label)) {
                  log(label + ': ' + (Date.now() - started.get(label)) + ' ms');
                  started.delete(label);
                }
              },
              group(...args) { if (args.length > 0) log(host.format(args)); },
              groupCollapsed(...args) { if (args.length > 0) log(host.format(args)); },
              groupEnd() { },
              clear() { },
            };
          }

          function xml2Json(xml) {
            const xml2js = host.require('xml2js');
            let result;
            let error = null;
            xml2js.parseString(String(xml), { explicitArray: false, async: false }, (failed, value) => { error = failed; result = value; });
            if (error) throw error;
            return result;
          }

          const scriptConsole = makeConsole();
          host.onConsole(scriptConsole);
          Object.assign(globalThis, timerFunctions, {
            pm,
            postman,
            console: scriptConsole,
            require: host.require,
            fetch: (...args) => track(host.fetch(...args)),
            Buffer: host.Buffer,
            URL: host.URL,
            URLSearchParams: host.URLSearchParams,
            TextEncoder: host.TextEncoder,
            TextDecoder: host.TextDecoder,
            AbortController: host.AbortController,
            FormData: host.FormData,
            Headers: host.Headers,
            atob: host.atob,
            btoa: host.btoa,
            structuredClone: host.structuredClone,
            crypto: host.crypto,
            xml2Json,
            environment: pm.environment.toObject(),
            globals: pm.globals.toObject(),
            data: pm.iterationData.toObject(),
            iteration: pm.info.iteration,
            request: { url: String(pm.request.url), method: pm.request.method, headers: pm.request.headers.toObject(true, true), data: pm.request.body.raw, name: pm.info.requestName },
          });

          // Postman's built-in libraries, loaded from the workspace's node_modules when first used. A global getter that
          // throws reads as "not defined" in a vm context, so a missing package is a stand-in that throws when used.
          const missing = error => {
            const fail = () => { throw error; };
            return new Proxy(function () { }, { get: fail, apply: fail, construct: fail });
          };
          for (const [name, id] of [['_', 'lodash'], ['CryptoJS', 'crypto-js'], ['cheerio', 'cheerio'], ['tv4', 'tv4']]) {
            Object.defineProperty(globalThis, name, {
              configurable: true,
              get() {
                let value;
                try {
                  value = host.require(id);
                } catch (error) {
                  return missing(error);
                }

                Object.defineProperty(globalThis, name, { value, writable: true, configurable: true });
                return value;
              },
              set(value) { Object.defineProperty(globalThis, name, { value, writable: true, configurable: true }); },
            });
          }

          if (pm.response !== undefined) {
            const response = pm.response;
            Object.defineProperties(globalThis, {
              responseBody: { get: () => response.text(), configurable: true },
              responseCode: { value: { code: response.code, name: response.status, detail: response.status }, writable: true, configurable: true },
              responseHeaders: { get: () => response.headers.toObject(false, true), configurable: true },
              responseTime: { value: response.responseTime, writable: true, configurable: true },
              responseCookies: { get: () => response.cookies.all(), configurable: true },
            });
          }

          async function runAll() {
            for (const script of message.scripts || []) {
              current = script.name;
              globalThis.tests = {};
              try {
                await host.compile(script.name, script.code)();
              } catch (error) {
                fail(error);
              }

              await settled();

              // One turn of the event loop: a promise the script rejected without handling it is reported by then.
              await new PromiseClass(resolve => { host.setImmediate(resolve); });
              const legacy = globalThis.tests;
              if (legacy !== null && typeof legacy === 'object') {
                for (const name of Object.keys(legacy)) tests.push({ name, passed: Boolean(legacy[name]), script: script.name });
              }

              if (failure !== null) break;
            }

            for (const [id, clear] of timers) clear(id);
            timers.clear();
            const result = { changes, tests, logs, dropped, error: null };
            if (failure !== null) {
              const failed = (message.scripts || []).find(item => item.name === failure.script);
              result.error = { script: failure.script, message: fullDescribe(failure.error), traceback: traceback(failure.error, failure.script, failed ? failed.code : '') };
            }

            if (phase === 'prerequest') {
              const request = pm.request;
              try {
                result.request = {
                  method: String(request.method),
                  url: String(request.url),
                  headers: request.headers.members
                    .filter(member => !member.disabled && String(member.key).length > 0)
                    .map(member => [String(member.key), member.value === null || member.value === undefined ? '' : String(member.value)]),
                  body: request.body.raw === undefined || request.body.raw === null ? null : String(request.body.raw),
                };
              } catch (error) {
                // What the scripts left in pm.request can't be read (a query that is no longer a list …): as in Python, the
                // request isn't sent, and what the scripts printed, tested and set is kept.
                if (result.error === null) {
                  result.error = { script: '', message: 'The pre-request scripts left a request that cannot be sent: ' + fullDescribe(error), traceback: '' };
                }
              }
            }

            return stringify(result);
          }

          return runAll();
        }
        """;
}
