using System;
using System.Text;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Server.HttpSys;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace SelfHostServer
{
    public class Program
    {
        private static readonly byte[] _helloWorldPayload = Encoding.UTF8.GetBytes("Hello, World!");

        public static void Main(string[] args)
        {
            Console.Write("create and (l)isten, (c)reate only, (a)ttach to existing, or attach (o)r create? ");
            var key = Console.ReadKey();
            Console.WriteLine();

            var host = new WebHostBuilder()
                .UseHttpSys(options =>
                {
                    switch (key.KeyChar)
                    {
                        case 'c':
                            options.Mode = RequestQueueMode.Controler;
                            break;
                        case 'a':
                            options.Mode = RequestQueueMode.AttachToExisting;
                            break;
                        case 'o':
                            options.Mode = RequestQueueMode.AttachOrCreate;
                            break;
                        case 'l':
                        default:
                            options.Mode = RequestQueueMode.Create;
                            break;
                    }
                    options.RequestQueueName = "queuename";
                })
                .ConfigureLogging(loggerFactory =>
                {
                    loggerFactory.AddConsole();
                })
                .Configure(app =>
                {
                    app.Run(context =>
                    {
                        context.Response.StatusCode = 200;
                        context.Response.ContentType = "text/plain";
                        context.Response.ContentLength = _helloWorldPayload.Length;
                        return context.Response.Body.WriteAsync(_helloWorldPayload, 0, _helloWorldPayload.Length);
                        // return context.Response.WriteAsync("Hello world from " + context.Request.Host + " at " + DateTime.Now + (key.KeyChar == 'a' ? " attached": " created"));
                    });
                })
                .Build();

            host.Run();
        }
    }
}
