// Copyright (c) .NET Foundation. All rights reserved.
// Licensed under the Apache License, Version 2.0. See License.txt in the project root for license information.

using System;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Server.HttpSys;
using Microsoft.Extensions.DependencyInjection;

namespace Microsoft.AspNetCore.Hosting
{
    public static class WebHostBuilderHttpSysExtensions
    {
        /// <summary>
        /// Specify HttpSys as the server to be used by the web host.
        /// </summary>
        /// <param name="hostBuilder">
        /// The Microsoft.AspNetCore.Hosting.IWebHostBuilder to configure.
        /// </param>
        /// <returns>
        /// The Microsoft.AspNetCore.Hosting.IWebHostBuilder.
        /// </returns>
        public static IWebHostBuilder UseHttpSys(this IWebHostBuilder hostBuilder)
        {
            return hostBuilder.ConfigureServices(services => {
                services.AddSingleton<IServer, MessagePump>();
                services.AddAuthentication(o => RegisterAuthenticationSchemes(services, o));
            });
        }

        private static void RegisterAuthenticationSchemes(IServiceCollection services, AuthenticationOptions options)
        {
            // TODO: don't know which schemes are configured yet, so have to listen to all
            options.AddScheme("Kerberos", builder => builder.HandlerType = typeof(AuthenticationHandler));
            options.AddScheme("Negotiate", builder => builder.HandlerType = typeof(AuthenticationHandler));
            options.AddScheme("NTLM", builder => builder.HandlerType = typeof(AuthenticationHandler));
            options.AddScheme("Basic", builder => builder.HandlerType = typeof(AuthenticationHandler));
            options.AddScheme("None", builder => builder.HandlerType = typeof(AuthenticationHandler));
        }

        /// <summary>
        /// Specify HttpSys as the server to be used by the web host.
        /// </summary>
        /// <param name="hostBuilder">
        /// The Microsoft.AspNetCore.Hosting.IWebHostBuilder to configure.
        /// </param>
        /// <param name="options">
        /// A callback to configure HttpSys options.
        /// </param>
        /// <returns>
        /// The Microsoft.AspNetCore.Hosting.IWebHostBuilder.
        /// </returns>
        public static IWebHostBuilder UseHttpSys(this IWebHostBuilder hostBuilder, Action<HttpSysOptions> options)
        {
            return hostBuilder.UseHttpSys().ConfigureServices(services =>
            {
                services.Configure(options);
            });
        }
    }
}
