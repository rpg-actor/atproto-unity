using System.Reflection;
using NUnit.Framework;
using UnityEngine;

namespace AtProtoUnity.Tests
{
    [Category("Unit")]
    public sealed class AtProtoOAuthTests
    {
        [Test]
        public void LoginDefaultsToAutomaticRedirectMode()
        {
            var gameObject = new GameObject("OAuth redirect mode test");
            try
            {
                var login = gameObject.AddComponent<AtProtoLogin>();
                Assert.That(login.OAuthRedirectMode, Is.EqualTo(AtProtoOAuthRedirectMode.Auto));
            }
            finally
            {
                Object.DestroyImmediate(gameObject);
            }
        }

        [Test]
        public void CallbackRetainsTheAuthorizationServerIssuer()
        {
            var callback = new AtProtoOAuthCallback(
                "code", "state", issuer: "https://auth.example");
            Assert.That(callback.Issuer, Is.EqualTo("https://auth.example"));
        }

        [Test]
        public void HandleResolutionRequiresTheMatchingDidDocumentClaim()
        {
            var claimMethod = typeof(AtProtoClient).GetMethod(
                "DoesDidDocumentClaimHandle",
                BindingFlags.NonPublic | BindingFlags.Static);
            var didMethod = typeof(AtProtoClient).GetMethod(
                "DoesDidDocumentMatch",
                BindingFlags.NonPublic | BindingFlags.Static);
            Assert.That(claimMethod, Is.Not.Null);
            Assert.That(didMethod, Is.Not.Null);

            Assert.That(claimMethod.Invoke(null, new object[]
            {
                new[] { "at://alice.example" }, "alice.example"
            }), Is.EqualTo(true));
            Assert.That(claimMethod.Invoke(null, new object[]
            {
                new[] { "at://mallory.example" }, "alice.example"
            }), Is.EqualTo(false));
            Assert.That(didMethod.Invoke(null, new object[]
            {
                "did:plc:alice", "did:plc:alice"
            }), Is.EqualTo(true));
            Assert.That(didMethod.Invoke(null, new object[]
            {
                "did:plc:mallory", "did:plc:alice"
            }), Is.EqualTo(false));
        }

        [Test]
        public void AutomaticTransportRetriesAreLimitedToSafeMethods()
        {
            var method = typeof(AtProtoClient).GetMethod(
                "CanRetryAfterConnectionError",
                BindingFlags.NonPublic | BindingFlags.Static);
            Assert.That(method, Is.Not.Null);

            Assert.That(method.Invoke(null, new object[] { "GET" }), Is.EqualTo(true));
            Assert.That(method.Invoke(null, new object[] { "HEAD" }), Is.EqualTo(true));
            Assert.That(method.Invoke(null, new object[] { "POST" }), Is.EqualTo(false));
        }

        [TestCase("https://pds.example/xrpc/com.atproto.repo.getRecord", "https://pds.example", true)]
        [TestCase("https://PDS.example:443/xrpc/com.atproto.repo.getRecord", "https://pds.example", true)]
        [TestCase("https://other.example/xrpc/com.atproto.repo.getRecord", "https://pds.example", false)]
        [TestCase("https://pds.example.attacker.invalid/xrpc/test", "https://pds.example", false)]
        [TestCase("http://pds.example/xrpc/test", "https://pds.example", false)]
        [TestCase("https://user@pds.example/xrpc/test", "https://pds.example", false)]
        public void AuthenticatedRequestsAreRestrictedToThePdsOrigin(
            string requestUrl,
            string pdsEndpoint,
            bool expected)
        {
            var method = typeof(AtProtoAuthenticatedClient).GetMethod(
                "IsAllowedRequestUrl",
                BindingFlags.NonPublic | BindingFlags.Static);
            Assert.That(method, Is.Not.Null);
            Assert.That(method.Invoke(null, new object[] { requestUrl, pdsEndpoint }), Is.EqualTo(expected));
        }

        [Test]
        public void OAuthFailureIsNotReportedAsRejectedCredentials()
        {
            var error = new AtProtoRequestError("OAuth login failed. Failed to fetch");

            Assert.That(
                error.GetFriendlyMessage("login confirmation"),
                Does.Contain("browser login could not be completed"));
        }

        [Test]
        public void OAuthUnauthorizedResponseIsNotReportedAsRejectedCredentials()
        {
            var error = new AtProtoRequestError("Token request failed", 401, string.Empty);

            Assert.That(
                error.GetFriendlyMessage("login confirmation"),
                Does.Contain("authorization server rejected the sign-in"));
        }

        [Test]
        public void WebGlCallbackOverrideFailsClearlyInTheEditor()
        {
            var gameObject = new GameObject("OAuth platform override test");
            try
            {
                var login = gameObject.AddComponent<AtProtoLogin>();
                login.OAuthRedirectMode = AtProtoOAuthRedirectMode.WebGlBrowser;
                var result = login.LoginAsync("alice.example").GetAwaiter().GetResult();
                Assert.That(result.Succeeded, Is.False);
                Assert.That(result.Error.Message, Does.Contain("not supported on this platform"));
            }
            finally
            {
                Object.DestroyImmediate(gameObject);
            }
        }
    }
}