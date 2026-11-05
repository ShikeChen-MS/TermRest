using System.Text.Json.Nodes;
using TermRest.Core.Json;

namespace TermRest.Core.Model;

public sealed class AuthParam
{
    public string Key { get; set; } = "";

    /// <summary>Raw JSON value; Postman stores strings, booleans and objects here.</summary>
    public JsonNode? Value { get; set; }

    public string? Type { get; set; }

    public JsonObject? Source { get; set; }

    public string? ValueAsString => Value switch
    {
        null => null,
        JsonValue v => v.GetString(),
        _ => Value.ToJsonString(),
    };

    public AuthParam Clone() => new() { Key = Key, Value = Value?.DeepClone(), Type = Type, Source = Source };
}

/// <summary>Postman auth block. A missing block (<c>null</c> spec) means "inherit from parent".</summary>
public sealed class AuthSpec
{
    public static readonly string[] KnownTypes =
        ["noauth", "basic", "bearer", "apikey", "digest", "ntlm", "oauth2", "oauth1", "awsv4", "hawk", "edgegrid", "jwt", "asap"];

    public string Type { get; set; } = "noauth";

    public List<AuthParam> Params { get; set; } = [];

    public JsonObject? Source { get; set; }

    public string? Get(string key) => Params.FirstOrDefault(p => p.Key == key)?.ValueAsString;

    public void Set(string key, string? value)
    {
        var existing = Params.FirstOrDefault(p => p.Key == key);
        if (existing is null)
        {
            Params.Add(new AuthParam { Key = key, Value = value is null ? null : JsonValue.Create(value), Type = "string" });
        }
        else if (existing.ValueAsString != value)
        {
            existing.Value = value is null ? null : JsonValue.Create(value);
        }
    }

    public AuthSpec Clone() => new() { Type = Type, Params = Params.Select(p => p.Clone()).ToList(), Source = Source };

    public bool ContentEquals(AuthSpec? other)
    {
        if (other is null || Type != other.Type || Params.Count != other.Params.Count)
        {
            return false;
        }

        for (var i = 0; i < Params.Count; i++)
        {
            var a = Params[i];
            var b = other.Params[i];
            if (a.Key != b.Key || a.Type != b.Type || !JsonNode.DeepEquals(a.Value, b.Value))
            {
                return false;
            }
        }

        return true;
    }

    public static bool AreEqual(AuthSpec? a, AuthSpec? b) => a is null ? b is null : a.ContentEquals(b);

    public static string DisplayName(string? type) => type?.ToLowerInvariant() switch
    {
        null => "Inherit auth from parent",
        "noauth" => "No Auth",
        "basic" => "Basic Auth",
        "bearer" => "Bearer Token",
        "apikey" => "API Key",
        "digest" => "Digest Auth",
        "ntlm" => "NTLM",
        "oauth2" => "OAuth 2.0",
        "oauth1" => "OAuth 1.0",
        "awsv4" => "AWS Signature",
        "hawk" => "Hawk",
        "edgegrid" => "Akamai EdgeGrid",
        _ => type,
    };
}
