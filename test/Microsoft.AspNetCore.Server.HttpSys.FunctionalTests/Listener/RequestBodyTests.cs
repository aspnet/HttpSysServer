﻿// Copyright (c) .NET Foundation. All rights reserved.
// Licensed under the Apache License, Version 2.0. See License.txt in the project root for license information.

using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Testing.xunit;
using Xunit;

namespace Microsoft.AspNetCore.Server.HttpSys.Listener
{
    public class RequestBodyTests
    {
        [ConditionalFact]
        public async Task RequestBody_ReadSync_Success()
        {
            string address;
            using (var server = Utilities.CreateHttpServer(out address))
            {
                Task<string> responseTask = SendRequestAsync(address, "Hello World");

                var context = await server.AcceptAsync(Utilities.DefaultTimeout);
                byte[] input = new byte[100];
                int read = context.Request.Body.Read(input, 0, input.Length);
                context.Response.ContentLength = read;
                context.Response.Body.Write(input, 0, read);
                
                string response = await responseTask;
                Assert.Equal("Hello World", response);
            }
        }

        [ConditionalFact]
        public async Task RequestBody_ReadAync_Success()
        {
            string address;
            using (var server = Utilities.CreateHttpServer(out address))
            {
                Task<string> responseTask = SendRequestAsync(address, "Hello World");

                var context = await server.AcceptAsync(Utilities.DefaultTimeout);
                byte[] input = new byte[100];
                int read = await context.Request.Body.ReadAsync(input, 0, input.Length);
                context.Response.ContentLength = read;
                await context.Response.Body.WriteAsync(input, 0, read);

                string response = await responseTask;
                Assert.Equal("Hello World", response);
            }
        }
#if NET46
        [ConditionalFact]
        public async Task RequestBody_ReadBeginEnd_Success()
        {
            string address;
            using (var server = Utilities.CreateHttpServer(out address))
            {
                Task<string> responseTask = SendRequestAsync(address, "Hello World");

                var context = await server.AcceptAsync(Utilities.DefaultTimeout);
                byte[] input = new byte[100];
                int read = context.Request.Body.EndRead(context.Request.Body.BeginRead(input, 0, input.Length, null, null));
                context.Response.ContentLength = read;
                context.Response.Body.EndWrite(context.Response.Body.BeginWrite(input, 0, read, null, null));

                string response = await responseTask;
                Assert.Equal("Hello World", response);
            }
        }
#endif

        [ConditionalFact]
        public async Task RequestBody_InvalidBuffer_ArgumentException()
        {
            string address;
            using (var server = Utilities.CreateHttpServer(out address))
            {
                Task<string> responseTask = SendRequestAsync(address, "Hello World");

                var context = await server.AcceptAsync(Utilities.DefaultTimeout);
                byte[] input = new byte[100];
                Assert.Throws<ArgumentNullException>("buffer", () => context.Request.Body.Read(null, 0, 1));
                Assert.Throws<ArgumentOutOfRangeException>("offset", () => context.Request.Body.Read(input, -1, 1));
                Assert.Throws<ArgumentOutOfRangeException>("offset", () => context.Request.Body.Read(input, input.Length + 1, 1));
                Assert.Throws<ArgumentOutOfRangeException>("size", () => context.Request.Body.Read(input, 10, -1));
                Assert.Throws<ArgumentOutOfRangeException>("size", () => context.Request.Body.Read(input, 0, 0));
                Assert.Throws<ArgumentOutOfRangeException>("size", () => context.Request.Body.Read(input, 1, input.Length));
                Assert.Throws<ArgumentOutOfRangeException>("size", () => context.Request.Body.Read(input, 0, input.Length + 1));
                context.Dispose();

                string response = await responseTask;
                Assert.Equal(string.Empty, response);
            }
        }

        [ConditionalFact]
        public async Task RequestBody_ReadSyncPartialBody_Success()
        {
            StaggardContent content = new StaggardContent();
            string address;
            using (var server = Utilities.CreateHttpServer(out address))
            {
                Task<string> responseTask = SendRequestAsync(address, content);

                var context = await server.AcceptAsync(Utilities.DefaultTimeout);
                byte[] input = new byte[10];
                int read = context.Request.Body.Read(input, 0, input.Length);
                Assert.Equal(5, read);
                content.Block.Release();
                read = context.Request.Body.Read(input, 0, input.Length);
                Assert.Equal(5, read);
                context.Dispose();

                string response = await responseTask;
                Assert.Equal(string.Empty, response);
            }
        }

