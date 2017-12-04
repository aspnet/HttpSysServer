﻿// Copyright (c) .NET Foundation. All rights reserved.
// Licensed under the Apache License, Version 2.0. See License.txt in the project root for license information.

using System;
using Microsoft.AspNetCore.Http.Features;

namespace Microsoft.AspNetCore.Server.HttpSys
{
    public class HttpSysOptions
    {
        private const long DefaultRequestQueueLength = 1000; // Http.sys default.
        internal static readonly int DefaultMaxAccepts = 5 * Environment.ProcessorCount;
        // Matches the default maxAllowedContentLength in IIS (~28.6 MB)
        // https://www.iis.net/configreference/system.webserver/security/requestfiltering/requestlimits#005
        private const long DefaultMaxRequestBodySize = 30000000;

        // The native request queue
        private long _requestQueueLength = DefaultRequestQueueLength;
        private long? _maxConnections;
        private RequestQueue _requestQueue;
        private UrlGroup _urlGroup;
        private long? _maxRequestBodySize = DefaultMaxRequestBodySize;

        public HttpSysOptions()
        {
        }

        /// <summary>
        /// The maximum number of concurrent accepts.
        /// </summary>
        public int MaxAccepts { get; set; } = DefaultMaxAccepts;

        /// <summary>
        /// Attempts kernel mode caching for responses with eligible headers. The response may not include
        /// Set-Cookie, Vary, or Pragma headers. It must include a Cache-Control header with Public and
        /// either a Shared-Max-Age or Max-Age value, or an Expires header.
        /// </summary>
        public bool EnableResponseCaching { get; set; } = true;

        /// <summary>
        /// The url prefixes to register with Http.Sys. These may be modified at any time prior to disposing
        /// the listener.
        /// </summary>
        public UrlPrefixCollection UrlPrefixes { get; } = new UrlPrefixCollection();

        /// <summary>
        /// Http.Sys authentication settings. These may be modified at any time prior to disposing
        /// the listener.
        /// </summary>
        public AuthenticationManager Authentication { get; } = new AuthenticationManager();

        /// <summary>
        /// Exposes the Http.Sys timeout configurations.  These may also be configured in the registry.
        /// These may be modified at any time prior to disposing the listener.
        /// </summary>
        public TimeoutManager Timeouts { get; } = new TimeoutManager();

        /// <summary>
        /// Gets or Sets if response body writes that fail due to client disconnects should throw exceptions or
        /// complete normally. The default is false.
        /// </summary>
        public bool ThrowWriteExceptions { get; set; }

        /// <summary>
        /// Enable buffering of response data in the Kernel.
        /// It should be used by an application doing synchronous I/O or by an application doing asynchronous I/O with
        /// no more than one outstanding write at a time, and can significantly improve throughput over high-latency connections.
        /// Applications that use asynchronous I/O and that may have more than one send outstanding at a time should not use this flag.
        /// Enabling this can results in higher CPU and memory usage by Http.Sys.
        /// </summary>
        public bool EnableKernelResponseBuffering { get; set; }

        /// <summary>
        /// Gets or sets the maximum number of concurrent connections to accept, -1 for infinite, or null to
        /// use the machine wide setting from the registry. The default value is null.
        /// </summary>
        public long? MaxConnections
        {
            get => _maxConnections;
            set
            {
                if (value.HasValue && value < -1)
                {
                    throw new ArgumentOutOfRangeException(nameof(value), value, "The value must be positive, or -1 for infiniate.");
                }

                if (value.HasValue && _urlGroup != null)
                {
                    _urlGroup.SetMaxConnections(value.Value);
                }

                _maxConnections = value;
            }
        }

        /// <summary>
        /// Gets or sets the maximum number of requests that will be queued up in Http.Sys.
        /// </summary>
        public long RequestQueueLimit
        {
            get
            {
                return _requestQueueLength;
            }
            set
            {
                if (value <= 0)
                {
                    throw new ArgumentOutOfRangeException(nameof(value), value, "The value must be greater than zero.");
                }

                if (_requestQueue != null)
                {
                    _requestQueue.SetLengthLimit(_requestQueueLength);
                }
                // Only store it if it succeeds or hasn't started yet
                _requestQueueLength = value;
            }
        }

        /// <summary>
        /// Gets or sets the maximum allowed size of any request body in bytes.
        /// When set to null, the maximum request body size is unlimited.
        /// This limit has no effect on upgraded connections which are always unlimited.
        /// This can be overridden per-request via <see cref="IHttpMaxRequestBodySizeFeature"/>.
        /// </summary>
        /// <remarks>
        /// Defaults to 30,000,000 bytes, which is approximately 28.6MB.
        /// </remarks>
        public long? MaxRequestBodySize
        {
            get => _maxRequestBodySize;
            set
            {
                if (value < 0)
                {
                    throw new ArgumentOutOfRangeException(nameof(value), value, "The value must be greater or equal to zero.");
                }
                _maxRequestBodySize = value;
            }
        }

        /// <summary>
        /// Gets or sets a value that controls whether synchronous IO is allowed for the HttpContext.Request.Body and HttpContext.Response.Body.
        /// The default is `true`.
        /// </summary>
        public bool AllowSynchronousIO { get; set; } = true;

        internal void Apply(UrlGroup urlGroup, RequestQueue requestQueue)
        {
            _urlGroup = urlGroup;
            _requestQueue = requestQueue;

            if (_maxConnections.HasValue)
            {
                _urlGroup.SetMaxConnections(_maxConnections.Value);
            }

            if (_requestQueueLength != DefaultRequestQueueLength)
            {
                _requestQueue.SetLengthLimit(_requestQueueLength);
            }

            Authentication.SetUrlGroupSecurity(urlGroup);
            Timeouts.SetUrlGroupTimeouts(urlGroup);
        }
    }
}
