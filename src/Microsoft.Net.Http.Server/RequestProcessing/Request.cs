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
// <copyright file="HttpListenerRequest.cs" company="Microsoft">
//     Copyright (c) Microsoft Corporation.  All rights reserved.
// </copyright>
//------------------------------------------------------------------------------

using System;
using System.Collections;
using System.Globalization;
using System.IO;
using System.Net;
using System.Runtime.InteropServices;
using System.Security.Claims;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Framework.Logging;

namespace Microsoft.Net.Http.Server
{
    public sealed class Request
    {
        private RequestContext _requestContext;
        private NativeRequestContext _nativeRequestContext;

        private ulong _requestId;
        private ulong _connectionId;
        private ulong _contextId;

        private SslStatus _sslStatus;

        private string _httpMethod;
        private Version _httpVersion;

        // private Uri _requestUri;
        private string _rawUrl;
        private string _cookedUrlHost;
        private string _cookedUrlPath;
        private string _cookedUrlQuery;
        private string _pathBase;
        private string _path;

        private X509Certificate _clientCert;

        private RequestHeaders _headersImpl;
        private HeaderCollection _headers;
        private BoundaryType _contentBoundaryType;
        private long? _contentLength;
        private Stream _nativeStream;

        private SocketAddress _localEndPoint;
        private SocketAddress _remoteEndPoint;

        private ClaimsPrincipal _user;

        private bool _isDisposed = false;

        internal unsafe Request(RequestContext httpContext, NativeRequestContext memoryBlob)
        {
            // TODO: Verbose log
            _requestContext = httpContext;
            _nativeRequestContext = memoryBlob;
            _contentBoundaryType = BoundaryType.None;

            // Set up some of these now to avoid refcounting on memory blob later.
            _requestId = memoryBlob.RequestBlob->RequestId;
            _connectionId = memoryBlob.RequestBlob->ConnectionId;
            _contextId = memoryBlob.RequestBlob->UrlContext;
            _sslStatus = memoryBlob.RequestBlob->pSslInfo == null ? SslStatus.Insecure :
                memoryBlob.RequestBlob->pSslInfo->SslClientCertNegotiated == 0 ? SslStatus.NoClientCert :
                SslStatus.ClientCert;
            if (memoryBlob.RequestBlob->pRawUrl != null && memoryBlob.RequestBlob->RawUrlLength > 0)
            {
                _rawUrl = Marshal.PtrToStringAnsi((IntPtr)memoryBlob.RequestBlob->pRawUrl, memoryBlob.RequestBlob->RawUrlLength);
            }

            UnsafeNclNativeMethods.HttpApi.HTTP_COOKED_URL cookedUrl = memoryBlob.RequestBlob->CookedUrl;
            if (cookedUrl.pHost != null && cookedUrl.HostLength > 0)
            {
                _cookedUrlHost = Marshal.PtrToStringUni((IntPtr)cookedUrl.pHost, cookedUrl.HostLength / 2);
            }
            if (cookedUrl.pAbsPath != null && cookedUrl.AbsPathLength > 0)
            {
                _cookedUrlPath = Marshal.PtrToStringUni((IntPtr)cookedUrl.pAbsPath, cookedUrl.AbsPathLength / 2);
            }
            if (cookedUrl.pQueryString != null && cookedUrl.QueryStringLength > 0)
            {
                _cookedUrlQuery = Marshal.PtrToStringUni((IntPtr)cookedUrl.pQueryString, cookedUrl.QueryStringLength / 2);
            }

            UrlPrefix prefix = httpContext.Server.UrlPrefixes.GetPrefix((int)_contextId);
            string orriginalPath = RequestPath;

            // These paths are both unescaped already.
            if (orriginalPath.Length == prefix.Path.Length - 1)
            {
                // They matched exactly except for the trailing slash.
                _pathBase = orriginalPath;
                _path = string.Empty;
            }
            else
            {
                // url: /base/path, prefix: /base/, base: /base, path: /path
                // url: /, prefix: /, base: , path: /
                _pathBase = orriginalPath.Substring(0, prefix.Path.Length - 1);
                _path = orriginalPath.Substring(prefix.Path.Length - 1);
            }

            int major = memoryBlob.RequestBlob->Version.MajorVersion;
            int minor = memoryBlob.RequestBlob->Version.MinorVersion;
            if (major == 1 && minor == 1)
            {
                _httpVersion = Constants.V1_1;
            }
            else if (major == 1 && minor == 0)
            {
                _httpVersion = Constants.V1_0;
            }
            else
            {
                _httpVersion = new Version(major, minor);
            }

            _httpMethod = UnsafeNclNativeMethods.HttpApi.GetVerb(RequestBuffer, OriginalBlobAddress);
            _headersImpl = new RequestHeaders(_nativeRequestContext);
            _headers = new HeaderCollection(_headersImpl);

            UnsafeNclNativeMethods.HttpApi.HTTP_REQUEST_V2* requestV2 = (UnsafeNclNativeMethods.HttpApi.HTTP_REQUEST_V2*)memoryBlob.RequestBlob;
            _user = AuthenticationManager.GetUser(requestV2->pRequestInfo);

            // TODO: Verbose log parameters

            if (_requestContext.Logger.IsEnabled(LogLevel.Verbose))
            {
                RequestContext.Logger.WriteVerbose(new ReceiveRequestLogContext(this));
            }
        }

