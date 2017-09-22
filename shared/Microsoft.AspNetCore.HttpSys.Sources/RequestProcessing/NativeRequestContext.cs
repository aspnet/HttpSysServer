// Copyright (c) .NET Foundation. All rights reserved.
// Licensed under the Apache License, Version 2.0. See License.txt in the project root for license information.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using Microsoft.Extensions.Primitives;

namespace Microsoft.AspNetCore.HttpSys.Internal
{
    internal unsafe class NativeRequestContext : IDisposable
    {
        private const int AlignmentPadding = 8;
        private HttpApiTypes.HTTP_REQUEST* _nativeRequest;
        private byte[] _backingBuffer;
        private IntPtr _originalBufferAddress;
        private int _bufferAlignment;
        private SafeNativeOverlapped _nativeOverlapped;

        // To be used by HttpSys
        internal NativeRequestContext(NativeRequestInput input)
        {
            _nativeOverlapped = input.NativeOverlapped;
            _bufferAlignment = input.BufferAlignment;
            _nativeRequest = input.NativeRequest;
            _backingBuffer = input.BackingBuffer;
        }

        // To be used by IIS Integration.
        internal NativeRequestContext(HttpApiTypes.HTTP_REQUEST* request)
        {
            _nativeRequest = request;
        }


        internal SafeNativeOverlapped NativeOverlapped => _nativeOverlapped;

        internal HttpApiTypes.HTTP_REQUEST* NativeRequest
        {
            get
            {
                Debug.Assert(_nativeRequest != null || _backingBuffer == null, "native request accessed after ReleasePins().");
                return _nativeRequest;
            }
        }

        internal byte[] BackingBuffer
        {
            get
            {
                return _backingBuffer;
            }
        }

        internal HttpApiTypes.HTTP_REQUEST_V2* NativeRequestV2
        {
            get
            {
                Debug.Assert(_nativeRequest != null || _backingBuffer == null, "native request accessed after ReleasePins().");
                return (HttpApiTypes.HTTP_REQUEST_V2*)_nativeRequest;
            }
        }

        internal ulong RequestId
        {
            get { return NativeRequest->RequestId; }
            set { NativeRequest->RequestId = value; }
        }

        internal ulong ConnectionId => NativeRequest->ConnectionId;

        internal HttpApiTypes.HTTP_VERB VerbId => NativeRequest->Verb;

        internal ulong UrlContext => NativeRequest->UrlContext;

        internal ushort UnknownHeaderCount => NativeRequest->Headers.UnknownHeaderCount;

        internal SslStatus SslStatus
        {
            get
            {
                return NativeRequest->pSslInfo == null ? SslStatus.Insecure :
                    NativeRequest->pSslInfo->SslClientCertNegotiated == 0 ? SslStatus.NoClientCert :
                    SslStatus.ClientCert;
            }
        }

        internal uint Size
        {
            get { return (uint)_backingBuffer.Length - AlignmentPadding; }
        }

        // ReleasePins() should be called exactly once.  It must be called before Dispose() is called, which means it must be called
        // before an object (Request) which closes the RequestContext on demand is returned to the application.
        internal void ReleasePins()
        {
            Debug.Assert(_nativeRequest != null || _backingBuffer == null, "RequestContextBase::ReleasePins()|ReleasePins() called twice.");
            _originalBufferAddress = (IntPtr)_nativeRequest;
            _nativeRequest = null;
            _nativeOverlapped?.Dispose();
            _nativeOverlapped = null;
        }

        public void Dispose()
        {
            Debug.Assert(_nativeRequest == null, "RequestContextBase::Dispose()|Dispose() called before ReleasePins().");
            _nativeOverlapped?.Dispose();
        }

        internal void Reset(NativeRequestInput input, ulong requestId = 0)
        {
            Debug.Assert(_nativeRequest != null || _backingBuffer == null, "RequestContextBase::Dispose()|SetNativeRequest() called after ReleasePins().");
            _nativeOverlapped?.Dispose();
            _nativeOverlapped = input.NativeOverlapped;
            _bufferAlignment = input.BufferAlignment;
            _nativeRequest = input.NativeRequest;
            _backingBuffer = input.BackingBuffer;
            RequestId = requestId;
        }

        // These methods require the HTTP_REQUEST to still be pinned in its original location.

