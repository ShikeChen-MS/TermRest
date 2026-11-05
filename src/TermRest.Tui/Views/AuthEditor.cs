using Terminal.Gui.ViewBase;
using Terminal.Gui.Views;
using TermRest.Core.Model;

namespace TermRest.Tui.Views;

/// <summary>Auth tab: pick an auth type on the left, fill its fields on the right.</summary>
internal sealed class AuthEditor : View
{
    private static readonly (string? Type, string Label)[] Choices =
    [
        (null, "Inherit auth from parent"),
        ("noauth", "No Auth"),
        ("basic", "Basic Auth"),
        ("bearer", "Bearer Token"),
        ("apikey", "API Key"),
        ("digest", "Digest Auth"),
        ("ntlm", "NTLM"),
        ("oauth2", "OAuth 2.0 (access token)"),
    ];

    private readonly ListView _types;
    private readonly View _fields;
    private readonly Dictionary<string, AuthSpec> _byType = new(StringComparer.OrdinalIgnoreCase);
    private readonly System.Collections.ObjectModel.ObservableCollection<string> _labels = [];
    private AuthSpec? _auth;
    private string? _unsupportedType;
    private bool _loading;

    public AuthEditor()
    {
        CanFocus = true;
        Width = Dim.Fill();
        Height = Dim.Fill();
        _types = new ListView { X = 0, Y = 0, Width = 28, Height = Dim.Fill() };
        _types.SetSource(_labels);
        _types.ValueChanged += (_, e) =>
        {
            if (!_loading && e.NewValue is int index)
            {
                SelectType(index);
            }
        };
        _fields = new View { X = Pos.Right(_types) + 1, Y = 0, Width = Dim.Fill(), Height = Dim.Fill(), CanFocus = true };
        Add(_types, _fields);
    }

    public event EventHandler? Changed;

    /// <summary>Text describing what "inherit" resolves to (supplied by the main window).</summary>
    public Func<string>? DescribeInherited { get; set; }

    public AuthSpec? Auth => _auth;

    public void Load(AuthSpec? auth)
    {
        _loading = true;
        try
        {
            _byType.Clear();
            _auth = auth;
            if (auth is not null)
            {
                _byType[auth.Type] = auth;
            }

            _unsupportedType = auth is not null && Choices.All(c => !string.Equals(c.Type, auth.Type, StringComparison.OrdinalIgnoreCase)) ? auth.Type : null;
            _labels.Clear();
            foreach (var choice in Choices)
            {
                _labels.Add(choice.Label);
            }

            if (_unsupportedType is not null)
            {
                _labels.Add($"{AuthSpec.DisplayName(_unsupportedType)} (kept, not applied)");
            }

            _types.Value = IndexOf(auth?.Type);
            BuildFields();
        }
        finally
        {
            _loading = false;
        }
    }

    private int IndexOf(string? type)
    {
        if (type is null)
        {
            return 0;
        }

        for (var i = 0; i < Choices.Length; i++)
        {
            if (string.Equals(Choices[i].Type, type, StringComparison.OrdinalIgnoreCase))
            {
                return i;
            }
        }

        return _unsupportedType is not null ? Choices.Length : 1;
    }

    private void SelectType(int index)
    {
        var type = index < Choices.Length ? Choices[index].Type : _unsupportedType;
        if (type is null)
        {
            _auth = null;
        }
        else if (!_byType.TryGetValue(type, out var existing))
        {
            existing = new AuthSpec { Type = type };
            if (type == "apikey")
            {
                existing.Set("in", "header");
            }

            _byType[type] = existing;
            _auth = existing;
        }
        else
        {
            _auth = existing;
        }

        BuildFields();
        Changed?.Invoke(this, EventArgs.Empty);
    }

    private void BuildFields()
    {
        foreach (var view in _fields.SubViews.ToList())
        {
            _fields.Remove(view);
            view.Dispose();
        }

        var y = 0;
        void Note(string text)
        {
            _fields.Add(new Label { Text = text, X = 0, Y = y, Width = Dim.Fill() });
            y += 2;
        }

        TextField Field(string label, string key, bool secret = false)
        {
            _fields.Add(new Label { Text = label, X = 0, Y = y });
            var field = new TextField { X = 16, Y = y, Width = Dim.Fill(1), Text = _auth?.Get(key) ?? "", Secret = secret };
            field.TextChanged += (_, _) =>
            {
                if (!_loading && _auth is not null)
                {
                    _auth.Set(key, field.Text);
                    Changed?.Invoke(this, EventArgs.Empty);
                }
            };
            _fields.Add(field);
            y += 2;
            return field;
        }

        void Choice(string label, string key, string[] values, string[] labels)
        {
            _fields.Add(new Label { Text = label, X = 0, Y = y });
            var current = Array.IndexOf(values, _auth?.Get(key) ?? values[0]);
            var selector = new OptionSelector { X = 16, Y = y, Labels = labels, Orientation = Orientation.Horizontal, Value = Math.Max(0, current) };
            selector.ValueChanged += (_, e) =>
            {
                if (!_loading && _auth is not null && e.NewValue is int i)
                {
                    _auth.Set(key, values[i]);
                    Changed?.Invoke(this, EventArgs.Empty);
                }
            };
            _fields.Add(selector);
            y += 2;
        }

        switch (_auth?.Type)
        {
            case null:
                Note("This request uses the auth of its folder or collection.");
                Note(DescribeInherited?.Invoke() ?? "");
                break;
            case "noauth":
                Note("No authorization header is sent.");
                break;
            case "basic":
                Field("Username", "username");
                Field("Password", "password", secret: true);
                break;
            case "digest":
                Field("Username", "username");
                Field("Password", "password", secret: true);
                Note("The Digest challenge (realm, nonce, qop) is answered automatically.");
                break;
            case "ntlm":
                Field("Username", "username");
                Field("Password", "password", secret: true);
                Field("Domain", "domain");
                break;
            case "bearer":
                Field("Token", "token");
                break;
            case "apikey":
                Field("Key", "key");
                Field("Value", "value");
                Choice("Add to", "in", ["header", "query"], ["Header", "Query params"]);
                break;
            case "oauth2":
                Field("Access token", "accessToken");
                Field("Header prefix", "headerPrefix");
                Choice("Add token to", "addTokenTo", ["header", "queryParams"], ["Header", "Query params"]);
                Note("Paste a token; built-in token acquisition (incl. Entra ID with certificates) is on the roadmap.");
                break;
            default:
                Note($"'{AuthSpec.DisplayName(_auth.Type)}' is preserved in the collection but not applied when sending yet.");
                break;
        }

        _fields.Add(new Label { Text = "Fields may use {{variables}}.", X = 0, Y = y + 1 });
        _fields.SetNeedsLayout();
    }
}