        internal SslStatus SslStatus
        {
            get
            {
                return _sslStatus;
            }
        }

        internal ulong ConnectionId
        {
            get
            {
                return _connectionId;
            }
        }

        internal ulong ContextId
        {
            get { return _contextId; }
        }

        internal RequestContext RequestContext
        {
            get
            {
                return _requestContext;
            }
        }

        internal byte[] RequestBuffer
        {
            get
            {
                CheckDisposed();
                return _nativeRequestContext.RequestBuffer;
            }
        }

        internal IntPtr OriginalBlobAddress
        {
            get
            {
                CheckDisposed();
                return _nativeRequestContext.OriginalBlobAddress;
            }
        }

        // With the leading ?, if any
        public string QueryString
        {
            get
            {
                return _cookedUrlQuery ?? string.Empty;
            }
            set
            {
                _cookedUrlQuery = value;
            }
        }

        internal ulong RequestId
        {
            get
            {
                return _requestId;
            }
        }

        public long? ContentLength
        {
            get
            {
                if (_contentBoundaryType == BoundaryType.None)
                {
                    string transferEncoding = Headers.Get(HttpKnownHeaderNames.TransferEncoding) ?? string.Empty;
                    if (string.Equals("chunked", transferEncoding.Trim(), StringComparison.OrdinalIgnoreCase))
                    {
                        _contentBoundaryType = BoundaryType.Chunked;
                    }
                    else
                    {
                        string length = Headers.Get(HttpKnownHeaderNames.ContentLength) ?? string.Empty;
                        long value;
                        if (long.TryParse(length.Trim(), NumberStyles.None,
                            CultureInfo.InvariantCulture.NumberFormat, out value))
                        {
                            _contentBoundaryType = BoundaryType.ContentLength;
                            _contentLength = value;
                        }
                        else
                        {
                            _contentBoundaryType = BoundaryType.Invalid;
                        }
                    }
                }

                return _contentLength;
            }
        }

        public HeaderCollection Headers
        {
            get { return _headers; }
        }

        public string Method
        {
            get { return _httpMethod; }
        }

        public Stream Body
        {
            get
            {
                if (_nativeStream == null)
                {
                    _nativeStream = HasEntityBody ? new RequestStream(RequestContext) : Stream.Null;
                }
                return _nativeStream;
            }
        }

        public string PathBase
        {
            get { return _pathBase; }
        }

        public string Path
        {
            get { return _path; }
        }

        public bool IsLocal
        {
            get
            {
                return LocalEndPoint.GetIPAddress().Equals(RemoteEndPoint.GetIPAddress());
            }
        }

