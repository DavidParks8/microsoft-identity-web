﻿// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License.

using Azure.Core;
using Microsoft.Identity.Client;
using Microsoft.Identity.Client.Extensions.Msal;
using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Microsoft.Identity.App.DeveloperCredentials
{
    public class MsalTokenCredential : TokenCredential
    {
#pragma warning disable S1075 // URIs should not be hardcoded
        private const string RedirectUri = "http://localhost";
#pragma warning restore S1075 // URIs should not be hardcoded

        public MsalTokenCredential(string? tenantId, string? username, string instance = "https://login.microsoftonline.com")
        {
            TenantId = tenantId ?? "organizations"; // MSA-passthrough
            Username = username;
            Instance = instance;
        }

        private IPublicClientApplication? App { get; set; }
        private string? TenantId { get; set; }
        private string Instance { get; set; }
        private string? Username { get; set; }

        public override AccessToken GetToken(TokenRequestContext requestContext, CancellationToken cancellationToken)
        {
            return GetTokenAsync(requestContext, cancellationToken).ConfigureAwait(false).GetAwaiter().GetResult();
        }

        private async Task<IPublicClientApplication> GetOrCreateApp()
        {
            if (App == null)
            {
                // On Windows, USERPROFILE is guarantied to be set
                string userProfile = Environment.GetEnvironmentVariable("USERPROFILE")!;
                string cacheDir = Path.Combine(userProfile, @"AppData\Local\.IdentityService");

                // TODO: what about the other platforms?

                var storageProperties =
                     new StorageCreationPropertiesBuilder(
                         "msal.cache",
                         cacheDir,
                         "872cd9fa-d31f-45e0-9eab-6e460a02d1f1"
                         //"04b07795-8ddb-461a-bbee-02f9e1bf7b46"
                         /*"1950a258-227b-4e31-a9cf-717495945fc2"*/)
                     /*
                     .WithLinuxKeyring(
                         Config.LinuxKeyRingSchema,
                         Config.LinuxKeyRingCollection,
                         Config.LinuxKeyRingLabel,
                         Config.LinuxKeyRingAttr1,
                         Config.LinuxKeyRingAttr2)
                     .WithMacKeyChain(
                         Config.KeyChainServiceName,
                         Config.KeyChainAccountName)
                     */
                     .Build();

                App = PublicClientApplicationBuilder.Create(storageProperties.ClientId)
                  .WithRedirectUri(RedirectUri)
                  .Build();

                // This hooks up the cross-platform cache into MSAL
                var cacheHelper = await MsalCacheHelper.CreateAsync(storageProperties).ConfigureAwait(false);
                cacheHelper.RegisterCache(App.UserTokenCache);
            }
            return App;
        }
        
        public override async ValueTask<AccessToken> GetTokenAsync(TokenRequestContext requestContext, CancellationToken cancellationToken)
        {
            var app = await GetOrCreateApp();
            AuthenticationResult result;
            var accounts = await app.GetAccountsAsync()!;
            IAccount? account;

            if (!string.IsNullOrEmpty(Username))
            {
                account = accounts.FirstOrDefault(account => account.Username == Username);
            }
            else
            {
                account = accounts.FirstOrDefault();
            }
            try
            {
                result = await app.AcquireTokenSilent(requestContext.Scopes, account)
                    .WithAuthority(Instance, TenantId)
                    .ExecuteAsync(cancellationToken);
            }
            catch (MsalUiRequiredException ex)
            {
                Console.WriteLine("Please re-sign-in in Visual Studio. ");
                result = await app.AcquireTokenInteractive(requestContext.Scopes)
                    .WithAccount(account)
                    .WithClaims(ex.Claims)
                    .WithAuthority(Instance, TenantId)
                    .ExecuteAsync(cancellationToken);
            }
            return new AccessToken(result.AccessToken, result.ExpiresOn);
        }
    }
}
