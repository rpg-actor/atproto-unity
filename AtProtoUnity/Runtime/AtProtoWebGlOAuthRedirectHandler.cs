using System;
using System.Net;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using Newtonsoft.Json;
using UnityEngine;

namespace AtProtoUnity
{
#if UNITY_WEBGL && !UNITY_EDITOR
    public sealed class WebGlAtProtoOAuthRedirectHandlerFactory : IAtProtoOAuthRedirectHandlerFactory
    {
        readonly string redirectUri;

        public WebGlAtProtoOAuthRedirectHandlerFactory(string redirectUri)
        {
            this.redirectUri = redirectUri;
        }

        public IAtProtoOAuthRedirectHandler Create()
        {
            return new WebGlAtProtoOAuthRedirectHandler(redirectUri);
        }
    }

    sealed class WebGlAtProtoOAuthRedirectHandler : IAtProtoOAuthRedirectHandler, IAtProtoOAuthAuthorizationLauncher
    {
        const int CallbackCapacity = 8192;
        readonly byte[] callbackBuffer = new byte[CallbackCapacity];

        public string RedirectUri { get; }

        public WebGlAtProtoOAuthRedirectHandler(string redirectUri)
        {
            if (string.IsNullOrWhiteSpace(redirectUri))
            {
                if (!Uri.TryCreate(Application.absoluteURL, UriKind.Absolute, out var pageUri))
                    throw new InvalidOperationException("The WebGL page URL could not be resolved for the OAuth callback.");
                redirectUri = new Uri(pageUri, "AtProtoOAuthCallback.html").ToString();
            }
            if (!IsValidRedirectUri(redirectUri))
            {
                if (Uri.TryCreate(redirectUri, UriKind.Absolute, out var invalidUri)
                    && string.Equals(invalidUri.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase)
                    && string.Equals(invalidUri.Host, "localhost", StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException(
                        "For local WebGL OAuth, open the game at http://127.0.0.1:<same-port>/ instead of localhost. The OAuth callback requires a loopback IP address.");
                throw new InvalidOperationException(
                    "The WebGL OAuth redirect URI must use HTTPS, or a loopback IP address for local development.");
            }

            RedirectUri = redirectUri;
            var result = AtProtoOAuth_OpenPopup(redirectUri);
            if (result < 0)
                throw new InvalidOperationException("The WebGL OAuth redirect URI must have the same origin as the game.");
            if (result == 0)
                throw new InvalidOperationException("The OAuth popup was blocked. Allow popups for this game and try again.");
        }

        public void OpenAuthorizationUrl(string url)
        {
            if (AtProtoOAuth_NavigatePopup(url) == 0)
                throw new InvalidOperationException("The OAuth popup was closed before authorization started.");
        }

        public async Awaitable<AtProtoOAuthCallback> WaitForCallbackAsync(
            TimeSpan timeout,
            CancellationToken cancellationToken = default)
        {
            var deadline = DateTime.UtcNow + timeout;
            while (DateTime.UtcNow < deadline)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var length = AtProtoOAuth_TakeCallback(callbackBuffer, callbackBuffer.Length);
                if (length < 0)
                    throw new InvalidOperationException("The OAuth callback response was too large.");
                if (length > 0)
                {
                    var message = JsonConvert.DeserializeObject<CallbackMessage>(
                        Encoding.UTF8.GetString(callbackBuffer, 0, length));
                    if (message == null || message.Type != "atproto-oauth-callback")
                        throw new InvalidOperationException("The OAuth callback response was invalid.");
                    return new AtProtoOAuthCallback(
                        message.Code,
                        message.State,
                        message.Error,
                        message.ErrorDescription,
                        message.Issuer);
                }

                await Awaitable.WaitForSecondsAsync(0.05f, cancellationToken);
            }

            throw new TimeoutException("The OAuth callback was not received before the timeout.");
        }

        public void Dispose()
        {
            AtProtoOAuth_ClosePopup();
        }

        static bool IsValidRedirectUri(string value)
        {
            if (AtProtoClient.IsHttpsUrl(value))
                return true;
            return Uri.TryCreate(value, UriKind.Absolute, out var uri)
                && string.Equals(uri.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase)
                && IPAddress.TryParse(uri.Host, out var address)
                && IPAddress.IsLoopback(address);
        }

        [DllImport("__Internal")]
        static extern int AtProtoOAuth_OpenPopup(string redirectUri);

        [DllImport("__Internal")]
        static extern int AtProtoOAuth_NavigatePopup(string url);

        [DllImport("__Internal")]
        static extern int AtProtoOAuth_TakeCallback([Out] byte[] buffer, int capacity);

        [DllImport("__Internal")]
        static extern void AtProtoOAuth_ClosePopup();

        sealed class CallbackMessage
        {
            [JsonProperty("type")] public string Type { get; set; }
            [JsonProperty("code")] public string Code { get; set; }
            [JsonProperty("state")] public string State { get; set; }
            [JsonProperty("iss")] public string Issuer { get; set; }
            [JsonProperty("error")] public string Error { get; set; }
            [JsonProperty("error_description")] public string ErrorDescription { get; set; }
        }
    }
#endif
}