        public bool IsSecureConnection
        {
            get
            {
                return _sslStatus != SslStatus.Insecure;
            }
        }
        /*
        internal string RawUrl
        {
            get
            {
                return _rawUrl;
            }
        }
        */
        public Version ProtocolVersion
        {
            get
            {
                return _httpVersion;
            }
        }

        public bool HasEntityBody
        {
            get
            {
                // accessing the ContentLength property delay creates _contentBoundaryType
                return (ContentLength.HasValue && ContentLength.Value > 0 && _contentBoundaryType == BoundaryType.ContentLength)
                    || _contentBoundaryType == BoundaryType.Chunked;
            }
        }

        private SocketAddress RemoteEndPoint
        {
            get
            {
                PopulateRemoteEndpoint();
                return _remoteEndPoint;
            }
        }

        private SocketAddress LocalEndPoint
        {
            get
            {
                PopulateLocalEndpoint();
                return _localEndPoint;
            }
        }

        public IPAddress RemoteIpAddress
        {
            get { return RemoteEndPoint.GetIPAddress(); }
        }

        public IPAddress LocalIpAddress
        {
            get { return LocalEndPoint.GetIPAddress(); }
        }

        public int RemotePort
        {
            get { return RemoteEndPoint.GetPort(); }
        }

        public int LocalPort
        {
            get { return LocalEndPoint.GetPort(); }
        }

        public string Scheme
        {
            get { return IsSecureConnection ? Constants.HttpsScheme : Constants.HttpScheme; }
        }
        /*
        internal Uri RequestUri
        {
            get
            {
                if (_requestUri == null)
                {
                    _requestUri = RequestUriBuilder.GetRequestUri(
                        _rawUrl, RequestScheme, _cookedUrlHost, _cookedUrlPath, _cookedUrlQuery);
                }

                return _requestUri;
            }
        }
        */
        internal string RequestPath
        {
            get
            {
                return RequestUriBuilder.GetRequestPath(_rawUrl, _cookedUrlPath);
            }
        }

        internal bool IsUpgradable
        {
            get
            {
                // HTTP.Sys allows you to upgrade anything to opaque unless content-length > 0 or chunked are specified.
                return !HasEntityBody;
            }
        }

        public string ContentType
        {
            get
            {
                return Headers.Get(HttpKnownHeaderNames.ContentLength);
            }
        }

        internal ClaimsPrincipal User
        {
            get { return _user; }
        }

        internal bool IsMarshallingCompleted
        {
            get; private set;
        }

        internal UnsafeNclNativeMethods.HttpApi.HTTP_VERB GetKnownMethod()
        {
            return UnsafeNclNativeMethods.HttpApi.GetKnownVerb(RequestBuffer, OriginalBlobAddress);
        }

        // Populates the client certificate.  The result may be null if there is no client cert.
        // TODO: Does it make sense for this to be invoked multiple times (e.g. renegotiate)? Client and server code appear to
        // enable this, but it's unclear what Http.Sys would do.
        public async Task<X509Certificate> GetClientCertificateAsync(CancellationToken cancellationToken = default(CancellationToken))
        {
            if (SslStatus == SslStatus.Insecure)
            {
                // Non-SSL
                return null;
            }
            // TODO: Verbose log
            if (_clientCert != null)
            {
                return _clientCert;
            }

            ClientCertLoader certLoader = new ClientCertLoader(RequestContext);
            try
            {
                await certLoader.LoadClientCertificateAsync(cancellationToken).SupressContext();
                // Populate the environment.
                if (certLoader.ClientCert != null)
                {
                    _clientCert = certLoader.ClientCert;
                }
                // TODO: Expose errors and exceptions?
            }
            catch (Exception)
            {
                if (certLoader != null)
                {
                    certLoader.Dispose();
                }
                throw;
            }
            return _clientCert;
        }

        // Use this to save the blob from dispose if this object was never used (never given to a user) and is about to be
        // disposed.
        internal void DetachBlob(NativeRequestContext memoryBlob)
        {
            if (memoryBlob != null && (object)memoryBlob == (object)_nativeRequestContext)
            {
                _nativeRequestContext = null;
            }
        }

