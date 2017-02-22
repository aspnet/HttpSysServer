using System;
using System.Collections.Generic;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Server.HttpSys;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace SelfHostServer
{
    public class Program
    {
        public static void Main(string[] args)
        {
            Console.Write("create and (l)isten, (c)reate only, or (a)ttach to existing and listen? ");
            var key = Console.ReadKey();
            Console.WriteLine();

            var host = new WebHostBuilder()
                .UseHttpSys(options =>
                {
                    options.AttachToExistingRequestQueue = key.KeyChar == 'a';
                    options.MaxAccepts = key.KeyChar == 'c' ? 0 : 5;
                    options.RequestQueueName = "queuename";
                })
                .ConfigureLogging(loggerFactory =>
                {
                    loggerFactory.AddConsole();
                })
                .Configure(app =>
                {
                    app.Run(async context =>
                    {
                        context.Response.ContentType = "text/plain";
                        await context.Response.WriteAsync("Hello world from " + context.Request.Host + " at " + DateTime.Now + (key.KeyChar == 'a' ? " attached": " created"));
                        // await context.Response.WriteAsync("Hello world from " + context.Request.Host + " at " + DateTime.Now);
                    });
                })
                .Build();

            host.Run();
        }

        // Options can also be configured in Startup:
        public void ConfigureServices(IServiceCollection services)
        {
            // Server options can be configured here instead of in Main.
            services.Configure<HttpSysOptions>(options =>
            {
                options.Authentication.Schemes = AuthenticationSchemes.None;
                options.Authentication.AllowAnonymous = true;
            });
        }
    }
}
