// Copyright (c) .NET Foundation. All rights reserved.
// Licensed under the Apache License, Version 2.0. See License.txt in the project root for license information.

using System;
using Microsoft.Net.Http.Server;

namespace Microsoft.AspNetCore.Server.WebListener
{
    public class WebListenerOptions
    {
        internal static readonly int DefaultMaxAccepts = 5 * Environment.ProcessorCount;

        /// <summary>
        /// Settings for the underlying WebListener instance.
        /// </summary>
        public WebListenerSettings ListenerSettings { get; } = new WebListenerSettings();

        /// <summary>
        /// The maximum number of concurrent calls to WebListener.AcceptAsync().
        /// </summary>
        public int MaxAccepts { get; set; } = DefaultMaxAccepts;

        /// <summary>
        /// Attempts kernel mode caching for responses with eligible headers. The response may not include
        /// Set-Cookie, Vary, or Pragma headers. It must include a Cache-Control header with Public and
        /// either a Shared-Max-Age or Max-Age value, or an Expires header.
        /// </summary>
        public bool EnableResponseCaching { get; set; } = true;

        /// <summary>
        /// The amount of time to wait for active requests to drain while the server is shutting down.
        /// New requests will receive a 503 response in this time period. The default is 5 seconds.
        /// </summary>
        public TimeSpan ShutdownTimeout { get; set; } = TimeSpan.FromSeconds(5);
    }
}