        // Finalizes ownership of the memory blob.  DetachBlob can't be called after this.
        internal void ReleasePins()
        {
            _nativeRequestContext.ReleasePins();
        }

        // should only be called from RequestContext
        internal void Dispose()
        {
            // TODO: Verbose log
            _isDisposed = true;
            NativeRequestContext memoryBlob = _nativeRequestContext;
            if (memoryBlob != null)
            {
                memoryBlob.Dispose();
                _nativeRequestContext = null;
            }
            if (_nativeStream != null)
            {
                _nativeStream.Dispose();
            }
        }

        internal void CheckDisposed()
        {
            if (_isDisposed)
            {
                throw new ObjectDisposedException(this.GetType().FullName);
            }
        }

        internal void CompleteMarshalling()
        {
            if (_nativeRequestContext == null)
            {
                throw new InvalidOperationException("Native request context is already discarded. Cannot complete marshalling");
            }

            unsafe
            {
                fixed (byte* pMemoryBlob = _nativeRequestContext.RequestBuffer)
                {
                    UnsafeNclNativeMethods.HttpApi.HTTP_REQUEST* request = (UnsafeNclNativeMethods.HttpApi.HTTP_REQUEST*)pMemoryBlob;
                    if (request->EntityChunkCount > 0)
                    {
                        throw new NotImplementedException("CompleteMarchalling currently only supports WebSocket case and should not contain entity chunk");
                    }
                }
            }

            // upgrade all pass-through fields so backing buffer can be freed
            PopulateLocalEndpoint();
            PopulateRemoteEndpoint();
            _headersImpl.CompleteMarshalling();

            _nativeRequestContext.Dispose();
            _nativeRequestContext = null;
            IsMarshallingCompleted = true;
        }

        internal void SwitchToOpaqueMode()
        {
            if (_nativeStream == null || _nativeStream == Stream.Null)
            {
                _nativeStream = new RequestStream(RequestContext);
            }
        }

        private void PopulateRemoteEndpoint()
        {
            if (_remoteEndPoint == null)
            {
                _remoteEndPoint = UnsafeNclNativeMethods.HttpApi.GetRemoteEndPoint(RequestBuffer, OriginalBlobAddress);
            }
        }

        private void PopulateLocalEndpoint()
        {
            if (_localEndPoint == null)
            {
                _localEndPoint = UnsafeNclNativeMethods.HttpApi.GetLocalEndPoint(RequestBuffer, OriginalBlobAddress);
            }
        }

        private class ReceiveRequestLogContext : LoggerStructureBase
        {
            private readonly Request _request;

            internal ReceiveRequestLogContext(Request request)
            {
                _request = request;
                Message = "Received Request";
            }

            public string Method { get { return _request.Method; } }
            public string PathBase { get { return _request.PathBase; } }
            public string Path { get { return _request.Path; } }
            public string Query { get { return _request.QueryString; } }
            public string Protocol { get { return "HTTP/" + _request.ProtocolVersion.ToString(2); } }
            public IEnumerable Headers { get { return new HeadersLogStructure(_request.Headers); } }

            public override string Format()
            {
                var requestBuilder = new StringBuilder("Received request: ");

                // GET /path?query HTTP/1.1
                requestBuilder.Append(Method);
                requestBuilder.Append(" ");
                requestBuilder.Append(PathBase);
                requestBuilder.Append(Path);
                requestBuilder.Append(Query);
                requestBuilder.Append(" ");
                requestBuilder.Append(Protocol);
                requestBuilder.Append("; Headers: { ");

                foreach (var header in _request.Headers)
                {
                    foreach (var value in header.Value)
                    {
                        requestBuilder.Append(header.Key);
                        requestBuilder.Append(": ");
                        requestBuilder.Append(value);
                        requestBuilder.Append("; ");
                    }
                }
                requestBuilder.Append("}");
                return requestBuilder.ToString();
            }
        }
    }
}
