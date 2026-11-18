using System.Collections.ObjectModel;
using Terminal.Gui.Input;
using Terminal.Gui.ViewBase;
using Terminal.Gui.Views;
using TermRest.Core.Auth;
using TermRest.Core.Model;
using TermRest.Tui.Dialogs;

namespace TermRest.Tui.Views;

/// <summary>Auth tab: pick an auth type on the left, fill its fields on the right.</summary>
internal sealed class AuthEditor : View
{
    private const int LabelWidth = 16;

    private static readonly (string? Type, string Label)[] Choices =
    [
        (null, "Inherit auth from parent"),
        ("noauth", "No Auth"),
        ("basic", "Basic Auth"),
        ("bearer", "Bearer Token"),
        ("apikey", "API Key"),
        ("digest", "Digest Auth"),
        ("ntlm", "NTLM"),
        ("oauth2", "OAuth 2.0"),
    ];

    /// <summary>How the OAuth 2.0 grants (<see cref="OAuth2Settings.GrantTypes"/>, in that order) are shown.</summary>
    private static readonly string[] GrantLabels = ["Authorization code", "Authorization code (PKCE)", "Client credentials", "Password credentials", "Implicit (not fetched)"];

    private static readonly string[] AlgorithmLabels = ["For the key", .. ClientAssertion.Algorithms];

    private readonly ListView _types;
    private readonly ScrollingForm _fields;
    private readonly Dictionary<string, AuthSpec> _byType = new(StringComparer.OrdinalIgnoreCase);
    private readonly ObservableCollection<string> _labels = [];

    /// <summary>The controls of the settings shown, by key: the one in focus keeps it when the fields are built again.</summary>
    private readonly Dictionary<string, View> _controls = new(StringComparer.Ordinal);
    private AuthSpec? _auth;
    private string? _unsupportedType;
    private bool _loading;
    private bool _statusPending;
    private int _y;
    private Label? _tokenStatus;

    public AuthEditor()
    {
        CanFocus = true;
        Width = Dim.Fill();
        Height = Dim.Fill();
        _types = new ListView { X = 0, Y = 0, Width = 28, Height = Dim.Fill() };
        _types.SetSource(_labels);

        // ListView's Ctrl+N (down) would change the auth type instead of the window's New request; Ctrl+P (up) goes with it.
        _types.KeyBindings.Remove(Key.N.WithCtrl);
        _types.KeyBindings.Remove(Key.P.WithCtrl);
        _types.ValueChanged += (_, e) =>
        {
            if (!_loading && e.NewValue is int index)
            {
                SelectType(index);
            }
        };
        _fields = new ScrollingForm { X = Pos.Right(_types) + 1, Y = 0, Width = Dim.Fill(), Height = Dim.Fill() };
        Add(_types, _fields);
    }

    public event EventHandler? Changed;

    /// <summary>Get new token was pressed: an OAuth 2.0 token is fetched now (see <see cref="DescribeToken"/>).</summary>
    public event EventHandler? GetTokenRequested;

    /// <summary>Forget token was pressed: the OAuth 2.0 token fetched for the settings is forgotten.</summary>
    public event EventHandler? ForgetTokenRequested;

    /// <summary>Text describing what "inherit" resolves to (supplied by the main window).</summary>
    public Func<string>? DescribeInherited { get; set; }

    /// <summary>What the OAuth 2.0 token of the settings shown is: none yet, fetched, expired (supplied by the main window).</summary>
    public Func<string>? DescribeToken { get; set; }

    /// <summary>The names of the workspace's client certificates, which can sign OAuth 2.0 client assertions.</summary>
    public Func<IReadOnlyList<string>>? CertificateNames { get; set; }

    public AuthSpec? Auth => _auth;

    /// <summary>What the OAuth 2.0 token line says (for tests); empty for other auth.</summary>
    internal string TokenStatus => _tokenStatus?.Text ?? "";

    /// <summary>Describes again the auth the request inherits (it was added to a collection, or moved to another folder).</summary>
    public void RefreshInherited()
    {
        if (_auth is null)
        {
            BuildFields();
        }
    }

