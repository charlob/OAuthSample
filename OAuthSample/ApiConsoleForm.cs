using System;
using System.Collections.Generic;
using System.Drawing;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using System.Windows.Forms;

namespace OAuthSample
{
    /// <summary>
    /// The hub for calling the eNVD GraphQL API. It owns the endpoint and the query buttons,
    /// and reads the access token from the shared <see cref="TokenStore"/> on each call (so it
    /// always uses the latest token, even after a silent refresh in either login form).
    ///
    /// Dependency gate: <c>Get User Details</c> needs no envdAccountId — it *returns* it
    /// (<c>userDetails.defaultAccount</c>). Running it captures the envdAccountId, which enables
    /// every query that requires it. <see cref="WriteJson"/> renders responses indented and
    /// syntax-colored, VS Code-dark style.
    /// </summary>
    public sealed class ApiConsoleForm : Form
    {
        private static readonly Color PlainColor = Color.FromArgb(210, 220, 210);
        private static readonly Color PunctColor = Color.FromArgb(212, 212, 212);
        private static readonly Color KeyColor = Color.FromArgb(156, 220, 254);
        private static readonly Color StringColor = Color.FromArgb(206, 145, 120);
        private static readonly Color NumberColor = Color.FromArgb(181, 206, 168);
        private static readonly Color LiteralColor = Color.FromArgb(86, 156, 214);
        private static readonly Color DimColor = Color.FromArgb(120, 130, 120);

        private readonly TokenStore _store = new TokenStore();

        private readonly TextBox _txtUrl;
        private readonly TextBox _txtEnvd;
        private readonly TextBox _txtConsignment;
        private readonly TextBox _txtTemplate;
        private readonly TextBox _txtRole;
        private readonly Label _lblStatus;
        private readonly RichTextBox _out;

        private readonly List<KeyValuePair<Button, ApiQuery>> _queryButtons = new List<KeyValuePair<Button, ApiQuery>>();
        private bool _busy;