        internal string GetVerb()
        {
            var verb = NativeRequest->Verb;
            if (verb > HttpApiTypes.HTTP_VERB.HttpVerbUnknown && verb < HttpApiTypes.HTTP_VERB.HttpVerbMaximum)
            {
                return HttpApiTypes.HttpVerbs[(int)verb];
            }
            else if (verb == HttpApiTypes.HTTP_VERB.HttpVerbUnknown && NativeRequest->pUnknownVerb != null)
            {
                return HeaderEncoding.GetString(NativeRequest->pUnknownVerb, NativeRequest->UnknownVerbLength);
            }

            return null;
        }

        internal string GetRawUrl()
        {
            if (NativeRequest->pRawUrl != null && NativeRequest->RawUrlLength > 0)
            {
                return Marshal.PtrToStringAnsi((IntPtr)NativeRequest->pRawUrl, NativeRequest->RawUrlLength);
            }
            return null;
        }

        internal byte[] GetRawUrlInBytes()
        {
            if (NativeRequest->pRawUrl != null && NativeRequest->RawUrlLength > 0)
            {
                var result = new byte[NativeRequest->RawUrlLength];
                Marshal.Copy((IntPtr)NativeRequest->pRawUrl, result, 0, NativeRequest->RawUrlLength);

                return result;
            }

            return null;
        }

        internal CookedUrl GetCookedUrl()
        {
            return new CookedUrl(NativeRequest->CookedUrl);
        }

        internal Version GetVersion()
        {
            var major = NativeRequest->Version.MajorVersion;
            var minor = NativeRequest->Version.MinorVersion;
            if (major == 1 && minor == 1)
            {
                return Constants.V1_1;
            }
            else if (major == 1 && minor == 0)
            {
                return Constants.V1_0;
            }
            return new Version(major, minor);
        }

        internal bool CheckAuthenticated()
        {
            var requestInfo = NativeRequestV2->pRequestInfo;
            var infoCount = NativeRequestV2->RequestInfoCount;

            for (int i = 0; i < infoCount; i++)
            {
                var info = &requestInfo[i];
                if (info != null
                    && info->InfoType == HttpApiTypes.HTTP_REQUEST_INFO_TYPE.HttpRequestInfoTypeAuth
                    && info->pInfo->AuthStatus == HttpApiTypes.HTTP_AUTH_STATUS.HttpAuthStatusSuccess)
                {
                    return true;
                }
            }
            return false;
        }

        // These methods are for accessing the request structure after it has been unpinned. They need to adjust addresses
        // in case GC has moved the original object.

        internal string GetKnownHeader(HttpSysRequestHeader header)
        {
            fixed (byte* pMemoryBlob = _backingBuffer)
            {
                var request = (HttpApiTypes.HTTP_REQUEST*)(pMemoryBlob + _bufferAlignment);
                long fixup = pMemoryBlob - (byte*)_originalBufferAddress;
                int headerIndex = (int)header;
                string value = null;

                HttpApiTypes.HTTP_KNOWN_HEADER* pKnownHeader = (&request->Headers.KnownHeaders) + headerIndex;
                // For known headers, when header value is empty, RawValueLength will be 0 and
                // pRawValue will point to empty string ("\0")
                if (pKnownHeader->pRawValue != null)
                {
                    value = HeaderEncoding.GetString(pKnownHeader->pRawValue + fixup, pKnownHeader->RawValueLength);
                }

                return value;
            }
        }

        internal void GetUnknownHeaders(IDictionary<string, StringValues> unknownHeaders)
        {
            // Return value.
            fixed (byte* pMemoryBlob = _backingBuffer)
            {
                var request = (HttpApiTypes.HTTP_REQUEST*)(pMemoryBlob + _bufferAlignment);
                long fixup = pMemoryBlob - (byte*)_originalBufferAddress;
                int index;

                // unknown headers
                if (request->Headers.UnknownHeaderCount != 0)
                {
                    var pUnknownHeader = (HttpApiTypes.HTTP_UNKNOWN_HEADER*)(fixup + (byte*)request->Headers.pUnknownHeaders);
                    for (index = 0; index < request->Headers.UnknownHeaderCount; index++)
                    {
                        // For unknown headers, when header value is empty, RawValueLength will be 0 and
                        // pRawValue will be null.
                        if (pUnknownHeader->pName != null && pUnknownHeader->NameLength > 0)
                        {
                            var headerName = HeaderEncoding.GetString(pUnknownHeader->pName + fixup, pUnknownHeader->NameLength);
                            string headerValue;
                            if (pUnknownHeader->pRawValue != null && pUnknownHeader->RawValueLength > 0)
                            {
                                headerValue = HeaderEncoding.GetString(pUnknownHeader->pRawValue + fixup, pUnknownHeader->RawValueLength);
                            }
                            else
                            {
                                headerValue = string.Empty;
                            }
                            // Note that Http.Sys currently collapses all headers of the same name to a single coma separated string,
                            // so we can just call Set.
                            unknownHeaders[headerName] = headerValue;
                        }
                        pUnknownHeader++;
                    }
                }
            }
        }

