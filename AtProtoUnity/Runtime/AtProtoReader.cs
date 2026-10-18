using System;
using System.Threading;
using UnityEngine;

namespace AtProtoUnity
{
    // Unity-facing component that resolves an account and reads public records.
    public sealed class AtProtoReader : MonoBehaviour
    {
        [SerializeField] string handle = "atproto.com";
        [SerializeField] string collectionNsid = "app.bsky.actor.profile";
        [SerializeField, Range(1, 100)] int limit = 20;
        [SerializeField] bool resolveOnStart;
        [SerializeField] string identityEndpoint = AtProtoClient.DefaultIdentityEndpoint;
        [SerializeField] string plcDirectoryEndpoint = AtProtoClient.DefaultPlcDirectoryEndpoint;
        [SerializeField] AtProtoLogin login;
        [SerializeField] bool useLoginIdentity;

        AtProtoClient client;
        string clientIdentityEndpoint;
        string clientPlcDirectoryEndpoint;
        AtProtoIdentity identity;
        CancellationTokenSource operationCancellation;

        public string Handle
        {
            get => handle;
            set => handle = value;
        }

        public string CollectionNsid
        {
            get => collectionNsid;
            set => collectionNsid = value;
        }

        public int Limit
        {
            get => limit;
            set => limit = Mathf.Clamp(value, 1, 100);
        }

        public AtProtoIdentity Identity => identity;
        public AtProtoLogin Login
        {
            get => login;
            set => login = value;
        }

        public bool UseLoginIdentity
        {
            get => useLoginIdentity;
            set => useLoginIdentity = value;
        }

        public string Did => identity?.Did;
        public string PdsEndpoint => identity?.PdsEndpoint;
        public AtProtoRecordPage LastPage { get; private set; }
        public string NextCursor => LastPage?.Cursor;
        public AtProtoRepositoryDescription LastRepositoryDescription { get; private set; }

        // Raised after a successful operation; the fire-and-forget methods report results only this way.
        public event Action<string> DidResolved;
        public event Action<AtProtoRecordPage> RecordsRead;
        public event Action<AtProtoRepositoryDescription> RepositoryExplored;

        void Start()
        {
            if (resolveOnStart)
                ResolveHandle();
        }

        // Fire-and-forget. Starting any of these three cancels the reader's previous request.
        public void ResolveHandle()
        {
            Begin(ResolveHandleAsync);
        }

        // Resolves the handle, then reads one page of the configured collection.
        public void ReadRecords()
        {
            Begin(ReadRecordsAsync);
        }

        // Resolves the handle, then lists the collections in its repository.
        public void ExploreCollections()
        {
            Begin(ExploreCollectionsAsync);
        }

        // Reads the next page identified by LastPage.Cursor.
        public void ReadNextRecords()
        {
            Begin(ReadNextRecordsAsync);
        }

        // Awaitable forms for runtime code and editor tooling. Cancelling aborts the network request.
        public Awaitable ResolveHandleAsync(CancellationToken cancellationToken = default)
        {
            EnsureClient();
            return ResolveHandleRoutineAsync(cancellationToken);
        }

        public async Awaitable<AtProtoResult<AtProtoIdentity>> ResolveIdentityAsync(
            CancellationToken cancellationToken = default)
        {
            EnsureClient();
            if (useLoginIdentity)
            {
                if (login == null || login.Identity == null)
                    return AtProtoResult<AtProtoIdentity>.Failure(
                        new AtProtoRequestError("An authenticated AtProtoLogin session is required."));

                SetIdentity(login.Identity);
                return AtProtoResult<AtProtoIdentity>.Success(identity);
            }

            var result = await client.ResolveHandleAsync(handle, cancellationToken);
            if (result.Succeeded)
                SetIdentity(result.Value);
            return result;
        }

        public async Awaitable<AtProtoResult<AtProtoRecordPage>> FetchRecordsAsync(
            string requestedCollectionNsid,
            int requestedLimit,
            string cursor,
            CancellationToken cancellationToken = default)
        {
            EnsureClient();
            var identityResult = await EnsureIdentityAsync(cancellationToken);
            if (!identityResult.Succeeded)
                return AtProtoResult<AtProtoRecordPage>.Failure(identityResult.Error);

            var selectedCollection = string.IsNullOrWhiteSpace(requestedCollectionNsid)
                ? collectionNsid
                : requestedCollectionNsid;
            var selectedLimit = requestedLimit < 1 ? limit : requestedLimit;
            var result = HasAuthenticatedTransport
                ? await login.AuthenticatedClient.ListRecordsAsync(
                    identityResult.Value,
                    selectedCollection,
                    selectedLimit,
                    cursor,
                    cancellationToken)
                : await client.ListRecordsAsync(
                    identityResult.Value,
                    selectedCollection,
                    selectedLimit,
                    cursor,
                    cancellationToken);
            if (result.Succeeded)
            {
                LastPage = result.Value;
                RecordsRead?.Invoke(result.Value);
            }

            return result;
        }

