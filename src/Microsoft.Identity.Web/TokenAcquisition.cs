﻿// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IdentityModel.Tokens.Jwt;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Security.Claims;
using System.Security.Cryptography.X509Certificates;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Primitives;
using Microsoft.Identity.Client;
using Microsoft.Identity.Web.TokenCacheProviders;
using Microsoft.Net.Http.Headers;

namespace Microsoft.Identity.Web
{
    /// <summary>
    /// Token acquisition service.
    /// </summary>
    internal class TokenAcquisition : ITokenAcquisitionInternal
    {
        private readonly IOptionsMonitor<MicrosoftIdentityOptions> _microsoftIdentityOptionsMonitor;
        private readonly IOptionsMonitor<ConfidentialClientApplicationOptions> _applicationOptionsMonitor;
        private readonly IMsalTokenCacheProvider _tokenCacheProvider;

        private IConfidentialClientApplication? _application;
        private readonly IHttpContextAccessor _httpContextAccessor;
        private HttpContext? CurrentHttpContext => _httpContextAccessor.HttpContext;
        private readonly IMsalHttpClientFactory _httpClientFactory;
        private readonly ILogger _logger;
        private readonly IServiceProvider _serviceProvider;

        /// <summary>
        /// Constructor of the TokenAcquisition service. This requires the Azure AD Options to
        /// configure the confidential client application and a token cache provider.
        /// This constructor is called by ASP.NET Core dependency injection.
        /// </summary>
        /// <param name="tokenCacheProvider">The App token cache provider.</param>
        /// <param name="httpContextAccessor">Access to the HttpContext of the request.</param>
        /// <param name="microsoftIdentityOptionsMonitor">Configuration options.</param>
        /// <param name="applicationOptionsMonitor">MSAL.NET configuration options.</param>
        /// <param name="httpClientFactory">HTTP client factory.</param>
        /// <param name="logger">Logger.</param>
        /// <param name="serviceProvider">Service provider.</param>
        public TokenAcquisition(
            IMsalTokenCacheProvider tokenCacheProvider,
            IHttpContextAccessor httpContextAccessor,
            IOptionsMonitor<MicrosoftIdentityOptions> microsoftIdentityOptionsMonitor,
            IOptionsMonitor<ConfidentialClientApplicationOptions> applicationOptionsMonitor,
            IHttpClientFactory httpClientFactory,
            ILogger<TokenAcquisition> logger,
            IServiceProvider serviceProvider)
        {
            _httpContextAccessor = httpContextAccessor;
            _microsoftIdentityOptionsMonitor = microsoftIdentityOptionsMonitor;
            _applicationOptionsMonitor = applicationOptionsMonitor;
            _tokenCacheProvider = tokenCacheProvider;
            _httpClientFactory = new MsalAspNetCoreHttpClientFactory(httpClientFactory);
            _logger = logger;
            _serviceProvider = serviceProvider;
        }

        internal void GetOptions(
            string authenticationScheme,
            out MicrosoftIdentityOptions microsoftIdentityOptions,
            out ConfidentialClientApplicationOptions applicationOptions)
        {
            microsoftIdentityOptions = _microsoftIdentityOptionsMonitor.Get(authenticationScheme);
            applicationOptions = _applicationOptionsMonitor.Get(authenticationScheme);

            applicationOptions.ClientId ??= microsoftIdentityOptions.ClientId;
            applicationOptions.Instance ??= microsoftIdentityOptions.Instance;
            applicationOptions.ClientSecret ??= microsoftIdentityOptions.ClientSecret;
            applicationOptions.TenantId ??= microsoftIdentityOptions.TenantId;
        }

        /// <summary>
        /// Scopes which are already requested by MSAL.NET. They should not be re-requested;.
        /// </summary>
        private readonly string[] _scopesRequestedByMsal = new string[]
        {
            OidcConstants.ScopeOpenId,
            OidcConstants.ScopeProfile,
            OidcConstants.ScopeOfflineAccess,
        };

        /// <summary>
        /// Meta-tenant identifiers which are not allowed in client credentials.
        /// </summary>
        private readonly ISet<string> _metaTenantIdentifiers = new HashSet<string>(
            new[]
            {
                Constants.Common,
                Constants.Organizations,
            },
            StringComparer.OrdinalIgnoreCase);

