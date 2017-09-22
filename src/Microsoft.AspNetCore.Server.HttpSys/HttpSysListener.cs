// Copyright (c) .NET Foundation. All rights reserved.
// Licensed under the Apache License, Version 2.0. See License.txt in the project root for license information.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.HttpSys.Internal;
using Microsoft.Extensions.Logging;

namespace Microsoft.AspNetCore.Server.HttpSys
{
    /// <summary>
    /// An HTTP server wrapping the Http.Sys APIs that accepts requests.
    /// </summary>
    internal class HttpSysListener : IDisposable
    {
        // Win8# 559317 fixed a bug in Http.sys's HttpReceiveClientCertificate method.
        // Without this fix IOCP callbacks were not being called although ERROR_IO_PENDING was
        // returned from HttpReceiveClientCertificate when using the 
        // FileCompletionNotificationModes.SkipCompletionPortOnSuccess flag.
        // This bug was only hit when the buffer passed into HttpReceiveClientCertificate
        // (1500 bytes initially) is too small for the certificate.
        // Due to this bug in downlevel operating systems the FileCompletionNotificationModes.SkipCompletionPortOnSuccess
        // flag is only used on Win8 and later.
        internal static readonly bool SkipIOCPCallbackOnSuccess = ComNetOS.IsWin8orLater;

        // Mitigate potential DOS attacks by limiting the number of unknown headers we accept.  Numerous header names 
        // with hash collisions will cause the server to consume excess CPU.  1000 headers limits CPU time to under 
        // 0.5 seconds per request.  Respond with a 400 Bad Request.
        private const int UnknownHeaderLimit = 1000;

        private volatile State _state; // m_State is set only within lock blocks, but often read outside locks.

        private ServerSession _serverSession;
        private UrlGroup _urlGroup;
        private RequestQueue _requestQueue;
        private DisconnectListener _disconnectListener;

        private object _internalLock;

        public HttpSysListener(HttpSysOptions options, ILoggerFactory loggerFactory)
        {
            if (options == null)
            {
                throw new ArgumentNullException(nameof(options));
            }
            if (loggerFactory == null)
            {
                throw new ArgumentNullException(nameof(loggerFactory));
            }

            if (!HttpApi.Supported)
            {
                throw new PlatformNotSupportedException();
            }

            Debug.Assert(HttpApi.ApiVersion == HttpApi.HTTP_API_VERSION.Version20, "Invalid Http api version");

            Options = options;

            Logger = LogHelper.CreateLogger(loggerFactory, typeof(HttpSysListener));

            _state = State.Stopped;
            _internalLock = new object();

            // V2 initialization sequence:
            // 1. Create server session
            // 2. Create url group
            // 3. Create request queue - Done in Start()
            // 4. Add urls to url group - Done in Start()
            // 5. Attach request queue to url group - Done in Start()

            try
            {
                _serverSession = new ServerSession();

                _urlGroup = new UrlGroup(_serverSession, Logger);

                _requestQueue = new RequestQueue(_urlGroup, Logger);

                _disconnectListener = new DisconnectListener(_requestQueue, Logger);
            }
            catch (Exception exception)
            {
                // If Url group or request queue creation failed, close server session before throwing.
                _requestQueue?.Dispose();
                _urlGroup?.Dispose();
                _serverSession?.Dispose();
                LogHelper.LogException(Logger, ".Ctor", exception);
                throw;
            }
        }

        internal enum State
        {
            Stopped,
            Started,
            Disposed,
        }

        internal ILogger Logger { get; private set; }

        internal UrlGroup UrlGroup
        {
            get { return _urlGroup; }
        }

        internal RequestQueue RequestQueue
        {
            get { return _requestQueue; }
        }

        internal DisconnectListener DisconnectListener
        {
            get { return _disconnectListener; }
        }

        public HttpSysOptions Options { get; }

        public bool IsListening
        {
            get { return _state == State.Started; }
        }

        /// <summary>
        /// Start accepting incoming requests.
        /// </summary>
        public void Start()
        {
            CheckDisposed();

            LogHelper.LogInfo(Logger, "Start");

            // Make sure there are no race conditions between Start/Stop/Abort/Close/Dispose.
            // Start needs to setup all resources. Abort/Stop must not interfere while Start is
            // allocating those resources.
            lock (_internalLock)
            {
                try
                {
                    CheckDisposed();
                    if (_state == State.Started)
                    {
                        return;
                    }

                    Options.Apply(UrlGroup, RequestQueue);

                    _requestQueue.AttachToUrlGroup();

                    // All resources are set up correctly. Now add all prefixes.
                    try
                    {
                        Options.UrlPrefixes.RegisterAllPrefixes(UrlGroup);
                    }
                    catch (HttpSysException)
                    {
                        // If an error occurred while adding prefixes, free all resources allocated by previous steps.
                        _requestQueue.DetachFromUrlGroup();
                        throw;
                    }

                    _state = State.Started;
                }
                catch (Exception exception)
                {
                    // Make sure the HttpListener instance can't be used if Start() failed.
                    _state = State.Disposed;
                    DisposeInternal();
                    LogHelper.LogException(Logger, "Start", exception);
                    throw;
                }
            }
        }

        private void Stop()
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

                    Options.UrlPrefixes.UnregisterAllPrefixes();

                    _state = State.Stopped;

