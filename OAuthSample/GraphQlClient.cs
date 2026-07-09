using System;
using System.Collections;
using System.Collections.Generic;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Threading.Tasks;
using System.Web.Script.Serialization;

namespace OAuthSample
{
    /// <summary>Minimal GraphQL-over-HTTP caller used by the "Call test API" button.</summary>
    internal static class GraphQlClient
    {
        private static readonly HttpClient Http = new HttpClient();

        public const string GetUserDetailsQuery =
            "query GetUserDetails { userDetails { firstName lastName email isNewUser " +
            "defaultAccount accounts { envdAccountId accountLabel accountId accountType } } }";

        /// <summary>POSTs a GraphQL query with a Bearer token (and optional envdAccountId header).</summary>
        public static async Task<Tuple<int, string>> PostAsync(
            string endpoint, string accessToken, string query, string envdAccountId)
        {
            using (var req = new HttpRequestMessage(HttpMethod.Post, endpoint))
            {
                string payload = new JavaScriptSerializer().Serialize(new Dictionary<string, object> { ["query"] = query });
                req.Content = new StringContent(payload, Encoding.UTF8, "application/json");
                req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
                if (!string.IsNullOrEmpty(envdAccountId))
                    req.Headers.TryAddWithoutValidation("envdAccountId", envdAccountId);

                using (var resp = await Http.SendAsync(req))
                {
                    string body = await resp.Content.ReadAsStringAsync();
                    return Tuple.Create((int)resp.StatusCode, body);
                }
            }
        }

        /// <summary>Pulls the envdAccountId out of a GetUserDetails response — the default
        /// account if identifiable, otherwise the first account.</summary>
        public static string ExtractEnvdAccountId(string body)
        {
            try
            {
                var root = new JavaScriptSerializer().Deserialize<Dictionary<string, object>>(body);
                if (root == null || !root.TryGetValue("data", out var d) || !(d is Dictionary<string, object> data))
                    return null;
                if (!data.TryGetValue("userDetails", out var u) || !(u is Dictionary<string, object> ud))
                    return null;

                string def = ud.TryGetValue("defaultAccount", out var da) && da != null ? da.ToString() : null;
                if (!ud.TryGetValue("accounts", out var a) || !(a is IEnumerable accounts))
                    return null;

                string first = null, matched = null;
                foreach (var item in accounts)
                {
                    if (!(item is Dictionary<string, object> acc))
                        continue;
                    string envd = acc.TryGetValue("envdAccountId", out var e) && e != null ? e.ToString() : null;
                    if (string.IsNullOrEmpty(envd))
                        continue;
                    if (first == null)
                        first = envd;
                    string accId = acc.TryGetValue("accountId", out var ai) && ai != null ? ai.ToString() : null;
                    if (def != null && (def == accId || def == envd))
                        matched = envd;
                }
                return matched ?? first;
            }
            catch
            {
                return null;
            }
        }

        /// <summary>Indents JSON for readable console output; returns non-JSON bodies unchanged.</summary>
        public static string Pretty(string json)
        {
            if (string.IsNullOrWhiteSpace(json))
                return json ?? "";
            string trimmed = json.TrimStart();
            if (trimmed.Length == 0 || (trimmed[0] != '{' && trimmed[0] != '['))
                return json;

            var sb = new StringBuilder();
            int indent = 0;
            bool inString = false, escape = false;
            foreach (char c in json)
            {
                if (inString)
                {
                    sb.Append(c);
                    if (escape) escape = false;
                    else if (c == '\\') escape = true;
                    else if (c == '"') inString = false;
                    continue;
                }
                switch (c)
                {
                    case '"': inString = true; sb.Append(c); break;
                    case '{':
                    case '[': sb.Append(c); indent++; NewLine(sb, indent); break;
                    case '}':
                    case ']': indent--; NewLine(sb, indent); sb.Append(c); break;
                    case ',': sb.Append(c); NewLine(sb, indent); break;
                    case ':': sb.Append(": "); break;
                    default:
                        if (!char.IsWhiteSpace(c)) sb.Append(c);
                        break;
                }
            }
            return sb.ToString();
        }

        private static void NewLine(StringBuilder sb, int indent)
        {
            sb.Append('\n').Append(new string(' ', Math.Max(0, indent) * 2));
        }
    }
}
