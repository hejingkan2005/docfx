// Copyright (c) Microsoft. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading.Tasks;
using System.Web;
using Newtonsoft.Json;
using Polly;
using Polly.Extensions.Http;

namespace Microsoft.Docs.Build
{
    internal class OpsConfigAdapter : IDisposable
    {
        public const string BuildConfigApi = "https://ops/buildconfig/";
        private const string MonikerDefinitionApi = "https://ops/monikerDefinition/";
        private const string MetadataSchemaApi = "https://ops/metadataschema/";
        private const string MarkdownValidationRulesApi = "https://ops/markdownvalidationrules/";

        public static readonly string ValidationServiceEndpoint = s_isProduction
            ? "https://docs.microsoft.com"
            : "https://ppe.docs.microsoft.com";

        private static readonly string s_environment = Environment.GetEnvironmentVariable("DOCS_ENVIRONMENT");
        private static readonly bool s_isProduction = string.IsNullOrEmpty(s_environment) || string.Equals("PROD", s_environment, StringComparison.OrdinalIgnoreCase);

        private static readonly string s_buildServiceEndpoint = s_isProduction
            ? "https://op-build-prod.azurewebsites.net"
            : "https://op-build-sandbox2.azurewebsites.net";

        private readonly Action<HttpRequestMessage> _credentialProvider;
        private readonly ErrorLog _errorLog;
        private readonly HttpClient _http = new HttpClient();
        private readonly (string, Func<Uri, Task<string>>)[] _apis;

        public OpsConfigAdapter(ErrorLog errorLog, Action<HttpRequestMessage> credentialProvider)
        {
            _errorLog = errorLog;
            _credentialProvider = credentialProvider;
            _apis = new (string, Func<Uri, Task<string>>)[]
            {
                (BuildConfigApi, GetBuildConfig),
                (MonikerDefinitionApi, GetMonikerDefinition),
                (MetadataSchemaApi, GetMetadataSchema),
                (MarkdownValidationRulesApi, GetMarkdownValidationRules),
            };
        }

        public async Task<HttpResponseMessage> InterceptHttpRequest(HttpRequestMessage request)
        {
            foreach (var (baseUrl, rule) in _apis)
            {
                if (request.RequestUri.OriginalString.StartsWith(baseUrl))
                {
                    return new HttpResponseMessage { Content = new StringContent(await rule(request.RequestUri)) };
                }
            }
            return null;
        }

        public void Dispose()
        {
            _http.Dispose();
        }

        private async Task<string> GetBuildConfig(Uri url)
        {
            var queries = HttpUtility.ParseQueryString(url.Query);
            var name = queries["name"];
            var repository = queries["repository_url"];
            var branch = queries["branch"];
            var locale = queries["locale"];
            var xrefEndpoint = queries["xref_endpoint"];
            var xrefQueryTags = string.IsNullOrEmpty(queries["xref_query_tags"]) ? new List<string>() : queries["xref_query_tags"].Split(',').ToList();

            var fetchUrl = $"{s_buildServiceEndpoint}/v2/Queries/Docsets?git_repo_url={repository}&docset_query_status=Created";
            var docsetInfo = await Fetch(fetchUrl, nullOn404: true);
            if (docsetInfo is null)
            {
                throw Errors.DocsetNotProvisioned(name).ToException(isError: false);
            }

            var docsets = JsonConvert.DeserializeAnonymousType(
                docsetInfo,
                new[] { new { name = "", base_path = "", site_name = "", product_name = "" } });

            var docset = docsets.FirstOrDefault(d => string.Equals(d.name, name, StringComparison.OrdinalIgnoreCase));
            if (docset is null)
            {
                throw Errors.DocsetNotProvisioned(name).ToException(isError: false);
            }

            var metadataServiceQueryParams = $"?repository_url={HttpUtility.UrlEncode(repository)}&branch={HttpUtility.UrlEncode(branch)}";

            var xrefMapQueryParams = $"?site_name={docset.site_name}&branch_name={branch}&exclude_depot_name={docset.product_name}.{name}";
            var xrefMapApiEndpoint = GetXrefMapApiEndpoint(xrefEndpoint);
            if (docset.base_path != "/")
            {
                xrefQueryTags.Add(docset.base_path);
            }
            var xrefMaps = new List<string>();
            foreach (var tag in xrefQueryTags)
            {
                var links = await GetXrefMaps(xrefMapApiEndpoint, tag, xrefMapQueryParams);
                xrefMaps.AddRange(links);
            }

            return JsonConvert.SerializeObject(new
            {
                product = docset.product_name,
                siteName = docset.site_name,
                hostName = GetHostName(docset.site_name),
                basePath = docset.base_path,
                xrefHostName = GetXrefHostName(docset.site_name, branch),
                monikerDefinition = MonikerDefinitionApi,
                markdownValidationRules = $"{MarkdownValidationRulesApi}{metadataServiceQueryParams}",
                metadataSchema = new[]
                {
                    Path.Combine(AppContext.BaseDirectory, "data/schemas/OpsMetadata.json"),
                    $"{MetadataSchemaApi}{metadataServiceQueryParams}",
                },
                xref = xrefMaps,
            });
        }