        /// <summary>
        /// This handler is executed after the authorization code is received (once the user signs-in and consents) during the
        /// <a href='https://docs.microsoft.com/azure/active-directory/develop/v2-oauth2-auth-code-flow'>authorization code flow</a> in a web app.
        /// It uses the code to request an access token from the Microsoft identity platform and caches the tokens and an entry about the signed-in user's account in the MSAL's token cache.
        /// The access token (and refresh token) provided in the <see cref="AuthorizationCodeReceivedContext"/>, once added to the cache, are then used to acquire more tokens using the
        /// <a href='https://docs.microsoft.com/azure/active-directory/develop/v2-oauth2-on-behalf-of-flow'>on-behalf-of flow</a> for the signed-in user's account,
        /// in order to call to downstream APIs.
        /// </summary>
        /// <param name="context">The context used when an 'AuthorizationCode' is received over the OpenIdConnect protocol.</param>
        /// <param name="scopes">scopes to request access to.</param>
        /// <param name="authenticationScheme">Authentication scheme to use (by default, OpenIdConnectDefaults.AuthenticationScheme).</param>
        /// <example>
        /// From the configuration of the Authentication of the ASP.NET Core web API:
        /// <code>OpenIdConnectOptions options;</code>
        ///
        /// Subscribe to the authorization code received event:
        /// <code>
        ///  options.Events = new OpenIdConnectEvents();
        ///  options.Events.OnAuthorizationCodeReceived = OnAuthorizationCodeReceived;
        /// }
        /// </code>
        ///
        /// And then in the OnAuthorizationCodeRecieved method, call <see cref="AddAccountToCacheFromAuthorizationCodeAsync"/>:
        /// <code>
        /// private async Task OnAuthorizationCodeReceived(AuthorizationCodeReceivedContext context)
        /// {
        ///   var tokenAcquisition = context.HttpContext.RequestServices.GetRequiredService&lt;ITokenAcquisition&gt;();
        ///    await _tokenAcquisition.AddAccountToCacheFromAuthorizationCode(context, new string[] { "user.read" });
        /// }
        /// </code>
        /// </example>
        public async Task AddAccountToCacheFromAuthorizationCodeAsync(
            AuthorizationCodeReceivedContext context,
            IEnumerable<string> scopes,
            string authenticationScheme /*= OpenIdConnectDefaults.AuthenticationScheme*/)
        {
            if (context == null)
            {
                throw new ArgumentNullException(nameof(context));
            }

            if (scopes == null)
            {
                throw new ArgumentNullException(nameof(scopes));
            }

            MicrosoftIdentityOptions microsoftIdentityOptions;
            ConfidentialClientApplicationOptions applicationOptions;
            GetOptions(authenticationScheme, out microsoftIdentityOptions, out applicationOptions);

            try
            {
                _application = await GetOrBuildConfidentialClientApplicationAsync(microsoftIdentityOptions, applicationOptions)
                    .ConfigureAwait(false);

                // Do not share the access token with ASP.NET Core otherwise ASP.NET will cache it and will not send the OAuth 2.0 request in
                // case a further call to AcquireTokenByAuthorizationCodeAsync in the future is required for incremental consent (getting a code requesting more scopes)
                // Share the ID token though
                var builder = _application
                    .AcquireTokenByAuthorizationCode(scopes.Except(_scopesRequestedByMsal), context.ProtocolMessage.Code)
                    .WithSendX5C(microsoftIdentityOptions.SendX5C);

                if (microsoftIdentityOptions.IsB2C)
                {
                    string? userFlow = context.Principal?.GetUserFlowId();
                    var authority = $"{applicationOptions.Instance}{ClaimConstants.Tfp}/{microsoftIdentityOptions.Domain}/{userFlow ?? microsoftIdentityOptions.DefaultUserFlow}";
                    builder.WithB2CAuthority(authority);
                }

                var result = await builder.ExecuteAsync()
                                          .ConfigureAwait(false);

                context.HandleCodeRedemption(null, result.IdToken);
            }
            catch (MsalException ex)
            {
                _logger.LogInformation(
                    ex,
                    LogMessages.ExceptionOccurredWhenAddingAnAccountToTheCacheFromAuthCode);
                throw;
            }
        }

