using Microsoft.AspNetCore.HttpSys.Internal;
using System;
using System.Collections.Generic;
using System.Text;

namespace Microsoft.AspNetCore.HttpSys.Internal
{
    internal unsafe class NativeRequestInput
    {
        internal NativeRequestInput(SafeNativeOverlapped nativeOverlapped, int bufferAlignment, HttpApiTypes.HTTP_REQUEST* nativeRequest, byte[] backingBuffer)
        {
            NativeOverlapped = nativeOverlapped;
            BufferAlignment = bufferAlignment;
            NativeRequest = NativeRequest;
            BackingBuffer = backingBuffer;
        }
        internal SafeNativeOverlapped NativeOverlapped { get; set; }
        internal int BufferAlignment { get; set; }
        internal HttpApiTypes.HTTP_REQUEST* NativeRequest { get; set; }
        internal byte[] BackingBuffer { get; set; }
    }
}