        [ConditionalFact]
        public async Task RequestBody_ReadAsyncPartialBody_Success()
        {
            StaggardContent content = new StaggardContent();
            string address;
            using (var server = Utilities.CreateHttpServer(out address))
            {
                Task<string> responseTask = SendRequestAsync(address, content);

                var context = await server.AcceptAsync(Utilities.DefaultTimeout);
                byte[] input = new byte[10];
                int read = await context.Request.Body.ReadAsync(input, 0, input.Length);
                Assert.Equal(5, read);
                content.Block.Release();
                read = await context.Request.Body.ReadAsync(input, 0, input.Length);
                Assert.Equal(5, read);
                context.Dispose();

                string response = await responseTask;
                Assert.Equal(string.Empty, response);
            }
        }

        [ConditionalFact]
        public async Task RequestBody_PostWithImidateBody_Success()
        {
            string address;
            using (var server = Utilities.CreateHttpServer(out address))
            {
                Task<string> responseTask = SendSocketRequestAsync(address);

                var context = await server.AcceptAsync(Utilities.DefaultTimeout);
                byte[] input = new byte[11];
                int read = await context.Request.Body.ReadAsync(input, 0, input.Length);
                Assert.Equal(10, read);
                read = await context.Request.Body.ReadAsync(input, 0, input.Length);
                Assert.Equal(0, read);
                context.Response.ContentLength = 10;
                await context.Response.Body.WriteAsync(input, 0, 10);
                context.Dispose();

                string response = await responseTask;
                string[] lines = response.Split('\r', '\n');
                Assert.Equal(13, lines.Length);
                Assert.Equal("HTTP/1.1 200 OK", lines[0]);
                Assert.Equal("0123456789", lines[12]);
            }
        }

        [ConditionalFact]
        public async Task RequestBody_ReadAsyncAlreadyCanceled_ReturnsCanceledTask()
        {
            string address;
            using (var server = Utilities.CreateHttpServer(out address))
            {
                Task<string> responseTask = SendRequestAsync(address, "Hello World");

                var context = await server.AcceptAsync(Utilities.DefaultTimeout);

                byte[] input = new byte[10];
                var cts = new CancellationTokenSource();
                cts.Cancel();

                Task<int> task = context.Request.Body.ReadAsync(input, 0, input.Length, cts.Token);
                Assert.True(task.IsCanceled);

                context.Dispose();

                string response = await responseTask;
                Assert.Equal(string.Empty, response);
            }
        }

        [ConditionalFact]
        public async Task RequestBody_ReadAsyncPartialBodyWithCancellationToken_Success()
        {
            StaggardContent content = new StaggardContent();
            string address;
            using (var server = Utilities.CreateHttpServer(out address))
            {
                Task<string> responseTask = SendRequestAsync(address, content);

                var context = await server.AcceptAsync(Utilities.DefaultTimeout);
                byte[] input = new byte[10];
                var cts = new CancellationTokenSource();
                int read = await context.Request.Body.ReadAsync(input, 0, input.Length, cts.Token);
                Assert.Equal(5, read);
                content.Block.Release();
                read = await context.Request.Body.ReadAsync(input, 0, input.Length, cts.Token);
                Assert.Equal(5, read);
                context.Dispose();

                string response = await responseTask;
                Assert.Equal(string.Empty, response);
            }
        }

        [ConditionalFact]
        public async Task RequestBody_ReadAsyncPartialBodyWithTimeout_Success()
        {
            StaggardContent content = new StaggardContent();
            string address;
            using (var server = Utilities.CreateHttpServer(out address))
            {
                Task<string> responseTask = SendRequestAsync(address, content);

                var context = await server.AcceptAsync(Utilities.DefaultTimeout);
                byte[] input = new byte[10];
                var cts = new CancellationTokenSource();
                cts.CancelAfter(TimeSpan.FromSeconds(5));
                int read = await context.Request.Body.ReadAsync(input, 0, input.Length, cts.Token);
                Assert.Equal(5, read);
                content.Block.Release();
                read = await context.Request.Body.ReadAsync(input, 0, input.Length, cts.Token);
                Assert.Equal(5, read);
                context.Dispose();

                string response = await responseTask;
                Assert.Equal(string.Empty, response);
            }
        }

        [ConditionalFact]
        public async Task RequestBody_ReadAsyncPartialBodyAndCancel_Canceled()
        {
            StaggardContent content = new StaggardContent();
            string address;
            using (var server = Utilities.CreateHttpServer(out address))
            {
                Task<string> responseTask = SendRequestAsync(address, content);

                var context = await server.AcceptAsync(Utilities.DefaultTimeout);
                byte[] input = new byte[10];
                var cts = new CancellationTokenSource();
                int read = await context.Request.Body.ReadAsync(input, 0, input.Length, cts.Token);
                Assert.Equal(5, read);
                var readTask = context.Request.Body.ReadAsync(input, 0, input.Length, cts.Token);
                Assert.False(readTask.IsCanceled);
                cts.Cancel();
                await Assert.ThrowsAsync<IOException>(async () => await readTask);
                content.Block.Release();
                context.Dispose();

                await Assert.ThrowsAsync<HttpRequestException>(async () => await responseTask);
            }
        }