        /// <summary>
        /// Typically used from a web app or web API controller, this method retrieves an access token
        /// for a downstream API using;
        /// 1) the token cache (for web apps and web APIs) if a token exists in the cache
        /// 2) or the <a href='https://docs.microsoft.com/azure/active-directory/develop/v2-oauth2-on-behalf-of-flow'>on-behalf-of flow</a>
        /// in web APIs, for the user account that is ascertained from claims provided in the <see cref="HttpContext.User"/>
        /// instance of the current HttpContext.
        /// </summary>
        /// <param name="scopes">Scopes to request for the downstream API to call.</param>
        /// <param name="tenantId">Enables overriding of the tenant/account for the same identity. This is useful in the
        /// cases where a given account is a guest in other tenants, and you want to acquire tokens for a specific tenant, like where the user is a guest.</param>
        /// <param name="userFlow">Azure AD B2C user flow to target.</param>
        /// <param name="user">Optional claims principal representing the user. If not provided, will use the signed-in
        /// user (in a web app), or the user for which the token was received (in a web API)
        /// cases where a given account is a guest in other tenants, and you want to acquire tokens for a specific tenant, like where the user is a guest.</param>
        /// <param name="tokenAcquisitionOptions">Options passed-in to create the token acquisition options object which calls into MSAL .NET.</param>
        /// <param name="authenticationScheme">Authentication scheme. If null, will use OpenIdConnectDefault.AuthenticationScheme
        /// if called from a web app, and JwtBearerDefault.AuthenticationScheme if called from a web APIs.</param>
        /// <returns>An access token to call the downstream API and populated with this downstream API's scopes.</returns>
        /// <remarks>Calling this method from a web API supposes that you have previously called,
        /// in a method called by JwtBearerOptions.Events.OnTokenValidated, the HttpContextExtensions.StoreTokenUsedToCallWebAPI method
        /// passing the validated token (as a JwtSecurityToken). Calling it from a web app supposes that
        /// you have previously called AddAccountToCacheFromAuthorizationCodeAsync from a method called by
        /// OpenIdConnectOptions.Events.OnAuthorizationCodeReceived.</remarks>
        public async Task<AuthenticationResult> GetAuthenticationResultForUserAsync(
            IEnumerable<string> scopes,
            string? tenantId = null,
            string? userFlow = null,
            ClaimsPrincipal? user = null,
            TokenAcquisitionOptions? tokenAcquisitionOptions = null,
            string? authenticationScheme = null)
        {
            if (scopes == null)
            {
                throw new ArgumentNullException(nameof(scopes));
            }

            user = await GetAuthenticatedUserAsync(user).ConfigureAwait(false);

            MicrosoftIdentityOptions microsoftIdentityOptions;
            ConfidentialClientApplicationOptions applicationOptions;
            GetOptions(GetEffectiveAuthenticationScheme(authenticationScheme), out microsoftIdentityOptions, out applicationOptions);

            _application = await GetOrBuildConfidentialClientApplicationAsync(microsoftIdentityOptions, applicationOptions).ConfigureAwait(false);

            string authority = CreateAuthorityBasedOnTenantIfProvided(_application, tenantId);

            AuthenticationResult? authenticationResult;

            try
            {
                // Access token will return if call is from a web API
                authenticationResult = await GetAuthenticationResultForWebApiToCallDownstreamApiAsync(
                    _application,
                    authority,
                    scopes,
                    tokenAcquisitionOptions,
                    microsoftIdentityOptions).ConfigureAwait(false);

                if (authenticationResult != null)
                {
                    return authenticationResult;
                }

                // If access token is null, this is a web app
                return await GetAuthenticationResultForWebAppWithAccountFromCacheAsync(
                     _application,
                     user,
                     scopes,
                     authority,
                     microsoftIdentityOptions,
                     userFlow: userFlow)
                     .ConfigureAwait(false);
            }
            catch (MsalUiRequiredException ex)
            {
                // GetAccessTokenForUserAsync is an abstraction that can be called from a web app or a web API
                _logger.LogInformation(ex.Message);

                // Case of the web app: we let the MsalUiRequiredException be caught by the
                // AuthorizeForScopesAttribute exception filter so that the user can consent, do 2FA, etc ...
                throw new MicrosoftIdentityWebChallengeUserException(ex, scopes.ToArray(), userFlow);
            }
        }

        /// <inheritdoc/>
        public string GetEffectiveAuthenticationScheme(string? authenticationScheme)
        {
            if (authenticationScheme != null)
            {
                return authenticationScheme;
            }
            else
            {
                return (CurrentHttpContext?.GetTokenUsedToCallWebAPI() != null)
                 ? JwtBearerDefaults.AuthenticationScheme : OpenIdConnectDefaults.AuthenticationScheme;
            }
        }