        public Awaitable<AtProtoResult<AtProtoRecordPage>> FetchRecordsAsync(
            CancellationToken cancellationToken = default)
        {
            return FetchRecordsAsync(null, limit, null, cancellationToken);
        }

        public async Awaitable<AtProtoResult<AtProtoRecordPage>> FetchNextPageAsync(
            CancellationToken cancellationToken = default)
        {
            var page = LastPage;
            if (page == null || !page.HasNextPage)
                return AtProtoResult<AtProtoRecordPage>.Failure(
                    new AtProtoRequestError("A previous page with a continuation cursor is required."));

            return await FetchRecordsAsync(
                page.CollectionNsid,
                limit,
                page.Cursor,
                cancellationToken);
        }

        public async Awaitable<AtProtoResult<AtProtoRecord>> FetchRecordAsync(
            string requestedCollectionNsid,
            string recordKey,
            CancellationToken cancellationToken = default)
        {
            EnsureClient();
            var identityResult = await EnsureIdentityAsync(cancellationToken);
            if (!identityResult.Succeeded)
                return AtProtoResult<AtProtoRecord>.Failure(identityResult.Error);

            return HasAuthenticatedTransport
                ? await login.AuthenticatedClient.GetRecordAsync(
                    identityResult.Value,
                    requestedCollectionNsid,
                    recordKey,
                    cancellationToken)
                : await client.GetRecordAsync(
                    identityResult.Value,
                    requestedCollectionNsid,
                    recordKey,
                    cancellationToken);
        }

        public async Awaitable<AtProtoResult<T>> FetchRecordAsync<T>(
            string requestedCollectionNsid,
            string recordKey,
            CancellationToken cancellationToken = default)
        {
            EnsureClient();
            var identityResult = await EnsureIdentityAsync(cancellationToken);
            if (!identityResult.Succeeded)
                return AtProtoResult<T>.Failure(identityResult.Error);

            return HasAuthenticatedTransport
                ? await login.AuthenticatedClient.GetRecordAsync<T>(
                    identityResult.Value,
                    requestedCollectionNsid,
                    recordKey,
                    cancellationToken)
                : await client.GetRecordAsync<T>(
                    identityResult.Value,
                    requestedCollectionNsid,
                    recordKey,
                    cancellationToken);
        }

        public Awaitable ReadRecordsAsync(CancellationToken cancellationToken = default)
        {
            EnsureClient();
            return ReadRecordsRoutineAsync(null, limit, null, false, cancellationToken);
        }

        // Reads the given collection without changing the configured one.
        public Awaitable ReadRecordsAsync(
            string requestedCollectionNsid,
            CancellationToken cancellationToken = default)
        {
            EnsureClient();
            return ReadRecordsRoutineAsync(
                requestedCollectionNsid,
                limit,
                null,
                false,
                cancellationToken);
        }

        // Reads a page with an explicit size and cursor without changing the Inspector settings.
        public Awaitable ReadRecordsAsync(
            string requestedCollectionNsid,
            int requestedLimit,
            string cursor,
            CancellationToken cancellationToken = default)
        {
            EnsureClient();
            return ReadRecordsRoutineAsync(
                requestedCollectionNsid,
                requestedLimit,
                cursor,
                false,
                cancellationToken);
        }

        // Fetches the page after LastPage; the current page is replaced, not accumulated.
        public async Awaitable ReadNextRecordsAsync(CancellationToken cancellationToken = default)
        {
            var page = LastPage;
            if (page == null || !page.HasNextPage)
            {
                LogFailure(
                    "Next record page",
                    new AtProtoRequestError("A previous page with a continuation cursor is required."));
                return;
            }

            EnsureClient();
            await ReadRecordsRoutineAsync(
                page.CollectionNsid,
                limit,
                page.Cursor,
                true,
                cancellationToken);
        }

        public Awaitable ExploreCollectionsAsync(CancellationToken cancellationToken = default)
        {
            EnsureClient();
            return ExploreCollectionsRoutineAsync(cancellationToken);
        }

        // Requires a resolved identity. The caller owns the returned texture and must destroy it.
        public Awaitable<AtProtoResult<Texture2D>> GetBlobAsync(
            string cid,
            CancellationToken cancellationToken = default)
        {
            EnsureClient();
            return HasAuthenticatedTransport
                ? login.AuthenticatedClient.GetBlobAsync(identity, cid, cancellationToken)
                : client.GetBlobAsync(identity, cid, cancellationToken);
        }

