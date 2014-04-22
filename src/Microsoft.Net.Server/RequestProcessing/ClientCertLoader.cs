﻿// -----------------------------------------------------------------------
// <copyright file="ClientCertLoader.cs" company="Microsoft">
//      Copyright (c) Microsoft Corporation.  All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

using System;
using System.Diagnostics.CodeAnalysis;
using System.Diagnostics.Contracts;
using System.Runtime.InteropServices;
using System.Security;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Threading;
using System.Threading.Tasks;

namespace Microsoft.Net.Server
{
    // This class is used to load the client certificate on-demand.  Because client certs are optional, all
    // failures are handled internally and reported via ClientCertException or ClientCertError.
    internal unsafe sealed class ClientCertLoader : IAsyncResult, IDisposable
    {
        private const uint CertBoblSize = 1500;
        private static readonly IOCompletionCallback IOCallback = new IOCompletionCallback(WaitCallback);
        
        private SafeNativeOverlapped _overlapped;
        private byte[] _backingBuffer;
        private UnsafeNclNativeMethods.HttpApi.HTTP_SSL_CLIENT_CERT_INFO* _memoryBlob;
        private uint _size;
        private TaskCompletionSource<object> _tcs;
        private RequestContext _requestContext;

        private int _clientCertError;
        private X509Certificate2 _clientCert;
        private Exception _clientCertException;

        internal ClientCertLoader(RequestContext requestContext)
        {
            _requestContext = requestContext;
            _tcs = new TaskCompletionSource<object>();
            // we will use this overlapped structure to issue async IO to ul
            // the event handle will be put in by the BeginHttpApi2.ERROR_SUCCESS() method
            Reset(CertBoblSize);
        }

        internal X509Certificate2 ClientCert
        {
            get
            {
                Contract.Assert(Task.IsCompleted);
                return _clientCert;
            }
        }

        internal int ClientCertError
        {
            get
            {
                Contract.Assert(Task.IsCompleted);
                return _clientCertError;
            }
        }

        internal Exception ClientCertException
        {
            get
            {
                Contract.Assert(Task.IsCompleted);
                return _clientCertException;
            }
        }

        private RequestContext RequestContext
        {
            get
            {
                return _requestContext;
            }
        }

        private Task Task
        {
            get
            {
                return _tcs.Task;
            }
        }

        private SafeNativeOverlapped NativeOverlapped
        {
            get
            {
                return _overlapped;
            }
        }

        private UnsafeNclNativeMethods.HttpApi.HTTP_SSL_CLIENT_CERT_INFO* RequestBlob
        {
            get
            {
                return _memoryBlob;
            }
        }

        private void Reset(uint size)
        {
            if (size == _size)
            {
                return;
            }
            if (_size != 0)
            {
                _overlapped.Dispose();
            }
            _size = size;
            if (size == 0)
            {
                _overlapped = null;
                _memoryBlob = null;
                _backingBuffer = null;
                return;
            }
            _backingBuffer = new byte[checked((int)size)];
            Overlapped overlapped = new Overlapped();
            overlapped.AsyncResult = this;
            _overlapped = new SafeNativeOverlapped(overlapped.Pack(IOCallback, _backingBuffer));
            _memoryBlob = (UnsafeNclNativeMethods.HttpApi.HTTP_SSL_CLIENT_CERT_INFO*)Marshal.UnsafeAddrOfPinnedArrayElement(_backingBuffer, 0);
        }

        // When you use netsh to configure HTTP.SYS with clientcertnegotiation = enable
        // which means negotiate client certificates, when the client makes the
        // initial SSL connection, the server (HTTP.SYS) requests the client certificate.
        //
        // Some apps may not want to negotiate the client cert at the beginning,
        // perhaps serving the default.htm. In this case the HTTP.SYS is configured
        // with clientcertnegotiation = disabled, which means that the client certificate is
        // optional so initially when SSL is established HTTP.SYS won't ask for client
        // certificate. This works fine for the default.htm in the case above,
        // however, if the app wants to demand a client certificate at a later time
        // perhaps showing "YOUR ORDERS" page, then the server wants to negotiate
        // Client certs. This will in turn makes HTTP.SYS to do the
        // SEC_I_RENOGOTIATE through which the client cert demand is made
        //
        // NOTE: When calling HttpReceiveClientCertificate you can get
        // ERROR_NOT_FOUND - which means the client did not provide the cert
        // If this is important, the server should respond with 403 forbidden
        // HTTP.SYS will not do this for you automatically
        internal Task LoadClientCertificateAsync()
        {
            uint size = CertBoblSize;
            bool retry;
            do
            {
                retry = false;
                uint bytesReceived = 0;

                uint statusCode =
                    UnsafeNclNativeMethods.HttpApi.HttpReceiveClientCertificate(
                        RequestContext.RequestQueueHandle,
                        RequestContext.Request.ConnectionId,
                        (uint)UnsafeNclNativeMethods.HttpApi.HTTP_FLAGS.NONE,
                        RequestBlob,
                        size,
                        &bytesReceived,
                        NativeOverlapped);

                if (statusCode == UnsafeNclNativeMethods.ErrorCodes.ERROR_MORE_DATA)
                {
                    UnsafeNclNativeMethods.HttpApi.HTTP_SSL_CLIENT_CERT_INFO* pClientCertInfo = RequestBlob;
                    size = bytesReceived + pClientCertInfo->CertEncodedSize;
                    Reset(size);
                    retry = true;
                }
                else if (statusCode == UnsafeNclNativeMethods.ErrorCodes.ERROR_NOT_FOUND)
                {
                    // The client did not send a cert.
                    Complete(0, null);
                }
                else if (statusCode == UnsafeNclNativeMethods.ErrorCodes.ERROR_SUCCESS &&
                    WebListener.SkipIOCPCallbackOnSuccess)
                {
                    IOCompleted(statusCode, bytesReceived);
                }
                else if (statusCode != UnsafeNclNativeMethods.ErrorCodes.ERROR_SUCCESS &&
                    statusCode != UnsafeNclNativeMethods.ErrorCodes.ERROR_IO_PENDING)
                {
                    // Some other bad error, possible(?) return values are:
                    // ERROR_INVALID_HANDLE, ERROR_INSUFFICIENT_BUFFER, ERROR_OPERATION_ABORTED
                    // Also ERROR_BAD_DATA if we got it twice or it reported smaller size buffer required.
                    Fail(new WebListenerException((int)statusCode));
                }
            }
            while (retry);

            return Task;
        }