        /// <summary>
        /// Acquires a token from the authority configured in the app, for the confidential client itself (not on behalf of a user)
        /// using the client credentials flow. See https://aka.ms/msal-net-client-credentials.
        /// </summary>
        /// <param name="scope">The scope requested to access a protected API. For this flow (client credentials), the scope
        /// should be of the form "{ResourceIdUri/.default}" for instance <c>https://management.azure.net/.default</c> or, for Microsoft
        /// Graph, <c>https://graph.microsoft.com/.default</c> as the requested scopes are defined statically with the application registration
        /// in the portal, and cannot be overridden in the application, as you can request a token for only one resource at a time (use
        /// several calls to get tokens for other resources).</param>
        /// <param name="tenant">Enables overriding of the tenant/account for the same identity. This is useful
        /// for multi tenant apps or daemons.</param>
        /// <param name="tokenAcquisitionOptions">Options passed-in to create the token acquisition object which calls into MSAL .NET.</param>
        /// <param name="authenticationScheme">AuthenticationScheme to use.</param>
        /// <returns>An access token for the app itself, based on its scopes.</returns>
        public async Task<string> GetAccessTokenForAppAsync(
            string scope,
            string? tenant = null,
            TokenAcquisitionOptions? tokenAcquisitionOptions = null,
            string? authenticationScheme = null)
        {
            if (string.IsNullOrEmpty(scope))
            {
                throw new ArgumentNullException(nameof(scope));
            }

            if (!scope.EndsWith("/.default", true, CultureInfo.InvariantCulture))
            {
                throw new ArgumentException(IDWebErrorMessage.ClientCredentialScopeParameterShouldEndInDotDefault, nameof(scope));
            }

            MicrosoftIdentityOptions microsoftIdentityOptions;
            ConfidentialClientApplicationOptions applicationOptions;
            GetOptions(
                GetEffectiveAuthenticationScheme(authenticationScheme),
                out microsoftIdentityOptions,
                out applicationOptions);

            if (string.IsNullOrEmpty(tenant))
            {
                tenant = applicationOptions.TenantId;
            }

            if (!string.IsNullOrEmpty(tenant) && _metaTenantIdentifiers.Contains(tenant))
            {
                throw new ArgumentException(IDWebErrorMessage.ClientCredentialTenantShouldBeTenanted, nameof(tenant));
            }

            // Use MSAL to get the right token to call the API
            _application = await GetOrBuildConfidentialClientApplicationAsync(microsoftIdentityOptions, applicationOptions).ConfigureAwait(false);
            string authority = CreateAuthorityBasedOnTenantIfProvided(_application, tenant);

            AuthenticationResult result;
            var builder = _application
                   .AcquireTokenForClient(new string[] { scope }.Except(_scopesRequestedByMsal))
                   .WithSendX5C(microsoftIdentityOptions.SendX5C)
                   .WithAuthority(authority);

            if (tokenAcquisitionOptions != null)
            {
                builder.WithExtraQueryParameters(tokenAcquisitionOptions.ExtraQueryParameters);
                builder.WithCorrelationId(tokenAcquisitionOptions.CorrelationId);
                builder.WithForceRefresh(tokenAcquisitionOptions.ForceRefresh);
                builder.WithClaims(tokenAcquisitionOptions.Claims);
            }

            result = await builder.ExecuteAsync()
                                  .ConfigureAwait(false);

            return result.AccessToken;
        }

        /// <summary>
        /// Typically used from a web app or web API controller, this method retrieves an access token
        /// for a downstream API using;
        /// 1) the token cache (for web apps and web APIs) if a token exists in the cache
        /// 2) or the <a href='https://docs.microsoft.com/azure/active-directory/develop/v2-oauth2-on-behalf-of-flow'>on-behalf-of flow</a>
        /// in web APIs, for the user account that is ascertained from the claims provided in the <see cref="HttpContext.User"/>
        /// instance of the current HttpContext.
        /// </summary>
        /// <param name="scopes">Scopes to request for the downstream API to call.</param>
        /// <param name="tenantId">Enables overriding of the tenant/account for the same identity. This is useful in the
        /// cases where a given account is a guest in other tenants, and you want to acquire tokens for a specific tenant.</param>
        /// <param name="userFlow">Azure AD B2C user flow to target.</param>
        /// <param name="user">Optional claims principal representing the user. If not provided, will use the signed-in
        /// user (in a web app), or the user for which the token was received (in a web API)
        /// cases where a given account is a guest in other tenants, and you want to acquire tokens for a specific tenant.</param>
        /// <param name="tokenAcquisitionOptions">Options passed-in to create the token acquisition object which calls into MSAL .NET.</param>
        /// <param name="authenticationScheme">Authentication scheme. If null, will use OpenIdConnectDefault.AuthenticationScheme
        /// if called from a web app, and JwtBearerDefault.AuthenticationScheme if called from a web APIs.</param>
        /// <returns>An access token to call the downstream API and populated with this downstream API's scopes.</returns>
        /// <remarks>Calling this method from a web API supposes that you have previously called,
        /// in a method called by JwtBearerOptions.Events.OnTokenValidated, the HttpContextExtensions.StoreTokenUsedToCallWebAPI method
        /// passing the validated token (as a JwtSecurityToken). Calling it from a web app supposes that
        /// you have previously called AddAccountToCacheFromAuthorizationCodeAsync from a method called by
        /// OpenIdConnectOptions.Events.OnAuthorizationCodeReceived.</remarks>
        public async Task<string> GetAccessTokenForUserAsync(
            IEnumerable<string> scopes,
            string? tenantId = null,
            string? userFlow = null,
            ClaimsPrincipal? user = null,
            TokenAcquisitionOptions? tokenAcquisitionOptions = null,
            string? authenticationScheme = null)
        {
            AuthenticationResult result =
                await GetAuthenticationResultForUserAsync(
                scopes,
                tenantId,
                userFlow,
                user,
                tokenAcquisitionOptions,
                authenticationScheme).ConfigureAwait(false);
            return result.AccessToken;
        }