        [ConditionalFact]
        public async Task RequestBody_ReadAsyncPartialBodyAndExpiredTimeout_Canceled()
        {
            StaggardContent content = new StaggardContent();
            string address;
            using (var server = Utilities.CreateHttpServer(out address))
            {
                Task<string> responseTask = SendRequestAsync(address, content);

                var context = await server.AcceptAsync(Utilities.DefaultTimeout);
                byte[] input = new byte[10];
                var cts = new CancellationTokenSource();
                int read = await context.Request.Body.ReadAsync(input, 0, input.Length, cts.Token);
                Assert.Equal(5, read);
                cts.CancelAfter(TimeSpan.FromMilliseconds(100));
                var readTask = context.Request.Body.ReadAsync(input, 0, input.Length, cts.Token);
                Assert.False(readTask.IsCanceled);
                await Assert.ThrowsAsync<IOException>(async () => await readTask);
                content.Block.Release();
                context.Dispose();

                await Assert.ThrowsAsync<HttpRequestException>(async () => await responseTask);
            }
        }

        // Make sure that using our own disconnect token as a read cancellation token doesn't
        // cause recursion problems when it fires and calls Abort.
        [ConditionalFact]
        public async Task RequestBody_ReadAsyncPartialBodyAndDisconnectedClient_Canceled()
        {
            StaggardContent content = new StaggardContent();
            string address;
            using (var server = Utilities.CreateHttpServer(out address))
            {
                var client = new HttpClient();
                var responseTask = client.PostAsync(address, content);

                var context = await server.AcceptAsync(Utilities.DefaultTimeout);
                byte[] input = new byte[10];
                int read = await context.Request.Body.ReadAsync(input, 0, input.Length, context.DisconnectToken);
                Assert.False(context.DisconnectToken.IsCancellationRequested);
                // The client should timeout and disconnect, making this read fail.
                var assertTask = Assert.ThrowsAsync<IOException>(async () => await context.Request.Body.ReadAsync(input, 0, input.Length, context.DisconnectToken));
                client.CancelPendingRequests();
                await assertTask;
                content.Block.Release();
                context.Dispose();

                await Assert.ThrowsAsync<TaskCanceledException>(async () => await responseTask);
            }
        }

        private Task<string> SendRequestAsync(string uri, string upload)
        {
            return SendRequestAsync(uri, new StringContent(upload));
        }

        private async Task<string> SendRequestAsync(string uri, HttpContent content)
        {
            using (HttpClient client = new HttpClient())
            {
                client.Timeout = TimeSpan.FromSeconds(10);
                HttpResponseMessage response = await client.PostAsync(uri, content);
                response.EnsureSuccessStatusCode();
                return await response.Content.ReadAsStringAsync();
            }
        }

        private async Task<string> SendSocketRequestAsync(string address)
        {
            // Connect with a socket
            Uri uri = new Uri(address);
            TcpClient client = new TcpClient();
            try
            {
                await client.ConnectAsync(uri.Host, uri.Port);
                NetworkStream stream = client.GetStream();

                // Send an HTTP GET request
                byte[] requestBytes = BuildPostRequest(uri);
                await stream.WriteAsync(requestBytes, 0, requestBytes.Length);
                StreamReader reader = new StreamReader(stream);
                return await reader.ReadToEndAsync();
            }
            catch (Exception)
            {
                ((IDisposable)client).Dispose();
                throw;
            }
        }

        private byte[] BuildPostRequest(Uri uri)
        {
            StringBuilder builder = new StringBuilder();
            builder.Append("POST");
            builder.Append(" ");
            builder.Append(uri.PathAndQuery);
            builder.Append(" HTTP/1.1");
            builder.AppendLine();

            builder.Append("Host: ");
            builder.Append(uri.Host);
            builder.Append(':');
            builder.Append(uri.Port);
            builder.AppendLine();

            builder.AppendLine("Connection: close");
            builder.AppendLine("Content-Length: 10");
            builder.AppendLine();
            builder.Append("0123456789");
            return Encoding.ASCII.GetBytes(builder.ToString());
        }

        private class StaggardContent : HttpContent
        {
            public StaggardContent()
            {
                Block = new SemaphoreSlim(0, 1);
            }

            public SemaphoreSlim Block { get; private set; }

            protected async override Task SerializeToStreamAsync(Stream stream, TransportContext context)
            {
                await stream.WriteAsync(new byte[5], 0, 5);
                await Block.WaitAsync();
                await stream.WriteAsync(new byte[5], 0, 5);
            }

            protected override bool TryComputeLength(out long length)
            {
                length = 10;
                return true;
            }
        }
    }
}
