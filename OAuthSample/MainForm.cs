using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using System.Windows.Forms;

namespace OAuthSample
{
    /// <summary>
    /// Minimal WinForms harness that walks the OAuth 2.0 Authorization Code + PKCE
    /// flow end to end and logs every step, so you can watch exactly where a
    /// callback is (or isn't) being captured.
    /// </summary>
    public sealed class MainForm : Form
    {
        // Shared HttpClient for the token exchange.
        private static readonly HttpClient Http = new HttpClient();

        private readonly TextBox _txtAuthority;
        private readonly TextBox _txtClientId;
        private readonly TextBox _txtRedirect;
        private readonly TextBox _txtScope;
        private readonly TextBox _txtAudience;
        private readonly Button _btnConnect;
        private readonly TextBox _txtLog;

        public MainForm()
        {
            Text = "OAuth 2.0 Sample (Authorization Code + PKCE)";
            Width = 900;
            Height = 640;
            MinimumSize = new Size(700, 480);
            StartPosition = FormStartPosition.CenterScreen;
            Font = new Font("Segoe UI", 9f);

            var layout = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                ColumnCount = 2,
                Padding = new Padding(12),
                AutoSize = false,
            };
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 130));
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            Controls.Add(layout);

            _txtAuthority = AddRow(layout, "Authority", "https://sso-s.mla.com.au");
            _txtClientId = AddRow(layout, "Client ID", "");
            _txtRedirect = AddRow(layout, "Callback URI", "http://localhost:5000/callback");
            _txtScope = AddRow(layout, "Scope", "openid profile email");
            _txtAudience = AddRow(layout, "Audience (opt.)", "");

            _btnConnect = new Button
            {
                Text = "Connect",
                AutoSize = true,
                Padding = new Padding(16, 4, 16, 4),
                Anchor = AnchorStyles.Left,
            };
            _btnConnect.Click += OnConnectClick;
            layout.Controls.Add(new Label { Text = "", AutoSize = true }, 0, layout.RowCount);
            layout.Controls.Add(_btnConnect, 1, layout.RowCount);
            layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            layout.RowCount++;

            _txtLog = new TextBox
            {
                Multiline = true,
                ReadOnly = true,
                ScrollBars = ScrollBars.Both,
                WordWrap = false,
                Dock = DockStyle.Fill,
                BackColor = Color.FromArgb(24, 24, 24),
                ForeColor = Color.FromArgb(220, 220, 220),
                Font = new Font("Consolas", 9f),
            };
            layout.Controls.Add(_txtLog, 0, layout.RowCount);
            layout.SetColumnSpan(_txtLog, 2);
            layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            layout.RowCount++;

            Log("Ready. Fill in Client ID (and adjust Callback URI to match what's");
            Log("registered at the IdP), then click Connect.");
        }

        private static TextBox AddRow(TableLayoutPanel layout, string label, string value)
        {
            var lbl = new Label
            {
                Text = label,
                AutoSize = true,
                Anchor = AnchorStyles.Left,
                Margin = new Padding(3, 8, 3, 3),
            };
            var box = new TextBox { Text = value, Dock = DockStyle.Fill };
            int row = layout.RowCount;
            layout.Controls.Add(lbl, 0, row);
            layout.Controls.Add(box, 1, row);
            layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            layout.RowCount++;
            return box;
        }

        private async void OnConnectClick(object sender, EventArgs e)
        {
            _btnConnect.Enabled = false;
            try
            {
                await RunFlowAsync();
            }
            catch (Exception ex)
            {
                Log("ERROR: " + ex.Message);
            }
            finally
            {
                _btnConnect.Enabled = true;
            }
        }

        private async Task RunFlowAsync()
        {
            string authority = _txtAuthority.Text.Trim().TrimEnd('/');
            string clientId = _txtClientId.Text.Trim();
            string redirectUri = _txtRedirect.Text.Trim();
            string scope = _txtScope.Text.Trim();
            string audience = _txtAudience.Text.Trim();

            if (string.IsNullOrEmpty(clientId))
            {
                Log("Please enter a Client ID.");
                return;
            }
            if (!Uri.TryCreate(redirectUri, UriKind.Absolute, out var redirect))
            {
                Log("Callback URI is not a valid absolute URL.");
                return;
            }

            // We listen on the loopback ROOT (e.g. http://localhost:5000/) rather than
            // the exact /callback path. HttpListener path-prefix matching is fussy about
            // trailing slashes, and a missed match is the classic "listener never fires"
            // bug. Listening at the root and inspecting the path ourselves is bulletproof.
            string rootPrefix = $"{redirect.Scheme}://{redirect.Host}:{redirect.Port}/";

            string state = RandomUrlToken(16);
            string codeVerifier = RandomUrlToken(32);
            string codeChallenge = Base64Url(Sha256(codeVerifier));

            var authorizeUrl = new StringBuilder();
            authorizeUrl.Append(authority).Append("/authorize?");
            authorizeUrl.Append("response_type=code");
            authorizeUrl.Append("&client_id=").Append(Uri.EscapeDataString(clientId));
            authorizeUrl.Append("&redirect_uri=").Append(Uri.EscapeDataString(redirectUri));
            authorizeUrl.Append("&scope=").Append(Uri.EscapeDataString(scope));
            authorizeUrl.Append("&state=").Append(Uri.EscapeDataString(state));
            authorizeUrl.Append("&code_challenge=").Append(codeChallenge);
            authorizeUrl.Append("&code_challenge_method=S256");
            if (!string.IsNullOrEmpty(audience))
                authorizeUrl.Append("&audience=").Append(Uri.EscapeDataString(audience));

            using (var listener = new HttpListener())
            {
                listener.Prefixes.Add(rootPrefix);
                try
                {
                    listener.Start();
                }
                catch (HttpListenerException ex)
                {
                    Log("Could not start listener on " + rootPrefix);
                    Log("  " + ex.Message);
                    Log("  (Non-localhost prefixes need a netsh urlacl reservation or admin rights.)");
                    return;
                }

                Log("");
                Log("Listening on " + rootPrefix);
                Log("Opening browser for authorization...");
                Log("  authorize: " + authority + "/authorize");
                Log("  state:     " + state);
                Log("  challenge: " + codeChallenge + " (S256)");

                Process.Start(new ProcessStartInfo(authorizeUrl.ToString()) { UseShellExecute = true });

                string code = await WaitForCodeAsync(listener, state);
                if (code == null)
                    return; // reason already logged

                Log("");
                Log("Authorization code captured. Exchanging for tokens...");
                await ExchangeCodeAsync(authority, clientId, redirectUri, code, codeVerifier);
            }
        }

        /// <summary>
        /// Pumps the listener until the browser hits us with a request that carries
        /// a <c>code</c> (or an <c>error</c>). Stray requests (favicon, etc.) are logged
        /// and answered with 404 so we don't accidentally consume the real callback.
        /// </summary>
        private async Task<string> WaitForCodeAsync(HttpListener listener, string expectedState)
        {
            while (true)
            {
                var ctx = await listener.GetContextAsync();
                var req = ctx.Request;
                var query = req.QueryString;

                Log("  <- " + req.HttpMethod + " " + req.Url.PathAndQuery);

                string code = query["code"];
                string error = query["error"];
                string returnedState = query["state"];

                if (error != null)
                {
                    Log("  Authorization error: " + error + " - " + query["error_description"]);
                    Respond(ctx, "Authorization failed: " + WebUtility.HtmlEncode(error));
                    return null;
                }

                if (code == null)
                {
                    // Not the callback (favicon, probe, etc.). Brush it off and keep waiting.
                    Respond(ctx, "Waiting for OAuth callback...", status: 404);
                    continue;
                }

                if (returnedState != expectedState)
                {
                    Log("  WARNING: state mismatch (possible CSRF). Expected " + expectedState +
                        " but got " + returnedState);
                    Respond(ctx, "State mismatch - aborting.");
                    return null;
                }

                Respond(ctx, "<h2>Authentication complete</h2><p>You can close this window and return to the app.</p>");
                return code;
            }
        }

        private async Task ExchangeCodeAsync(
            string authority, string clientId, string redirectUri, string code, string codeVerifier)
        {
            var form = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["grant_type"] = "authorization_code",
                ["client_id"] = clientId,
                ["code"] = code,
                ["redirect_uri"] = redirectUri,
                ["code_verifier"] = codeVerifier,
            });

            string tokenUrl = authority + "/oauth/token";
            Log("  POST " + tokenUrl);

            HttpResponseMessage resp = await Http.PostAsync(tokenUrl, form);
            string body = await resp.Content.ReadAsStringAsync();

            Log("  HTTP " + (int)resp.StatusCode + " " + resp.StatusCode);

            if (!resp.IsSuccessStatusCode)
            {
                Log("  Token endpoint returned an error:");
                Log("  " + body);
                return;
            }

            Log("");
            Log("=== Token response ===");
            try
            {
                var map = new JavaScriptSerializer().Deserialize<Dictionary<string, object>>(body);
                foreach (var kvp in map)
                {
                    string val = kvp.Value?.ToString() ?? "";
                    // Tokens are long; show enough to confirm without flooding the log.
                    if ((kvp.Key.EndsWith("_token") || kvp.Key == "id_token") && val.Length > 24)
                        val = val.Substring(0, 24) + "... (" + val.Length + " chars)";
                    Log("  " + kvp.Key + ": " + val);
                }
            }
            catch
            {
                Log(body);
            }
            Log("======================");
        }

        private static void Respond(HttpListenerContext ctx, string html, int status = 200)
        {
            try
            {
                byte[] buffer = Encoding.UTF8.GetBytes(
                    "<html><body style='font-family:Segoe UI,Arial,sans-serif'>" + html + "</body></html>");
                ctx.Response.StatusCode = status;
                ctx.Response.ContentType = "text/html";
                ctx.Response.ContentLength64 = buffer.Length;
                ctx.Response.OutputStream.Write(buffer, 0, buffer.Length);
                ctx.Response.OutputStream.Close();
            }
            catch
            {
                // Browser may have already gone; nothing useful to do.
            }
        }

        // --- PKCE / crypto helpers -------------------------------------------------

        private static byte[] Sha256(string input)
        {
            using (var sha = SHA256.Create())
                return sha.ComputeHash(Encoding.ASCII.GetBytes(input));
        }

        private static string RandomUrlToken(int bytes)
        {
            var buffer = new byte[bytes];
            using (var rng = RandomNumberGenerator.Create())
                rng.GetBytes(buffer);
            return Base64Url(buffer);
        }

        private static string Base64Url(byte[] data)
        {
            return Convert.ToBase64String(data)
                .TrimEnd('=')
                .Replace('+', '-')
                .Replace('/', '_');
        }

        // --- logging ---------------------------------------------------------------

        private void Log(string message)
        {
            if (_txtLog.InvokeRequired)
            {
                _txtLog.BeginInvoke(new Action<string>(Log), message);
                return;
            }
            _txtLog.AppendText("[" + DateTime.Now.ToString("HH:mm:ss") + "] " + message + Environment.NewLine);
        }
    }
}