        /// <summary>
        /// Used in web APIs (no user interaction).
        /// Replies to the client through the HTTP response by sending a 403 (forbidden) and populating the 'WWW-Authenticate' header so that
        /// the client, in turn, can trigger a user interaction so that the user consents to more scopes.
        /// </summary>
        /// <param name="scopes">Scopes to consent to.</param>
        /// <param name="msalServiceException">The <see cref="MsalUiRequiredException"/> that triggered the challenge.</param>
        /// <param name="httpResponse">The <see cref="HttpResponse"/> to update.</param>
        /// <param name="authenticationScheme">Authentication scheme. If null, will use OpenIdConnectDefault.AuthenticationScheme
        /// if called from a web app, and JwtBearerDefault.AuthenticationScheme if called from a web APIs.</param>
        /// <returns>A <see cref="Task"/> representing the asynchronous operation.</returns>
        public async Task ReplyForbiddenWithWwwAuthenticateHeaderAsync(
            IEnumerable<string> scopes,
            MsalUiRequiredException msalServiceException,
            HttpResponse? httpResponse = null,
            string authenticationScheme = JwtBearerDefaults.AuthenticationScheme)
        {
            // A user interaction is required, but we are in a web API, and therefore, we need to report back to the client through a 'WWW-Authenticate' header https://tools.ietf.org/html/rfc6750#section-3.1
            string proposedAction = Constants.Consent;
            if (msalServiceException.ErrorCode == MsalError.InvalidGrantError && AcceptedTokenVersionMismatch(msalServiceException))
            {
                throw msalServiceException;
            }

            MicrosoftIdentityOptions microsoftIdentityOptions;
            ConfidentialClientApplicationOptions applicationOptions;
            GetOptions(authenticationScheme, out microsoftIdentityOptions, out applicationOptions);

            _application = await GetOrBuildConfidentialClientApplicationAsync(
                microsoftIdentityOptions,
                applicationOptions).ConfigureAwait(false);

            string consentUrl = $"{_application.Authority}/oauth2/v2.0/authorize?client_id={applicationOptions.ClientId}"
                + $"&response_type=code&redirect_uri={_application.AppConfig.RedirectUri}"
                + $"&response_mode=query&scope=offline_access%20{string.Join("%20", scopes)}";

            IDictionary<string, string> parameters = new Dictionary<string, string>()
                {
                    { Constants.ConsentUrl, consentUrl },
                    { Constants.Claims, msalServiceException.Claims },
                    { Constants.Scopes, string.Join(",", scopes) },
                    { Constants.ProposedAction, proposedAction },
                };

            string parameterString = string.Join(", ", parameters.Select(p => $"{p.Key}=\"{p.Value}\""));

            httpResponse ??= CurrentHttpContext?.Response;

            if (httpResponse == null)
            {
                throw new InvalidOperationException(IDWebErrorMessage.HttpContextAndHttpResponseAreNull);
            }

            var headers = httpResponse.Headers;
            httpResponse.StatusCode = (int)HttpStatusCode.Forbidden;

            headers[HeaderNames.WWWAuthenticate] = new StringValues($"{Constants.Bearer} {parameterString}");
        }

        /// <summary>
        /// Removes the account associated with context.HttpContext.User from the MSAL.NET cache.
        /// </summary>
        /// <param name="context">RedirectContext passed-in to a <see cref="OpenIdConnectEvents.OnRedirectToIdentityProviderForSignOut"/>
        /// OpenID Connect event.</param>
        /// <param name="authenticationScheme">Authentication scheme. If null, will use OpenIdConnectDefault.AuthenticationScheme
        /// if called from a web app, and JwtBearerDefault.AuthenticationScheme if called from a web APIs.</param>
        /// <returns>A <see cref="Task"/> that represents a completed account removal operation.</returns>
        public async Task RemoveAccountAsync(RedirectContext context, string authenticationScheme)
        {
            ClaimsPrincipal user = context.HttpContext.User;
            string? userId = user.GetMsalAccountId();
            if (!string.IsNullOrEmpty(userId))
            {
                MicrosoftIdentityOptions microsoftIdentityOptions;
                ConfidentialClientApplicationOptions applicationOptions;
                GetOptions(authenticationScheme, out microsoftIdentityOptions, out applicationOptions);

                IConfidentialClientApplication app = await GetOrBuildConfidentialClientApplicationAsync(
                    microsoftIdentityOptions,
                    applicationOptions).ConfigureAwait(false);

                if (microsoftIdentityOptions.IsB2C)
                {
                    await _tokenCacheProvider.ClearAsync(userId).ConfigureAwait(false);
                }
                else
                {
                    string? identifier = context.HttpContext.User.GetMsalAccountId();
                    IAccount account = await app.GetAccountAsync(identifier).ConfigureAwait(false);

                    if (account != null)
                    {
                        await app.RemoveAsync(account).ConfigureAwait(false);
                        await _tokenCacheProvider.ClearAsync(userId).ConfigureAwait(false);
                    }
                }
            }
        }

