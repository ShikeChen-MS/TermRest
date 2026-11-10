using System.Text;

namespace TermRest.Core.Scripting;

/// <summary>
/// The PowerShell side of <see cref="ScriptWorker"/>, for PowerShell 7 or later (pwsh). No file is written: the command
/// line holds a short bootstrap (<see cref="Bootstrap"/>) that reads the worker's code, base64-encoded, from the first
/// line of input (<see cref="Preamble"/>) and runs it. It reads one JSON message per line, runs the scripts in it with
/// the <c>$tr</c> object, and writes one line back after <see cref="ReplyMarker"/>. Scripts are parsed with a file name
/// of their own, so errors show their lines; what they write (output, Write-Host, warnings…) becomes their output.
/// </summary>
internal static class PowerShellWorkerCode
{
    /// <summary>Starts each answer: lines without it come from programs that scripts started.</summary>
    public const string ReplyMarker = "\u001eTR ";

    /// <summary>The command (pwsh -Command): reads the worker from the first line of input and runs it.</summary>
    public const string Bootstrap =
        "$i=[IO.StreamReader]::new([Console]::OpenStandardInput(),[Text.UTF8Encoding]::new($false));" +
        "& ([ScriptBlock]::Create([Text.Encoding]::UTF8.GetString([Convert]::FromBase64String($i.ReadLine())))) $i";

    /// <summary>The first line of input: the worker's code, base64-encoded (UTF-8).</summary>
    public static string Preamble { get; } = Convert.ToBase64String(Encoding.UTF8.GetBytes(Source));

