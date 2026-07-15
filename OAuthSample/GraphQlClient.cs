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
    /// <summary>Minimal GraphQL-over-HTTP caller plus the eNVD query catalog used by the API Console.
    /// Per the MLA Postman reference, <c>envdAccountId</c> and other inputs are passed as GraphQL
    /// <b>variables</b> (not HTTP headers), so <see cref="PostAsync"/> takes a variables map.</summary>
    internal static class GraphQlClient
    {
        private static readonly HttpClient Http = new HttpClient();

        // --- query catalog (verbatim from the eNVD "Complete Collection" Postman export) --------

        public const string GetUserDetailsQuery =
            "query GetUserDetails { userDetails { firstName lastName email isNewUser " +
            "defaultAccount accounts { envdAccountId accountLabel accountId accountType roles " +
            "properties { propertyName accreditations { lPA eU mSA nFAS eUGFHQB hRG } } } } }";

        public const string GetConsignmentsQuery = @"query GetConsignments($envdAccountId: String!, $first: Int, $after: String, $status: ConsignmentStatus, $species: ConsignmentSpecies, $searchText: String, $fromDate: DateTime, $toDate: DateTime, $onlySubmitted: Boolean) { consignments(envdAccountId: $envdAccountId, first: $first, after: $after, status: $status, species: $species, searchText: $searchText, fromDate: $fromDate, toDate: $toDate, onlySubmitted: $onlySubmitted) { totalCount pageInfo { hasNextPage endCursor } items { id number status species movementDate movementTime heads createdBy updatedAt owner { name pic } origin { name pic } destination { name pic } consignee { name pic } } } }";

        public const string GetConsignmentByIdQuery = @"query GetConsignmentDetails($id: String!, $envdAccountId: String!) { consignment(id: $id, envdAccountId: $envdAccountId) { id number status species movementDate movementTime heads pdfUrl createdBy updatedAt owner { name pic address { line1 town state postcode } } origin { name pic address { line1 town state postcode } } destination { name pic address { line1 town state postcode } } consignee { name pic address { line1 town state postcode } } forms { type serialNumber } answers { questionId index value } questions { id text type order help tip readOnly triggers { questionId value } acceptableAnswers { displayName value } validators { type parameter errorMessage } } declaration { accept fullName phone email date signature certificateNumber address { line1 town state postcode } } } }";

        public const string GetStakeholdersQuery = @"query GetStakeholders($envdAccountId: String!, $consignmentNumber: String!, $role: String!) { consignmentUserMapping(envdAccountId: $envdAccountId, consignmentNumber: $consignmentNumber, role: $role) { email firstName lastName role mappingId pic numOfHeads } }";

        public const string GetConsignmentsPagedQuery = @"query GetConsignmentsWithPagination($envdAccountId: String!, $first: Int!, $after: String) { consignments(envdAccountId: $envdAccountId, first: $first, after: $after) { totalCount pageInfo { hasNextPage endCursor } edges { cursor node { id number status species movementDate heads } } } }";

        public const string GetTemplatesQuery = @"query GetTemplates($envdAccountId: String!, $first: Int, $after: String) { templates(envdAccountId: $envdAccountId, first: $first, after: $after) { pageInfo { hasNextPage endCursor } edges { cursor node { number name species owner { name pic } destination { name pic } forms { type } } } } }";

        public const string GetTemplateByIdQuery = @"query GetTemplateDetails($id: String!, $envdAccountId: String!) { template(id: $id, envdAccountId: $envdAccountId) { id number name species owner { name pic address { line1 town state postcode } } origin { name pic } destination { name pic address { line1 town state postcode } } consignee { name pic } forms { type serialNumber } } }";

        public const string GetCommentsQuery = @"query GetComments($envdAccountId: String!, $consignmentNumber: String!) { comments(envdAccountId: $envdAccountId, consignmentNumber: $consignmentNumber) { commentId consignmentNumber role firstName lastName commentText createdAt } }";

        public const string GetAllQuestionsQuery = @"query GetAllQuestions { questions { items { id order text readOnly forms type help tip triggers { questionId value } validators { type parameter errorMessage } acceptableAnswers { displayName value } childQuestions { id order text readOnly forms type triggers { questionId value } validators { type parameter errorMessage } acceptableAnswers { displayName value } childQuestions { id order text readOnly forms type triggers { questionId value } acceptableAnswers { displayName value } validators { type parameter errorMessage } } } } } }";

        // --- transport --------------------------------------------------------------------------

        /// <summary>POSTs a GraphQL query with a Bearer token. Variables (including
        /// <c>envdAccountId</c>) are sent in the request body as a proper GraphQL
        /// <c>variables</c> object — never as an HTTP header.</summary>
        public static async Task<Tuple<int, string>> PostAsync(
            string endpoint, string accessToken, string query, IDictionary<string, object> variables)
        {
            using (var req = new HttpRequestMessage(HttpMethod.Post, endpoint))
            {
                var payload = new Dictionary<string, object> { ["query"] = query };
                if (variables != null && variables.Count > 0)
                    payload["variables"] = variables;

                string body = new JavaScriptSerializer().Serialize(payload);
                req.Content = new StringContent(body, Encoding.UTF8, "application/json");
                req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);

                using (var resp = await Http.SendAsync(req))
                {
                    string respBody = await resp.Content.ReadAsStringAsync();
                    return Tuple.Create((int)resp.StatusCode, respBody);
                }
            }
        }

        // --- response extraction ----------------------------------------------------------------

        /// <summary>Pulls the envdAccountId out of a GetUserDetails response. Per the MLA
        /// reference, <c>userDetails.defaultAccount</c> holds it directly; falls back to the
        /// first account's envdAccountId.</summary>
        public static string ExtractEnvdAccountId(string body)
        {
            try
            {
                var data = Prop(Deserialize(body), "data");
                var ud = Prop(data, "userDetails");

                string def = Str(Prop(ud, "defaultAccount"));
                if (def != null)
                    return def;

                if (Prop(ud, "accounts") is IEnumerable accounts)
                    foreach (var item in accounts)
                    {
                        string envd = Str(Prop(item, "envdAccountId"));
                        if (envd != null)
                            return envd;
                    }
                return null;
            }
            catch
            {
                return null;
            }
        }

        /// <summary>Pulls a consignment number out of a consignment response — a single
        /// consignment (by id), a list (items[]) or a paged result (edges[].node). Used to
        /// auto-fill the Consignment # field, the way the Postman test scripts do.</summary>
        public static string ExtractConsignmentNumber(string body)
        {
            try
            {
                var data = Prop(Deserialize(body), "data");

                string single = Str(Prop(Prop(data, "consignment"), "number"));
                if (single != null)
                    return single;

                var cons = Prop(data, "consignments");

                if (Prop(cons, "items") is IEnumerable items)
                    foreach (var it in items)
                        return Str(Prop(it, "number"));

                if (Prop(cons, "edges") is IEnumerable edges)
                    foreach (var ed in edges)
                        return Str(Prop(Prop(ed, "node"), "number"));

                return null;
            }
            catch
            {
                return null;
            }
        }

        // --- tiny JSON accessors (JavaScriptSerializer: objects -> Dictionary, arrays -> object[]) -

        private static Dictionary<string, object> Deserialize(string body) =>
            new JavaScriptSerializer().Deserialize<Dictionary<string, object>>(body);

        private static object Prop(object obj, string key) =>
            obj is Dictionary<string, object> d && d.TryGetValue(key, out var v) ? v : null;

        private static string Str(object obj)
        {
            string s = obj?.ToString();
            return string.IsNullOrEmpty(s) ? null : s;
        }
    }
}