        // Clears cached identity and, for a new read, the previous page before lookup.
        async Awaitable ResolveHandleRoutineAsync(
            CancellationToken cancellationToken,
            bool clearRecordPage = true)
        {
            if (clearRecordPage)
                LastPage = null;
            LastRepositoryDescription = null;

            if (useLoginIdentity)
            {
                identity = login?.Identity;
                if (identity == null)
                    LogFailure(
                        "Authenticated identity resolution",
                        new AtProtoRequestError("An authenticated AtProtoLogin session is required."));
                else
                    SetIdentity(identity);
                return;
            }

            identity = null;

            var result = await client.ResolveHandleAsync(handle, cancellationToken);

            if (!result.Succeeded)
            {
                LogFailure("Handle resolution", result.Error);
                return;
            }

            SetIdentity(result.Value);
        }

        // Re-resolves the handle for a new read so a changed handle or endpoint is never used with a stale identity.
        async Awaitable ReadRecordsRoutineAsync(
            string requestedCollectionNsid,
            int requestedLimit,
            string cursor,
            bool preserveCurrentPage,
            CancellationToken cancellationToken)
        {
            // Load More reuses the identity so a network blip can't discard the pages already shown.
            if (!preserveCurrentPage || identity == null)
                await ResolveHandleRoutineAsync(cancellationToken, !preserveCurrentPage);

            if (identity == null)
                return;

            var selectedCollection = string.IsNullOrWhiteSpace(requestedCollectionNsid)
                ? collectionNsid
                : requestedCollectionNsid;
            var selectedLimit = requestedLimit < 1 ? limit : requestedLimit;
            if (!preserveCurrentPage)
                LastPage = null;

            var result = HasAuthenticatedTransport
                ? await login.AuthenticatedClient.ListRecordsAsync(
                    identity,
                    selectedCollection,
                    selectedLimit,
                    cursor,
                    cancellationToken)
                : await client.ListRecordsAsync(
                    identity,
                    selectedCollection,
                    selectedLimit,
                    cursor,
                    cancellationToken);

            if (!result.Succeeded)
            {
                LogFailure("Record listing", result.Error);
                return;
            }

            var page = result.Value;
            LastPage = page;
            RecordsRead?.Invoke(page);
            Debug.Log(
                $"[AtProto] {page.Records.Count} records from {identity.Did}/{selectedCollection.Trim()}.",
                this);
        }

        async Awaitable ExploreCollectionsRoutineAsync(CancellationToken cancellationToken)
        {
            await ResolveHandleRoutineAsync(cancellationToken);

            if (identity == null)
                return;

            var result = HasAuthenticatedTransport
                ? await login.AuthenticatedClient.DescribeRepoAsync(
                    identity,
                    cancellationToken)
                : await client.DescribeRepoAsync(
                    identity,
                    cancellationToken);

            if (!result.Succeeded)
            {
                LogFailure("Repository exploration", result.Error);
                return;
            }

            LastRepositoryDescription = result.Value;
            RepositoryExplored?.Invoke(result.Value);
            Debug.Log(
                $"[AtProto] {result.Value.Collections.Count} collections found in {identity.Did}.",
                this);
        }

        void OnDestroy()
        {
            CancelOperation();
        }

        void SetIdentity(AtProtoIdentity resolved)
        {
            identity = resolved;
            Debug.Log($"[AtProto] {resolved.Handle} resolved to {resolved.Did}.", this);
            DidResolved?.Invoke(resolved.Did);
        }

        async Awaitable<AtProtoResult<AtProtoIdentity>> EnsureIdentityAsync(
            CancellationToken cancellationToken)
        {
            if (identity != null)
                return AtProtoResult<AtProtoIdentity>.Success(identity);

            return await ResolveIdentityAsync(cancellationToken);
        }

        // Created lazily because edit-mode use never runs Awake, and rebuilt when the endpoint fields change.
        void EnsureClient()
        {
            if (client != null
                && clientIdentityEndpoint == identityEndpoint
                && clientPlcDirectoryEndpoint == plcDirectoryEndpoint)
                return;

            client = new AtProtoClient(identityEndpoint, plcDirectoryEndpoint);
            clientIdentityEndpoint = identityEndpoint;
            clientPlcDirectoryEndpoint = plcDirectoryEndpoint;
        }

        bool HasAuthenticatedTransport =>
            useLoginIdentity && login != null && login.AuthenticatedClient != null;

        void Begin(Func<CancellationToken, Awaitable> operationFactory)
        {
            CancelOperation();
            operationCancellation = new CancellationTokenSource();
            Run(operationFactory, operationCancellation);
        }

        // async void because nothing can await it; failures are logged here instead.
        async void Run(
            Func<CancellationToken, Awaitable> operationFactory,
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

        void LogFailure(string operationName, AtProtoRequestError error)
        {
            var details = error.ResponseBody;
            if (!string.IsNullOrEmpty(details))
                details = "\n" + details;
            Debug.LogError($"[AtProto] {operationName} failed: {error}{details}", this);
        }
    }
}