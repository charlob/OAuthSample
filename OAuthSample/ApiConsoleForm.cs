using System;
using System.Collections.Generic;
using System.Drawing;
using System.Windows.Forms;

namespace OAuthSample
{
    /// <summary>A separate, reusable output window for API responses (cleared per call).
    /// <see cref="WriteJson"/> renders JSON indented and syntax-colored, VS Code-dark style.</summary>
    public sealed class ApiConsoleForm : Form
    {
        private static readonly Color PlainColor = Color.FromArgb(210, 220, 210);
        private static readonly Color PunctColor = Color.FromArgb(212, 212, 212);
        private static readonly Color KeyColor = Color.FromArgb(156, 220, 254);
        private static readonly Color StringColor = Color.FromArgb(206, 145, 120);
        private static readonly Color NumberColor = Color.FromArgb(181, 206, 168);
        private static readonly Color LiteralColor = Color.FromArgb(86, 156, 214);

        private readonly RichTextBox _out;

        public ApiConsoleForm()
        {
            Text = "API Console";
            ClientSize = new Size(760, 560);
            StartPosition = FormStartPosition.CenterParent;
            Font = new Font("Segoe UI", 9f);

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
            Controls.Add(_out);
        }

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

        public void ShowConsole(IWin32Window owner)
        {
            if (!Visible)
                Show(owner);
            if (WindowState == FormWindowState.Minimized)
                WindowState = FormWindowState.Normal;
            BringToFront();
            Activate();
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