        /// <summary>
        /// Creates an MSAL confidential client application, if needed.
        /// </summary>
        internal /* for testing */ async Task<IConfidentialClientApplication> GetOrBuildConfidentialClientApplicationAsync(
            MicrosoftIdentityOptions microsoftIdentityOptions,
            ConfidentialClientApplicationOptions applicationOptions)
        {
            if (_application == null)
            {
                return await BuildConfidentialClientApplicationAsync(microsoftIdentityOptions, applicationOptions).ConfigureAwait(false);
            }

            return _application;
        }

        /// <summary>
        /// Creates an MSAL confidential client application.
        /// </summary>
        private async Task<IConfidentialClientApplication> BuildConfidentialClientApplicationAsync(
            MicrosoftIdentityOptions microsoftIdentityOptions,
            ConfidentialClientApplicationOptions applicationOptions)
        {
            var request = CurrentHttpContext?.Request;
            string? currentUri = null;

            if (!string.IsNullOrEmpty(applicationOptions.RedirectUri))
            {
                currentUri = applicationOptions.RedirectUri;
            }

            if (request != null && string.IsNullOrEmpty(currentUri))
            {
                currentUri = UriHelper.BuildAbsolute(
                    request.Scheme,
                    request.Host,
                    request.PathBase,
                    microsoftIdentityOptions.CallbackPath.Value ?? string.Empty);
            }

            PrepareAuthorityInstanceForMsal(microsoftIdentityOptions, applicationOptions);

            MicrosoftIdentityOptionsValidation.ValidateEitherClientCertificateOrClientSecret(
                 applicationOptions.ClientSecret,
                 microsoftIdentityOptions.ClientCertificates);

            try
            {
                var builder = ConfidentialClientApplicationBuilder
                        .CreateWithApplicationOptions(applicationOptions)
                        .WithHttpClientFactory(_httpClientFactory)
                        .WithLogging(
                            Log,
                            ConvertMicrosoftExtensionsLogLevelToMsal(_logger),
                            enablePiiLogging: applicationOptions.EnablePiiLogging);

                // The redirect URI is not needed for OBO
                if (!string.IsNullOrEmpty(currentUri))
                {
                    builder.WithRedirectUri(currentUri);
                }

                string authority;

                if (microsoftIdentityOptions.IsB2C)
                {
                    authority = $"{applicationOptions.Instance}{ClaimConstants.Tfp}/{microsoftIdentityOptions.Domain}/{microsoftIdentityOptions.DefaultUserFlow}";
                    builder.WithB2CAuthority(authority);
                }
                else
                {
                    authority = $"{applicationOptions.Instance}{applicationOptions.TenantId}/";
                    builder.WithAuthority(authority);
                }

                if (microsoftIdentityOptions.ClientCertificates != null)
                {
                    X509Certificate2? certificate = DefaultCertificateLoader.LoadFirstCertificate(microsoftIdentityOptions.ClientCertificates);
                    builder.WithCertificate(certificate);
                }

                IConfidentialClientApplication app = builder.Build();
                _application = app;
                // Initialize token cache providers
                await _tokenCacheProvider.InitializeAsync(app.AppTokenCache).ConfigureAwait(false);
                await _tokenCacheProvider.InitializeAsync(app.UserTokenCache).ConfigureAwait(false);
                return app;
            }
            catch (Exception ex)
            {
                _logger.LogInformation(
                    ex,
                    IDWebErrorMessage.ExceptionAcquiringTokenForConfidentialClient);
                throw;
            }
        }

        private void PrepareAuthorityInstanceForMsal(
            MicrosoftIdentityOptions microsoftIdentityOptions,
            ConfidentialClientApplicationOptions applicationOptions)
        {
            if (microsoftIdentityOptions.IsB2C && applicationOptions.Instance.EndsWith("/tfp/"))
            {
                applicationOptions.Instance = applicationOptions.Instance.Replace("/tfp/", string.Empty).TrimEnd('/') + "/";
            }
            else
            {
                applicationOptions.Instance = applicationOptions.Instance.TrimEnd('/') + "/";
            }
        }