        public ApiConsoleForm()
        {
            Text = "API Console";
            ClientSize = new Size(820, 640);
            MinimumSize = new Size(640, 480);
            StartPosition = FormStartPosition.CenterParent;
            Font = new Font("Segoe UI", 9f);

            var root = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, Padding = new Padding(10) };
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize)); // fields
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize)); // status
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize)); // buttons
            root.RowStyles.Add(new RowStyle(SizeType.Percent, 100)); // output
            Controls.Add(root);

            // --- fields ---
            var fields = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, AutoSize = true, Margin = new Padding(0) };
            fields.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 120));
            fields.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            _txtUrl = AddField(fields, "GraphQL API", "https://api.uat.integritysystems.com.au/v2/graphql");
            _txtEnvd = AddField(fields, "envd Account Id", "");
            _txtConsignment = AddField(fields, "Consignment #", "");
            _txtTemplate = AddField(fields, "Template #", "");
            _txtRole = AddField(fields, "Stakeholder role", "AUTHVIEWER");
            root.Controls.Add(fields, 0, 0);

            // Re-evaluate which buttons are enabled whenever a gating field changes.
            _txtEnvd.TextChanged += (s, e) => UpdateButtons();
            _txtConsignment.TextChanged += (s, e) => UpdateButtons();
            _txtTemplate.TextChanged += (s, e) => UpdateButtons();

            _lblStatus = new Label { AutoSize = true, Margin = new Padding(3, 4, 3, 8), ForeColor = SystemColors.GrayText };
            root.Controls.Add(_lblStatus, 0, 1);

            // --- query buttons ---
            var buttons = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, WrapContents = true, Margin = new Padding(0, 0, 0, 8) };
            foreach (var q in BuildQueries())
            {
                var btn = new Button { Text = q.Name, AutoSize = true, Padding = new Padding(10, 3, 10, 3), Margin = new Padding(0, 0, 6, 6) };
                var query = q; // capture
                btn.Click += async (s, e) => await RunQueryAsync(query);
                buttons.Controls.Add(btn);
                _queryButtons.Add(new KeyValuePair<Button, ApiQuery>(btn, q));
            }
            root.Controls.Add(buttons, 0, 2);

            _out = new RichTextBox
            {
                ReadOnly = true,
                WordWrap = false,
                Dock = DockStyle.Fill,
                BackColor = Color.FromArgb(18, 18, 18),
                ForeColor = PlainColor,
                Font = new Font("Consolas", 9.5f),
                BorderStyle = BorderStyle.None,
            };
            root.Controls.Add(_out, 0, 3);

            Append("Ready. Run \"Get User Details\" first — it captures the envd Account Id" + Environment.NewLine, DimColor);
            Append("that the other queries require." + Environment.NewLine, DimColor);
            UpdateButtons();
        }

        private static TextBox AddField(TableLayoutPanel layout, string label, string value)
        {
            var lbl = new Label { Text = label, AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(3, 7, 3, 3) };
            var box = new TextBox { Text = value, Dock = DockStyle.Fill, Margin = new Padding(3, 4, 3, 4) };
            int row = layout.RowCount;
            layout.Controls.Add(lbl, 0, row);
            layout.Controls.Add(box, 1, row);
            layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            layout.RowCount++;
            return box;
        }

        // --- query catalog (UI side: variables + enablement gate) ------------------------------

        /// <summary>One callable query: its GraphQL text, how to build its variables from the
        /// fields, what it requires to be enabled, and an optional response capture.</summary>
        private sealed class ApiQuery
        {
            public string Name;
            public string Query;
            public bool NeedsEnvd;
            public bool NeedsConsignment;
            public bool NeedsTemplate;
            public Func<ApiConsoleForm, Dictionary<string, object>> Variables;
            public Action<ApiConsoleForm, string> Capture;
        }

        private List<ApiQuery> BuildQueries()
        {
            return new List<ApiQuery>
            {
                new ApiQuery
                {
                    Name = "Get User Details",
                    Query = GraphQlClient.GetUserDetailsQuery,
                    Capture = (f, body) => f.CaptureEnvd(body),
                },
                new ApiQuery
                {
                    Name = "Get Consignments",
                    Query = GraphQlClient.GetConsignmentsQuery,
                    NeedsEnvd = true,
                    Variables = f => new Dictionary<string, object> { ["envdAccountId"] = f.Envd, ["first"] = 20 },
                    Capture = (f, body) => f.CaptureConsignment(body),
                },
                new ApiQuery
                {
                    Name = "Get Consignment By Id",
                    Query = GraphQlClient.GetConsignmentByIdQuery,
                    NeedsEnvd = true,
                    NeedsConsignment = true,
                    Variables = f => new Dictionary<string, object> { ["id"] = f.Consignment, ["envdAccountId"] = f.Envd },
                    Capture = (f, body) => f.CaptureConsignment(body),
                },
                new ApiQuery
                {
                    Name = "Consignments (paged)",
                    Query = GraphQlClient.GetConsignmentsPagedQuery,
                    NeedsEnvd = true,
                    Variables = f => new Dictionary<string, object> { ["envdAccountId"] = f.Envd, ["first"] = 20, ["after"] = null },
                    Capture = (f, body) => f.CaptureConsignment(body),
                },
                new ApiQuery
                {
                    Name = "Get Stakeholders",
                    Query = GraphQlClient.GetStakeholdersQuery,
                    NeedsEnvd = true,
                    NeedsConsignment = true,
                    Variables = f => new Dictionary<string, object>
                    {
                        ["envdAccountId"] = f.Envd,
                        ["consignmentNumber"] = f.Consignment,
                        ["role"] = f._txtRole.Text.Trim(),
                    },
                },
                new ApiQuery
                {
                    Name = "Get Comments",
                    Query = GraphQlClient.GetCommentsQuery,
                    NeedsEnvd = true,
                    NeedsConsignment = true,
                    Variables = f => new Dictionary<string, object> { ["envdAccountId"] = f.Envd, ["consignmentNumber"] = f.Consignment },
                },
                new ApiQuery
                {
                    Name = "Get Templates",
                    Query = GraphQlClient.GetTemplatesQuery,
                    NeedsEnvd = true,
                    Variables = f => new Dictionary<string, object> { ["envdAccountId"] = f.Envd, ["first"] = 10 },
                },
                new ApiQuery
                {
                    Name = "Get Template By Id",
                    Query = GraphQlClient.GetTemplateByIdQuery,
                    NeedsEnvd = true,
                    NeedsTemplate = true,
                    Variables = f => new Dictionary<string, object> { ["id"] = f.Template, ["envdAccountId"] = f.Envd },
                },
                new ApiQuery
                {
                    Name = "Get All Questions",
                    Query = GraphQlClient.GetAllQuestionsQuery,
                },
            };
        }

        private string Envd => _txtEnvd.Text.Trim();
        private string Consignment => _txtConsignment.Text.Trim();
        private string Template => _txtTemplate.Text.Trim();

        private void CaptureEnvd(string body)
        {
            string envd = GraphQlClient.ExtractEnvdAccountId(body);
            if (!string.IsNullOrEmpty(envd))
                _txtEnvd.Text = envd;
        }

        private void CaptureConsignment(string body)
        {
            string number = GraphQlClient.ExtractConsignmentNumber(body);
            if (!string.IsNullOrEmpty(number))
                _txtConsignment.Text = number;
        }

        // --- run --------------------------------------------------------------------------------

        private async Task RunQueryAsync(ApiQuery q)
        {
            if (_busy)
                return;

            var rec = _store.Load();
            if (rec == null || string.IsNullOrEmpty(rec.AccessToken))
            {
                ClearConsole();
                Append("No access token — Connect / Login (or Refresh) in the login window first." + Environment.NewLine, LiteralColor);
                return;
            }

            string endpoint = _txtUrl.Text.Trim();
            if (string.IsNullOrEmpty(endpoint))
            {
                ClearConsole();
                Append("Enter the GraphQL API URL first." + Environment.NewLine, LiteralColor);
                return;
            }

            Dictionary<string, object> variables = q.Variables?.Invoke(this);

            SetBusy(true);
            try
            {
                ClearConsole();
                Append("POST " + endpoint + Environment.NewLine, DimColor);
                Append("Authorization: Bearer " + Truncate(rec.AccessToken) + Environment.NewLine, DimColor);
                Append(q.Name + Environment.NewLine, DimColor);
                if (variables != null && variables.Count > 0)
                    Append("variables: " + new JavaScriptSerializer().Serialize(variables) + Environment.NewLine, DimColor);
                Append(Environment.NewLine, PlainColor);

                var result = await GraphQlClient.PostAsync(endpoint, rec.AccessToken, q.Query, variables);

                Append("HTTP " + result.Item1 + Environment.NewLine + Environment.NewLine, result.Item1 == 200 ? NumberColor : LiteralColor);
                WriteJson(result.Item2);

                q.Capture?.Invoke(this, result.Item2);
            }
            catch (Exception ex)
            {
                Append("ERROR: " + ex.Message + Environment.NewLine, LiteralColor);
            }
            finally
            {
                SetBusy(false);
            }
        }

        private void SetBusy(bool busy)
        {
            _busy = busy;
            UpdateButtons();
        }

        private void UpdateButtons()
        {
            var rec = _store.Load();
            bool hasToken = rec != null && !string.IsNullOrEmpty(rec.AccessToken);
            bool hasEnvd = Envd.Length > 0;
            bool hasConsignment = Consignment.Length > 0;
            bool hasTemplate = Template.Length > 0;

            foreach (var pair in _queryButtons)
            {
                ApiQuery q = pair.Value;
                bool ok = hasToken
                    && (!q.NeedsEnvd || hasEnvd)
                    && (!q.NeedsConsignment || hasConsignment)
                    && (!q.NeedsTemplate || hasTemplate);
                pair.Key.Enabled = !_busy && ok;
            }

            _lblStatus.Text = !hasToken
                ? "No access token — Connect / Login in the login window, then reopen or click a button."
                : hasEnvd
                    ? "Token loaded · envdAccountId set — all queries available."
                    : "Token loaded · run Get User Details to capture the envdAccountId.";
        }

        // --- output window ----------------------------------------------------------------------

        public void ClearConsole() => _out.Clear();

        /// <summary>Writes a plain line in the default console color.</summary>
        public void Write(string text)
        {
            Append((text ?? "") + Environment.NewLine, PlainColor);
        }

        /// <summary>Writes JSON indented and syntax-colored. Falls back to a plain
        /// <see cref="Write"/> if the text doesn't look like a JSON object/array
        /// (e.g. an error page or empty body).</summary>
        public void WriteJson(string json)
        {
            if (string.IsNullOrWhiteSpace(json))
            {
                Write(json ?? "");
                return;
            }
            string trimmed = json.TrimStart();
            if (trimmed.Length == 0 || (trimmed[0] != '{' && trimmed[0] != '['))
            {
                Write(json);
                return;
            }

            var tokens = JsonTokenizer.Tokenize(json);
            int indent = 0;
            for (int i = 0; i < tokens.Count; i++)
            {
                var tok = tokens[i];
                switch (tok.Kind)
                {
                    case JsonTokenKind.OpenBrace:
                    case JsonTokenKind.OpenBracket:
                        Append(tok.Text, PunctColor);
                        bool isEmpty = i + 1 < tokens.Count && IsMatchingClose(tok.Kind, tokens[i + 1].Kind);
                        if (isEmpty)
                        {
                            Append(tokens[i + 1].Text, PunctColor);
                            i++;
                        }
                        else
                        {
                            indent++;
                            AppendNewLine(indent);
                        }
                        break;

                    case JsonTokenKind.CloseBrace:
                    case JsonTokenKind.CloseBracket:
                        indent--;
                        AppendNewLine(indent);
                        Append(tok.Text, PunctColor);
                        break;

                    case JsonTokenKind.Colon:
                        Append(": ", PunctColor);
                        break;

                    case JsonTokenKind.Comma:
                        Append(",", PunctColor);
                        AppendNewLine(indent);
                        break;

                    case JsonTokenKind.Key:
                        Append(tok.Text, KeyColor);
                        break;
                    case JsonTokenKind.String:
                        Append(tok.Text, StringColor);
                        break;
                    case JsonTokenKind.Number:
                        Append(tok.Text, NumberColor);
                        break;
                    case JsonTokenKind.Literal:
                        Append(tok.Text, LiteralColor);
                        break;
                }
            }
            Append(Environment.NewLine, PlainColor);
        }

        private static bool IsMatchingClose(JsonTokenKind open, JsonTokenKind close)
        {
            return (open == JsonTokenKind.OpenBrace && close == JsonTokenKind.CloseBrace) ||
                   (open == JsonTokenKind.OpenBracket && close == JsonTokenKind.CloseBracket);
        }

        private void AppendNewLine(int indent)
        {
            Append(Environment.NewLine + new string(' ', Math.Max(0, indent) * 2), PunctColor);
        }

        private void Append(string text, Color color)
        {
            _out.SelectionStart = _out.TextLength;
            _out.SelectionLength = 0;
            _out.SelectionColor = color;
            _out.AppendText(text);
        }

        private static string Truncate(string value)
        {
            if (string.IsNullOrEmpty(value)) return "(none)";
            return value.Length > 24 ? value.Substring(0, 24) + "… (" + value.Length + " chars)" : value;
        }

        public void ShowConsole(IWin32Window owner)
        {
            if (!Visible)
                Show(owner);
            if (WindowState == FormWindowState.Minimized)
                WindowState = FormWindowState.Normal;
            UpdateButtons(); // reflect the current saved token (e.g. just logged in)
            BringToFront();
            Activate();
        }

        // Re-check the saved token whenever the console regains focus — the user may have
        // logged in or refreshed in the other window while this one was already open.
        protected override void OnActivated(EventArgs e)
        {
            base.OnActivated(e);
            if (!_busy)
                UpdateButtons();
        }
    }

    internal enum JsonTokenKind { OpenBrace, CloseBrace, OpenBracket, CloseBracket, Colon, Comma, Key, String, Number, Literal }

    internal struct JsonToken
    {
        public JsonTokenKind Kind;
        public string Text;
    }

    /// <summary>A small hand-rolled JSON tokenizer — just enough to drive indentation and
    /// syntax coloring in <see cref="ApiConsoleForm"/>. Not a validating parser: it assumes
    /// well-formed JSON (as returned by the GraphQL endpoint) and does no error recovery.</summary>
    internal static class JsonTokenizer
    {
        public static List<JsonToken> Tokenize(string json)
        {
            var tokens = new List<JsonToken>();
            int i = 0, n = json.Length;

            while (i < n)
            {
                char c = json[i];
                if (char.IsWhiteSpace(c)) { i++; continue; }

                switch (c)
                {
                    case '{': tokens.Add(new JsonToken { Kind = JsonTokenKind.OpenBrace, Text = "{" }); i++; continue;
                    case '}': tokens.Add(new JsonToken { Kind = JsonTokenKind.CloseBrace, Text = "}" }); i++; continue;
                    case '[': tokens.Add(new JsonToken { Kind = JsonTokenKind.OpenBracket, Text = "[" }); i++; continue;
                    case ']': tokens.Add(new JsonToken { Kind = JsonTokenKind.CloseBracket, Text = "]" }); i++; continue;
                    case ':': tokens.Add(new JsonToken { Kind = JsonTokenKind.Colon, Text = ":" }); i++; continue;
                    case ',': tokens.Add(new JsonToken { Kind = JsonTokenKind.Comma, Text = "," }); i++; continue;
                }

                if (c == '"')
                {
                    int start = i;
                    i++;
                    while (i < n)
                    {
                        if (json[i] == '\\' && i + 1 < n) { i += 2; continue; }
                        if (json[i] == '"') { i++; break; }
                        i++;
                    }
                    // Kind is refined to Key below once we can see the following token.
                    tokens.Add(new JsonToken { Kind = JsonTokenKind.String, Text = json.Substring(start, i - start) });
                    continue;
                }

                if (c == '-' || char.IsDigit(c))
                {
                    int start = i;
                    i++;
                    while (i < n && (char.IsDigit(json[i]) || json[i] == '.' || json[i] == 'e' || json[i] == 'E' ||
                                      json[i] == '+' || json[i] == '-'))
                        i++;
                    tokens.Add(new JsonToken { Kind = JsonTokenKind.Number, Text = json.Substring(start, i - start) });
                    continue;
                }

                if (char.IsLetter(c))
                {
                    int start = i;
                    while (i < n && char.IsLetter(json[i]))
                        i++;
                    tokens.Add(new JsonToken { Kind = JsonTokenKind.Literal, Text = json.Substring(start, i - start) });
                    continue;
                }

                i++; // stray character — skip
            }

            // A string immediately followed by ':' is an object key, not a value.
            for (int t = 0; t < tokens.Count - 1; t++)
            {
                if (tokens[t].Kind == JsonTokenKind.String && tokens[t + 1].Kind == JsonTokenKind.Colon)
                {
                    var tok = tokens[t];
                    tok.Kind = JsonTokenKind.Key;
                    tokens[t] = tok;
                }
            }

            return tokens;
        }
    }
}
