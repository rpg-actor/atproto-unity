using System;
using System.Collections.Generic;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using UnityEngine;

namespace AtProtoUnity
{
    // Sign-in component. UI can subscribe to StateChanged and call Login or LoginAsync.
    public sealed class AtProtoLogin : MonoBehaviour
    {
        static readonly TimeSpan AccessTokenRefreshSkew = TimeSpan.FromMinutes(1);
        static readonly TimeSpan CallbackTimeout = TimeSpan.FromMinutes(5);

        [SerializeField] string handle;
        [SerializeField] string clientId = "http://localhost";
        [SerializeField] string resource;
        [SerializeField] string identityEndpoint = AtProtoClient.DefaultIdentityEndpoint;
        [SerializeField] string plcDirectoryEndpoint = AtProtoClient.DefaultPlcDirectoryEndpoint;
        [SerializeField] AtProtoLoginMethod loginMethod = AtProtoLoginMethod.OAuth;
        [SerializeField] List<AtProtoRecordPermission> recordPermissions = new();
        [SerializeField] AtProtoBlobPermission blobPermission = new();

        AtProtoClient client;
        AtProtoAuthenticatedClient authenticatedClient;
        AtProtoDpopKeyPair dpopKeyPair;
        CancellationTokenSource operationCancellation;
        TaskCompletionSource<AtProtoResult<AtProtoAuthSession>> refreshTask;
        IAtProtoOAuthRedirectHandlerFactory redirectHandlerFactory;
        AtProtoAuthSession session;
        AtProtoLoginState state = AtProtoLoginState.SignedOut;
        AtProtoRequestError lastError;
        int sessionVersion;

        public string Handle
        {
            get => handle;
            set => handle = value;
        }

        public string ClientId
        {
            get => clientId;
            set => clientId = value;
        }

        public string Scope => AtProtoOAuthScopes.BuildScope(recordPermissions, blobPermission);

        public IReadOnlyList<AtProtoRecordPermission> RecordPermissions => recordPermissions;
        public AtProtoBlobPermission BlobPermission => blobPermission;

        public string Resource
        {
            get => resource;
            set => resource = value;
        }

        public AtProtoLoginMethod LoginMethod
        {
            get => loginMethod;
            set => loginMethod = value;
        }

        public AtProtoLoginState State => state;
        public AtProtoAuthSession Session => session;
        public AtProtoIdentity Identity => session?.ToIdentity();
        public AtProtoAuthenticatedClient AuthenticatedClient => authenticatedClient;
        public AtProtoRequestError LastError => lastError;
        public AtProtoClient Client
        {
            get
            {
                EnsureRuntime();
                return client;
            }
        }

        public IAtProtoOAuthRedirectHandlerFactory RedirectHandlerFactory
        {
            get
            {
                EnsureRuntime();
                return redirectHandlerFactory;
            }
            set => redirectHandlerFactory = value;
        }

        public event Action<AtProtoLoginState> StateChanged;
        public event Action<AtProtoAuthSession> SessionChanged;

        void Awake()
        {
            EnsureRuntime();
        }

        public void Login()
        {
            Begin(cancellationToken => LoginAsync(null, cancellationToken));
        }

        public Awaitable<AtProtoResult<AtProtoAuthSession>> LoginAsync(
            string requestedHandle = null,
            CancellationToken cancellationToken = default)
        {
            return LoginAsync(LoginMethod, requestedHandle, null, cancellationToken);
        }

        public Awaitable<AtProtoResult<AtProtoAuthSession>> LoginAsync(
            AtProtoLoginMethod method,
            string requestedHandle,
            string secret,
            CancellationToken cancellationToken = default)
        {
            return method == AtProtoLoginMethod.OAuth
                ? LoginOAuthAsync(requestedHandle, cancellationToken)
                : LoginWithSecretAsync(method, requestedHandle, secret, cancellationToken);
        }