        private async Task<AuthenticationResult?> GetAuthenticationResultForWebApiToCallDownstreamApiAsync(
           IConfidentialClientApplication application,
           string authority,
           IEnumerable<string> scopes,
           TokenAcquisitionOptions? tokenAcquisitionOptions,
           MicrosoftIdentityOptions microsoftIdentityOptions)
        {
            try
            {
                // In web API, validatedToken will not be null
                JwtSecurityToken? validatedToken = CurrentHttpContext?.GetTokenUsedToCallWebAPI();

                // Case of web APIs: we need to do an on-behalf-of flow, with the token used to call the API
                if (validatedToken != null)
                {
                    // In the case the token is a JWE (encrypted token), we use the decrypted token.
                    string tokenUsedToCallTheWebApi = validatedToken.InnerToken == null ? validatedToken.RawData
                                                : validatedToken.InnerToken.RawData;
                    var builder = application
                                        .AcquireTokenOnBehalfOf(
                                            scopes.Except(_scopesRequestedByMsal),
                                            new UserAssertion(tokenUsedToCallTheWebApi))
                                        .WithSendX5C(microsoftIdentityOptions.SendX5C)
                                        .WithAuthority(authority);

                    if (tokenAcquisitionOptions != null)
                    {
                        builder.WithExtraQueryParameters(tokenAcquisitionOptions.ExtraQueryParameters);
                        builder.WithCorrelationId(tokenAcquisitionOptions.CorrelationId);
                        builder.WithForceRefresh(tokenAcquisitionOptions.ForceRefresh);
                        builder.WithClaims(tokenAcquisitionOptions.Claims);
                    }

                    return await builder.ExecuteAsync()
                                        .ConfigureAwait(false);
                }

                return null;
            }
            catch (MsalUiRequiredException ex)
            {
                _logger.LogInformation(
                    string.Format(
                        CultureInfo.InvariantCulture,
                        LogMessages.ErrorAcquiringTokenForDownstreamWebApi,
                        ex.Message));
                throw;
            }
        }

        /// <summary>
        /// Gets an access token for a downstream API on behalf of the user described by its claimsPrincipal.
        /// </summary>
        /// <param name="application"><see cref="IConfidentialClientApplication"/>.</param>
        /// <param name="claimsPrincipal">Claims principal for the user on behalf of whom to get a token.</param>
        /// <param name="scopes">Scopes for the downstream API to call.</param>
        /// <param name="authority">(optional) Authority based on a specific tenant for which to acquire a token to access the scopes
        /// on behalf of the user described in the claimsPrincipal.</param>
        /// <param name="microsoftIdentityOptions">Options.</param>
        /// <param name="userFlow">Azure AD B2C user flow to target.</param>
        /// <param name="tokenAcquisitionOptions">Options passed-in to create the token acquisition object which calls into MSAL .NET.</param>
        private async Task<AuthenticationResult> GetAuthenticationResultForWebAppWithAccountFromCacheAsync(
            IConfidentialClientApplication application,
            ClaimsPrincipal? claimsPrincipal,
            IEnumerable<string> scopes,
            string? authority,
            MicrosoftIdentityOptions microsoftIdentityOptions,
            string? userFlow = null,
            TokenAcquisitionOptions? tokenAcquisitionOptions = null)
        {
            IAccount? account = null;
            if (microsoftIdentityOptions.IsB2C && !string.IsNullOrEmpty(userFlow))
            {
                string? nameIdentifierId = claimsPrincipal?.GetNameIdentifierId();
                string? utid = claimsPrincipal?.GetHomeTenantId();
                string? b2cAccountIdentifier = string.Format(CultureInfo.InvariantCulture, "{0}-{1}.{2}", nameIdentifierId, userFlow, utid);
                account = await application.GetAccountAsync(b2cAccountIdentifier).ConfigureAwait(false);
            }
            else
            {
                string? accountIdentifier = claimsPrincipal?.GetMsalAccountId();

                if (accountIdentifier != null)
                {
                    account = await application.GetAccountAsync(accountIdentifier).ConfigureAwait(false);
                }
            }

            return await GetAuthenticationResultForWebAppWithAccountFromCacheAsync(
                application,
                account,
                scopes,
                authority,
                microsoftIdentityOptions,
                userFlow,
                tokenAcquisitionOptions).ConfigureAwait(false);
        }