                    _requestQueue.DetachFromUrlGroup();
                }
            }
            catch (Exception exception)
            {
                LogHelper.LogException(Logger, "Stop", exception);
                throw;
            }
        }

        /// <summary>
        /// Stop the server and clean up.
        /// </summary>
        public void Dispose()
        {
            Dispose(true);
        }

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
                    LogHelper.LogInfo(Logger, "Dispose");

                    Stop();
                    DisposeInternal();
                }
                catch (Exception exception)
                {
                    LogHelper.LogException(Logger, "Dispose", exception);
                    throw;
                }
                finally
                {
                    _state = State.Disposed;
                }
            }
        }

        private void DisposeInternal()
        {
            // V2 stopping sequence:
            // 1. Detach request queue from url group - Done in Stop()/Abort()
            // 2. Remove urls from url group - Done in Stop()
            // 3. Close request queue - Done in Stop()/Abort()
            // 4. Close Url group.
            // 5. Close server session.

            _requestQueue.Dispose();

            _urlGroup.Dispose();

            Debug.Assert(_serverSession != null, "ServerSessionHandle is null in CloseV2Config");
            Debug.Assert(!_serverSession.Id.IsInvalid, "ServerSessionHandle is invalid in CloseV2Config");

            _serverSession.Dispose();
        }

        /// <summary>
        /// Accept a request from the incoming request queue.
        /// </summary>
        public Task<RequestContext> AcceptAsync()
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
                    // some other bad error, possible(?) return values are:
                    // ERROR_INVALID_HANDLE, ERROR_INSUFFICIENT_BUFFER, ERROR_OPERATION_ABORTED
                    asyncResult.Dispose();
                    throw new HttpSysException((int)statusCode);
                }
            }
            catch (Exception exception)
            {
                LogHelper.LogException(Logger, "GetContextAsync", exception);
                throw;
            }

            return asyncResult.Task;
        }

        internal unsafe bool ValidateRequest(NativeRequestContext requestMemory)
        {
            // Block potential DOS attacks
            if (requestMemory.UnknownHeaderCount > UnknownHeaderLimit)
            {
                SendError(requestMemory.RequestId, StatusCodes.Status400BadRequest, authChallenges: null);
                return false;
            }
            return true;
        }

        internal unsafe bool ValidateAuth(NativeRequestContext requestMemory)
        {
            if (!Options.Authentication.AllowAnonymous && !requestMemory.CheckAuthenticated())
            {
                SendError(requestMemory.RequestId, StatusCodes.Status401Unauthorized,
                    AuthenticationManager.GenerateChallenges(Options.Authentication.Schemes));
                return false;
            }
            return true;
        }

        private unsafe void SendError(ulong requestId, int httpStatusCode, IList<string> authChallenges)
        {
            HttpApi.HTTP_RESPONSE_V2 httpResponse = new HttpApi.HTTP_RESPONSE_V2();
            httpResponse.Response_V1.Version = new HttpApi.HTTP_VERSION();
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

                    HttpApi.HTTP_RESPONSE_INFO[] knownHeaderInfo = null;
                    knownHeaderInfo = new HttpApi.HTTP_RESPONSE_INFO[1];
                    gcHandle = GCHandle.Alloc(knownHeaderInfo, GCHandleType.Pinned);
                    pinnedHeaders.Add(gcHandle);
                    httpResponse.pResponseInfo = (HttpApi.HTTP_RESPONSE_INFO*)gcHandle.AddrOfPinnedObject();

                    knownHeaderInfo[httpResponse.ResponseInfoCount].Type = HttpApi.HTTP_RESPONSE_INFO_TYPE.HttpResponseInfoTypeMultipleKnownHeaders;
                    knownHeaderInfo[httpResponse.ResponseInfoCount].Length =
                        (uint)Marshal.SizeOf<HttpApi.HTTP_MULTIPLE_KNOWN_HEADERS>();

                    HttpApi.HTTP_MULTIPLE_KNOWN_HEADERS header = new HttpApi.HTTP_MULTIPLE_KNOWN_HEADERS();

                    header.HeaderId = HttpApi.HTTP_RESPONSE_HEADER_ID.Enum.HttpHeaderWwwAuthenticate;
                    header.Flags = HttpApi.HTTP_RESPONSE_INFO_FLAGS.PreserveOrder; // The docs say this is for www-auth only.

                    HttpApi.HTTP_KNOWN_HEADER[] nativeHeaderValues = new HttpApi.HTTP_KNOWN_HEADER[authChallenges.Count];
                    gcHandle = GCHandle.Alloc(nativeHeaderValues, GCHandleType.Pinned);
                    pinnedHeaders.Add(gcHandle);
                    header.KnownHeaders = (HttpApi.HTTP_KNOWN_HEADER*)gcHandle.AddrOfPinnedObject();

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
                    knownHeaderInfo[0].pInfo = (HttpApi.HTTP_MULTIPLE_KNOWN_HEADERS*)gcHandle.AddrOfPinnedObject();

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
                            HttpApi.HttpSendHttpResponse(
                                _requestQueue.Handle,
                                requestId,
                                0,
                                &httpResponse,
                                null,
                                &dataWritten,
                                IntPtr.Zero,
                                0,
                                SafeNativeOverlapped.Zero,
                                IntPtr.Zero);
                    }
                }
                if (statusCode != UnsafeNclNativeMethods.ErrorCodes.ERROR_SUCCESS)
                {
                    // if we fail to send a 401 something's seriously wrong, abort the request
                    HttpApi.HttpCancelHttpRequest(_requestQueue.Handle, requestId, IntPtr.Zero);
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

        private void CheckDisposed()
        {
            if (_state == State.Disposed)
            {
                throw new ObjectDisposedException(this.GetType().FullName);
            }
        }
    }
}