        async Awaitable<AtProtoResult<AtProtoAuthSession>> LoginOAuthAsync(
            string requestedHandle,
            CancellationToken cancellationToken = default)
        {
            EnsureRuntime();
            var loginHandle = string.IsNullOrWhiteSpace(requestedHandle)
                ? handle
                : requestedHandle;
            if (string.IsNullOrWhiteSpace(loginHandle))
                return Fail(new AtProtoRequestError("A handle is required to begin login."));

            SetState(AtProtoLoginState.Authorizing);
            lastError = null;
            IAtProtoOAuthRedirectHandler redirectHandler = null;
            AtProtoDpopKeyPair loginKeyPair = null;
            var focusWindow = IntPtr.Zero;
            try
            {
                var identityResult = await client.ResolveHandleAsync(loginHandle, cancellationToken);
                if (!identityResult.Succeeded)
                    return Fail(identityResult.Error);

                var identity = identityResult.Value;
                var oauthResource = string.IsNullOrWhiteSpace(resource)
                    ? identity.PdsEndpoint
                    : resource.Trim();
                var metadataResult = await DiscoverMetadataAsync(
                    identity.PdsEndpoint,
                    cancellationToken);
                if (!metadataResult.Succeeded)
                    return Fail(metadataResult.Error);
                var metadata = metadataResult.Value;

                ValidateMetadata(metadata);
                redirectHandler = redirectHandlerFactory.Create();
                loginKeyPair = AtProtoOAuthCrypto.CreateDpopKeyPair();
                var pkce = AtProtoOAuthCrypto.CreatePkceChallenge();
                var stateToken = AtProtoOAuthCrypto.CreateRandomToken(16);
                var effectiveClientId = BuildClientId(
                    clientId,
                    redirectHandler.RedirectUri,
                    Scope);

                var nonce = new DpopNonceState();
                var authorizationUrl = await BuildAuthorizationUrlAsync(
                    metadata,
                    effectiveClientId,
                    redirectHandler.RedirectUri,
                    pkce,
                    stateToken,
                    identity.Handle ?? loginHandle,
                    oauthResource,
                    nonce,
                    loginKeyPair,
                    cancellationToken);
                if (!authorizationUrl.Succeeded)
                    return Fail(authorizationUrl.Error);

                focusWindow = AtProtoWindowFocus.Capture();
                Application.OpenURL(authorizationUrl.Value);
                var callback = await redirectHandler.WaitForCallbackAsync(
                    CallbackTimeout,
                    cancellationToken);
                if (!callback.Succeeded)
                    return Fail(new AtProtoRequestError(
                        string.IsNullOrEmpty(callback.ErrorDescription)
                            ? "OAuth authorization was denied."
                            : callback.ErrorDescription));
                if (!string.Equals(callback.State, stateToken, StringComparison.Ordinal))
                    return Fail(new AtProtoRequestError(
                        "The OAuth state did not match the login request."));

                var tokenResult = await ExchangeCodeAsync(
                    metadata,
                    effectiveClientId,
                    redirectHandler.RedirectUri,
                    callback.Code,
                    pkce.Verifier,
                    identity,
                    oauthResource,
                    loginKeyPair,
                    nonce,
                    cancellationToken);
                if (!tokenResult.Succeeded)
                    return Fail(tokenResult.Error);

                ActivateSession(tokenResult.Value, loginKeyPair);
                loginKeyPair = null;
                return AtProtoResult<AtProtoAuthSession>.Success(session);
            }
            catch (OperationCanceledException)
            {
                SetState(session == null ? AtProtoLoginState.SignedOut : AtProtoLoginState.Authenticated);
                throw;
            }
            catch (Exception exception)
            {
                return Fail(new AtProtoRequestError("OAuth login failed.", exception));
            }
            finally
            {
                AtProtoWindowFocus.Restore(focusWindow);
                redirectHandler?.Dispose();
                loginKeyPair?.Dispose();
            }
        }

