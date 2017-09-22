// Copyright (c) .NET Foundation. All rights reserved.
// Licensed under the Apache License, Version 2.0. See License.txt in the project root for license information.

using System;

namespace Microsoft.AspNetCore.HttpSys.Internal
{
    internal static class Constants
    {
        internal const string HttpScheme = "http";
        internal const string HttpsScheme = "https";
        internal const string Chunked = "chunked";
        internal const string Close = "close";
        internal const string Zero = "0";
        internal const string SchemeDelimiter = "://";
        internal const string DefaultServerAddress = "http://localhost:5000";

        internal static Version V1_0 = new Version(1, 0);
        internal static Version V1_1 = new Version(1, 1);
    }
}
