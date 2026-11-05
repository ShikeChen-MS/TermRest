namespace TermRest.Core.Tests.Infrastructure;

/// <summary>
/// Writes a Postman collection format v3 workspace (postman/collections, environments, globals) in the styles the
/// Postman app produces: folded long text, single- and double-quoted values, header maps and lists, every body type,
/// list-form credentials, multi-auth folders, examples, a non-HTTP request and a folder without a definition file.
/// </summary>
internal static class V3Fixture
{
    public static readonly Dictionary<string, string> Files = new()
    {
        [".postman/resources.yaml"] = """
            workspace:
              id: 00000000-0000-4000-8000-000000000000
            """,
        ["postman/collections/Widget API/.resources/definition.yaml"] = """
            $kind: collection
            description: >-
              Widget service used by the TermRest tests.


              Covers the request shapes Postman writes.
            variables:
              - key: baseUrl
                value: https://api.example.com
              - key: apiKey
                secret: true
            auth:
              type: apikey
              credentials:
                key: X-Api-Key
                value: '{{apiKey}}'
                in: header
            scripts:
              - type: http:afterResponse
                code: |-
                  pm.test('JSON', () => pm.response.to.be.json);
                language: text/javascript
            """,
        ["postman/collections/Widget API/Health.request.yaml"] = """
            $kind: http-request
            url: '{{baseUrl}}/health'
            method: GET
            auth:
              type: noauth
            order: 1000
            """,
        ["postman/collections/Widget API/Widgets/.resources/definition.yaml"] = """
            $kind: collection
            description: Widget endpoints.
            auth:
              - id: 0b1c6a5e-0000-4000-8000-000000000001
                type: bearer
                name: bearer auth
                credentials:
                  token: '{{token}}'
            order: 2000
            """,
        ["postman/collections/Widget API/Widgets/List widgets.request.yaml"] = """
            $kind: http-request
            description: Lists the widgets the caller can see. Results are paginated and sorted by
              name; use the cursor from the previous page.
            url: '{{baseUrl}}/v1/widgets?limit=50'
            method: GET
            headers:
              Accept: application/json
            queryParams:
              - key: limit
                value: '50'
              - key: cursor
                value: '{{cursor}}'
                disabled: true
                description: From the previous page
            scripts:
              - type: afterResponse
                code: |-
                  pm.test('ok', () => pm.response.to.have.status(200));
                language: text/javascript
            examples: ./.resources/List widgets.resources/examples
            order: 1000
            """,
        ["postman/collections/Widget API/Widgets/.resources/List widgets.resources/examples/200 OK.example.yaml"] = """
            $kind: http-example
            request:
              url: ''
              method: GET
            response:
              statusCode: 200
              statusText: OK
              headers:
                Content-Type: application/json
              body:
                type: json
                content: '[]'
            order: 1000
            """,
        ["postman/collections/Widget API/Widgets/Create widget.request.yaml"] = """
            $kind: http-request
            url: "{{baseUrl}}/v1/widgets"
            method: POST
            headers:
              - key: Content-Type
                value: application/json
              - key: X-Request-Id
                value: "{{$guid}}"
                description: Correlation id
              - key: X-Debug
                value: "1"
                disabled: true
            body:
              type: json
              content: |-
                {
                  "name": "Sprocket",
                  "size": 3
                }
            order: 2000
            """,
        ["postman/collections/Widget API/Widgets/Upload image.request.yaml"] = """
            $kind: http-request
            url: "{{baseUrl}}/v1/widgets/images"
            method: POST
            body:
              type: formdata
              content:
                - type: text
                  key: title
                  value: Foo Bar
                  disabled: false
                - type: file
                  key: file
                  src: []
                  disabled: false
            order: 3000
            """,
        ["postman/collections/Widget API/Widgets/Login form.request.yaml"] = """
            $kind: http-request
            url: "{{baseUrl}}/oauth/token"
            method: POST
            body:
              type: urlencoded
              content:
                - key: grant_type
                  value: client_credentials
                - key: scope
                  value: widgets.read
                  disabled: true
            auth:
              type: basic
              credentials:
                - key: username
                  value: "{{clientId}}"
                - key: password
                  value: "{{clientSecret}}"
            settings:
              strictSSL: false
              disabledSystemHeaders:
                - user-agent
            order: 4000
            """,
        ["postman/collections/Widget API/Widgets/GraphQL query.request.yaml"] = """
            $kind: http-request
            url: "{{baseUrl}}/graphql"
            method: POST
            body:
              type: graphql
              content:
                query: |-
                  query { widgets { id name } }
                variables: '{"first": 5}'
                operationName: null
            order: 5000
            """,
        ["postman/collections/Widget API/Widgets/Subscribe.request.yaml"] = """
            $kind: websocket-request
            url: wss://api.example.com/stream
            order: 6000
            """,
        ["postman/collections/Widget API/Admin/Reset.request.yaml"] = """
            $kind: http-request
            url: "{{baseUrl}}/v1/widgets/:id/reset"
            method: DELETE
            pathVariables:
              - key: id
                value: "42"
            """,
        ["postman/environments/Local.environment.yaml"] = """
            name: Local
            values:
              - key: baseUrl
                value: 'http://localhost:8080'
                enabled: true
              - key: token
                value: ''
                enabled: true
                type: secret
              - key: cursor
                value: abc
                enabled: false
            """,
        ["postman/globals/workspace.globals.yaml"] = """
            name: Globals
            values: []
            """,
    };

    /// <summary>Writes the workspace into a new temp directory and returns its path.</summary>
    public static string Create()
    {
        var root = TestFiles.NewTempDirectory();
        foreach (var (path, content) in Files)
        {
            var full = Path.Combine(root, path.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(full)!);
            File.WriteAllText(full, content.ReplaceLineEndings("\n") + "\n");
        }

        return root;
    }

    public static string CollectionPath(string root) => Path.Combine(root, "postman", "collections", "Widget API");

    /// <summary>Every file under <paramref name="root"/> with its content (relative path with forward slashes).</summary>
    public static Dictionary<string, string> Snapshot(string root) =>
        Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
            .ToDictionary(f => Path.GetRelativePath(root, f).Replace('\\', '/'), File.ReadAllText, StringComparer.Ordinal);
}
