// Copyright (c) Microsoft. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using System;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Graph;
using Microsoft.Identity.Client;

namespace Microsoft.Docs.Build
{
    internal class MicrosoftGraphAuthenticationProvider : IAuthenticationProvider, IDisposable
    {
        private static readonly string[] scopes = { "https://graph.microsoft.com/.default" };

        private readonly IConfidentialClientApplication _cca;
        private readonly SemaphoreSlim _semaphore = new SemaphoreSlim(1, 1);

        private AuthenticationResult? _authenticationResult;

        public MicrosoftGraphAuthenticationProvider(string tenantId, string clientId, string clientSecret)
        {
            _cca = ConfidentialClientApplicationBuilder.Create(clientId)
                .WithClientSecret(clientSecret)
                .WithAuthority(new Uri($"https://login.microsoftonline.com/{tenantId}/v2.0"))
                .WithRedirectUri("http://www.microsoft.com")
                .Build();
        }

        public async Task AuthenticateRequestAsync(HttpRequestMessage request)
        {
            var accessToken = await GetAccessTokenAsync();
            request.Headers.Authorization = new AuthenticationHeaderValue("bearer", accessToken);
        }

        public void Dispose()
        {
            _semaphore.Dispose();
        }

        private async Task<string> GetAccessTokenAsync()
        {
            try
            {
                await _semaphore.WaitAsync();
                if (_authenticationResult == null || _authenticationResult.ExpiresOn.UtcDateTime < DateTime.UtcNow.AddMinutes(-1))
                {
                    _authenticationResult = await _cca.AcquireTokenForClient(scopes).ExecuteAsync();
                }
                return _authenticationResult.AccessToken;
            }
            finally
            {
                _semaphore.Release();
            }
        }
    }
}
