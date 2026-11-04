using System;
using System.Collections.Generic;
using System.Threading;
using Newtonsoft.Json;
using UnityEngine;

namespace AtProtoUnity
{
    public enum AtProtoLoginMethod
    {
        OAuth,
        Password
    }

    public enum AtProtoOAuthRedirectMode
    {
        Auto,
        DesktopLoopback,
        WebGlBrowser
    }

    public enum AtProtoLoginState
    {
        SignedOut,
        Authorizing,
        Authenticated,
        Refreshing,
        Failed
    }

    // Everything needed to make authenticated PDS requests.
    public sealed class AtProtoAuthSession
    {
        public string Did { get; }
        public string Handle { get; }
        public string PdsEndpoint { get; }
        public string AuthorizationServer { get; }
        public string TokenEndpoint { get; }
        public string ClientId { get; }
        public string Scope { get; }
        public string Resource { get; }
        public string AccessToken { get; }
        public string RefreshToken { get; }
        public DateTime AccessTokenExpiresAtUtc { get; }
        public AtProtoLoginMethod LoginMethod { get; }

        public AtProtoAuthSession(
            string did,
            string handle,
            string pdsEndpoint,
            string authorizationServer,
            string tokenEndpoint,
            string clientId,
            string scope,
            string resource,
            string accessToken,
            string refreshToken,
            DateTime accessTokenExpiresAtUtc,
            AtProtoLoginMethod loginMethod = AtProtoLoginMethod.OAuth)
        {
            Did = did;
            Handle = handle;
            PdsEndpoint = pdsEndpoint;
            AuthorizationServer = authorizationServer;
            TokenEndpoint = tokenEndpoint;
            ClientId = clientId;
            Scope = scope;
            Resource = resource;
            AccessToken = accessToken;
            RefreshToken = refreshToken;
            AccessTokenExpiresAtUtc = accessTokenExpiresAtUtc;
            LoginMethod = loginMethod;
        }

        public bool IsAccessTokenExpired(TimeSpan skew)
        {
            return DateTime.UtcNow + skew >= AccessTokenExpiresAtUtc;
        }

        public AtProtoIdentity ToIdentity()
        {
            return new AtProtoIdentity(Handle, Did, PdsEndpoint);
        }
    }

    public sealed class AtProtoOAuthMetadata
    {
        [JsonProperty("issuer")] public string Issuer { get; set; }
        [JsonProperty("authorization_endpoint")] public string AuthorizationEndpoint { get; set; }
        [JsonProperty("token_endpoint")] public string TokenEndpoint { get; set; }
        [JsonProperty("pushed_authorization_request_endpoint")] public string PushedAuthorizationRequestEndpoint { get; set; }
        [JsonProperty("code_challenge_methods_supported")] public List<string> CodeChallengeMethodsSupported { get; set; }
    }

    public sealed class AtProtoOAuthCallback
    {
        public string Code { get; }
        public string State { get; }
        public string Issuer { get; }
        public string Error { get; }
        public string ErrorDescription { get; }

        public bool Succeeded => !string.IsNullOrEmpty(Code) && string.IsNullOrEmpty(Error);

        public AtProtoOAuthCallback(
            string code,
            string state,
            string error = null,
            string errorDescription = null,
            string issuer = null)
        {
            Code = code;
            State = state;
            Error = error;
            ErrorDescription = errorDescription;
            Issuer = issuer;
        }
    }

    // Desktop uses a loopback listener; other platforms can supply an app link or hosted callback.
    public interface IAtProtoOAuthRedirectHandler : IDisposable
    {
        string RedirectUri { get; }
        Awaitable<AtProtoOAuthCallback> WaitForCallbackAsync(
            TimeSpan timeout,
            CancellationToken cancellationToken = default);
    }

    public interface IAtProtoOAuthRedirectHandlerFactory
    {
        IAtProtoOAuthRedirectHandler Create();
    }

    public interface IAtProtoOAuthAuthorizationLauncher
    {
        void OpenAuthorizationUrl(string url);
    }
}