// Copyright (c) .NET Foundation. All rights reserved.
// Licensed under the Apache License, Version 2.0. See License.txt in the project root for license information.

using System;
using System.Threading;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Microsoft.AspNetCore.Server.HttpSys
{
    internal static class Utilities
    {
        // When tests projects are run in parallel, overlapping port ranges can cause a race condition when looking for free
        // ports during dynamic port allocation.
        private const int BasePort = 5001;
        private const int MaxPort = 8000;
        private static int NextPort = BasePort;
        private static object PortLock = new object();

        internal static IServer CreateHttpServer(out string baseAddress, RequestDelegate app)
        {
            string root;
            return CreateDynamicHttpServer(string.Empty, AuthenticationSchemes.None, true, out root, out baseAddress, app);
        }

        internal static IServer CreateHttpServerReturnRoot(string path, out string root, RequestDelegate app)
        {
            string baseAddress;
            return CreateDynamicHttpServer(path, AuthenticationSchemes.None, true, out root, out baseAddress, app);
        }

        internal static IServer CreateHttpAuthServer(AuthenticationSchemes authType, bool allowAnonymous, out string baseAddress, RequestDelegate app)
        {
            string root;
            return CreateDynamicHttpServer(string.Empty, authType, allowAnonymous, out root, out baseAddress, app);
        }

        internal static IWebHost CreateDynamicHost(string basePath, AuthenticationSchemes authType, bool allowAnonymous, out string root, out string baseAddress, RequestDelegate app)
        {
            lock (PortLock)
            {
                while (NextPort < MaxPort)
                {
                    var port = NextPort++;
                    var prefix = UrlPrefix.Create("http", "localhost", port, basePath);
                    root = prefix.Scheme + "://" + prefix.Host + ":" + prefix.Port;
                    baseAddress = prefix.ToString();

                    var builder = new WebHostBuilder()
                        .UseHttpSys(options =>
                        {
                            options.UrlPrefixes.Add(prefix);
                            options.Authentication.Schemes = authType;
                            options.Authentication.AllowAnonymous = allowAnonymous;
                        })
                        .ConfigureServices(s => s.AddAuthentication())
                        .Configure(appBuilder => appBuilder.Run(app));

                    var host = builder.Build();


                    try
                    {
                        host.Start();
                        return host;
                    }
                    catch (HttpSysException)
                    {
                    }

                }
                NextPort = BasePort;
            }
            throw new Exception("Failed to locate a free port.");
        }

        internal static IServer CreateDynamicHttpServer(string basePath, AuthenticationSchemes authType, bool allowAnonymous, out string root, out string baseAddress, RequestDelegate app)
        {
            lock (PortLock)
            {
                while (NextPort < MaxPort)
                {

                    var port = NextPort++;
                    var prefix = UrlPrefix.Create("http", "localhost", port, basePath);
                    root = prefix.Scheme + "://" + prefix.Host + ":" + prefix.Port;
                    baseAddress = prefix.ToString();

                    var server = new MessagePump(Options.Create(new HttpSysOptions()), new LoggerFactory(), new IAuthenticationSchemeProvider[0]);
                    server.Features.Get<IServerAddressesFeature>().Addresses.Add(baseAddress);
                    server.Listener.Options.Authentication.Schemes = authType;
                    server.Listener.Options.Authentication.AllowAnonymous = allowAnonymous;
                    try
                    {
                        server.StartAsync(new DummyApplication(app), CancellationToken.None).Wait();
                        return server;
                    }
                    catch (HttpSysException)
                    {
                    }
                }
                NextPort = BasePort;
            }
            throw new Exception("Failed to locate a free port.");
        }

        internal static IServer CreateHttpsServer(RequestDelegate app)
        {
            return CreateServer("https", "localhost", 9090, string.Empty, app);
        }

        internal static IServer CreateServer(string scheme, string host, int port, string path, RequestDelegate app)
        {
            var server = new MessagePump(Options.Create(new HttpSysOptions()), new LoggerFactory(), new IAuthenticationSchemeProvider[0]);
            server.Features.Get<IServerAddressesFeature>().Addresses.Add(UrlPrefix.Create(scheme, host, port, path).ToString());
            server.StartAsync(new DummyApplication(app), CancellationToken.None).Wait();
            return server;
        }
    }
}