        async Awaitable<AtProtoResult<AtProtoAuthSession>> LoginWithSecretAsync(
            AtProtoLoginMethod method,
            string requestedHandle,
            string secret,
            CancellationToken cancellationToken)
        {
            EnsureRuntime();
            var loginHandle = string.IsNullOrWhiteSpace(requestedHandle)
                ? handle
                : requestedHandle;
            if (string.IsNullOrWhiteSpace(loginHandle))
                return Fail(new AtProtoRequestError("A handle is required to begin login."));
            if (string.IsNullOrWhiteSpace(secret))
                return Fail(new AtProtoRequestError(
                    "A password is required to begin login."));

            SetState(AtProtoLoginState.Authorizing);
            lastError = null;
            try
            {
                var identityResult = await client.ResolveHandleAsync(loginHandle, cancellationToken);
                if (!identityResult.Succeeded)
                    return Fail(identityResult.Error);

                var identity = identityResult.Value;
                var sessionEndpoint = AtProtoClient.BuildXrpcUrl(
                    identity.PdsEndpoint,
                    "com.atproto.server.createSession",
                    new Dictionary<string, string>());
                var response = await client.SendAsync(
                    "POST",
                    sessionEndpoint,
                    Encoding.UTF8.GetBytes(JsonConvert.SerializeObject(
                        new CreateSessionRequest
                        {
                            Identifier = loginHandle.Trim().TrimStart('@'),
                            Password = secret
                        })),
                    "application/json",
                    cancellationToken: cancellationToken);
                if (!response.Succeeded)
                    return Fail(response.Error);

                var sessionResult = ParseSessionResponse(
                    response.Value.Body,
                    method,
                    identity);
                if (!sessionResult.Succeeded)
                    return Fail(sessionResult.Error);

                ActivateSession(sessionResult.Value, null);
                return AtProtoResult<AtProtoAuthSession>.Success(session);
            }
            catch (OperationCanceledException)
            {
                SetState(session == null ? AtProtoLoginState.SignedOut : AtProtoLoginState.Authenticated);
                throw;
            }
            catch (Exception exception)
            {
                return Fail(new AtProtoRequestError(
                    "Password login failed.",
                    exception));
            }
        }

        public async Awaitable<AtProtoResult<AtProtoAuthSession>> RefreshAsync(
            CancellationToken cancellationToken = default)
        {
            EnsureRuntime();
            if (session == null
                || (session.LoginMethod == AtProtoLoginMethod.OAuth && dpopKeyPair == null)
                || string.IsNullOrEmpty(session.RefreshToken))
                return Fail(new AtProtoRequestError("There is no authenticated AT Protocol session to refresh."));

            var existingTask = refreshTask;
            if (existingTask != null)
                return await WaitForRefreshAsync(existingTask.Task, cancellationToken);

            var createdTask = new TaskCompletionSource<AtProtoResult<AtProtoAuthSession>>();
            var runningTask = Interlocked.CompareExchange(ref refreshTask, createdTask, null);
            if (runningTask != null)
                return await WaitForRefreshAsync(runningTask.Task, cancellationToken);

            CompleteRefresh(createdTask);
            return await WaitForRefreshAsync(createdTask.Task, cancellationToken);
        }

        public void Logout()
        {
            CancelOperation();
            sessionVersion++;
            authenticatedClient = null;
            dpopKeyPair?.Dispose();
            dpopKeyPair = null;
            session = null;
            lastError = null;
            SetState(AtProtoLoginState.SignedOut);
            SessionChanged?.Invoke(null);
        }

        void OnDestroy()
        {
            CancelOperation();
            dpopKeyPair?.Dispose();
        }

        async void CompleteRefresh(TaskCompletionSource<AtProtoResult<AtProtoAuthSession>> completion)
        {
            AtProtoResult<AtProtoAuthSession> result;
            try
            {
                result = await RefreshCoreAsync(CancellationToken.None);
            }
            catch (Exception exception)
            {
                result = AtProtoResult<AtProtoAuthSession>.Failure(
                    new AtProtoRequestError("AT Protocol token refresh failed.", exception));
                if (session != null)
                {
                    SetState(AtProtoLoginState.Failed);
                    lastError = result.Error;
                }
            }
            completion.TrySetResult(result);
            Interlocked.CompareExchange(ref refreshTask, null, completion);
        }