        private void Complete(int certErrors, X509Certificate2 cert)
        {
            // May be null
            _clientCert = cert;
            _clientCertError = certErrors;
            _tcs.TrySetResult(null);
            Dispose();
        }

        private void Fail(Exception ex)
        {
            // TODO: Log
            _clientCertException = ex;
            _tcs.TrySetResult(null);
        }

        private unsafe void IOCompleted(uint errorCode, uint numBytes)
        {
            IOCompleted(this, errorCode, numBytes);
        }

        [SuppressMessage("Microsoft.Design", "CA1031:DoNotCatchGeneralExceptionTypes", Justification = "Redirected to callback")]
        private static unsafe void IOCompleted(ClientCertLoader asyncResult, uint errorCode, uint numBytes)
        {
            RequestContext requestContext = asyncResult.RequestContext;
            try
            {
                if (errorCode == UnsafeNclNativeMethods.ErrorCodes.ERROR_MORE_DATA)
                {
                    // There is a bug that has existed in http.sys since w2k3.  Bytesreceived will only
                    // return the size of the initial cert structure.  To get the full size,
                    // we need to add the certificate encoding size as well.

                    UnsafeNclNativeMethods.HttpApi.HTTP_SSL_CLIENT_CERT_INFO* pClientCertInfo = asyncResult.RequestBlob;
                    asyncResult.Reset(numBytes + pClientCertInfo->CertEncodedSize);

                    uint bytesReceived = 0;
                    errorCode =
                        UnsafeNclNativeMethods.HttpApi.HttpReceiveClientCertificate(
                            requestContext.RequestQueueHandle,
                            requestContext.Request.ConnectionId,
                            (uint)UnsafeNclNativeMethods.HttpApi.HTTP_FLAGS.NONE,
                            asyncResult._memoryBlob,
                            asyncResult._size,
                            &bytesReceived,
                            asyncResult._overlapped);

                    if (errorCode == UnsafeNclNativeMethods.ErrorCodes.ERROR_IO_PENDING ||
                       (errorCode == UnsafeNclNativeMethods.ErrorCodes.ERROR_SUCCESS && !WebListener.SkipIOCPCallbackOnSuccess))
                    {
                        return;
                    }
                }

                if (errorCode == UnsafeNclNativeMethods.ErrorCodes.ERROR_NOT_FOUND)
                {
                    // The client did not send a cert.
                    asyncResult.Complete(0, null);
                }
                else if (errorCode != UnsafeNclNativeMethods.ErrorCodes.ERROR_SUCCESS)
                {
                    asyncResult.Fail(new WebListenerException((int)errorCode));
                }
                else
                {
                    UnsafeNclNativeMethods.HttpApi.HTTP_SSL_CLIENT_CERT_INFO* pClientCertInfo = asyncResult._memoryBlob;
                    if (pClientCertInfo == null)
                    {
                        asyncResult.Complete(0, null);
                    }
                    else
                    {
                        if (pClientCertInfo->pCertEncoded != null)
                        {
                            try
                            {
                                byte[] certEncoded = new byte[pClientCertInfo->CertEncodedSize];
                                Marshal.Copy((IntPtr)pClientCertInfo->pCertEncoded, certEncoded, 0, certEncoded.Length);
                                asyncResult.Complete((int)pClientCertInfo->CertFlags, new X509Certificate2(certEncoded));
                            }
                            catch (CryptographicException exception)
                            {
                                // TODO: Log
                                asyncResult.Fail(exception);
                            }
                            catch (SecurityException exception)
                            {
                                // TODO: Log
                                asyncResult.Fail(exception);
                            }
                        }
                    }
                }
            }
            catch (Exception exception)
            {
                asyncResult.Fail(exception);
            }
        }

        private static unsafe void WaitCallback(uint errorCode, uint numBytes, NativeOverlapped* nativeOverlapped)
        {
            Overlapped callbackOverlapped = Overlapped.Unpack(nativeOverlapped);
            ClientCertLoader asyncResult = (ClientCertLoader)callbackOverlapped.AsyncResult;

            IOCompleted(asyncResult, errorCode, numBytes);
        }

        public void Dispose()
        {
            Dispose(true);
        }

        private void Dispose(bool disposing)
        {
            if (disposing)
            {
                if (_overlapped != null)
                {
                    _memoryBlob = null;
                    _overlapped.Dispose();
                }
            }
        }

        public object AsyncState
        {
            get { return _tcs.Task.AsyncState; }
        }

        public WaitHandle AsyncWaitHandle
        {
            get { return ((IAsyncResult)_tcs.Task).AsyncWaitHandle; }
        }

        public bool CompletedSynchronously
        {
            get { return ((IAsyncResult)_tcs.Task).CompletedSynchronously; }
        }

        public bool IsCompleted
        {
            get { return _tcs.Task.IsCompleted; }
        }
    }
}
