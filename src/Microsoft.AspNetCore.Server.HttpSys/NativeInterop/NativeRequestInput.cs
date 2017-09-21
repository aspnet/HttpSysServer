// Copyright (c) .NET Foundation. All rights reserved.
// Licensed under the Apache License, Version 2.0. See License.txt in the project root for license information.

namespace Microsoft.AspNetCore.HttpSys.Internal
{
    internal unsafe class NativeRequestInput
    {
        internal NativeRequestInput(SafeNativeOverlapped nativeOverlapped, int bufferAlignment, HttpApiTypes.HTTP_REQUEST* nativeRequest, byte[] backingBuffer)
        {
            NativeOverlapped = nativeOverlapped;
            BufferAlignment = bufferAlignment;
            NativeRequest = nativeRequest;
            BackingBuffer = backingBuffer;
        }
        internal SafeNativeOverlapped NativeOverlapped { get; set; }
        internal int BufferAlignment { get; set; }
        internal HttpApiTypes.HTTP_REQUEST* NativeRequest { get; set; }
        internal byte[] BackingBuffer { get; set; }
    }
}