        async Awaitable<AtProtoResult<AtProtoAuthSession>> RefreshCoreAsync(
            CancellationToken cancellationToken)
        {
            SetState(AtProtoLoginState.Refreshing);
            var refreshVersion = sessionVersion;
            var refreshSession = session;
            if (session.LoginMethod != AtProtoLoginMethod.OAuth)
                return await RefreshDirectSessionCoreAsync(
                    refreshSession,
                    refreshVersion,
                    cancellationToken);

            var form = new Dictionary<string, string>
            {
                ["grant_type"] = "refresh_token",
                ["refresh_token"] = session.RefreshToken,
                ["client_id"] = session.ClientId
            };
            if (!string.IsNullOrEmpty(session.Resource))
                form["resource"] = session.Resource;

            var result = await SendDpopFormAsync(
                session.TokenEndpoint,
                form,
                new DpopNonceState(),
                dpopKeyPair,
                cancellationToken);
            if (IsSuperseded(refreshVersion, refreshSession))
                return SupersededRefreshFailure();
            if (!result.Succeeded)
            {
                SetState(AtProtoLoginState.Failed);
                lastError = result.Error;
                return AtProtoResult<AtProtoAuthSession>.Failure(result.Error);
            }

            var tokenResult = ParseTokenResponse(
                result.Value.Body,
                session,
                session.ToIdentity());
            if (!tokenResult.Succeeded)
            {
                SetState(AtProtoLoginState.Failed);
                lastError = tokenResult.Error;
                return tokenResult;
            }

            ActivateSession(tokenResult.Value, dpopKeyPair);
            return AtProtoResult<AtProtoAuthSession>.Success(session);
        }

        bool IsSuperseded(int refreshVersion, AtProtoAuthSession refreshSession)
        {
            return refreshVersion != sessionVersion || !ReferenceEquals(refreshSession, session);
        }

        static AtProtoResult<AtProtoAuthSession> SupersededRefreshFailure()
        {
            return AtProtoResult<AtProtoAuthSession>.Failure(
                new AtProtoRequestError("The authenticated session changed during token refresh."));
        }

        async Awaitable<AtProtoResult<AtProtoAuthSession>> RefreshDirectSessionCoreAsync(
            AtProtoAuthSession refreshSession,
            int refreshVersion,
            CancellationToken cancellationToken)
        {
            var response = await client.SendAsync(
                "POST",
                refreshSession.TokenEndpoint,
                headers: new Dictionary<string, string>
                {
                    ["Authorization"] = "Bearer " + refreshSession.RefreshToken
                },
                cancellationToken: cancellationToken);
            if (!response.Succeeded)
            {
                if (!IsSuperseded(refreshVersion, refreshSession))
                {
                    SetState(AtProtoLoginState.Failed);
                    lastError = response.Error;
                }
                return AtProtoResult<AtProtoAuthSession>.Failure(response.Error);
            }

            if (IsSuperseded(refreshVersion, refreshSession))
                return SupersededRefreshFailure();

            var tokenResult = ParseSessionResponse(
                response.Value.Body,
                refreshSession.LoginMethod,
                refreshSession.ToIdentity());
            if (!tokenResult.Succeeded)
            {
                SetState(AtProtoLoginState.Failed);
                lastError = tokenResult.Error;
                return tokenResult;
            }

            ActivateSession(tokenResult.Value, null);
            return AtProtoResult<AtProtoAuthSession>.Success(session);
        }