    private const string Source = """
        param([System.IO.TextReader] $Reader)

        # Requests come through $Reader (stdin); answers go to a copy of stdout, each after a marker. What scripts write to the
        # console ([Console]::WriteLine) is captured, so it cannot mix into the answers.
        $ErrorActionPreference = 'Stop'
        $ProgressPreference = 'SilentlyContinue'
        $TrOut = [System.IO.StreamWriter]::new([Console]::OpenStandardOutput(), [System.Text.UTF8Encoding]::new($false))
        $TrMarker = [string][char]0x1E + 'TR '
        $TrConsole = [System.IO.StringWriter]::new()
        [Console]::SetOut($TrConsole)
        [Console]::SetError($TrConsole)

        # Requests keep coming through $Reader and answers through $TrOut, which hold the original stdin and stdout. Programs that
        # scripts start then read nothing and write to stderr: they can neither take a request nor mix into an answer. PowerShell
        # has no command for it, so the two functions of the OS are declared here (no compiler needed); without them, the
        # protocol still works.
        $TrNothing = $null
        try {
            $module = [System.Reflection.Emit.AssemblyBuilder]::DefineDynamicAssembly([System.Reflection.AssemblyName]::new('TermRestStreams'), 'Run').DefineDynamicModule('TermRestStreams')
            if ($IsWindows) {
                $type = $module.DefineType('TermRestStreams.Kernel32', 'Public, Abstract, Sealed')
                $type.DefinePInvokeMethod('SetStdHandle', 'kernel32.dll', 'Public, Static, PinvokeImpl', 'Standard', [bool], [type[]] @([int], [IntPtr]), 'Winapi', 'Unicode').SetImplementationFlags('PreserveSig')
                $type.DefinePInvokeMethod('GetStdHandle', 'kernel32.dll', 'Public, Static, PinvokeImpl', 'Standard', [IntPtr], [type[]] @([int]), 'Winapi', 'Unicode').SetImplementationFlags('PreserveSig')
                $native = $type.CreateType()
                $TrNothing = [System.IO.FileStream]::new('NUL', 'Open', 'Read', 'ReadWrite, Inheritable')
                [void] $native::SetStdHandle(-10, $TrNothing.SafeFileHandle.DangerousGetHandle())
                [void] $native::SetStdHandle(-11, $native::GetStdHandle(-12))
            }
            else {
                foreach ($library in 'libc.so.6', 'libc') {
                    try {
                        $type = $module.DefineType('TermRestStreams.' + $library.Replace('.', '_'), 'Public, Abstract, Sealed')
                        $type.DefinePInvokeMethod('dup2', $library, 'Public, Static, PinvokeImpl', 'Standard', [int], [type[]] @([int], [int]), 'Cdecl', 'Ansi').SetImplementationFlags('PreserveSig')
                        $native = $type.CreateType()
                        $TrNothing = [System.IO.FileStream]::new('/dev/null', 'Open', 'Read')
                        [void] $native::dup2($TrNothing.SafeFileHandle.DangerousGetHandle().ToInt32(), 0)
                        [void] $native::dup2(2, 1)
                        break
                    }
                    catch {
                        # try the next name of the C library
                    }
                }
            }
        }
        catch {
            # a nicety: the protocol works without it
        }

        $TrMaxLines = 1000
        $TrCompiled = [System.Collections.Generic.Dictionary[string, object]]::new([StringComparer]::Ordinal)
        $TrNames = [System.Collections.Generic.Dictionary[string, string]]::new([StringComparer]::Ordinal)
        $TrSources = [System.Collections.Generic.Dictionary[string, string[]]]::new([StringComparer]::Ordinal)
        if ($null -eq ('System.Text.Json.JsonDocument' -as [type])) {
            Add-Type -AssemblyName System.Text.Json
        }

        class TrAssertionException : System.Exception {
            TrAssertionException([string] $message) : base($message) {
            }
        }

        class TrText {
            # Variables and headers are text: strings stay as they are, anything else becomes JSON.
            static [string] Of([object] $value) {
                if ($null -eq $value) { return '' }
                if ($value -is [string]) { return $value }
                if ($value -is [bool]) { if ($value) { return 'true' } else { return 'false' } }
                if ($value -is [datetime]) { return $value.ToString('o', [cultureinfo]::InvariantCulture) }
                if ([TrText]::IsNumber($value)) { return ([System.IFormattable] $value).ToString($null, [cultureinfo]::InvariantCulture) }
                return ConvertTo-Json -InputObject $value -Depth 20 -Compress
            }

            static [string] Show([object] $value) {
                if ($null -eq $value) { return '$null' }
                if ($value -is [string]) { return ConvertTo-Json -InputObject $value -Compress }
                if ($value -is [bool]) { if ($value) { return '$true' } else { return '$false' } }
                if ([TrText]::IsNumber($value)) { return ([System.IFormattable] $value).ToString($null, [cultureinfo]::InvariantCulture) }
                return ConvertTo-Json -InputObject $value -Depth 5 -Compress -WarningAction SilentlyContinue
            }

            static [bool] IsNumber([object] $value) {
                if ($null -eq $value) { return $false }
                $code = [type]::GetTypeCode($value.GetType())
                return $code -ge [System.TypeCode]::SByte -and $code -le [System.TypeCode]::Decimal
            }

            # Numbers compare as numbers, text exactly, objects (JSON, hashtables) by their members, lists item by item.
            static [bool] Same([object] $expected, [object] $actual) {
                if ($null -eq $expected -or $null -eq $actual) { return ($null -eq $expected) -and ($null -eq $actual) }
                if ([TrText]::IsNumber($expected) -and [TrText]::IsNumber($actual)) { return [double] $expected -eq [double] $actual }
                if ($expected -is [string] -or $actual -is [string]) {
                    return ($expected -is [string]) -and ($actual -is [string]) -and [string]::Equals($expected, $actual, [StringComparison]::Ordinal)
                }

                if ($expected -is [bool] -or $actual -is [bool]) { return ($expected -is [bool]) -and ($actual -is [bool]) -and $expected -eq $actual }
                $left = [TrText]::Members($expected)
                $right = [TrText]::Members($actual)
                if ($null -ne $left -and $null -ne $right) {
                    if ($left.Count -ne $right.Count) { return $false }
                    foreach ($key in $left.Keys) {
                        if (-not $right.Contains($key) -or -not [TrText]::Same($left[$key], $right[$key])) { return $false }
                    }

                    return $true
                }

                if ($expected -is [System.Collections.IEnumerable] -and $actual -is [System.Collections.IEnumerable]) {
                    $a = @($expected)
                    $b = @($actual)
                    if ($a.Count -ne $b.Count) { return $false }
                    for ($i = 0; $i -lt $a.Count; $i++) {
                        if (-not [TrText]::Same($a[$i], $b[$i])) { return $false }
                    }

                    return $true
                }

                return [object]::Equals($expected, $actual)
            }

            # The members of an object from JSON, or the entries of a hashtable; null for anything else.
            static [System.Collections.Specialized.OrderedDictionary] Members([object] $value) {
                $members = [System.Collections.Specialized.OrderedDictionary]::new([StringComparer]::Ordinal)
                if ($value -is [System.Collections.IDictionary]) {
                    foreach ($key in $value.Keys) { $members[[string] $key] = $value[$key] }
                    return $members
                }

                if ($value -is [System.Management.Automation.PSCustomObject]) {
                    foreach ($property in $value.PSObject.Properties) { $members[$property.Name] = $property.Value }
                    return $members
                }

                return $null
            }

            # The code of an argument of the $tr method running now, as the script wrote it: for messages of failed assertions.
            static [string] Argument([string] $method, [int] $index) {
                foreach ($frame in Get-PSCallStack) {
                    if ($null -eq $frame.ScriptName -or -not $frame.ScriptName.StartsWith('termrest-script-')) { continue }
                    $tokens = $null
                    $errors = $null
                    $ast = [System.Management.Automation.Language.Parser]::ParseInput($frame.Position.Text, [ref] $tokens, [ref] $errors)
                    foreach ($call in $ast.FindAll({ param($node) $node -is [System.Management.Automation.Language.InvokeMemberExpressionAst] }, $true)) {
                        if ($call.Member.Extent.Text -eq $method -and $call.Arguments.Count -gt $index) { return $call.Arguments[$index].Extent.Text }
                    }

                    return $frame.Position.Text
                }

                return $method
            }
        }

        # The variables of one scope: text values by name.
        class TrScope {
            hidden [System.Collections.Generic.Dictionary[string, string]] $Values
            hidden [System.Collections.Generic.Dictionary[string, object]] $Changes

            TrScope([System.Collections.Generic.Dictionary[string, string]] $values, [System.Collections.Generic.Dictionary[string, object]] $changes) {
                $this.Values = $values
                $this.Changes = $changes
            }

            [object] Get([string] $name) { return $this.Get($name, $null) }

            [object] Get([string] $name, [object] $default) {
                $value = $null
                if ($this.Values.TryGetValue($name, [ref] $value)) { return $value }
                return $default
            }

            # Sets a value until TermRest closes: text as it is, anything else as JSON (true, 42, {"a":1}).
            [void] Set([string] $name, [object] $value) {
                $text = [TrText]::Of($value)
                $this.Values[$name] = $text
                $this.Changes[$name] = $text
            }

            [void] Unset([string] $name) {
                [void] $this.Values.Remove($name)
                $this.Changes[$name] = $null
            }

            [bool] Has([string] $name) { return $this.Values.ContainsKey($name) }

            [void] Clear() {
                foreach ($name in @($this.Values.Keys)) { $this.Unset($name) }
            }

            [hashtable] ToHashtable() {
                $table = [hashtable]::new([StringComparer]::Ordinal)
                foreach ($pair in $this.Values.GetEnumerator()) { $table[$pair.Key] = $pair.Value }
                return $table
            }

            [string] ToString() { return [TrText]::Show($this.ToHashtable()) }
        }

        # $tr.Variables: this request's variables, then the environment's, the collection's and the globals; Set is for this request.
        class TrVariables : TrScope {
            hidden [object[]] $Scopes

            TrVariables([System.Collections.Generic.Dictionary[string, string]] $local, [System.Collections.Generic.Dictionary[string, object]] $changes, [object[]] $scopes) : base($local, $changes) {
                $this.Scopes = $scopes
            }

            [object] Get([string] $name, [object] $default) {
                foreach ($scope in $this.Scopes) {
                    $value = $null
                    if ($scope.TryGetValue($name, [ref] $value)) { return $value }
                }

                return $default
            }

            [bool] Has([string] $name) {
                foreach ($scope in $this.Scopes) {
                    if ($scope.ContainsKey($name)) { return $true }
                }

                return $false
            }

            [hashtable] ToHashtable() {
                $table = [hashtable]::new([StringComparer]::Ordinal)
                for ($i = $this.Scopes.Count - 1; $i -ge 0; $i--) {
                    foreach ($pair in $this.Scopes[$i].GetEnumerator()) { $table[$pair.Key] = $pair.Value }
                }

                return $table
            }

            # Replaces {{name}} with the variables' values; unknown names stay as they are.
            [string] ReplaceIn([string] $text) {
                for ($round = 0; $round -lt 10; $round++) {
                    $found = [regex]::Matches($text, '\{\{([^{}]+)\}\}')
                    $builder = [System.Text.StringBuilder]::new()
                    $last = 0
                    $changed = $false
                    foreach ($match in $found) {
                        [void] $builder.Append($text, $last, $match.Index - $last)
                        $value = $this.Get($match.Groups[1].Value.Trim(), $null)
                        if ($null -eq $value) {
                            [void] $builder.Append($match.Value)
                        }
                        else {
                            [void] $builder.Append($value)
                            $changed = $true
                        }

                        $last = $match.Index + $match.Length
                    }

                    [void] $builder.Append($text, $last, $text.Length - $last)
                    $text = $builder.ToString()
                    if (-not $changed) { break }
                }

                return $text
            }
        }

        # Headers in order: names are case-insensitive and may repeat.
        class TrHeaders {
            hidden [System.Collections.Generic.List[string[]]] $Pairs = [System.Collections.Generic.List[string[]]]::new()
            hidden [bool] $Writable
            [int] $Count

            TrHeaders([object] $pairs, [bool] $writable) {
                foreach ($pair in @($pairs)) {
                    if ($null -ne $pair -and $pair.Count -ge 2) { $this.Pairs.Add([string[]] @([string] $pair[0], [string] $pair[1])) }
                }

                $this.Writable = $writable
                $this.Count = $this.Pairs.Count
            }

            [object] Get([string] $name) {
                foreach ($pair in $this.Pairs) {
                    if ($pair[0] -eq $name) { return $pair[1] }
                }

                return $null
            }

            [string[]] GetAll([string] $name) {
                $values = [System.Collections.Generic.List[string]]::new()
                foreach ($pair in $this.Pairs) {
                    if ($pair[0] -eq $name) { $values.Add($pair[1]) }
                }

                return $values.ToArray()
            }

            [bool] Has([string] $name) { return $null -ne $this.Get($name) }

            # Gives the header this one value, where it was (new ones go last).
            [void] Set([string] $name, [object] $value) {
                $this.Check()
                $index = -1
                for ($i = $this.Pairs.Count - 1; $i -ge 0; $i--) {
                    if ($this.Pairs[$i][0] -eq $name) {
                        $this.Pairs.RemoveAt($i)
                        $index = $i
                    }
                }

                if ($index -lt 0) { $index = $this.Pairs.Count }
                $this.Pairs.Insert([Math]::Min($index, $this.Pairs.Count), [string[]] @($name, [TrText]::Of($value)))
                $this.Count = $this.Pairs.Count
            }

            [void] Add([string] $name, [object] $value) {
                $this.Check()
                $this.Pairs.Add([string[]] @($name, [TrText]::Of($value)))
                $this.Count = $this.Pairs.Count
            }

            [void] Remove([string] $name) {
                $this.Check()
                for ($i = $this.Pairs.Count - 1; $i -ge 0; $i--) {
                    if ($this.Pairs[$i][0] -eq $name) { $this.Pairs.RemoveAt($i) }
                }

                $this.Count = $this.Pairs.Count
            }

            [hashtable] ToHashtable() {
                $table = @{}
                foreach ($pair in $this.Pairs) { $table[$pair[0]] = $pair[1] }
                return $table
            }

            [object[]] Items() {
                $items = [System.Collections.Generic.List[object]]::new()
                foreach ($pair in $this.Pairs) { $items.Add([pscustomobject] @{ Name = $pair[0]; Value = $pair[1] }) }
                return $items.ToArray()
            }

            [string] ToString() { return ($this.Pairs | ForEach-Object { $_[0] + ': ' + $_[1] }) -join ', ' }

            hidden [object] ToJson() { return $this.Pairs.ToArray() }

            hidden [void] Check() {
                if (-not $this.Writable) { throw [System.InvalidOperationException]::new('These headers cannot be changed: change the request in a pre-request script.') }
            }
        }

        # $tr.Request: as written (pre-request scripts can change it) or as sent (test scripts).
        class TrRequest {
            [object] $Name
            [string] $Method
            [string] $Url
            [TrHeaders] $Headers
            # The raw body; $null when the body is not raw text (form fields, a file…).
            [object] $Body

            TrRequest([System.Collections.IDictionary] $data, [bool] $writable) {
                $this.Name = $data['name']
                $this.Method = if ($data['method']) { $data['method'] } else { 'GET' }
                $this.Url = $data['url']
                $this.Body = $data['body']
                $this.Headers = [TrHeaders]::new($data['headers'], $writable)
            }

            [string] ToString() { return $this.Method + ' ' + $this.Url }
        }

        # $tr.Response: what arrived, in post-response scripts.
        class TrResponse {
            [int] $Status
            [int] $Code
            [string] $Reason
            [TrHeaders] $Headers
            # How long the response took, in milliseconds.
            [double] $Time
            [long] $Size
            hidden [object] $TextValue
            hidden [object] $Base64

            TrResponse([System.Collections.IDictionary] $data) {
                $this.Status = $this.Code = $data['status']
                $this.Reason = $data['reason']
                $this.Headers = [TrHeaders]::new($data['headers'], $false)
                $this.Time = $data['time']
                $this.Size = $data['size']
                $this.TextValue = $data['text']
                $this.Base64 = $data['base64']
            }

            [byte[]] Content() {
                if ($null -ne $this.Base64) { return [Convert]::FromBase64String($this.Base64) }
                return [System.Text.Encoding]::UTF8.GetBytes([string] $this.TextValue)
            }

            [string] Text() {
                if ($null -ne $this.TextValue) { return $this.TextValue }
                return [System.Text.Encoding]::UTF8.GetString($this.Content())
            }

            # The body as JSON (ConvertFrom-Json): $json.name, $json.items…; throws when it is not JSON.
            [object] Json() { return ConvertFrom-Json -InputObject $this.Text() }

            [string] ToString() { return [string] $this.Status + ' ' + $this.Reason }
        }

        class TrInfo {
            # 'prerequest' or 'test'.
            [string] $Phase
            # The script running now, such as "request 'Users/Create'".
            [string] $Script
            [object] $RequestName
            [object] $Collection
            [object] $Environment
        }

        # What scripts get as $tr: the request, the response, variables, tests.
        class Tr {
            [TrScope] $Environment
            [TrScope] $CollectionVariables
            [TrScope] $Globals
            [TrVariables] $Variables
            [TrRequest] $Request
            # The response; $null in pre-request scripts.
            [TrResponse] $Response
            [TrInfo] $Info
            hidden [System.Collections.Generic.List[object]] $Tests = [System.Collections.Generic.List[object]]::new()

            # Records a test, which fails when its script block throws ($tr.Assert, $tr.AssertEqual, throw…).
            [void] Test([string] $name, [scriptblock] $test) {
                $result = [ordered] @{ name = $name; passed = $true; script = $this.Info.Script }
                try {
                    $null = & $test
                }
                catch {
                    $result.passed = $false
                    $result.error = if ($_.Exception -is [TrAssertionException]) { $_.Exception.Message } else { $_.ToString() }
                }

                $this.Tests.Add($result)
            }

            [void] Assert([object] $condition) { $this.Assert($condition, $null) }

            [void] Assert([object] $condition, [string] $message) {
                if (-not $condition) {
                    if (-not $message) { $message = 'assertion failed: ' + [TrText]::Argument('Assert', 0) }
                    throw [TrAssertionException]::new($message)
                }
            }

            [void] AssertEqual([object] $expected, [object] $actual) { $this.AssertEqual($expected, $actual, $null) }

            # Compares numbers as numbers, text exactly, JSON objects and hashtables by their members, and lists item by item.
            [void] AssertEqual([object] $expected, [object] $actual, [string] $message) {
                if (-not [TrText]::Same($expected, $actual)) {
                    if (-not $message) { $message = [TrText]::Argument('AssertEqual', 1) + ': expected ' + [TrText]::Show($expected) + ', got ' + [TrText]::Show($actual) }
                    throw [TrAssertionException]::new($message)
                }
            }
        }

        function ConvertFrom-TrJson([System.Text.Json.JsonElement] $Element) {
            switch ($Element.ValueKind) {
                'Object' {
                    $object = [System.Collections.Generic.Dictionary[string, object]]::new([StringComparer]::Ordinal)
                    foreach ($property in $Element.EnumerateObject()) { $object[$property.Name] = ConvertFrom-TrJson $property.Value }
                    return , $object
                }
                'Array' {
                    $array = [System.Collections.Generic.List[object]]::new()
                    foreach ($item in $Element.EnumerateArray()) { $array.Add((ConvertFrom-TrJson $item)) }
                    return , $array
                }
                'String' { return $Element.GetString() }
                'Number' {
                    $whole = 0L
                    if ($Element.TryGetInt64([ref] $whole)) { return $whole }
                    return $Element.GetDouble()
                }
                'True' { return $true }
                'False' { return $false }
                default { return $null }
            }
        }

        function Get-TrValues($Variables, [string] $Scope) {
            $values = [System.Collections.Generic.Dictionary[string, string]]::new([StringComparer]::Ordinal)
            if ($null -ne $Variables -and $Variables.ContainsKey($Scope) -and $null -ne $Variables[$Scope]) {
                foreach ($pair in $Variables[$Scope].GetEnumerator()) { $values[$pair.Key] = [string] $pair.Value }
            }

            return , $values
        }

        # The script's lines in the stack, outermost first, then the error, as Python shows them.
        function Get-TrTraceback($Record) {
            $text = [System.Text.StringBuilder]::new()
            $frames = @(($Record.ScriptStackTrace -split "`r?`n") | Where-Object { $_ })
            [array]::Reverse($frames)
            foreach ($frame in $frames) {
                if ($frame -notmatch '^at (?<where>.*), (?<file>termrest-script-\d+): line (?<line>\d+)$') { continue }
                $number = [int] $Matches.line
                [void] $text.Append('  File "').Append($TrNames[$Matches.file]).Append('", line ').Append($number).Append("`n")
                $lines = $TrSources[$Matches.file]
                if ($number -ge 1 -and $number -le $lines.Count -and $lines[$number - 1].Trim()) {
                    [void] $text.Append('    ').Append($lines[$number - 1].Trim()).Append("`n")
                }
            }

            return $text.Append((Get-TrFailure $Record)).ToString()
        }

        function Get-TrFailure($Record) {
            $exception = $Record.Exception
            while ($exception -is [System.Management.Automation.MethodInvocationException] -and $null -ne $exception.InnerException) { $exception = $exception.InnerException }
            $type = if ($exception -is [TrAssertionException]) { 'AssertionException' } else { $exception.GetType().Name }
            return $type + ': ' + $exception.Message
        }

        # What a script wrote: output, Write-Host and the other streams, and [Console] writes, as lines of text.
        function ConvertTo-TrLines($Items) {
            $lines = [System.Collections.Generic.List[string]]::new()
            $pending = [System.Collections.Generic.List[object]]::new()
            $flush = {
                if ($pending.Count -gt 0) {
                    $block = @($pending | Out-String -Stream -Width 160 | ForEach-Object { $_.TrimEnd() })
                    $first = 0
                    $last = $block.Count - 1
                    while ($first -le $last -and -not $block[$first]) { $first++ }
                    while ($last -ge $first -and -not $block[$last]) { $last-- }
                    for ($i = $first; $i -le $last; $i++) { $lines.Add($block[$i]) }
                    $pending.Clear()
                }
            }

            foreach ($item in $Items) {
                $text = if ($item -is [System.Management.Automation.InformationRecord]) { [string] $item.MessageData }
                elseif ($item -is [System.Management.Automation.WarningRecord]) { 'WARNING: ' + $item.Message }
                elseif ($item -is [System.Management.Automation.VerboseRecord]) { 'VERBOSE: ' + $item.Message }
                elseif ($item -is [System.Management.Automation.DebugRecord]) { 'DEBUG: ' + $item.Message }
                elseif ($item -is [System.Management.Automation.ErrorRecord]) { 'ERROR: ' + $item.ToString() }
                elseif ($item -is [string]) { $item }
                else { $null }
                if ($null -eq $text) {
                    $pending.Add($item)
                    continue
                }

                . $flush
                foreach ($line in $text.Split("`n")) { $lines.Add($line.TrimEnd("`r")) }
            }

            . $flush
            $console = $TrConsole.ToString()
            [void] $TrConsole.GetStringBuilder().Clear()
            if ($console) {
                foreach ($line in $console.TrimEnd("`r", "`n").Split("`n")) { $lines.Add($line.TrimEnd("`r")) }
            }

            return $lines.ToArray()
        }

        # Runs one script with $tr in its scope; what it writes is collected as it comes, so it is kept if the script fails.
        function Invoke-TrScript([scriptblock] $TrScript, [Tr] $tr, [System.Collections.Generic.List[object]] $TrItems) {
            & $TrScript *>&1 | ForEach-Object { $TrItems.Add($_) }
        }

        function Invoke-TrMessage([string] $Line) {
            $document = [System.Text.Json.JsonDocument]::Parse($Line)
            try {
                $message = ConvertFrom-TrJson $document.RootElement
            }
            finally {
                $document.Dispose()
            }

            $phase = [string] $message['phase']
            $changes = [ordered] @{}
            foreach ($scope in 'environment', 'collection', 'globals', 'local') {
                $changes[$scope] = [System.Collections.Generic.Dictionary[string, object]]::new([StringComparer]::Ordinal)
            }

            $variables = $message['variables']
            $environment = Get-TrValues $variables 'environment'
            $collection = Get-TrValues $variables 'collection'
            $globals = Get-TrValues $variables 'globals'
            $local = Get-TrValues $variables 'local'
            $data = $message['info']
            $info = [TrInfo]::new()
            $info.Phase = $phase
            if ($null -ne $data) {
                $info.RequestName = $data['request_name']
                $info.Collection = $data['collection']
                $info.Environment = $data['environment']
            }

            $tr = [Tr]::new()
            $tr.Environment = [TrScope]::new($environment, $changes['environment'])
            $tr.CollectionVariables = [TrScope]::new($collection, $changes['collection'])
            $tr.Globals = [TrScope]::new($globals, $changes['globals'])
            $tr.Variables = [TrVariables]::new($local, $changes['local'], @($local, $environment, $collection, $globals))
            $tr.Request = [TrRequest]::new($message['request'], $phase -eq 'prerequest')
            if ($null -ne $message['response']) { $tr.Response = [TrResponse]::new($message['response']) }
            $tr.Info = $info
            $logs = [System.Collections.Generic.List[object]]::new()
            $dropped = 0
            $failure = $null
            foreach ($script in @($message['scripts'])) {
                $name = [string] $script['name']
                $code = [string] $script['code']
                $info.Script = $name
                $items = [System.Collections.Generic.List[object]]::new()
                $compiled = $null
                if (-not $TrCompiled.TryGetValue($code, [ref] $compiled)) {
                    $file = 'termrest-script-' + $TrCompiled.Count
                    $tokens = $null
                    $errors = $null
                    $ast = [System.Management.Automation.Language.Parser]::ParseInput($code, $file, [ref] $tokens, [ref] $errors)
                    $compiled = if ($errors.Count -gt 0) { $errors } else { $ast.GetScriptBlock() }
                    $TrCompiled[$code] = $compiled
                    $TrSources[$file] = $code.Replace("`r`n", "`n").Split("`n")
                    $TrNames[$file] = $name
                }

                if ($compiled -is [scriptblock]) {
                    $TrNames[$compiled.File] = $name
                    try {
                        Invoke-TrScript $compiled $tr $items
                    }
                    catch {
                        $failure = [ordered] @{ script = $name; message = Get-TrFailure $_; traceback = Get-TrTraceback $_ }
                    }
                }
                else {
                    # Syntax errors, as Python shows them: the first in the message, all in the traceback (one mistake often
                    # makes several, on the same line).
                    $first = $compiled[0]
                    $traceback = [System.Text.StringBuilder]::new()
                    $previous = -1
                    foreach ($syntax in $compiled | Select-Object -First 10) {
                        if ($syntax.Extent.StartLineNumber -ne $previous) {
                            $previous = $syntax.Extent.StartLineNumber
                            [void] $traceback.Append('  File "').Append($name).Append('", line ').Append($previous).Append("`n")
                            if ($syntax.Extent.StartScriptPosition.Line.Trim()) { [void] $traceback.Append('    ').Append($syntax.Extent.StartScriptPosition.Line.Trim()).Append("`n") }
                        }

                        [void] $traceback.Append('ParseError: ').Append($syntax.Message).Append("`n")
                    }

                    $failure = [ordered] @{ script = $name; message = 'ParseError: ' + $first.Message + ' (line ' + $first.Extent.StartLineNumber + ')'; traceback = $traceback.ToString().TrimEnd() }
                }

                foreach ($line in @(ConvertTo-TrLines $items)) {
                    if ($logs.Count -lt $TrMaxLines) {
                        $logs.Add([ordered] @{ script = $name; text = if ($line.Length -gt 4000) { $line.Substring(0, 4000) } else { $line } })
                    }
                    else {
                        $dropped++
                    }
                }

                if ($null -ne $failure) { break }
            }

            $result = [ordered] @{ changes = $changes; tests = $tr.Tests; logs = $logs; dropped = $dropped; error = $failure }
            if ($phase -eq 'prerequest') {
                $result.request = [ordered] @{ method = $tr.Request.Method; url = $tr.Request.Url; headers = $tr.Request.Headers.ToJson(); body = $tr.Request.Body }
            }

            return $result
        }

        while ($true) {
            $line = $Reader.ReadLine()
            if ($null -eq $line) { break }
            try {
                $reply = Invoke-TrMessage $line
            }
            catch {
                $reply = @{ fatal = ($_ | Out-String) + $_.ScriptStackTrace }
            }

            $TrOut.Write($TrMarker + (ConvertTo-Json -InputObject $reply -Depth 10 -Compress) + "`n")
            $TrOut.Flush()
        }
        """;
}