        internal SocketAddress GetRemoteEndPoint()
        {
            return GetEndPoint(localEndpoint: false);
        }

        internal SocketAddress GetLocalEndPoint()
        {
            return GetEndPoint(localEndpoint: true);
        }

        private SocketAddress GetEndPoint(bool localEndpoint)
        {
            fixed (byte* pMemoryBlob = _backingBuffer)
            {
                var request = (HttpApiTypes.HTTP_REQUEST*)(pMemoryBlob + _bufferAlignment);
                var source = localEndpoint ? (byte*)request->Address.pLocalAddress : (byte*)request->Address.pRemoteAddress;

                if (source == null)
                {
                    return null;
                }
                var address = (IntPtr)(pMemoryBlob + _bufferAlignment - (byte*)_originalBufferAddress + source);
                return CopyOutAddress(address);
            }
        }

        private static SocketAddress CopyOutAddress(IntPtr address)
        {
            ushort addressFamily = *((ushort*)address);
            if (addressFamily == (ushort)AddressFamily.InterNetwork)
            {
                var v4address = new SocketAddress(AddressFamily.InterNetwork, SocketAddress.IPv4AddressSize);
                fixed (byte* pBuffer = v4address.Buffer)
                {
                    for (int index = 2; index < SocketAddress.IPv4AddressSize; index++)
                    {
                        pBuffer[index] = ((byte*)address)[index];
                    }
                }
                return v4address;
            }
            if (addressFamily == (ushort)AddressFamily.InterNetworkV6)
            {
                var v6address = new SocketAddress(AddressFamily.InterNetworkV6, SocketAddress.IPv6AddressSize);
                fixed (byte* pBuffer = v6address.Buffer)
                {
                    for (int index = 2; index < SocketAddress.IPv6AddressSize; index++)
                    {
                        pBuffer[index] = ((byte*)address)[index];
                    }
                }
                return v6address;
            }

            return null;
        }

        internal uint GetChunks(ref int dataChunkIndex, ref uint dataChunkOffset, byte[] buffer, int offset, int size)
        {
            // Return value.
            uint dataRead = 0;
            fixed (byte* pMemoryBlob = _backingBuffer)
            {
                var request = (HttpApiTypes.HTTP_REQUEST*)(pMemoryBlob + _bufferAlignment);
                long fixup = pMemoryBlob - (byte*)_originalBufferAddress;

                if (request->EntityChunkCount > 0 && dataChunkIndex < request->EntityChunkCount && dataChunkIndex != -1)
                {
                    var pDataChunk = (HttpApiTypes.HTTP_DATA_CHUNK*)(fixup + (byte*)&request->pEntityChunks[dataChunkIndex]);

                    fixed (byte* pReadBuffer = buffer)
                    {
                        byte* pTo = &pReadBuffer[offset];

                        while (dataChunkIndex < request->EntityChunkCount && dataRead < size)
                        {
                            if (dataChunkOffset >= pDataChunk->fromMemory.BufferLength)
                            {
                                dataChunkOffset = 0;
                                dataChunkIndex++;
                                pDataChunk++;
                            }
                            else
                            {
                                byte* pFrom = (byte*)pDataChunk->fromMemory.pBuffer + dataChunkOffset + fixup;

                                uint bytesToRead = pDataChunk->fromMemory.BufferLength - (uint)dataChunkOffset;
                                if (bytesToRead > (uint)size)
                                {
                                    bytesToRead = (uint)size;
                                }
                                for (uint i = 0; i < bytesToRead; i++)
                                {
                                    *(pTo++) = *(pFrom++);
                                }
                                dataRead += bytesToRead;
                                dataChunkOffset += bytesToRead;
                            }
                        }
                    }
                }
                // we're finished.
                if (dataChunkIndex == request->EntityChunkCount)
                {
                    dataChunkIndex = -1;
                }
            }

            return dataRead;
        }
    }
}