        async Awaitable<AtProtoResult<AtProtoOAuthMetadata>> DiscoverMetadataAsync(
            string pdsEndpoint,
            CancellationToken cancellationToken)
        {
            var protectedResource = await client.SendAsync(
                "GET",
                pdsEndpoint.TrimEnd('/') + "/.well-known/oauth-protected-resource",
                cancellationToken: cancellationToken);
            if (!protectedResource.Succeeded)
                return AtProtoResult<AtProtoOAuthMetadata>.Failure(protectedResource.Error);

            ProtectedResourceMetadata resource;
            try
            {
                resource = JsonConvert.DeserializeObject<ProtectedResourceMetadata>(
                    protectedResource.Value.Body);
            }
            catch (Exception exception)
            {
                return AtProtoResult<AtProtoOAuthMetadata>.Failure(
                    new AtProtoRequestError("The OAuth protected-resource metadata was invalid.", exception));
            }

            if (resource?.AuthorizationServers == null || resource.AuthorizationServers.Count == 0)
                return AtProtoResult<AtProtoOAuthMetadata>.Failure(
                    new AtProtoRequestError("The PDS did not advertise an authorization server."));

            var authorizationServer = resource.AuthorizationServers[0].TrimEnd('/');
            if (!AtProtoClient.IsHttpsUrl(authorizationServer))
                return AtProtoResult<AtProtoOAuthMetadata>.Failure(
                    new AtProtoRequestError("The OAuth authorization server must use HTTPS."));
            var metadataResponse = await client.SendAsync(
                "GET",
                authorizationServer + "/.well-known/oauth-authorization-server",
                cancellationToken: cancellationToken);
            if (!metadataResponse.Succeeded)
                return AtProtoResult<AtProtoOAuthMetadata>.Failure(metadataResponse.Error);

            try
            {
                var metadata = JsonConvert.DeserializeObject<AtProtoOAuthMetadata>(
                    metadataResponse.Value.Body);
                metadata.Issuer = authorizationServer;
                return AtProtoResult<AtProtoOAuthMetadata>.Success(metadata);
            }
            catch (Exception exception)
            {
                return AtProtoResult<AtProtoOAuthMetadata>.Failure(
                    new AtProtoRequestError("The OAuth authorization-server metadata was invalid.", exception));
            }
        }

        async Awaitable<AtProtoResult<string>> BuildAuthorizationUrlAsync(
            AtProtoOAuthMetadata metadata,
            string effectiveClientId,
            string redirectUri,
            AtProtoPkceChallenge pkce,
            string stateToken,
            string loginHint,
            string requestedResource,
            DpopNonceState nonce,
            AtProtoDpopKeyPair keyPair,
            CancellationToken cancellationToken)
        {
            var parameters = new Dictionary<string, string>
            {
                ["response_type"] = "code",
                ["client_id"] = effectiveClientId,
                ["redirect_uri"] = redirectUri,
                ["scope"] = Scope,
                ["state"] = stateToken,
                ["code_challenge"] = pkce.Challenge,
                ["code_challenge_method"] = "S256"
            };
            if (!string.IsNullOrWhiteSpace(loginHint))
                parameters["login_hint"] = loginHint.Trim();
            if (!string.IsNullOrWhiteSpace(requestedResource))
                parameters["resource"] = requestedResource;

            if (!string.IsNullOrEmpty(metadata.PushedAuthorizationRequestEndpoint))
            {
                var parResult = await SendDpopFormAsync(
                    metadata.PushedAuthorizationRequestEndpoint,
                    parameters,
                    nonce,
                    keyPair,
                    cancellationToken);
                if (!parResult.Succeeded)
                    return AtProtoResult<string>.Failure(parResult.Error);

                PushedAuthorizationResponse pushed;
                try
                {
                    pushed = JsonConvert.DeserializeObject<PushedAuthorizationResponse>(
                        parResult.Value.Body);
                }
                catch (Exception exception)
                {
                    return AtProtoResult<string>.Failure(
                        new AtProtoRequestError("The pushed authorization response was invalid.", exception));
                }

                if (string.IsNullOrEmpty(pushed?.RequestUri))
                    return AtProtoResult<string>.Failure(
                        new AtProtoRequestError("The pushed authorization response had no request URI."));
                return AtProtoResult<string>.Success(
                    metadata.AuthorizationEndpoint + "?client_id="
                    + Uri.EscapeDataString(effectiveClientId)
                    + "&request_uri=" + Uri.EscapeDataString(pushed.RequestUri));
            }

            return AtProtoResult<string>.Success(
                metadata.AuthorizationEndpoint + "?" + FormEncode(parameters));
        }