        /// <summary>
        /// Gets an access token for a downstream API on behalf of the user whose account is passed as an argument.
        /// </summary>
        /// <param name="application"><see cref="IConfidentialClientApplication"/>.</param>
        /// <param name="account">User IAccount for which to acquire a token.
        /// See <see cref="Microsoft.Identity.Client.AccountId.Identifier"/>.</param>
        /// <param name="scopes">Scopes for the downstream API to call.</param>
        /// <param name="authority">Authority based on a specific tenant for which to acquire a token to access the scopes
        /// on behalf of the user.</param>
        /// <param name="microsoftIdentityOptions">Options.</param>
        /// <param name="userFlow">Azure AD B2C user flow.</param>
        /// <param name="tokenAcquisitionOptions">Options passed-in to create the token acquisition object which calls into MSAL .NET.</param>
        private async Task<AuthenticationResult> GetAuthenticationResultForWebAppWithAccountFromCacheAsync(
            IConfidentialClientApplication application,
            IAccount? account,
            IEnumerable<string> scopes,
            string? authority,
            MicrosoftIdentityOptions microsoftIdentityOptions,
            string? userFlow = null,
            TokenAcquisitionOptions? tokenAcquisitionOptions = null)
        {
            if (scopes == null)
            {
                throw new ArgumentNullException(nameof(scopes));
            }

            var builder = application
                    .AcquireTokenSilent(scopes.Except(_scopesRequestedByMsal), account)
                    .WithSendX5C(microsoftIdentityOptions.SendX5C);

            if (tokenAcquisitionOptions != null)
            {
                builder.WithExtraQueryParameters(tokenAcquisitionOptions.ExtraQueryParameters);
                builder.WithCorrelationId(tokenAcquisitionOptions.CorrelationId);
                builder.WithForceRefresh(tokenAcquisitionOptions.ForceRefresh);
                builder.WithClaims(tokenAcquisitionOptions.Claims);
            }

            // Acquire an access token as a B2C authority
            if (microsoftIdentityOptions.IsB2C)
            {
                string b2cAuthority = application.Authority.Replace(
                    new Uri(application.Authority).PathAndQuery,
                    $"/{ClaimConstants.Tfp}/{microsoftIdentityOptions.Domain}/{userFlow ?? microsoftIdentityOptions.DefaultUserFlow}");

                builder.WithB2CAuthority(b2cAuthority)
                       .WithSendX5C(microsoftIdentityOptions.SendX5C);
            }
            else
            {
                builder.WithAuthority(authority);
            }

            return await builder.ExecuteAsync()
                                .ConfigureAwait(false);
        }

        private static bool AcceptedTokenVersionMismatch(MsalUiRequiredException msalServiceException)
        {
            // Normally app developers should not make decisions based on the internal AAD code
            // however until the STS sends sub-error codes for this error, this is the only
            // way to distinguish the case.
            // This is subject to change in the future
            return msalServiceException.Message.Contains(
                ErrorCodes.B2CPasswordResetErrorCode,
                StringComparison.InvariantCulture);
        }

        private async Task<ClaimsPrincipal?> GetAuthenticatedUserAsync(ClaimsPrincipal? user)
        {
            if (user == null && _httpContextAccessor.HttpContext?.User != null)
            {
                user = _httpContextAccessor.HttpContext.User;
            }

            if (user == null)
            {
                try
                {
                    AuthenticationStateProvider? authenticationStateProvider =
                        _serviceProvider.GetService(typeof(AuthenticationStateProvider))
                        as AuthenticationStateProvider;

                    if (authenticationStateProvider != null)
                    {
                        // AuthenticationState provider is only available in Blazor
                        AuthenticationState state = await authenticationStateProvider.GetAuthenticationStateAsync().ConfigureAwait(false);
                        user = state.User;
                    }
                }
                catch
                {
                    // do nothing.
                }
            }

            return user;
        }

        internal /*for tests*/ string CreateAuthorityBasedOnTenantIfProvided(
            IConfidentialClientApplication application,
            string? tenant)
        {
            string authority;
            if (!string.IsNullOrEmpty(tenant))
            {
                authority = application.Authority.Replace(new Uri(application.Authority).PathAndQuery, $"/{tenant}/");
            }
            else
            {
                authority = application.Authority;
            }

            return authority;
        }

        private void Log(
          Client.LogLevel level,
          string message,
          bool containsPii)
        {
            switch (level)
            {
                case Client.LogLevel.Error:
                    _logger.LogError(message);
                    break;
                case Client.LogLevel.Warning:
                    _logger.LogWarning(message);
                    break;
                case Client.LogLevel.Info:
                    _logger.LogInformation(message);
                    break;
                case Client.LogLevel.Verbose:
                    _logger.LogInformation(message);
                    break;
                default:
                    break;
            }
        }

        private Client.LogLevel? ConvertMicrosoftExtensionsLogLevelToMsal(ILogger logger)
        {
            if (logger.IsEnabled(Microsoft.Extensions.Logging.LogLevel.Information))
            {
                return Client.LogLevel.Info;
            }
            else if (logger.IsEnabled(Microsoft.Extensions.Logging.LogLevel.Debug)
                || logger.IsEnabled(Microsoft.Extensions.Logging.LogLevel.Trace))
            {
                return Client.LogLevel.Verbose;
            }
            else if (logger.IsEnabled(Microsoft.Extensions.Logging.LogLevel.Warning))
            {
                return Client.LogLevel.Warning;
            }
            else if (logger.IsEnabled(Microsoft.Extensions.Logging.LogLevel.Error)
                || logger.IsEnabled(Microsoft.Extensions.Logging.LogLevel.Critical))
            {
                return Client.LogLevel.Error;
            }
            else
            {
                return null;
            }
        }
    }
}