        private string GetXrefMapApiEndpoint(string xrefEndpoint)
        {
            var isProduction = s_isProduction;
            if (!string.IsNullOrEmpty(xrefEndpoint))
            {
                isProduction = string.Equals(xrefEndpoint.TrimEnd('/'), "https://xref.docs.microsoft.com", StringComparison.OrdinalIgnoreCase);
            }
            return isProduction
                    ? "https://op-build-prod.azurewebsites.net"
                    : "https://op-build-sandbox2.azurewebsites.net";
        }

        private async Task<string[]> GetXrefMaps(string xrefMapApiEndpoint, string tag, string xrefMapQueryParams)
        {
            var url = $"{xrefMapApiEndpoint}/v1/xrefmap{tag}{xrefMapQueryParams}";
            var response = await Fetch(url, nullOn404: true);
            if (response is null)
            {
                return Array.Empty<string>();
            }
            else
            {
                return JsonConvert.DeserializeAnonymousType(response, new { links = new[] { "" } }).links;
            }
        }

        private Task<string> GetMonikerDefinition(Uri url)
        {
            return Fetch($"{s_buildServiceEndpoint}/v2/monikertrees/allfamiliesproductsmonikers");
        }

        private async Task<string> GetMarkdownValidationRules(Uri url)
        {
            var headers = GetValidationServiceHeaders(url);

            return await FetchValidationRules($"{ValidationServiceEndpoint}/api/metadata/rules/content", headers);
        }

        private async Task<string> GetMetadataSchema(Uri url)
        {
            var headers = GetValidationServiceHeaders(url);
            var rules = FetchValidationRules($"{ValidationServiceEndpoint}/api/metadata/rules", headers);
            var allowlists = FetchValidationRules($"{ValidationServiceEndpoint}/api/metadata/allowlists", headers);

            return OpsMetadataRuleConverter.GenerateJsonSchema(await rules, await allowlists);
        }

        private static Dictionary<string, string> GetValidationServiceHeaders(Uri url)
        {
            var queries = HttpUtility.ParseQueryString(url.Query);

            return new Dictionary<string, string>()
            {
                { "X-Metadata-RepositoryUrl", queries["repository_url"] },
                { "X-Metadata-RepositoryBranch", queries["branch"] },
            };
        }

        private async Task<string> FetchValidationRules(string url, IReadOnlyDictionary<string, string> headers = null, bool nullOn404 = false)
        {
            try
            {
                using (PerfScope.Start($"[{nameof(OpsConfigAdapter)}] Fetching '{url}'"))
                {
                    using var response = await HttpPolicyExtensions
                       .HandleTransientHttpError()
                       .Or<OperationCanceledException>()
                       .Or<IOException>()
                       .RetryAsync(3, onRetry: (_, i) => Log.Write($"[{i}] Retrying '{url}'"))
                       .ExecuteAsync(async () =>
                       {
                           using var request = new HttpRequestMessage(HttpMethod.Get, url);
                           _credentialProvider?.Invoke(request);
                           if (headers != null)
                           {
                               foreach (var (key, value) in headers)
                               {
                                   request.Headers.TryAddWithoutValidation(key, value);
                               }
                           }
                           var response = await _http.SendAsync(request);
                           if (response.Headers.TryGetValues("X-Metadata-Version", out var metadataVersion))
                           {
                               _errorLog.Write(Errors.MetadataValidationRuleset(string.Join(',', metadataVersion)));
                           }
                           return response;
                       });

                    return await response.EnsureSuccessStatusCode().Content.ReadAsStringAsync();
                }
            }
            catch (Exception ex)
            {
                // Getting validation rules failure should not block build proceeding,
                // catch and log the excpeition without rethrow.
                Log.Write(ex);
                _errorLog.Write(Errors.ValidationIncomplete());
                return "{}";
            }
        }

        private async Task<string> Fetch(string url, IReadOnlyDictionary<string, string> headers = null, bool nullOn404 = false)
        {
            using (PerfScope.Start($"[{nameof(OpsConfigAdapter)}] Fetching '{url}'"))
            using (var request = new HttpRequestMessage(HttpMethod.Get, url))
            {
                _credentialProvider?.Invoke(request);

                if (headers != null)
                {
                    foreach (var (key, value) in headers)
                    {
                        request.Headers.TryAddWithoutValidation(key, value);
                    }
                }

                var response = await _http.SendAsync(request);

                if (nullOn404 && response.StatusCode == HttpStatusCode.NotFound)
                {
                    return null;
                }
                return await response.EnsureSuccessStatusCode().Content.ReadAsStringAsync();
            }
        }

        private static string GetHostName(string siteName)
        {
            switch (siteName)
            {
                case "DocsAzureCN":
                    return s_isProduction ? "docs.azure.cn" : "ppe.docs.azure.cn";
                case "dev.microsoft.com":
                    return s_isProduction ? "developer.microsoft.com" : "devmsft-sandbox.azurewebsites.net";
                case "rd.microsoft.com":
                    return "rd.microsoft.com";
                default:
                    return s_isProduction ? "docs.microsoft.com" : "ppe.docs.microsoft.com";
            }
        }

        private static string GetXrefHostName(string siteName, string branch)
        {
            return !IsLive(branch) && s_isProduction ? $"review.{GetHostName(siteName)}" : GetHostName(siteName);
        }

        private static bool IsLive(string branch)
        {
            return branch == "live" || branch == "live-sxs";
        }
    }
}