        async Awaitable<AtProtoResult<AtProtoAuthSession>> ExchangeCodeAsync(
            AtProtoOAuthMetadata metadata,
            string effectiveClientId,
            string redirectUri,
            string code,
            string verifier,
            AtProtoIdentity identity,
            string requestedResource,
            AtProtoDpopKeyPair keyPair,
            DpopNonceState nonce,
            CancellationToken cancellationToken)
        {
            var form = new Dictionary<string, string>
            {
                ["grant_type"] = "authorization_code",
                ["code"] = code,
                ["redirect_uri"] = redirectUri,
                ["client_id"] = effectiveClientId,
                ["code_verifier"] = verifier
            };
            if (!string.IsNullOrEmpty(requestedResource))
                form["resource"] = requestedResource;

            var response = await SendDpopFormAsync(
                metadata.TokenEndpoint,
                form,
                nonce,
                keyPair,
                cancellationToken);
            if (!response.Succeeded)
                return AtProtoResult<AtProtoAuthSession>.Failure(response.Error);

            return ParseTokenResponse(
                response.Value.Body,
                new AtProtoAuthSession(
                    identity.Did,
                    identity.Handle,
                    identity.PdsEndpoint,
                    metadata.Issuer,
                    metadata.TokenEndpoint,
                    effectiveClientId,
                    Scope,
                    requestedResource,
                    null,
                    null,
                    DateTime.MinValue),
                identity);
        }

        async Awaitable<AtProtoResult<AtProtoHttpResponse>> SendDpopFormAsync(
            string url,
            IReadOnlyDictionary<string, string> form,
            DpopNonceState nonce,
            AtProtoDpopKeyPair keyPair,
            CancellationToken cancellationToken)
        {
            for (var attempt = 0; attempt < 2; attempt++)
            {
                var headers = new Dictionary<string, string>
                {
                    ["DPoP"] = AtProtoOAuthCrypto.CreateDpopProof(
                        keyPair,
                        "POST",
                        url,
                        nonce.Value)
                };
                var result = await client.SendAsync(
                    "POST",
                    url,
                    Encoding.UTF8.GetBytes(FormEncode(form)),
                    "application/x-www-form-urlencoded",
                    headers,
                    cancellationToken);
                var responseNonce = result.Succeeded
                    ? result.Value.GetHeader("dpop-nonce")
                    : result.Error.GetResponseHeader("dpop-nonce");
                if (!string.IsNullOrEmpty(responseNonce))
                    nonce.Value = responseNonce;
                if (result.Succeeded || string.IsNullOrEmpty(responseNonce) || attempt > 0)
                    return result;
            }

            throw new InvalidOperationException("The OAuth DPoP retry loop did not complete.");
        }

        AtProtoResult<AtProtoAuthSession> ParseTokenResponse(
            string body,
            AtProtoAuthSession previous,
            AtProtoIdentity identity)
        {
            TokenResponse token;
            try
            {
                token = JsonConvert.DeserializeObject<TokenResponse>(body);
            }
            catch (Exception exception)
            {
                return AtProtoResult<AtProtoAuthSession>.Failure(
                    new AtProtoRequestError("The OAuth token response was invalid.", exception));
            }

            if (token == null || string.IsNullOrEmpty(token.AccessToken))
                return AtProtoResult<AtProtoAuthSession>.Failure(
                    new AtProtoRequestError("The OAuth token response did not contain an access token."));

            var expiresIn = token.ExpiresIn > 0 ? token.ExpiresIn : 3600;
            return AtProtoResult<AtProtoAuthSession>.Success(
                new AtProtoAuthSession(
                    string.IsNullOrEmpty(token.Subject) ? identity.Did : token.Subject,
                    identity.Handle,
                    identity.PdsEndpoint,
                    previous.AuthorizationServer,
                    previous.TokenEndpoint,
                    previous.ClientId,
                    string.IsNullOrEmpty(token.Scope) ? previous.Scope : token.Scope,
                    previous.Resource,
                    token.AccessToken,
                    string.IsNullOrEmpty(token.RefreshToken) ? previous.RefreshToken : token.RefreshToken,
                    DateTime.UtcNow.AddSeconds(expiresIn)));
        }

