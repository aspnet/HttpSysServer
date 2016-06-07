// Copyright (c) Microsoft Open Technologies, Inc.
// All Rights Reserved
//
// Licensed under the Apache License, Version 2.0 (the "License");
// you may not use this file except in compliance with the License.
// You may obtain a copy of the License at
//
// http://www.apache.org/licenses/LICENSE-2.0
//
// THIS CODE IS PROVIDED *AS IS* BASIS, WITHOUT WARRANTIES OR
// CONDITIONS OF ANY KIND, EITHER EXPRESS OR IMPLIED, INCLUDING
// WITHOUT LIMITATION ANY IMPLIED WARRANTIES OR CONDITIONS OF
// TITLE, FITNESS FOR A PARTICULAR PURPOSE, MERCHANTABLITY OR
// NON-INFRINGEMENT.
// See the Apache 2 License for the specific language governing
// permissions and limitations under the License.

//------------------------------------------------------------------------------
// <copyright file="HttpListener.cs" company="Microsoft">
//     Copyright (c) Microsoft Corporation.  All rights reserved.
// </copyright>
//------------------------------------------------------------------------------

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.InteropServices;
using System.Security.Authentication.ExtendedProtection;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace Microsoft.Net.Http.Server
{
    /// <summary>
    /// An HTTP server wrapping the Http.Sys APIs that accepts requests.
    /// </summary>
    public sealed class WebListener : IDisposable
    {
        private const long DefaultRequestQueueLength = 1000;  // Http.sys default.
        private static readonly int RequestChannelBindStatusSize =
            Marshal.SizeOf<UnsafeNclNativeMethods.HttpApi.HTTP_REQUEST_CHANNEL_BIND_STATUS>();
        private static readonly int BindingInfoSize =
            Marshal.SizeOf<UnsafeNclNativeMethods.HttpApi.HTTP_BINDING_INFO>();

        // Win8# 559317 fixed a bug in Http.sys's HttpReceiveClientCertificate method.
        // Without this fix IOCP callbacks were not being called although ERROR_IO_PENDING was
        // returned from HttpReceiveClientCertificate when using the 
        // FileCompletionNotificationModes.SkipCompletionPortOnSuccess flag.
        // This bug was only hit when the buffer passed into HttpReceiveClientCertificate
        // (1500 bytes initially) is tool small for the certificate.
        // Due to this bug in downlevel operating systems the FileCompletionNotificationModes.SkipCompletionPortOnSuccess
        // flag is only used on Win8 and later.
        internal static readonly bool SkipIOCPCallbackOnSuccess = ComNetOS.IsWin8orLater;

        // Mitigate potential DOS attacks by limiting the number of unknown headers we accept.  Numerous header names 
        // with hash collisions will cause the server to consume excess CPU.  1000 headers limits CPU time to under 
        // 0.5 seconds per request.  Respond with a 400 Bad Request.
        private const int UnknownHeaderLimit = 1000;

        private readonly ConcurrentDictionary<ulong, ConnectionCancellation> _connectionCancellationTokens;

        private ILogger _logger;

        private SafeHandle _requestQueueHandle;
        private ThreadPoolBoundHandle _boundHandle;
        private volatile State _state; // m_State is set only within lock blocks, but often read outside locks.

        private bool _ignoreWriteExceptions;
        private HttpServerSessionHandle _serverSessionHandle;
        private ulong _urlGroupId;
        private TimeoutManager _timeoutManager;
        private AuthenticationManager _authManager;
        private bool _v2Initialized;

        private object _internalLock;

        private UrlPrefixCollection _urlPrefixes;

        // The native request queue
        private long? _requestQueueLength;

        private bool _bufferResponses = true;

        public WebListener()
        {
            if (!UnsafeNclNativeMethods.HttpApi.Supported)
            {
                throw new PlatformNotSupportedException();
            }

            Debug.Assert(UnsafeNclNativeMethods.HttpApi.ApiVersion ==
                UnsafeNclNativeMethods.HttpApi.HTTP_API_VERSION.Version20, "Invalid Http api version");

            SetLoggerFactory(null);

            _state = State.Stopped;
            _internalLock = new object();

            _urlPrefixes = new UrlPrefixCollection(this);
            _timeoutManager = new TimeoutManager(this);
            _authManager = new AuthenticationManager(this);
            _connectionCancellationTokens = new ConcurrentDictionary<ulong, ConnectionCancellation>();
        }

        internal enum State
        {
            Stopped,
            Started,
            Disposed,
        }

        internal ILogger Logger
        {
            get { return _logger; }
        }

        public UrlPrefixCollection UrlPrefixes
        {
            get { return _urlPrefixes; }
        }

        public bool BufferResponses
        {
            get { return _bufferResponses; }
            set { _bufferResponses = value; }
        }

        internal SafeHandle RequestQueueHandle
        {
            get
            {
                return _requestQueueHandle;
            }
        }

        internal ThreadPoolBoundHandle BoundHandle
        {
            get { return _boundHandle; }
        }

        internal ulong UrlGroupId
        {
            get { return _urlGroupId; }
        }

        /// <summary>
        /// Exposes the Http.Sys timeout configurations.  These may also be configured in the registry.
        /// </summary>
        public TimeoutManager TimeoutManager
        {
            get
            {
                ValidateV2Property();
                Debug.Assert(_timeoutManager != null, "Timeout manager is not assigned");
                return _timeoutManager;
            }
        }

        public AuthenticationManager AuthenticationManager
        {
            get
            {
                ValidateV2Property();
                Debug.Assert(_authManager != null, "Auth manager is not assigned");
                return _authManager;
            }
        }

        internal static bool IsSupported
        {
            get
            {
                return UnsafeNclNativeMethods.HttpApi.Supported;
            }
        }

        public bool IsListening
        {
            get
            {
                return _state == State.Started;
            }
        }

        internal bool IgnoreWriteExceptions
        {
            get
            {
                return _ignoreWriteExceptions;
            }
            set
            {
                CheckDisposed();
                _ignoreWriteExceptions = value;
            }
        }

        public void SetLoggerFactory(ILoggerFactory loggerFactory)
        {
            _logger = LogHelper.CreateLogger(loggerFactory, typeof(WebListener));
        }

        /// <summary>
        /// Sets the maximum number of requests that will be queued up in Http.Sys.
        /// </summary>
        /// <param name="limit"></param>
        public void SetRequestQueueLimit(long limit)
        {
            if (limit <= 0)
            {
                throw new ArgumentOutOfRangeException("limit", limit, string.Empty);
            }
            if ((!_requestQueueLength.HasValue && limit == DefaultRequestQueueLength)
                || (_requestQueueLength.HasValue && limit == _requestQueueLength.Value))
            {
                return;
            }

            _requestQueueLength = limit;

            SetRequestQueueLimit();
        }

        private unsafe void SetRequestQueueLimit()
        {
            // The listener must be active for this to work.  Call from Start after activating.
            if (!IsListening || !_requestQueueLength.HasValue)
            {
                return;
            }

            long length = _requestQueueLength.Value;
            uint result = UnsafeNclNativeMethods.HttpApi.HttpSetRequestQueueProperty(_requestQueueHandle,
                UnsafeNclNativeMethods.HttpApi.HTTP_SERVER_PROPERTY.HttpServerQueueLengthProperty,
                new IntPtr((void*)&length), (uint)Marshal.SizeOf<long>(), 0, IntPtr.Zero);

            if (result != 0)
            {
                throw new WebListenerException((int)result);
            }
        }

        private void ValidateV2Property()
        {
            // Make sure that calling CheckDisposed and SetupV2Config is an atomic operation. This 
            // avoids race conditions if the listener is aborted/closed after CheckDisposed(), but 
            // before SetupV2Config().
            lock (_internalLock)
            {
                CheckDisposed();
                SetupV2Config();
            }
        }

        internal void SetUrlGroupProperty(UnsafeNclNativeMethods.HttpApi.HTTP_SERVER_PROPERTY property, IntPtr info, uint infosize)
        {
            ValidateV2Property();
            uint statusCode = UnsafeNclNativeMethods.ErrorCodes.ERROR_SUCCESS;

            Debug.Assert(_urlGroupId != 0, "SetUrlGroupProperty called with invalid url group id");
            Debug.Assert(info != IntPtr.Zero, "SetUrlGroupProperty called with invalid pointer");

            // Set the url group property using Http Api.

            statusCode = UnsafeNclNativeMethods.HttpApi.HttpSetUrlGroupProperty(
                _urlGroupId, property, info, infosize);

            if (statusCode != UnsafeNclNativeMethods.ErrorCodes.ERROR_SUCCESS)
            {
                WebListenerException exception = new WebListenerException((int)statusCode);
                LogHelper.LogException(_logger, "SetUrlGroupProperty", exception);
                throw exception;
            }
        }

        private IntPtr DangerousGetHandle()
        {
            return _requestQueueHandle.DangerousGetHandle();
        }

        private unsafe void SetupV2Config()
        {
            uint statusCode = UnsafeNclNativeMethods.ErrorCodes.ERROR_SUCCESS;
            ulong id = 0;

            // If we have already initialized V2 config, then nothing to do.

            if (_v2Initialized)
            {
                return;
            }

            // V2 initialization sequence:
            // 1. Create server session
            // 2. Create url group
            // 3. Create request queue - Done in Start()
            // 4. Add urls to url group - Done in Start()
            // 5. Attach request queue to url group - Done in Start()

            try
            {
                statusCode = UnsafeNclNativeMethods.HttpApi.HttpCreateServerSession(
                    UnsafeNclNativeMethods.HttpApi.Version, &id, 0);

                if (statusCode != UnsafeNclNativeMethods.ErrorCodes.ERROR_SUCCESS)
                {
                    throw new WebListenerException((int)statusCode);
                }

                Debug.Assert(id != 0, "Invalid id returned by HttpCreateServerSession");

                _serverSessionHandle = new HttpServerSessionHandle(id);

                id = 0;
                statusCode = UnsafeNclNativeMethods.HttpApi.HttpCreateUrlGroup(
                    _serverSessionHandle.DangerousGetServerSessionId(), &id, 0);

                if (statusCode != UnsafeNclNativeMethods.ErrorCodes.ERROR_SUCCESS)
                {
                    throw new WebListenerException((int)statusCode);
                }

                Debug.Assert(id != 0, "Invalid id returned by HttpCreateUrlGroup");
                _urlGroupId = id;

                _v2Initialized = true;
            }
            catch (Exception exception)
            {
                // If V2 initialization fails, we mark object as unusable.
                _state = State.Disposed;
                // If Url group or request queue creation failed, close server session before throwing.
                if (_serverSessionHandle != null)
                {
                    _serverSessionHandle.Dispose();
                }
                LogHelper.LogException(_logger, "SetupV2Config", exception);
                throw;
            }
        }

        public void Start()
        {
            CheckDisposed();

            // TODO: _logger = LogHelper.CreateLogger(loggerFactory, typeof(OwinWebListener));
            LogHelper.LogInfo(_logger, "Start");

            // Make sure there are no race conditions between Start/Stop/Abort/Close/Dispose and
            // calls to SetupV2Config: Start needs to setup all resources (esp. in V2 where besides
            // the request handle, there is also a server session and a Url group. Abort/Stop must
            // not interfere while Start is allocating those resources. The lock also makes sure
            // all methods changing state can read and change the state in an atomic way.
            lock (_internalLock)
            {
                try
                {
                    CheckDisposed();
                    if (_state == State.Started)
                    {
                        return;
                    }

                    // SetupV2Config() is not called in the ctor, because it may throw. This would
                    // be a regression since in v1 the ctor never threw. Besides, ctors should do 
                    // minimal work according to the framework design guidelines.
                    SetupV2Config();
                    CreateRequestQueueHandle();
                    AttachRequestQueueToUrlGroup();

                    // All resources are set up correctly. Now add all prefixes.
                    try
                    {
                        _urlPrefixes.RegisterAllPrefixes();
                    }
                    catch (WebListenerException)
                    {
                        // If an error occurred while adding prefixes, free all resources allocated by previous steps.
                        DetachRequestQueueFromUrlGroup();
                        throw;
                    }

                    _state = State.Started;

                    SetRequestQueueLimit();
                }
                catch (Exception exception)
                {
                    // Make sure the HttpListener instance can't be used if Start() failed.
                    _state = State.Disposed;
                    CloseRequestQueueHandle();
                    CleanupV2Config();
                    LogHelper.LogException(_logger, "Start", exception);
                    throw;
                }
            }
        }

        private void CleanupV2Config()
        {
            // If we never setup V2, just return.
            if (!_v2Initialized)
            {
                return;
            }

            // V2 stopping sequence:
            // 1. Detach request queue from url group - Done in Stop()/Abort()
            // 2. Remove urls from url group - Done in Stop()
            // 3. Close request queue - Done in Stop()/Abort()
            // 4. Close Url group.
            // 5. Close server session.

            Debug.Assert(_urlGroupId != 0, "HttpCloseUrlGroup called with invalid url group id");

            uint statusCode = UnsafeNclNativeMethods.HttpApi.HttpCloseUrlGroup(_urlGroupId);

            if (statusCode != UnsafeNclNativeMethods.ErrorCodes.ERROR_SUCCESS)
            {
                LogHelper.LogError(_logger, "CleanupV2Config", "Result: " + statusCode);
            }
            _urlGroupId = 0;

            Debug.Assert(_serverSessionHandle != null, "ServerSessionHandle is null in CloseV2Config");
            Debug.Assert(!_serverSessionHandle.IsInvalid, "ServerSessionHandle is invalid in CloseV2Config");

            _serverSessionHandle.Dispose();
        }

        private unsafe void AttachRequestQueueToUrlGroup()
        {
            // Set the association between request queue and url group. After this, requests for registered urls will 
            // get delivered to this request queue.

            UnsafeNclNativeMethods.HttpApi.HTTP_BINDING_INFO info = new UnsafeNclNativeMethods.HttpApi.HTTP_BINDING_INFO();
            info.Flags = UnsafeNclNativeMethods.HttpApi.HTTP_FLAGS.HTTP_PROPERTY_FLAG_PRESENT;
            info.RequestQueueHandle = DangerousGetHandle();

            IntPtr infoptr = new IntPtr(&info);

            SetUrlGroupProperty(UnsafeNclNativeMethods.HttpApi.HTTP_SERVER_PROPERTY.HttpServerBindingProperty,
                infoptr, (uint)BindingInfoSize);
        }

        private unsafe void DetachRequestQueueFromUrlGroup()
        {
            Debug.Assert(_urlGroupId != 0, "DetachRequestQueueFromUrlGroup can't detach using Url group id 0.");

            // Break the association between request queue and url group. After this, requests for registered urls 
            // will get 503s.
            // Note that this method may be called multiple times (Stop() and then Abort()). This
            // is fine since http.sys allows to set HttpServerBindingProperty multiple times for valid 
            // Url groups.

            UnsafeNclNativeMethods.HttpApi.HTTP_BINDING_INFO info = new UnsafeNclNativeMethods.HttpApi.HTTP_BINDING_INFO();
            info.Flags = UnsafeNclNativeMethods.HttpApi.HTTP_FLAGS.NONE;
            info.RequestQueueHandle = IntPtr.Zero;

            IntPtr infoptr = new IntPtr(&info);

            uint statusCode = UnsafeNclNativeMethods.HttpApi.HttpSetUrlGroupProperty(_urlGroupId,
                UnsafeNclNativeMethods.HttpApi.HTTP_SERVER_PROPERTY.HttpServerBindingProperty,
                infoptr, (uint)BindingInfoSize);

            if (statusCode != UnsafeNclNativeMethods.ErrorCodes.ERROR_SUCCESS)
            {
                LogHelper.LogError(_logger, "DetachRequestQueueFromUrlGroup", "Result: " + statusCode);
            }
        }

        internal void Stop()
        {
            try
            {
                lock (_internalLock)
                {
                    CheckDisposed();
                    if (_state == State.Stopped)
                    {
                        return;
                    }

                    _urlPrefixes.UnregisterAllPrefixes();

                    _state = State.Stopped;

                    DetachRequestQueueFromUrlGroup();

                    // Even though it would be enough to just detach the request queue in v2, in order to
                    // keep app compat with earlier versions of the framework, we need to close the request queue.
                    // This will make sure that pending GetContext() calls will complete and throw an exception. Just
                    // detaching the url group from the request queue would not cause GetContext() to return.
                    CloseRequestQueueHandle();
                }
            }
            catch (Exception exception)
            {
                LogHelper.LogException(_logger, "Stop", exception);
                throw;
            }
        }

        private unsafe void CreateRequestQueueHandle()
        {
            uint statusCode = UnsafeNclNativeMethods.ErrorCodes.ERROR_SUCCESS;

            HttpRequestQueueV2Handle requestQueueHandle = null;
            statusCode =
                UnsafeNclNativeMethods.SafeNetHandles.HttpCreateRequestQueue(
                    UnsafeNclNativeMethods.HttpApi.Version, null, null, 0, out requestQueueHandle);

            if (statusCode != UnsafeNclNativeMethods.ErrorCodes.ERROR_SUCCESS)
            {
                throw new WebListenerException((int)statusCode);
            }

            // Disabling callbacks when IO operation completes synchronously (returns ErrorCodes.ERROR_SUCCESS)
            if (SkipIOCPCallbackOnSuccess &&
                !UnsafeNclNativeMethods.SetFileCompletionNotificationModes(
                    requestQueueHandle,
                    UnsafeNclNativeMethods.FileCompletionNotificationModes.SkipCompletionPortOnSuccess |
                    UnsafeNclNativeMethods.FileCompletionNotificationModes.SkipSetEventOnHandle))
            {
                throw new WebListenerException(Marshal.GetLastWin32Error());
            }

            _requestQueueHandle = requestQueueHandle;
            _boundHandle = ThreadPoolBoundHandle.BindHandle(_requestQueueHandle);
        }

        private unsafe void CloseRequestQueueHandle()
        {
            if ((_requestQueueHandle != null) && (!_requestQueueHandle.IsInvalid))
            {
                _requestQueueHandle.Dispose();
            }
            if (_boundHandle != null)
            {
                _boundHandle.Dispose();
            }
        }

        /// <summary>
        /// Stop the server and clean up.
        /// </summary>
        public void Dispose()
        {
            Dispose(true);
        }

        // old API, now private, and helper methods
        private void Dispose(bool disposing)
        {
            if (!disposing)
            {
                return;
            }

            lock (_internalLock)
            {
                try
                {
                    if (_state == State.Disposed)
                    {
                        return;
                    }
                    LogHelper.LogInfo(_logger, "Dispose");

                    Stop();
                    CleanupV2Config();
                }
                catch (Exception exception)
                {
                    LogHelper.LogException(_logger, "Dispose", exception);
                    throw;
                }
                finally
                {
                    _state = State.Disposed;
                }
            }
        }

        internal unsafe bool ValidateRequest(NativeRequestContext requestMemory)
        {
            // Block potential DOS attacks
            if (requestMemory.RequestBlob->Headers.UnknownHeaderCount > UnknownHeaderLimit)
            {
                SendError(requestMemory.RequestBlob->RequestId, HttpStatusCode.BadRequest, authChallenges: null);
                return false;
            }
            return true;
        }

        internal unsafe bool ValidateAuth(NativeRequestContext requestMemory)
        {
            var requestV2 = (UnsafeNclNativeMethods.HttpApi.HTTP_REQUEST_V2*)requestMemory.RequestBlob;
            if (!AuthenticationManager.AllowAnonymous && !AuthenticationManager.CheckAuthenticated(requestV2->pRequestInfo))
            {
                SendError(requestMemory.RequestBlob->RequestId, HttpStatusCode.Unauthorized,
                    AuthenticationManager.GenerateChallenges(AuthenticationManager.AuthenticationSchemes));
                return false;
            }
            return true;
        }

        [SuppressMessage("Microsoft.Reliability", "CA2000:Dispose objects before losing scope", Justification = "Disposed by callback")]
        public Task<RequestContext> GetContextAsync()
        {
            AsyncAcceptContext asyncResult = null;
            try
            {
                CheckDisposed();
                Debug.Assert(_state != State.Stopped, "Listener has been stopped.");
                // prepare the ListenerAsyncResult object (this will have it's own
                // event that the user can wait on for IO completion - which means we
                // need to signal it when IO completes)
                asyncResult = new AsyncAcceptContext(this);
                uint statusCode = asyncResult.QueueBeginGetContext();
                if (statusCode != UnsafeNclNativeMethods.ErrorCodes.ERROR_SUCCESS &&
                    statusCode != UnsafeNclNativeMethods.ErrorCodes.ERROR_IO_PENDING)
                {
                    // someother bad error, possible(?) return values are:
                    // ERROR_INVALID_HANDLE, ERROR_INSUFFICIENT_BUFFER, ERROR_OPERATION_ABORTED
                    asyncResult.Dispose();
                    throw new WebListenerException((int)statusCode);
                }
            }
            catch (Exception exception)
            {
                LogHelper.LogException(_logger, "GetContextAsync", exception);
                throw;
            }

            return asyncResult.Task;
        }

        internal CancellationToken RegisterForDisconnectNotification(RequestContext requestContext)
        {
            try
            {
                // Create exactly one CancellationToken per connection.
                ulong connectionId = requestContext.Request.ConnectionId;
                return GetConnectionCancellation(connectionId);
            }
            catch (Win32Exception exception)
            {
                LogHelper.LogException(_logger, "RegisterForDisconnectNotification", exception);
                return CancellationToken.None;
            }
        }

        private CancellationToken GetConnectionCancellation(ulong connectionId)
        {
            // Read case is performance sensitive 
            ConnectionCancellation cancellation;
            if (!_connectionCancellationTokens.TryGetValue(connectionId, out cancellation))
            {
                cancellation = GetCreatedConnectionCancellation(connectionId);
            }
            return cancellation.GetCancellationToken(connectionId);
        }

        private ConnectionCancellation GetCreatedConnectionCancellation(ulong connectionId)
        {
            // Race condition on creation has no side effects 
            ConnectionCancellation cancellation = new ConnectionCancellation(this);
            return _connectionCancellationTokens.GetOrAdd(connectionId, cancellation); 
        }

        private unsafe CancellationToken CreateDisconnectToken(ulong connectionId)
        {
            // Debug.WriteLine("Server: Registering connection for disconnect for connection ID: " + connectionId);

            // Create a nativeOverlapped callback so we can register for disconnect callback
            var cts = new CancellationTokenSource();

            SafeNativeOverlapped nativeOverlapped = null;
            nativeOverlapped = new SafeNativeOverlapped(_boundHandle, _boundHandle.AllocateNativeOverlapped(
                (errorCode, numBytes, overlappedPtr) =>
                {
                    // Debug.WriteLine("Server: http.sys disconnect callback fired for connection ID: " + connectionId);

                    // Free the overlapped
                    nativeOverlapped.Dispose();

                    // Pull the token out of the list and Cancel it.
                    ConnectionCancellation token;
                    _connectionCancellationTokens.TryRemove(connectionId, out token);
                    try
                    {
                        cts.Cancel();
                    }
                    catch (AggregateException exception)
                    {
                        LogHelper.LogException(_logger, "CreateDisconnectToken::Disconnected", exception);
                    }

                    cts.Dispose();
                },
                null, null));

            uint statusCode;
            try
            {
                statusCode = UnsafeNclNativeMethods.HttpApi.HttpWaitForDisconnectEx(requestQueueHandle: _requestQueueHandle,
                    connectionId: connectionId, reserved: 0, overlapped: nativeOverlapped);
            }
            catch (Win32Exception exception)
            {
                statusCode = (uint)exception.NativeErrorCode;
                LogHelper.LogException(_logger, "CreateDisconnectToken", exception);
            }

            if (statusCode != UnsafeNclNativeMethods.ErrorCodes.ERROR_IO_PENDING &&
                statusCode != UnsafeNclNativeMethods.ErrorCodes.ERROR_SUCCESS)
            {
                // We got an unknown result so return a None
                // TODO: return a canceled token?
                return CancellationToken.None;
            }

            if (statusCode == UnsafeNclNativeMethods.ErrorCodes.ERROR_SUCCESS && WebListener.SkipIOCPCallbackOnSuccess)
            {
                // IO operation completed synchronously - callback won't be called to signal completion.
                // TODO: return a canceled token?
                return CancellationToken.None;
            }

            return cts.Token;
        }

        private unsafe void SendError(ulong requestId, HttpStatusCode httpStatusCode, IList<string> authChallenges)
        {
            UnsafeNclNativeMethods.HttpApi.HTTP_RESPONSE_V2 httpResponse = new UnsafeNclNativeMethods.HttpApi.HTTP_RESPONSE_V2();
            httpResponse.Response_V1.Version = new UnsafeNclNativeMethods.HttpApi.HTTP_VERSION();
            httpResponse.Response_V1.Version.MajorVersion = (ushort)1;
            httpResponse.Response_V1.Version.MinorVersion = (ushort)1;

            List<GCHandle> pinnedHeaders = null;
            GCHandle gcHandle;
            try
            {
                // Copied from the multi-value headers section of SerializeHeaders
                if (authChallenges != null && authChallenges.Count > 0)
                {
                    pinnedHeaders = new List<GCHandle>();

                    UnsafeNclNativeMethods.HttpApi.HTTP_RESPONSE_INFO[] knownHeaderInfo = null;
                    knownHeaderInfo = new UnsafeNclNativeMethods.HttpApi.HTTP_RESPONSE_INFO[1];
                    gcHandle = GCHandle.Alloc(knownHeaderInfo, GCHandleType.Pinned);
                    pinnedHeaders.Add(gcHandle);
                    httpResponse.pResponseInfo = (UnsafeNclNativeMethods.HttpApi.HTTP_RESPONSE_INFO*)gcHandle.AddrOfPinnedObject();

                    knownHeaderInfo[httpResponse.ResponseInfoCount].Type = UnsafeNclNativeMethods.HttpApi.HTTP_RESPONSE_INFO_TYPE.HttpResponseInfoTypeMultipleKnownHeaders;
                    knownHeaderInfo[httpResponse.ResponseInfoCount].Length =
                        (uint)Marshal.SizeOf<UnsafeNclNativeMethods.HttpApi.HTTP_MULTIPLE_KNOWN_HEADERS>();

                    UnsafeNclNativeMethods.HttpApi.HTTP_MULTIPLE_KNOWN_HEADERS header = new UnsafeNclNativeMethods.HttpApi.HTTP_MULTIPLE_KNOWN_HEADERS();

                    header.HeaderId = UnsafeNclNativeMethods.HttpApi.HTTP_RESPONSE_HEADER_ID.Enum.HttpHeaderWwwAuthenticate;
                    header.Flags = UnsafeNclNativeMethods.HttpApi.HTTP_RESPONSE_INFO_FLAGS.PreserveOrder; // The docs say this is for www-auth only.

                    UnsafeNclNativeMethods.HttpApi.HTTP_KNOWN_HEADER[] nativeHeaderValues = new UnsafeNclNativeMethods.HttpApi.HTTP_KNOWN_HEADER[authChallenges.Count];
                    gcHandle = GCHandle.Alloc(nativeHeaderValues, GCHandleType.Pinned);
                    pinnedHeaders.Add(gcHandle);
                    header.KnownHeaders = (UnsafeNclNativeMethods.HttpApi.HTTP_KNOWN_HEADER*)gcHandle.AddrOfPinnedObject();

                    for (int headerValueIndex = 0; headerValueIndex < authChallenges.Count; headerValueIndex++)
                    {
                        // Add Value
                        string headerValue = authChallenges[headerValueIndex];
                        byte[] bytes = HeaderEncoding.GetBytes(headerValue);
                        nativeHeaderValues[header.KnownHeaderCount].RawValueLength = (ushort)bytes.Length;
                        gcHandle = GCHandle.Alloc(bytes, GCHandleType.Pinned);
                        pinnedHeaders.Add(gcHandle);
                        nativeHeaderValues[header.KnownHeaderCount].pRawValue = (sbyte*)gcHandle.AddrOfPinnedObject();
                        header.KnownHeaderCount++;
                    }

                    // This type is a struct, not an object, so pinning it causes a boxed copy to be created. We can't do that until after all the fields are set.
                    gcHandle = GCHandle.Alloc(header, GCHandleType.Pinned);
                    pinnedHeaders.Add(gcHandle);
                    knownHeaderInfo[0].pInfo = (UnsafeNclNativeMethods.HttpApi.HTTP_MULTIPLE_KNOWN_HEADERS*)gcHandle.AddrOfPinnedObject();

                    httpResponse.ResponseInfoCount = 1;
                }

                httpResponse.Response_V1.StatusCode = (ushort)httpStatusCode;
                string statusDescription = HttpReasonPhrase.Get(httpStatusCode);
                uint dataWritten = 0;
                uint statusCode;
                byte[] byteReason = HeaderEncoding.GetBytes(statusDescription);
                fixed (byte* pReason = byteReason)
                {
                    httpResponse.Response_V1.pReason = (sbyte*)pReason;
                    httpResponse.Response_V1.ReasonLength = (ushort)byteReason.Length;

                    byte[] byteContentLength = new byte[] { (byte)'0' };
                    fixed (byte* pContentLength = byteContentLength)
                    {
                        (&httpResponse.Response_V1.Headers.KnownHeaders)[(int)HttpSysResponseHeader.ContentLength].pRawValue = (sbyte*)pContentLength;
                        (&httpResponse.Response_V1.Headers.KnownHeaders)[(int)HttpSysResponseHeader.ContentLength].RawValueLength = (ushort)byteContentLength.Length;
                        httpResponse.Response_V1.Headers.UnknownHeaderCount = 0;

                        statusCode =
                            UnsafeNclNativeMethods.HttpApi.HttpSendHttpResponse(
                                _requestQueueHandle,
                                requestId,
                                0,
                                &httpResponse,
                                null,
                                &dataWritten,
                                SafeLocalFree.Zero,
                                0,
                                SafeNativeOverlapped.Zero,
                                IntPtr.Zero);
                    }
                }
                if (statusCode != UnsafeNclNativeMethods.ErrorCodes.ERROR_SUCCESS)
                {
                    // if we fail to send a 401 something's seriously wrong, abort the request
                    RequestContext.CancelRequest(_requestQueueHandle, requestId);
                }
            }
            finally
            {
                if (pinnedHeaders != null)
                {
                    foreach (GCHandle handle in pinnedHeaders)
                    {
                        if (handle.IsAllocated)
                        {
                            handle.Free();
                        }
                    }
                }
            }
        }

        private static int GetTokenOffsetFromBlob(IntPtr blob)
        {
            Debug.Assert(blob != IntPtr.Zero);
            IntPtr tokenPointer = Marshal.ReadIntPtr(blob, (int)Marshal.OffsetOf<UnsafeNclNativeMethods.HttpApi.HTTP_REQUEST_CHANNEL_BIND_STATUS>("ChannelToken"));
            Debug.Assert(tokenPointer != IntPtr.Zero);
            return (int)IntPtrHelper.Subtract(tokenPointer, blob);
        }

        private static int GetTokenSizeFromBlob(IntPtr blob)
        {
            Debug.Assert(blob != IntPtr.Zero);
            return Marshal.ReadInt32(blob, (int)Marshal.OffsetOf<UnsafeNclNativeMethods.HttpApi.HTTP_REQUEST_CHANNEL_BIND_STATUS>("ChannelTokenSize"));
        }

        internal ChannelBinding GetChannelBinding(ulong connectionId, bool isSecureConnection)
        {
            if (!isSecureConnection)
            {
                LogHelper.LogInfo(_logger, "Channel binding is not supported for HTTP.");
                return null;
            }

            ChannelBinding result = GetChannelBindingFromTls(connectionId);

            Debug.Assert(result != null, "GetChannelBindingFromTls returned null even though OS supposedly supports Extended Protection");
            LogHelper.LogInfo(_logger, "Channel binding retrieved.");
            return result;
        }

        private unsafe ChannelBinding GetChannelBindingFromTls(ulong connectionId)
        {
            // +128 since a CBT is usually <128 thus we need to call HRCC just once. If the CBT
            // is >128 we will get ERROR_MORE_DATA and call again
            int size = RequestChannelBindStatusSize + 128;

            Debug.Assert(size >= 0);

            byte[] blob = null;
            SafeLocalFreeChannelBinding token = null;

            uint bytesReceived = 0;
            uint statusCode;

            do
            {
                blob = new byte[size];
                fixed (byte* blobPtr = blob)
                {
                    // Http.sys team: ServiceName will always be null if 
                    // HTTP_RECEIVE_SECURE_CHANNEL_TOKEN flag is set.
                    statusCode = UnsafeNclNativeMethods.HttpApi.HttpReceiveClientCertificate(
                        RequestQueueHandle,
                        connectionId,
                        (uint)UnsafeNclNativeMethods.HttpApi.HTTP_FLAGS.HTTP_RECEIVE_SECURE_CHANNEL_TOKEN,
                        blobPtr,
                        (uint)size,
                        &bytesReceived,
                        SafeNativeOverlapped.Zero);

                    if (statusCode == UnsafeNclNativeMethods.ErrorCodes.ERROR_SUCCESS)
                    {
                        int tokenOffset = GetTokenOffsetFromBlob((IntPtr)blobPtr);
                        int tokenSize = GetTokenSizeFromBlob((IntPtr)blobPtr);
                        Debug.Assert(tokenSize < Int32.MaxValue);

                        token = SafeLocalFreeChannelBinding.LocalAlloc(tokenSize);

                        Marshal.Copy(blob, tokenOffset, token.DangerousGetHandle(), tokenSize);
                    }
                    else if (statusCode == UnsafeNclNativeMethods.ErrorCodes.ERROR_MORE_DATA)
                    {
                        int tokenSize = GetTokenSizeFromBlob((IntPtr)blobPtr);
                        Debug.Assert(tokenSize < Int32.MaxValue);

                        size = RequestChannelBindStatusSize + tokenSize;
                    }
                    else if (statusCode == UnsafeNclNativeMethods.ErrorCodes.ERROR_INVALID_PARAMETER)
                    {
                        LogHelper.LogError(_logger, "GetChannelBindingFromTls", "Channel binding is not supported.");
                        return null; // old schannel library which doesn't support CBT
                    }
                    else
                    {
                        // It's up to the consumer to fail if the missing ChannelBinding matters to them.
                        LogHelper.LogException(_logger, "GetChannelBindingFromTls", new WebListenerException((int)statusCode));
                        break;
                    }
                }
            }
            while (statusCode != UnsafeNclNativeMethods.ErrorCodes.ERROR_SUCCESS);

            return token;
        }

        internal void CheckDisposed()
        {
            if (_state == State.Disposed)
            {
                throw new ObjectDisposedException(this.GetType().FullName);
            }
        }
        
        private class ConnectionCancellation
        {
            private readonly WebListener _parent;
            private volatile bool _initialized; // Must be volatile because initialization is synchronized
            private CancellationToken _cancellationToken;

            public ConnectionCancellation(WebListener parent)
            {
                _parent = parent;
            }

            internal CancellationToken GetCancellationToken(ulong connectionId)
            {
                // Initialized case is performance sensitive
                if (_initialized)
                {
                    return _cancellationToken;
                }
                return InitializeCancellationToken(connectionId);
            }

            private CancellationToken InitializeCancellationToken(ulong connectionId)
            {
                object syncObject = this;
#pragma warning disable 420 // Disable warning about volatile by reference since EnsureInitialized does volatile operations
                return LazyInitializer.EnsureInitialized(ref _cancellationToken, ref _initialized, ref syncObject, () => _parent.CreateDisconnectToken(connectionId));
#pragma warning restore 420
            }
        }
    }
}
