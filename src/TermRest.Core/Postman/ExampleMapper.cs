using System.Text.Json.Nodes;
using TermRest.Core.Http;
using TermRest.Core.Json;
using TermRest.Core.Model;

namespace TermRest.Core.Postman;

/// <summary>
/// Maps a Postman saved example (an item's <c>response</c> entry) to <see cref="ExampleSpec"/> and back. Writing is
/// surgical, as for requests (see <see cref="PostmanRequestMapper"/>): only what changed is written, and the rest of the
/// example (its id, cookies, response time, unknown properties) stays as it is.
/// </summary>
public static class ExampleMapper
{
    /// <summary>The keys of a saved example, in Postman's order.</summary>
    internal static readonly string[] ExampleOrder =
        ["id", "name", "originalRequest", "status", "code", "_postman_previewlanguage", "header", "cookie", "responseTime", "body"];

    public static ExampleSpec Read(JsonObject example, PostmanSchemaVersion version)
    {
        var request = new HttpRequestSpec();
        PostmanRequestMapper.ReadRequest(example["originalRequest"], request, version);
        return new ExampleSpec
        {
            Name = example["name"].GetString() ?? "",
            Request = request,
            Code = example["code"].GetInt(),
            Status = example["status"].GetString() ?? "",
            Headers = PostmanRequestMapper.ReadKeyValues(example["header"]),
            Body = example["body"].GetString() ?? "",
            Language = example["_postman_previewlanguage"].GetString(),
        };
    }

    /// <summary>Writes the parts of <paramref name="spec"/> that differ from <paramref name="baseline"/> into the example.</summary>
    public static void Apply(JsonObject example, ExampleSpec spec, ExampleSpec baseline, PostmanSchemaVersion version)
    {
        if (spec.Name != baseline.Name)
        {
            example.SetOrdered("name", spec.Name, ExampleOrder);
        }

        if (!spec.Request.ContentEquals(baseline.Request))
        {
            var force = false;
            if (example["originalRequest"] is not JsonObject request)
            {
                request = new JsonObject();
                example.SetOrdered("originalRequest", request, ExampleOrder);
                force = true;
            }

            PostmanRequestMapper.ApplyRequest(request, spec.Request, baseline.Request, version, force);
        }

        if (spec.Status != baseline.Status)
        {
            example.SetOrdered("status", spec.Status, ExampleOrder);
        }

        if (spec.Code != baseline.Code)
        {
            if (spec.Code is { } code)
            {
                example.SetOrdered("code", code, ExampleOrder);
            }
            else
            {
                example.Remove("code");
            }
        }

        if (spec.Language != baseline.Language)
        {
            if (spec.Language is { } language)
            {
                example.SetOrdered("_postman_previewlanguage", language, ExampleOrder);
            }
            else
            {
                example.Remove("_postman_previewlanguage");
            }
        }

        if (!KeyValueEntry.ListEquals(spec.Headers, baseline.Headers))
        {
            example.SetOrdered("header", PostmanRequestMapper.WriteKeyValues(spec.Headers, KeyValueStyle.Header), ExampleOrder);
        }

        if (spec.Body != baseline.Body)
        {
            example.SetOrdered("body", spec.Body, ExampleOrder);
        }
    }

    /// <summary>A new saved example, written as Postman writes one.</summary>
    public static JsonObject Create(ExampleSpec spec, PostmanSchemaVersion version)
    {
        var request = new JsonObject();
        PostmanRequestMapper.ApplyRequest(request, spec.Request, new HttpRequestSpec(), version, force: true);
        var example = new JsonObject
        {
            ["name"] = spec.Name,
            ["originalRequest"] = request,
            ["status"] = spec.Status,
        };
        if (spec.Code is { } code)
        {
            example["code"] = code;
        }

        example["_postman_previewlanguage"] = spec.Language ?? "text";
        example["header"] = PostmanRequestMapper.WriteKeyValues(spec.Headers, KeyValueStyle.Header);
        example["cookie"] = new JsonArray();
        example["body"] = spec.Body;
        return example;
    }

    /// <summary>
    /// An example of <paramref name="request"/> that holds <paramref name="response"/>: its status, its headers as the
    /// server sent them, and its body as text. A binary body is left out (an example holds text): <paramref name="note"/>
    /// says so.
    /// </summary>
    public static ExampleSpec FromResponse(string name, HttpRequestSpec request, ResponseSnapshot response, out string? note)
    {
        var language = BodyFormatter.Language(response.Body, response.ContentType);
        note = null;
        var body = "";
        if (language == "binary" || BodyFormatter.IsBinary(response.Body, response.ContentType))
        {
            note = $"The body ({HttpExchange.FormatSize(response.Body.LongLength)} of {response.ContentType ?? "binary data"}) is not saved: an example holds text.";
            language = "text";
        }
        else
        {
            body = BodyFormatter.Decode(response.Body, response.ContentType);
            if (response.BodyTruncated)
            {
                note = "The body was cut at the maximum response size: the example has what was received.";
            }
        }

        return new ExampleSpec
        {
            Name = name,
            Request = request.Clone(),
            Code = response.StatusCode,
            Status = response.ReasonPhrase,
            Headers = [.. response.Headers.Select(h => new KeyValueEntry { Key = h.Key, Value = h.Value })],
            Body = body,
            Language = language,
        };
    }
}