        AtProtoResult<AtProtoAuthSession> ParseSessionResponse(
            string body,
            AtProtoLoginMethod method,
            AtProtoIdentity identity)
        {
            SessionResponse response;
            try
            {
                response = JsonConvert.DeserializeObject<SessionResponse>(body);
            }
            catch (Exception exception)
            {
                return AtProtoResult<AtProtoAuthSession>.Failure(
                    new AtProtoRequestError("The password session response was invalid.", exception));
            }

            if (response == null
                || string.IsNullOrEmpty(response.AccessJwt)
                || string.IsNullOrEmpty(response.RefreshJwt))
                return AtProtoResult<AtProtoAuthSession>.Failure(
                    new AtProtoRequestError(
                        "The password session response did not contain both session tokens."));

            var pdsEndpoint = identity.PdsEndpoint.TrimEnd('/');
            var refreshEndpoint = AtProtoClient.BuildXrpcUrl(
                pdsEndpoint,
                "com.atproto.server.refreshSession",
                new Dictionary<string, string>());
            var expiresIn = response.ExpiresIn > 0 ? response.ExpiresIn : 3600;
            return AtProtoResult<AtProtoAuthSession>.Success(
                new AtProtoAuthSession(
                    string.IsNullOrEmpty(response.Did) ? identity.Did : response.Did,
                    string.IsNullOrEmpty(response.Handle) ? identity.Handle : response.Handle,
                    pdsEndpoint,
                    pdsEndpoint,
                    refreshEndpoint,
                    null,
                    null,
                    null,
                    response.AccessJwt,
                    response.RefreshJwt,
                    DateTime.UtcNow.AddSeconds(expiresIn),
                    method));
        }

        void ActivateSession(AtProtoAuthSession value, AtProtoDpopKeyPair keyPair)
        {
            authenticatedClient = new AtProtoAuthenticatedClient(
                client,
                value,
                keyPair,
                AccessTokenRefreshSkew,
                RefreshAsync);
            if (!ReferenceEquals(dpopKeyPair, keyPair))
                dpopKeyPair?.Dispose();
            dpopKeyPair = keyPair;
            session = value;
            handle = value.Handle;
            lastError = null;
            SetState(AtProtoLoginState.Authenticated);
            SessionChanged?.Invoke(value);
        }

        AtProtoResult<AtProtoAuthSession> Fail(AtProtoRequestError error)
        {
            lastError = error;
            SetState(AtProtoLoginState.Failed);
            return AtProtoResult<AtProtoAuthSession>.Failure(error);
        }

        void EnsureRuntime()
        {
            if (client == null
                || client.IdentityEndpoint != identityEndpoint
                || client.PlcDirectoryEndpoint != plcDirectoryEndpoint)
            {
                client = new AtProtoClient(identityEndpoint, plcDirectoryEndpoint);
            }

            redirectHandlerFactory ??= new LoopbackAtProtoOAuthRedirectHandlerFactory();
        }

        void Begin(Func<CancellationToken, Awaitable<AtProtoResult<AtProtoAuthSession>>> operationFactory)
        {
            CancelOperation();
            operationCancellation = new CancellationTokenSource();
            Run(operationFactory, operationCancellation);
        }

        async void Run(
            Func<CancellationToken, Awaitable<AtProtoResult<AtProtoAuthSession>>> operationFactory,
            CancellationTokenSource cancellation)
        {
            try
            {
                await operationFactory(cancellation.Token);
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception exception)
            {
                Debug.LogException(exception, this);
            }
            finally
            {
                if (ReferenceEquals(operationCancellation, cancellation))
                {
                    operationCancellation = null;
                    cancellation.Dispose();
                }
            }
        }

