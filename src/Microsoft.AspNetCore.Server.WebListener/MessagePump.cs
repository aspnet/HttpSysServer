// Copyright (c) .NET Foundation. All rights reserved.
// Licensed under the Apache License, Version 2.0. See License.txt in the project root for license information.

using System;
using System.Collections.Generic;
using System.Diagnostics.Contracts;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.Net.Http.Server;

namespace Microsoft.AspNetCore.Server.WebListener
{
    internal class MessagePump : IServer
    {
        private readonly Microsoft.Net.Http.Server.WebListener _listener;
        private readonly ILogger _logger;

        private IHttpApplication<object> _application;

        private int _maxAccepts;
        private int _acceptorCounts;
        private Action<object> _processRequest;

        private bool _stopping;
        private int _outstandingRequests;
        private ManualResetEvent _shutdownSignal;
        private TimeSpan _shutdownTimeout;

        private readonly ServerAddressesFeature _serverAddresses;

        public MessagePump(IOptions<WebListenerOptions> options, ILoggerFactory loggerFactory)
        {
            if (options == null)
            {
                throw new ArgumentNullException(nameof(options));
            }
            if (loggerFactory == null)
            {
                throw new ArgumentNullException(nameof(loggerFactory));
            }

            var optionsInstance = options.Value;
            _listener = new Microsoft.Net.Http.Server.WebListener(optionsInstance.ListenerSettings);
            _logger = LogHelper.CreateLogger(loggerFactory, typeof(MessagePump));
            Features = new FeatureCollection();
            _serverAddresses = new ServerAddressesFeature();
            Features.Set<IServerAddressesFeature>(_serverAddresses);

            _processRequest = new Action<object>(ProcessRequestAsync);
            _maxAccepts = optionsInstance.MaxAccepts;
            EnableResponseCaching = optionsInstance.EnableResponseCaching;
            _shutdownSignal = new ManualResetEvent(false);
            _shutdownTimeout = optionsInstance.ShutdownTimeout;
        }

        internal Microsoft.Net.Http.Server.WebListener Listener
        {
            get { return _listener; }
        }

        internal bool EnableResponseCaching { get; set; }

        public IFeatureCollection Features { get; }

        public void Start<TContext>(IHttpApplication<TContext> application)
        {
            if (application == null)
            {
                throw new ArgumentNullException(nameof(application));
            }

            ParseAddresses(_serverAddresses.Addresses, Listener);

            // Can't call Start twice
            Contract.Assert(_application == null);

            Contract.Assert(application != null);

            _application = new ApplicationWrapper<TContext>(application);

            if (_listener.Settings.UrlPrefixes.Count == 0)
            {
                throw new InvalidOperationException("No address prefixes were defined.");
            }

            _listener.Start();

            ActivateRequestProcessingLimits();
        }

        private void ActivateRequestProcessingLimits()
        {
            for (int i = _acceptorCounts; i < _maxAccepts; i++)
            {
                ProcessRequestsWorker();
            }
        }

        // The message pump.
        // When we start listening for the next request on one thread, we may need to be sure that the
        // completion continues on another thread as to not block the current request processing.
        // The awaits will manage stack depth for us.
        private async void ProcessRequestsWorker()
        {
            int workerIndex = Interlocked.Increment(ref _acceptorCounts);
            while (!_stopping && workerIndex <= _maxAccepts)
            {
                // Receive a request
                RequestContext requestContext;
                try
                {
                    requestContext = await _listener.AcceptAsync().SupressContext();
                }
                catch (Exception exception)
                {
                    Contract.Assert(_stopping);
                    if (_stopping)
                    {
                        LogHelper.LogDebug(_logger, "ListenForNextRequestAsync-Stopping", exception);
                    }
                    else
                    {
                        LogHelper.LogException(_logger, "ListenForNextRequestAsync", exception);
                    }
                    return;
                }
                try
                {
                    Task ignored = Task.Factory.StartNew(_processRequest, requestContext);
                }
                catch (Exception ex)
                {
                    // Request processing failed to be queued in threadpool
                    // Log the error message, release throttle and move on
                    LogHelper.LogException(_logger, "ProcessRequestAsync", ex);
                }
            }
            Interlocked.Decrement(ref _acceptorCounts);
        }

        private async void ProcessRequestAsync(object requestContextObj)
        {
            var requestContext = requestContextObj as RequestContext;
            try
            {
                if (_stopping)
                {
                    SetFatalResponse(requestContext, 503);
                    return;
                }

                object context = null;
                Interlocked.Increment(ref _outstandingRequests);
                try
                {
                    var featureContext = new FeatureContext(requestContext, EnableResponseCaching);
                    context = _application.CreateContext(featureContext.Features);
                    try
                    {
                        await _application.ProcessRequestAsync(context).SupressContext();
                        await featureContext.OnStart();
                        requestContext.Dispose();
                        _application.DisposeContext(context, null);
                    }
                    finally
                    {
                        await featureContext.OnCompleted();
                    }
                }
                catch (Exception ex)
                {
                    LogHelper.LogException(_logger, "ProcessRequestAsync", ex);
                    if (requestContext.Response.HasStarted)
                    {
                        requestContext.Abort();
                    }
                    else
                    {
                        // We haven't sent a response yet, try to send a 500 Internal Server Error
                        requestContext.Response.Headers.Clear();
                        SetFatalResponse(requestContext, 500);
                    }
                    _application.DisposeContext(context, ex);
                }
                finally
                {
                    if (Interlocked.Decrement(ref _outstandingRequests) == 0 && _stopping)
                    {
                        _shutdownSignal.Set();
                    }
                }
            }
            catch (Exception ex)
            {
                LogHelper.LogException(_logger, "ProcessRequestAsync", ex);
                requestContext.Abort();
            }
        }

        private static void SetFatalResponse(RequestContext context, int status)
        {
            context.Response.StatusCode = status;
            context.Response.ContentLength = 0;
            context.Dispose();
        }

        private void ParseAddresses(ICollection<string> addresses, Microsoft.Net.Http.Server.WebListener listener)
        {
            foreach (var value in addresses)
            {
                listener.Settings.UrlPrefixes.Add(UrlPrefix.Create(value));
            }
        }

        public void Dispose()
        {
            _stopping = true;
            // Wait for active requests to drain
            if (_outstandingRequests > 0)
            {
                LogHelper.LogInfo(_logger, "Stopping, waiting for " + _outstandingRequests + " request(s) to drain.");
                var drained = _shutdownSignal.WaitOne(_shutdownTimeout);
                if (drained)
                {
                    LogHelper.LogInfo(_logger, "All requests drained successfully.");
                }
                else
                {
                    LogHelper.LogInfo(_logger, "Timed out, terminating " + _outstandingRequests + " request(s).");
                }
            }
            // All requests are finished
            _listener.Dispose();
        }

        private class ApplicationWrapper<TContext> : IHttpApplication<object>
        {
            private readonly IHttpApplication<TContext> _application;

            public ApplicationWrapper(IHttpApplication<TContext> application)
            {
                _application = application;
            }

            public object CreateContext(IFeatureCollection contextFeatures)
            {
                return _application.CreateContext(contextFeatures);
            }

            public void DisposeContext(object context, Exception exception)
            {
                _application.DisposeContext((TContext)context, exception);
            }

            public Task ProcessRequestAsync(object context)
            {
                return _application.ProcessRequestAsync((TContext)context);
            }
        }
    }
}