    /// <summary>Says again what the OAuth 2.0 token is (it was fetched or forgotten).</summary>
    public void RefreshTokenStatus()
    {
        if (_tokenStatus is not null)
        {
            _tokenStatus.Text = DescribeToken?.Invoke() ?? "";
        }
    }

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
            else if (type == "oauth2")
            {
                // Postman's default (authorization code) has the user sign in; the grant APIs are most often tested with is this.
                existing.Set("grant_type", "client_credentials");
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

        _controls.Clear();
        _tokenStatus = null;
        _y = 0;
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
                Field("Token", "token", secret: true);
                break;
            case "apikey":
                Field("Key", "key");
                Field("Value", "value", secret: true);
                Choice("Add to", "in", ["header", "query"], ["Header", "Query params"]);
                break;
            case "oauth2":
                BuildOAuth2();
                break;
            default:
                Note($"'{AuthSpec.DisplayName(_auth.Type)}' is preserved in the collection but not applied when sending yet.");
                break;
        }

        _fields.Add(new Label { Text = "Fields may use {{variables}}.", X = 0, Y = _y + 1 });
        _fields.SetContentHeight(_y + 2);
        _fields.Viewport = _fields.Viewport with { Y = 0 };
        _fields.SetNeedsLayout();
    }

    /// <summary>
    /// The OAuth 2.0 settings, a line each (they scroll when they don't fit), as Postman has them: the current token (the
    /// one fetched, with Get new token and Forget token; a token to send when none is fetched; where it goes), then what a
    /// new one is fetched with (the grant and its settings), and what empty fields mean.
    /// </summary>
    private void BuildOAuth2()
    {
        var grant = OAuth2Settings.ParseGrant(_auth!.Get("grant_type"));
        var code = grant is OAuth2Grant.AuthorizationCode or OAuth2Grant.AuthorizationCodeWithPkce;
        var fetched = grant is not (null or OAuth2Grant.Implicit);
        var notes = new List<string>();
        if (fetched)
        {
            // No shadows: the token's state is on the next line.
            var getToken = new Button { Text = "Get new token", X = 0, Y = _y, ShadowStyle = null };
            getToken.Accepting += (_, e) =>
            {
                e.Handled = true;
                GetTokenRequested?.Invoke(this, EventArgs.Empty);
            };
            var forget = new Button { Text = "Forget token", X = Pos.Right(getToken) + 1, Y = _y, ShadowStyle = null };
            forget.Accepting += (_, e) =>
            {
                e.Handled = true;
                ForgetTokenRequested?.Invoke(this, EventArgs.Empty);
            };
            _fields.Add(getToken, forget);
            _y++;
            _tokenStatus = new Label { HotKeySpecifier = Ui.NoHotKey, X = LabelWidth, Y = _y, Width = Dim.Fill(1), Height = 1, Text = DescribeToken?.Invoke() ?? "" };
            _fields.Add(new Label { Text = "Token", X = 0, Y = _y }, _tokenStatus);
            _y++;
        }

        Field("Access token", "accessToken", secret: true, step: 1);
        Field("Header prefix", "headerPrefix", step: 1, initial: "Bearer");
        Choice("Add token to", "addTokenTo", ["header", "queryParams"], ["Header", "Query params"], step: 1);
        notes.Add("The access token is sent when no token is fetched (its URLs are empty); a token fetched is kept in memory only.");
        _y++;

        DropDown("Grant type", "grant_type", OAuth2Settings.GrantTypes, GrantLabels, rebuild: true);
        if (grant is null)
        {
            notes.Add($"No token is fetched with grant type '{_auth.Get("grant_type")}' (it is kept).");
        }
        else if (grant == OAuth2Grant.Implicit)
        {
            notes.Add("No token is fetched with the implicit grant (retired by OAuth 2.0's security practice): use PKCE.");
        }

        if (fetched)
        {
            Field("Token URL", "accessTokenUrl", step: 1);
        }

        if (code || grant == OAuth2Grant.Implicit)
        {
            Field("Auth URL", "authUrl", step: 1);
            Field("Callback URL", "redirect_uri", step: 1);
        }

        Field("Client ID", "clientId", step: 1);
        var jwt = string.Equals(_auth.Get("client_authentication"), "private_key_jwt", StringComparison.OrdinalIgnoreCase);
        if (!jwt && grant != OAuth2Grant.Implicit)
        {
            Field("Client secret", "clientSecret", secret: true, step: 1);
        }

        Field("Scope", "scope", step: 1);
        if (grant == OAuth2Grant.Password)
        {
            Field("Username", "username", step: 1);
            Field("Password", "password", secret: true, step: 1);
        }

        if (grant == OAuth2Grant.AuthorizationCodeWithPkce)
        {
            Choice("Code challenge", "challengeAlgorithm", ["S256", "plain"], ["SHA-256", "Plain"], step: 1);
            Field("Code verifier", "code_verifier", step: 1);
        }

        if (code)
        {
            Field("State", "state", step: 1);
            notes.Add("Empty callback URL: TermRest takes the sign-in's response on a loopback port.");
            notes.Add(grant == OAuth2Grant.AuthorizationCodeWithPkce ? "Empty state or code verifier: a random one." : "Empty state: a random one.");
        }

        if (fetched)
        {
            Choice("Client auth", "client_authentication", OAuth2Settings.ClientAuthenticationValues, ["Basic header", "In body", "Signed JWT"], step: 1, rebuild: true);
            if (jwt)
            {
                DropDown("Certificate", "x-clientAssertionCertificate", null, [.. CertificateNames?.Invoke() ?? []], editable: true);
                DropDown("JWT algorithm", "x-clientAssertionAlgorithm", ["", .. ClientAssertion.Algorithms], AlgorithmLabels);
                Field("JWT key ID", "x-clientAssertionKeyId", step: 1);
                Field("JWT audience", "x-clientAssertionAudience", step: 1);
                Check("x-clientAssertionChain", "Send the certificate chain (x5c)");
                notes.Add("The certificate signs the client assertion (Tools › Client certificates…); empty JWT audience: the token URL.");
            }

            Field("Refresh URL", "refreshTokenUrl", step: 1);
            notes.Add("Empty refresh URL: the token URL.");
            if (((string[])["tokenRequestParams", "authRequestParams", "refreshRequestParams"])
                .Any(key => _auth.Params.FirstOrDefault(p => p.Key == key)?.Value is System.Text.Json.Nodes.JsonArray { Count: > 0 }))
            {
                notes.Add("The extra parameters of its token, auth or refresh requests (Postman's advanced options) go too.");
            }
        }

        _y++;
        foreach (var note in notes)
        {
            Note(note, 1);
        }
    }
    /// <summary>A line of text (it may name a folder or collection: shown as written).</summary>
    private void Note(string text, int step = 2)
    {
        _fields.Add(new Label { HotKeySpecifier = Ui.NoHotKey, Text = text, X = 0, Y = _y, Width = Dim.Fill(1) });
        _y += step;
    }

    /// <param name="initial">Shown for a setting not set (it is not written until edited).</param>
    private void Field(string label, string key, bool secret = false, int step = 2, string? initial = null)
    {
        _fields.Add(new Label { Text = label, X = 0, Y = _y });
        var field = new FormField { X = LabelWidth, Y = _y, Width = Dim.Fill(1), Text = _auth?.Get(key) ?? initial ?? "", Secret = secret };
        field.TextChanged += (_, _) =>
        {
            if (!_loading && _auth is not null)
            {
                _auth.Set(key, field.Text);
                OnEdited();
            }
        };
        _fields.Add(field);
        _controls[key] = field;
        _y += step;
    }

    private void Choice(string label, string key, string[] values, string[] labels, int step = 2, bool rebuild = false)
    {
        _fields.Add(new Label { Text = label, X = 0, Y = _y });
        var current = Array.FindIndex(values, v => string.Equals(v, _auth?.Get(key) ?? values[0], StringComparison.OrdinalIgnoreCase));
        var selector = new OptionSelector { X = LabelWidth, Y = _y, Labels = labels, Orientation = Orientation.Horizontal, Value = Math.Max(0, current) };
        selector.ValueChanged += (_, e) =>
        {
            if (!_loading && _auth is not null && e.NewValue is int i)
            {
                _auth.Set(key, values[i]);
                OnEdited(rebuild ? key : null);
            }
        };
        _fields.Add(selector);
        _controls[key] = selector;
        _y += step;
    }

    /// <summary>A dropdown: of <paramref name="values"/> shown as <paramref name="labels"/>, or of <paramref name="labels"/> themselves (null values).</summary>
    /// <param name="editable">Other text may be typed (a certificate not registered yet).</param>
    private void DropDown(string label, string key, string[]? values, string[] labels, bool editable = false, bool rebuild = false)
    {
        _fields.Add(new Label { Text = label, X = 0, Y = _y });
        var setting = _auth?.Get(key) ?? "";
        var index = values is null ? -1 : Array.FindIndex(values, v => string.Equals(v, setting.Trim(), StringComparison.OrdinalIgnoreCase));
        var dropDown = new FormDropDown
        {
            X = LabelWidth,
            Y = _y,
            Width = Dim.Fill(1),
            ReadOnly = !editable,
            Source = new ListWrapper<string>(new ObservableCollection<string>(labels)),
            Text = values is null ? setting : index >= 0 ? labels[index] : setting,
        };
        dropDown.TextChanged += (_, _) =>
        {
            if (_loading || _auth is null)
            {
                return;
            }

            var shown = Array.IndexOf(labels, dropDown.Text);
            var value = values is null || shown < 0 ? dropDown.Text : values[shown];
            if (value != (_auth.Get(key) ?? ""))
            {
                _auth.Set(key, value);
                OnEdited(rebuild ? key : null);
            }
        };
        _fields.Add(dropDown);
        _controls[key] = dropDown;
        _y++;
    }

    private void Check(string key, string text)
    {
        var box = new CheckBox
        {
            Text = text,
            X = LabelWidth,
            Y = _y,
            Value = string.Equals(_auth?.Get(key), "true", StringComparison.OrdinalIgnoreCase) ? CheckState.Checked : CheckState.UnChecked,
        };
        box.ValueChanged += (_, _) =>
        {
            if (!_loading && _auth is not null)
            {
                _auth.Set(key, box.Value == CheckState.Checked ? "true" : "false");
                OnEdited();
            }
        };
        _fields.Add(box);
        _controls[key] = box;
        _y++;
    }

    /// <param name="rebuilt">
    /// The setting edited, when it changes which fields there are (the grant type …): they are built again, after the
    /// control's own event, and it keeps the focus.
    /// </param>
    private void OnEdited(string? rebuilt = null)
    {
        Changed?.Invoke(this, EventArgs.Empty);
        if (rebuilt is not null)
        {
            Later(() =>
            {
                BuildFields();
                if (_controls.GetValueOrDefault(rebuilt) is { } control)
                {
                    control.SetFocus();
                }
            });
        }
        else if (_tokenStatus is not null && !_statusPending)
        {
            // The token is the one of the settings as they are now: said again once typing pauses.
            _statusPending = true;
            Later(() =>
            {
                _statusPending = false;
                RefreshTokenStatus();
            }, TimeSpan.FromMilliseconds(400));
        }
    }

    private void Later(Action action, TimeSpan? delay = null)
    {
        if (App is not { } app)
        {
            action();
            return;
        }

        app.AddTimeout(delay ?? TimeSpan.Zero, () =>
        {
            action();
            return false;
        });
    }

    /// <summary>
    /// A form that scrolls when it is taller than its space: with the wheel (over its fields and labels too), its scroll
    /// bar, and to the field in focus.
    /// </summary>
    private sealed class ScrollingForm : View
    {
        public ScrollingForm()
        {
            CanFocus = true;
            ViewportSettings |= ViewportSettingsFlags.HasVerticalScrollBar;
            AddCommand(Command.ScrollUp, () => ScrollVertical(-1));
            AddCommand(Command.ScrollDown, () => ScrollVertical(1));
            MouseBindings.ReplaceCommands(MouseFlags.WheeledUp, Command.ScrollUp);
            MouseBindings.ReplaceCommands(MouseFlags.WheeledDown, Command.ScrollDown);
        }

        protected override void OnFocusedChanged(View? previousFocused, View? focused)
        {
            base.OnFocusedChanged(previousFocused, focused);
            if (focused is null || focused.SuperView != this)
            {
                return;
            }

            var viewport = Viewport;
            var frame = focused.Frame;
            if (frame.Y < viewport.Y)
            {
                Viewport = viewport with { Y = frame.Y };
            }
            else if (frame.Bottom > viewport.Y + viewport.Height)
            {
                Viewport = viewport with { Y = frame.Bottom - viewport.Height };
            }
        }
    }

    /// <summary>A text field that leaves the wheel to the form: Terminal.Gui's takes a turn of it to focus itself.</summary>
    private sealed class FormField : TextField
    {
        protected override bool OnMouseEvent(Mouse mouse) => !mouse.IsWheel && base.OnMouseEvent(mouse);
    }

    /// <summary>A dropdown that leaves the wheel to the form: it would take a turn to focus itself, or change its choice.</summary>
    private sealed class FormDropDown : DropDownList
    {
        protected override bool OnMouseEvent(Mouse mouse) => !mouse.IsWheel && base.OnMouseEvent(mouse);
    }
}