        void CancelOperation()
        {
            var cancellation = operationCancellation;
            operationCancellation = null;
            if (cancellation == null)
                return;
            cancellation.Cancel();
            cancellation.Dispose();
        }

        void SetState(AtProtoLoginState value)
        {
            if (state == value)
                return;
            state = value;
            StateChanged?.Invoke(value);
        }

        async Awaitable<AtProtoResult<AtProtoAuthSession>> WaitForRefreshAsync(
            Task<AtProtoResult<AtProtoAuthSession>> task,
            CancellationToken cancellationToken)
        {
            while (!task.IsCompleted)
                await Awaitable.WaitForSecondsAsync(0.01f, cancellationToken);
            return task.GetAwaiter().GetResult();
        }

        static void ValidateMetadata(AtProtoOAuthMetadata metadata)
        {
            if (metadata == null
                || string.IsNullOrEmpty(metadata.AuthorizationEndpoint)
                || string.IsNullOrEmpty(metadata.TokenEndpoint))
                throw new InvalidOperationException("The OAuth authorization-server metadata is incomplete.");

            if (!AtProtoClient.IsHttpsUrl(metadata.AuthorizationEndpoint)
                || !AtProtoClient.IsHttpsUrl(metadata.TokenEndpoint)
                || (!string.IsNullOrEmpty(metadata.PushedAuthorizationRequestEndpoint)
                    && !AtProtoClient.IsHttpsUrl(metadata.PushedAuthorizationRequestEndpoint)))
                throw new InvalidOperationException("The OAuth server endpoints must use HTTPS.");

            if (metadata.CodeChallengeMethodsSupported != null
                && !metadata.CodeChallengeMethodsSupported.Contains("S256"))
                throw new InvalidOperationException("The OAuth server does not support PKCE S256.");
        }

        static string BuildClientId(string configuredClientId, string redirectUri, string requestedScope)
        {
            if (!string.Equals(configuredClientId?.TrimEnd('/'), "http://localhost", StringComparison.OrdinalIgnoreCase))
                return configuredClientId;
            return configuredClientId + "?redirect_uri=" + Uri.EscapeDataString(redirectUri)
                + "&scope=" + Uri.EscapeDataString(requestedScope ?? string.Empty);
        }

        static string FormEncode(IReadOnlyDictionary<string, string> values)
        {
            var builder = new StringBuilder();
            foreach (var value in values)
            {
                if (builder.Length > 0)
                    builder.Append('&');
                builder.Append(Uri.EscapeDataString(value.Key));
                builder.Append('=');
                builder.Append(Uri.EscapeDataString(value.Value ?? string.Empty));
            }
            return builder.ToString();
        }

        sealed class DpopNonceState
        {
            public string Value;
        }

        sealed class ProtectedResourceMetadata
        {
            [JsonProperty("authorization_servers")]
            public List<string> AuthorizationServers { get; set; }
        }

        sealed class PushedAuthorizationResponse
        {
            [JsonProperty("request_uri")]
            public string RequestUri { get; set; }
        }

        sealed class TokenResponse
        {
            [JsonProperty("access_token")]
            public string AccessToken { get; set; }
            [JsonProperty("refresh_token")]
            public string RefreshToken { get; set; }
            [JsonProperty("expires_in")]
            public long ExpiresIn { get; set; }
            [JsonProperty("sub")]
            public string Subject { get; set; }
            [JsonProperty("scope")]
            public string Scope { get; set; }
        }

        sealed class CreateSessionRequest
        {
            [JsonProperty("identifier")]
            public string Identifier { get; set; }
            [JsonProperty("password")]
            public string Password { get; set; }
        }

        sealed class SessionResponse
        {
            [JsonProperty("accessJwt")]
            public string AccessJwt { get; set; }
            [JsonProperty("refreshJwt")]
            public string RefreshJwt { get; set; }
            [JsonProperty("handle")]
            public string Handle { get; set; }
            [JsonProperty("did")]
            public string Did { get; set; }
            [JsonProperty("expiresIn")]
            public long ExpiresIn { get; set; }
        }
    }
}