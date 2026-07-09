using System;
using System.IO;
using System.Net;

namespace OAuthSample
{
    /// <summary>
    /// Builds the small self-contained HTML page the browser shows after the OAuth
    /// redirect. Pure inline CSS/SVG plus (optionally) base64-embedded logos, so it
    /// renders anywhere with no external requests.
    ///
    /// Logos: drop <c>emydex.png</c> and <c>mla.png</c> (or .svg/.jpg) into an
    /// <c>assets</c> folder next to the built .exe — they're embedded as data URIs at
    /// runtime. If a file is missing, a text placeholder is shown instead.
    /// </summary>
    internal static class LandingPage
    {
        public static string Waiting()
            => Document("#64748b", Dot(), "Waiting for the OAuth callback…",
                "<p class='muted'>Keep this window open — the app is listening for the redirect.</p>",
                showClose: false);

        public static string Complete()
            => Document("#16a34a", Check(), "Authentication complete",
                "<p class='muted'>You can close this window and return to the app.</p>",
                showClose: true);

        public static string Error(string message)
            => Document("#dc2626", Cross(), "Sign-in problem",
                "<p class='muted'>" + WebUtility.HtmlEncode(message ?? "Something went wrong.") + "</p>",
                showClose: true);

        public static string Success(string userName, DateTimeOffset expiry)
        {
            string expiryText = expiry == DateTimeOffset.MinValue
                ? "—"
                : expiry.ToLocalTime().ToString("dddd d MMM yyyy, h:mm tt");

            string details =
                "<div class='details'>" +
                    "<div class='row'><span class='k'>Signed in as</span>" +
                        "<span class='v'>" + WebUtility.HtmlEncode(userName) + "</span></div>" +
                    "<div class='row'><span class='k'>Access expires</span>" +
                        "<span class='v'>" + WebUtility.HtmlEncode(expiryText) + "</span></div>" +
                "</div>" +
                "<p class='muted'>You can close this window and return to the app.</p>";

            return Document("#16a34a", Check(), "Authentication complete", details, showClose: true);
        }

        private static string Document(string accent, string icon, string heading, string bodyHtml, bool showClose)
        {
            string close = showClose
                ? "<button class='btn' onclick='window.close()'>Close this tab</button>" +
                  "<p class='muted tiny'>If your browser keeps the tab open, just close it yourself.</p>"
                : "";

            return
                "<!doctype html><html lang='en'><head><meta charset='utf-8'>" +
                "<meta name='viewport' content='width=device-width,initial-scale=1'>" +
                "<title>" + WebUtility.HtmlEncode(heading) + "</title>" +
                "<style>" + Css(accent) + "</style></head><body><main class='card'>" +
                "<div class='logos'>" + Logo("emydex", "Emydex") + Logo("mla", "MLA") + "</div>" +
                "<div class='icon'>" + icon + "</div>" +
                "<h1>" + WebUtility.HtmlEncode(heading) + "</h1>" +
                bodyHtml +
                close +
                "</main></body></html>";
        }

        /// <summary>
        /// Returns an &lt;img&gt; with the logo embedded as a data URI if a matching file
        /// exists in an <c>assets</c> folder beside the exe; otherwise a text placeholder.
        /// </summary>
        private static string Logo(string baseName, string fallbackText)
        {
            try
            {
                string baseDir = AppDomain.CurrentDomain.BaseDirectory;
                string[] dirs = { Path.Combine(baseDir, "assets"), baseDir };
                string[] exts = { ".png", ".svg", ".jpg", ".jpeg", ".gif" };

                foreach (var dir in dirs)
                {
                    foreach (var ext in exts)
                    {
                        string path = Path.Combine(dir, baseName + ext);
                        if (!File.Exists(path))
                            continue;

                        string mime =
                            ext == ".svg" ? "image/svg+xml" :
                            ext == ".jpg" || ext == ".jpeg" ? "image/jpeg" :
                            ext == ".gif" ? "image/gif" : "image/png";
                        string data = Convert.ToBase64String(File.ReadAllBytes(path));
                        return "<img class='logo-img' src='data:" + mime + ";base64," + data + "' alt='" +
                               WebUtility.HtmlEncode(fallbackText) + "'>";
                    }
                }
            }
            catch
            {
                // fall through to the text placeholder
            }

            return "<span class='logo'>" + WebUtility.HtmlEncode(fallbackText) + "</span>";
        }

        private static string Css(string accent)
        {
            return
                "*{box-sizing:border-box}" +
                "body{margin:0;min-height:100vh;display:flex;align-items:center;justify-content:center;" +
                "font-family:'Segoe UI',system-ui,Arial,sans-serif;color:#0f172a;" +
                "background:linear-gradient(135deg,#eef2f7,#dbe4f0)}" +
                ".card{width:min(92vw,420px);background:#fff;border:1px solid #e5e9f0;border-radius:18px;" +
                "padding:32px 32px 26px;text-align:center;box-shadow:0 18px 50px rgba(15,23,42,.15)}" +
                ".logos{display:flex;align-items:center;justify-content:center;gap:22px;margin-bottom:20px;flex-wrap:wrap}" +
                ".logo-img{height:38px;width:auto;max-width:150px}" +
                ".logo{display:inline-flex;align-items:center;padding:7px 13px;border:1.5px dashed #b8c2d0;" +
                "border-radius:10px;font-weight:700;letter-spacing:.3px;color:#475569;font-size:14px}" +
                ".icon{width:58px;height:58px;margin:0 auto 10px;color:" + accent + "}" +
                ".icon svg{width:100%;height:100%}" +
                "h1{font-size:20px;margin:6px 0 8px}" +
                ".muted{color:#64748b;font-size:14px;margin:10px 0 0;line-height:1.45}" +
                ".tiny{font-size:12px;margin-top:9px}" +
                ".details{margin:18px 0 4px;background:#f6f8fb;border-radius:12px;padding:4px 2px;text-align:left}" +
                ".row{display:flex;justify-content:space-between;gap:16px;padding:11px 15px}" +
                ".row+.row{border-top:1px solid rgba(100,116,139,.18)}" +
                ".k{color:#64748b;font-size:13px}" +
                ".v{font-weight:600;font-size:14px;text-align:right;word-break:break-word}" +
                ".btn{margin-top:20px;padding:10px 20px;border:0;border-radius:10px;background:" + accent + ";" +
                "color:#fff;font-size:14px;font-weight:600;cursor:pointer;font-family:inherit}" +
                ".btn:hover{filter:brightness(1.06)}";
        }

        private static string Check()
        {
            return "<svg viewBox='0 0 24 24' fill='none' stroke='currentColor' stroke-width='2.2' " +
                   "stroke-linecap='round' stroke-linejoin='round'><circle cx='12' cy='12' r='10'/>" +
                   "<path d='M8 12.5l2.5 2.5L16 9'/></svg>";
        }

        private static string Cross()
        {
            return "<svg viewBox='0 0 24 24' fill='none' stroke='currentColor' stroke-width='2.2' " +
                   "stroke-linecap='round' stroke-linejoin='round'><circle cx='12' cy='12' r='10'/>" +
                   "<path d='M15 9l-6 6M9 9l6 6'/></svg>";
        }

        private static string Dot()
        {
            return "<svg viewBox='0 0 24 24' fill='none' stroke='currentColor' stroke-width='2.2' " +
                   "stroke-linecap='round'><path d='M12 3a9 9 0 1 0 9 9'/></svg>";
        }
    }
}
