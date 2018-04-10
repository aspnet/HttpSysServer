// Copyright (c) .NET Foundation. All rights reserved.
// Licensed under the Apache License, Version 2.0. See License.txt in the project root for license information.

namespace Microsoft.AspNetCore.Server.HttpSys
{
    /// <summary>
    /// Enum declaring the allowed values for the verbosity level when http.sys reject requests due to throttling.
    /// </summary>
    public enum Http503ResponseVerbosityLevel
    {
        /// <summary>
        /// A 503 response is not sent; the connection is reset. This is the default HTTP Server API behavior.
        /// </summary>
        Basic,

        /// <summary>
        /// The HTTP Server API sends a 503 response with a "Service Unavailable" reason phrase.
        /// </summary>
        Limited,

        /// <summary>
        /// The HTTP Server API sends a 503 response with a detailed reason phrase. 
        /// </summary>
        Full
    }
}
