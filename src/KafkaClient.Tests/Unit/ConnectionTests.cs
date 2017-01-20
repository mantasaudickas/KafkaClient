﻿using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using KafkaClient.Common;
using KafkaClient.Connections;
using KafkaClient.Protocol;
using Nito.AsyncEx;
using NUnit.Framework;
using NUnit.Framework.Internal;

namespace KafkaClient.Tests.Unit
{
    [Category("Unit")]
    [TestFixture]
    public class ConnectionTests
    {
        #region Construct...

        [Test]
        public async Task ShouldStartReadPollingOnConstruction()
        {
            var endpoint = await Endpoint.ResolveAsync(TestConfig.ServerUri(), TestConfig.Log);
            using (var conn = new Connection(endpoint, log: TestConfig.Log))
            {
                await TaskTest.WaitFor(() => conn.IsReaderAlive);
                Assert.That(conn.IsReaderAlive, Is.True);
            }
        }

        [Test]
        public async Task ShouldReportServerUriOnConstruction()
        {
            var endpoint = await Endpoint.ResolveAsync(TestConfig.ServerUri(), TestConfig.Log);
            using (var conn = new Connection(endpoint, log: TestConfig.Log))
            {
                Assert.That(conn.Endpoint, Is.EqualTo(endpoint));
            }
        }

        [Test]
        public async Task ThrowsConnectionExceptionOnInvalidEndpoint()
        {
            try {
                using (var conn = new ExplicitlyReadingConnection(new Endpoint(null, "not.com"))) {
                    await conn.SendAsync(new ApiVersionsRequest(), CancellationToken.None);
                }
                Assert.Fail("should have thrown ConnectionException");
            } catch (ConnectionException) {
                // expected
            }
        }

        #endregion Construct...

        #region Connection Tests...

        [Test]
        public async Task ShouldStartDedicatedThreadOnConstruction()
        {
            var count = 0;
            var config = new ConnectionConfiguration(onConnecting: (e, a, _) => Interlocked.Increment(ref count));
            var endpoint = await Endpoint.ResolveAsync(TestConfig.ServerUri(), TestConfig.Log);
            using (var conn = new Connection(endpoint, config, log: TestConfig.Log))
            {
                await TaskTest.WaitFor(() => count > 0);
                Assert.That(count, Is.GreaterThan(0));
            }
        }

        [Test]
        public async Task ShouldAttemptMultipleTimesWhenConnectionFails()
        {
            var count = 0;
            var config = new ConnectionConfiguration(onConnecting: (e, a, _) => Interlocked.Increment(ref count));
            using (var conn = new Connection(await Endpoint.ResolveAsync(TestConfig.ServerUri(), TestConfig.Log), config, TestConfig.Log))
            {
                var task = conn.SendAsync(new FetchRequest(), CancellationToken.None); //will force a connection
                await TaskTest.WaitFor(() => count > 1, 10000);
                Assert.That(count, Is.GreaterThan(1));
            }
        }

        #endregion Connection Tests...

        #region Dispose Tests...

        [Test]
        public async Task ShouldDisposeWithoutExceptionThrown()
        {
            var endpoint = await Endpoint.ResolveAsync(TestConfig.ServerUri(), TestConfig.Log);
            using (var server = new FakeTcpServer(TestConfig.Log, endpoint.Value.Port))
            {
                var conn = new Connection(endpoint, log: TestConfig.Log);
                await TaskTest.WaitFor(() => server.ConnectionEventcount > 0);
                using (conn) { }
            }
        }

        [Test]
        public async Task ShouldDisposeWithoutExceptionEvenWhileCallingSendAsync()
        {
            var endpoint = await Endpoint.ResolveAsync(TestConfig.ServerUri(), TestConfig.Log);
            using (var conn = new Connection(endpoint, log: TestConfig.Log))
            {
                var task = conn.SendAsync(new MetadataRequest(), CancellationToken.None);
                await Task.WhenAny(task, Task.Delay(1000)).ConfigureAwait(false);
                Assert.That(task.IsCompleted, Is.False, "The send task should still be pending.");
            }
        }

        [Test]
        public async Task ShouldDisposeEvenWhilePollingToReconnect()
        {
            int connectionAttempt = 0;
            var config = new ConnectionConfiguration(onConnecting: (e, a, _) => connectionAttempt = a);
            var endpoint = await Endpoint.ResolveAsync(TestConfig.ServerUri(), TestConfig.Log);
            using (var test = new Connection(endpoint, config, TestConfig.Log))
            {
                var taskResult = test.ConnectAsync(CancellationToken.None);

                await TaskTest.WaitFor(() => connectionAttempt > 1);

                test.Dispose();
                await Task.WhenAny(taskResult, Task.Delay(1000)).ConfigureAwait(false);

                Assert.That(taskResult.IsFaulted, Is.True);
                Assert.That(taskResult.Exception.InnerException, Is.TypeOf<ObjectDisposedException>());
            }
        }

        [Test]
        public async Task ShouldDisposeEvenWhileAwaitingReadAndThrowException()
        {
            int readSize = 0;
            var config = new ConnectionConfiguration(onReading: (e, size) => readSize = size);
            var endpoint = await Endpoint.ResolveAsync(TestConfig.ServerUri(), TestConfig.Log);
            using (new FakeTcpServer(TestConfig.Log, endpoint.Value.Port))
            using (var test = new ExplicitlyReadingConnection(endpoint, config, TestConfig.Log))
            {
                var socket = await test.ConnectAsync(CancellationToken.None);
                var buffer = new byte[4];
                var taskResult = test.ReadBytesAsync(socket, buffer, 4, _ => { }, CancellationToken.None);

                await TaskTest.WaitFor(() => readSize > 0);

                using (test) { }

                await Task.WhenAny(taskResult, Task.Delay(1000)).ConfigureAwait(false);

                Assert.That(taskResult.IsFaulted, Is.True);
                Assert.That(taskResult.Exception.InnerException, Is.TypeOf<ObjectDisposedException>());
            }
        }

        #endregion Dispose Tests...

        #region Read Tests...

        [Test]
        public async Task ShouldLogDisconnectAndRecover([Values(3, 4)] int connectionAttempts)
        {
            var mockLog = new MemoryLog();
            var disconnected = 0;
            var connected = 0;

            var config = new ConnectionConfiguration(
                onDisconnected: (e, exception) => {
                    Interlocked.Increment(ref disconnected);
                },
                onConnected: (e, attempt, elapsed) => {
                    Interlocked.Increment(ref connected);
                });

            var endpoint = await Endpoint.ResolveAsync(TestConfig.ServerUri(), TestConfig.Log);
            using (var server = new FakeTcpServer(TestConfig.Log, endpoint.Value.Port))
            using (new Connection(endpoint, config, log: mockLog))
            {
                for (var connectionAttempt = 1; connectionAttempt <= connectionAttempts; connectionAttempt++)
                {
                    var currentAttempt = connectionAttempt;
                    await TaskTest.WaitFor(() => server.ConnectionEventcount == currentAttempt);
                    Assert.That(server.ConnectionEventcount, Is.EqualTo(connectionAttempt));
                    await server.SendDataAsync(CreateCorrelationMessage(connectionAttempt));
                    TestConfig.Log.Write(LogLevel.Info, () => LogEvent.Create($"Sent CONNECTION attempt {connectionAttempt}"));
                    await TaskTest.WaitFor(() => connected == disconnected, 200);

                    Assert.That(mockLog.LogEvents.Count(e => e.Item1 == LogLevel.Info && e.Item2.Message.StartsWith("Polling receive thread has recovered on ")), Is.EqualTo(currentAttempt-1));

                    TestConfig.Log.Write(LogLevel.Info, () => LogEvent.Create($"Dropping CONNECTION attempt {connectionAttempt}"));
                    server.DropConnection();
                    await TaskTest.WaitFor(() => disconnected == currentAttempt);

                    Assert.That(mockLog.LogEvents.Count(e => e.Item1 == LogLevel.Error && e.Item2.Message.StartsWith("Polling failure on")), Is.AtLeast(currentAttempt));
                }
            }
        }

        [Test]
        public async Task ShouldSkipPartiallyReadMessage()
        {
            var mockLog = new MemoryLog { WriteToConsole = true };
            var bytesRead = 0;

            var config = new ConnectionConfiguration(onReadBytes: (e, attempted, actual, elapsed) => Interlocked.Add(ref bytesRead, actual));

            var endpoint = await Endpoint.ResolveAsync(TestConfig.ServerUri(), TestConfig.Log);
            using (var server = new FakeTcpServer(TestConfig.Log, endpoint.Value.Port))
            using (new Connection(endpoint, config, log: mockLog))
            {
                // send size
                var size = 200;
                await server.SendDataAsync(size.ToBytes());
                var randomizer = Randomizer.CreateRandomizer();
                var firstBytes = new byte[99];
                for (var i = 0; i < firstBytes.Length; i++)
                {
                    firstBytes[i] = randomizer.NextByte();
                }

                // send half of payload
                await server.SendDataAsync(firstBytes);
                await TaskTest.WaitFor(() => bytesRead == firstBytes.Length);

                Assert.That(mockLog.LogEvents.Count(e => e.Item1 == LogLevel.Warn && e.Item2.Message.StartsWith("Skipping")), Is.EqualTo(0));

                server.DropConnection();

                // send half of payload should be skipped
                var lastBytes = new byte[size];
                for (var i = 0; i < lastBytes.Length; i++)
                {
                    lastBytes[i] = randomizer.NextByte();
                }
                await server.SendDataAsync(lastBytes);
                await TaskTest.WaitFor(() => bytesRead >= size);

                Assert.That(mockLog.LogEvents.Count(e => e.Item1 == LogLevel.Warn && e.Item2.Message.StartsWith($"Skipping {size - firstBytes.Length} bytes on")), Is.EqualTo(1));
            }
        }

        [Test]
        public async Task ReadShouldIgnoreMessageWithUnknownCorrelationId()
        {
            const int correlationId = 99;
            var receivedData = false;

            var mockLog = new MemoryLog();

            var config = new ConnectionConfiguration(onRead: (e, buffer, elapsed) => receivedData = true);
            var endpoint = await Endpoint.ResolveAsync(TestConfig.ServerUri(), TestConfig.Log);
            using (var server = new FakeTcpServer(TestConfig.Log, endpoint.Value.Port))
            using (var conn = new Connection(endpoint, config, log: mockLog))
            {
                //send correlation message
                await server.SendDataAsync(CreateCorrelationMessage(correlationId));

                //wait for connection
                await TaskTest.WaitFor(() => server.ConnectionEventcount > 0);
                Assert.That(server.ConnectionEventcount, Is.EqualTo(1));

                await TaskTest.WaitFor(() => receivedData);

                // shortly after receivedData, but still after
                await TaskTest.WaitFor(() => mockLog.LogEvents.Any(e => e.Item1 == LogLevel.Warn && e.Item2.Message == $"Unexpected response from {endpoint} with correlation id {correlationId} (not in request queue)."));
            }
        }

        [Test]
        public async Task ReadShouldCancelWhileAwaitingResponse()
        {
            var count = 0;
            var semaphore = new SemaphoreSlim(0);
            var config = new ConnectionConfiguration(onReadingBytes: (e, available) =>
            {
                Interlocked.Increment(ref count);
                semaphore.Release();
                
            });
            var endpoint = await Endpoint.ResolveAsync(TestConfig.ServerUri(), TestConfig.Log);
            using (var server = new FakeTcpServer(TestConfig.Log, endpoint.Value.Port))
            using (var conn = new Connection(endpoint, config, log: TestConfig.Log))
            {
                var token = new CancellationTokenSource();

                var taskResult = conn.SendAsync(new FetchRequest(), token.Token);

                Thread.Sleep(100);
                token.Cancel();

                semaphore.Wait(TimeSpan.FromSeconds(1));
                Assert.That(count, Is.EqualTo(1), "Read should have cancelled and incremented count.");
                Assert.That(taskResult.IsCanceled, Is.True);
            }
        }

        [Test]
        public async Task ReadShouldCancelWhileAwaitingReconnection()
        {
            int connectionAttempt = 0;
            var config = new ConnectionConfiguration(onConnecting: (e, attempt, elapsed) => connectionAttempt = attempt);
            var endpoint = await Endpoint.ResolveAsync(TestConfig.ServerUri(), TestConfig.Log);
            using (var conn = new Connection(endpoint, config, log: TestConfig.Log))
            using (var token = new CancellationTokenSource())
            {
                var taskResult = conn.SendAsync(new FetchRequest(), token.Token);

                await TaskTest.WaitFor(() => connectionAttempt > 1);

                token.Cancel();

                await Task.WhenAny(taskResult, Task.Delay(500));

                Assert.That(taskResult.IsCanceled, Is.True);
            }
        }

        [Test]
        public async Task ReadShouldReconnectEvenAfterCancelledRead()
        {
            int connectionAttempt = 0;
            var config = new ConnectionConfiguration(onConnecting: (e, attempt, elapsed) => Interlocked.Exchange(ref connectionAttempt, attempt));
            var endpoint = await Endpoint.ResolveAsync(TestConfig.ServerUri(), TestConfig.Log);
            using (var conn = new Connection(endpoint, config, log: TestConfig.Log))
            using (var token = new CancellationTokenSource())
            {
                var taskResult = conn.SendAsync(new FetchRequest(), token.Token);

                await TaskTest.WaitFor(() => connectionAttempt > 1);

                var attemptsMadeSoFar = connectionAttempt;

                token.Cancel();

                await TaskTest.WaitFor(() => connectionAttempt > attemptsMadeSoFar);

                Assert.That(connectionAttempt, Is.GreaterThan(attemptsMadeSoFar));
            }
        }

        private class ExplicitlyReadingConnection : Connection
        {
            public ExplicitlyReadingConnection(Endpoint endpoint, IConnectionConfiguration configuration = null, ILog log = null) 
                : base(endpoint, configuration, log)
            {
                // to avoid reading on a background task
                ActiveReaderCount = 1;
            }

            public override void Dispose()
            {
                base.Dispose();
                DisposeFinal();
            }
        }

        [Test]
        public async Task ReadShouldBlockUntilAllBytesRequestedAreReceived()
        {
            var sendCompleted = 0;
            var bytesReceived = 0;
            var config = new ConnectionConfiguration(
                onReadBytes: (e, attempted, actual, elapsed) => Interlocked.Add(ref bytesReceived, actual));
            var endpoint = await Endpoint.ResolveAsync(TestConfig.ServerUri(), TestConfig.Log);
            using (var server = new FakeTcpServer(TestConfig.Log, endpoint.Value.Port))
            using (var conn = new ExplicitlyReadingConnection(endpoint, config, log: TestConfig.Log))
            {
                var socket = await conn.ConnectAsync(CancellationToken.None);
                var buffer = new byte[4];
                var taskResult = conn.ReadBytesAsync(socket, buffer, 4, _ => { }, CancellationToken.None).ContinueWith(t =>
                    {
                        Interlocked.Increment(ref sendCompleted);
                    });

                // Sending first 3 bytes...
                await server.HasClientConnected;
                await server.SendDataAsync(new byte[] { 0, 0, 0 });

                // Ensuring task blocks...
                await TaskTest.WaitFor(() => bytesReceived > 0);
                Assert.That(taskResult.IsCompleted, Is.False, "Task should still be running, blocking.");
                Assert.That(sendCompleted, Is.EqualTo(0), "Should still block even though bytes have been received.");
                Assert.That(bytesReceived, Is.EqualTo(3), "Three bytes should have been received and we are waiting on the last byte.");

                // Sending last byte...
                var sendLastByte = server.SendDataAsync(new byte[] { 0 }).Wait(TimeSpan.FromSeconds(10));
                Assert.That(sendLastByte, Is.True, "Last byte should have sent.");

                // Ensuring task unblocks...
                await TaskTest.WaitFor(() => taskResult.IsCompleted);
                Assert.That(bytesReceived, Is.EqualTo(4), "Should have received 4 bytes.");
                Assert.That(taskResult.IsCompleted, Is.True, "Task should have completed.");
                Assert.That(sendCompleted, Is.EqualTo(1), "Task ContinueWith should have executed.");
            }
        }

        [Test]
        public async Task ReadShouldBeAbleToReceiveMoreThanOnce()
        {
            var endpoint = await Endpoint.ResolveAsync(TestConfig.ServerUri(), TestConfig.Log);
            using (var server = new FakeTcpServer(TestConfig.Log, endpoint.Value.Port))
            using (var conn = new ExplicitlyReadingConnection(endpoint, log: TestConfig.Log))
            {
                const int firstMessage = 99;
                const string secondMessage = "testmessage";

                // Sending first message to receive...
                var send = server.SendDataAsync(firstMessage.ToBytes());

                var socket = await conn.ConnectAsync(CancellationToken.None);
                var buffer = new byte[48];
                await conn.ReadBytesAsync(socket, buffer, 4, _ => { }, CancellationToken.None);
                Assert.That(buffer.ToInt32(), Is.EqualTo(firstMessage));

                // Sending second message to receive...
                var send2 = server.SendDataAsync(secondMessage);
                var result = new MemoryStream();
                await conn.ReadBytesAsync(socket, buffer, secondMessage.Length, _ => { result.Write(buffer, 0, _); }, CancellationToken.None);
                Assert.That(Encoding.ASCII.GetString(result.ToArray(), 0, (int)result.Position), Is.EqualTo(secondMessage));
            }
        }

        [Test]
        public async Task ReadShouldBeAbleToReceiveMoreThanOnceAsyncronously()
        {
            var endpoint = await Endpoint.ResolveAsync(TestConfig.ServerUri(), TestConfig.Log);
            using (var server = new FakeTcpServer(TestConfig.Log, endpoint.Value.Port))
            using (var conn = new ExplicitlyReadingConnection(endpoint, log: TestConfig.Log))
            {
                const int firstMessage = 99;
                const int secondMessage = 100;

                var socket = await conn.ConnectAsync(CancellationToken.None);

                // Sending first message to receive..."
                var send1 = server.SendDataAsync(firstMessage.ToBytes());
                var buffer1 = new byte[4];
                var firstResponseTask = conn.ReadBytesAsync(socket, buffer1, 4, _ => { }, CancellationToken.None);

                // Sending second message to receive...
                var send2 = server.SendDataAsync(secondMessage.ToBytes());
                var buffer2 = new byte[4];
                var secondResponseTask = conn.ReadBytesAsync(socket, buffer2, 4, _ => { }, CancellationToken.None);

                await Task.WhenAll(firstResponseTask, secondResponseTask);
                Assert.That(buffer1.ToInt32(), Is.EqualTo(firstMessage));
                Assert.That(buffer2.ToInt32(), Is.EqualTo(secondMessage));
            }
        }

        [Test]
        public async Task ReadShouldNotLoseDataFromStreamOverMultipleReads()
        {
            var endpoint = await Endpoint.ResolveAsync(TestConfig.ServerUri(), TestConfig.Log);
            using (var server = new FakeTcpServer(TestConfig.Log, endpoint.Value.Port))
            using (var conn = new ExplicitlyReadingConnection(endpoint, log: TestConfig.Log))
            {
                const int firstMessage = 99;
                const string secondMessage = "testmessage";
                var bytes = Encoding.UTF8.GetBytes(secondMessage);

                var payload = new KafkaWriter()
                    .Write(firstMessage)
                    .Write(bytes, false);

                //send the combined payload
                var send = server.SendDataAsync(payload.ToBytesNoLength());

                var socket = await conn.ConnectAsync(CancellationToken.None);
                var buffer = new byte[48];
                await conn.ReadBytesAsync(socket, buffer, 4, _ => { }, CancellationToken.None);
                Assert.That(buffer.ToInt32(), Is.EqualTo(firstMessage));

                // Sending second message to receive...
                var send2 = server.SendDataAsync(secondMessage);
                var result = new MemoryStream();
                await conn.ReadBytesAsync(socket, buffer, secondMessage.Length, _ => { result.Write(buffer, 0, _); }, CancellationToken.None);
                Assert.That(Encoding.ASCII.GetString(result.ToArray(), 0, (int)result.Position), Is.EqualTo(secondMessage));
            }
        }

        [Test]
        public async Task ReadShouldThrowServerDisconnectedExceptionWhenDisconnected()
        {
            var endpoint = await Endpoint.ResolveAsync(TestConfig.ServerUri(), TestConfig.Log);
            using (var server = new FakeTcpServer(TestConfig.Log, endpoint.Value.Port))
            using (var conn = new ExplicitlyReadingConnection(endpoint, log: TestConfig.Log))
            {
                var socket = await conn.ConnectAsync(CancellationToken.None);
                var buffer = new byte[48];
                var taskResult = conn.ReadBytesAsync(socket, buffer, 4, _ => { }, CancellationToken.None);

                //wait till connected
                await TaskTest.WaitFor(() => server.ConnectionEventcount > 0);

                server.DropConnection();

                await TaskTest.WaitFor(() => server.DisconnectionEventCount > 0);

                await Task.WhenAny(taskResult, Task.Delay(1000)).ConfigureAwait(false);

                Assert.That(taskResult.IsFaulted, Is.True);
                Assert.That(taskResult.Exception.InnerException, Is.TypeOf<ConnectionException>());
            }
        }

        [Test]
        public async Task WhenNoConnectionThrowSocketExceptionAfterMaxRetry()
        {
            var reconnectionAttempt = 0;
            const int maxRetries = 3;
            var endpoint = await Endpoint.ResolveAsync(TestConfig.ServerUri(), TestConfig.Log);
            var config = new ConnectionConfiguration(
                new Retry(null, maxRetries),
                onConnecting: (e, attempt, elapsed) => Interlocked.Increment(ref reconnectionAttempt)
                );
            using (var conn = new ExplicitlyReadingConnection(endpoint, config, log: TestConfig.Log)) {
                try {
                    await conn.ConnectAsync(CancellationToken.None);
                    Assert.Fail("Did not throw ConnectionException");
                } catch (ConnectionException) {
                    // expected
                }
                Assert.That(reconnectionAttempt, Is.EqualTo(maxRetries + 1));
            }
        }

        [Test]
        public async Task ShouldReconnectAfterLosingConnectionAndBeAbleToStartNewRead()
        {
            var log = TestConfig.Log;
            var endpoint = await Endpoint.ResolveAsync(TestConfig.ServerUri(), TestConfig.Log);
            using (var server = new FakeTcpServer(TestConfig.Log, endpoint.Value.Port)) {
                var serverDisconnects = 0;
                var serverConnects = 0;
                var clientDisconnects = 0;
                var clientReads = 0;
                var clientBytesRead = 0;

                server.OnClientConnected = () => Interlocked.Increment(ref serverConnects);
                server.OnClientDisconnected = () => Interlocked.Increment(ref serverDisconnects);
                var config = new ConnectionConfiguration(
                    onDisconnected: (e, exception) => Interlocked.Increment(ref clientDisconnects),
                    onReading:(e, available) => Interlocked.Increment(ref clientReads),
                    onRead: (e, read, elapsed) => Interlocked.Add(ref clientBytesRead, read));
                using (var conn = new Connection(endpoint, config, log)) {
                    await TaskTest.WaitFor(() => serverConnects > 0);
                    await TaskTest.WaitFor(() => clientReads > 0, 1000);

                    server.DropConnection();

                    await TaskTest.WaitFor(() => clientDisconnects > 0, 10000);
                    Assert.That(clientDisconnects, Is.AtLeast(1), "The client should have disconnected.");
                    Assert.That(clientBytesRead, Is.EqualTo(0), "client should not have received any bytes.");

                    await TaskTest.WaitFor(() => serverConnects == 2, 6000);
                    Assert.That(serverConnects, Is.EqualTo(2), "Socket should have reconnected.");

                    await server.SendDataAsync(99.ToBytes());
                    await TaskTest.WaitFor(() => clientBytesRead == 4, 1000);
                    Assert.That(clientBytesRead, Is.AtLeast(4), "client should have read the 4 bytes.");
                }
            }
        }

        [Test]
        public async Task ReadShouldStackReadRequestsAndReturnOneAtATime()
        {
            var endpoint = await Endpoint.ResolveAsync(TestConfig.ServerUri(), TestConfig.Log);
            using (var server = new FakeTcpServer(TestConfig.Log, endpoint.Value.Port))
            {
                var messages = new[] { "test1", "test2", "test3", "test4" };
                var expectedLength = "test1".Length;

                var payload = new KafkaWriter().Write(messages);

                using (var conn = new ExplicitlyReadingConnection(endpoint, log: TestConfig.Log)) {
                    var socket = await conn.ConnectAsync(CancellationToken.None);
                    var tasks = messages.Select(
                        x =>
                        {
                            var b = new byte[x.Length];
                            return conn.ReadBytesAsync(socket, b, b.Length, _ => { }, CancellationToken.None);
                        }).ToArray();

                    var send = server.SendDataAsync(payload.ToBytes());

                    Task.WaitAll(tasks);

                    foreach (var task in tasks) {
                        Assert.That(task.Result, Is.EqualTo(expectedLength));
                    }
                }
            }
        }

        #endregion Read Tests...

        #region Send Tests...

        [Test]
        public async Task SendAsyncShouldTimeoutWhenSendAsyncTakesTooLong()
        {
            var endpoint = await Endpoint.ResolveAsync(TestConfig.ServerUri(), TestConfig.Log);
            using (var server = new FakeTcpServer(TestConfig.Log, endpoint.Value.Port))
            using (var conn = new Connection(endpoint, new ConnectionConfiguration(requestTimeout: TimeSpan.FromMilliseconds(1)), log: TestConfig.Log))
            {
                await TaskTest.WaitFor(() => server.ConnectionEventcount > 0);
                Assert.That(server.ConnectionEventcount, Is.EqualTo(1));

                var taskResult = conn.SendAsync(new MetadataRequest(), CancellationToken.None);

                taskResult.ContinueWith(t => taskResult = t).Wait(TimeSpan.FromMilliseconds(100));

                Assert.That(taskResult.IsFaulted, Is.True, "Task should have reported an exception.");
                Assert.That(taskResult.Exception.InnerException, Is.TypeOf<TimeoutException>());
            }
        }

        [Test, Timeout(15000)]
        public async Task SendAsyncShouldNotAllowResponseToTimeoutWhileAwaitingKafkaToEstableConnection()
        {
            var endpoint = await Endpoint.ResolveAsync(TestConfig.ServerUri(), TestConfig.Log);
            using (var conn = new Connection(endpoint, new ConnectionConfiguration(requestTimeout: TimeSpan.FromSeconds(1000)), log: TestConfig.Log))
            {
                // SendAsync blocked by reconnection attempts
                var taskResult = conn.SendAsync(new MetadataRequest(), CancellationToken.None);

                // Task result should be WaitingForActivation
                Assert.That(taskResult.IsFaulted, Is.False);
                Assert.That(taskResult.Status, Is.EqualTo(TaskStatus.WaitingForActivation));

                // Starting server to establish connection
                using (var server = new FakeTcpServer(TestConfig.Log, endpoint.Value.Port))
                {
                    server.OnClientConnected = () => Console.WriteLine("Client connected...");
                    server.OnBytesReceived = b => {
                        var request = KafkaDecoder.DecodeHeader(b);
                        AsyncContext.Run(async () => await server.SendDataAsync(MessageHelper.CreateMetadataResponse(request.CorrelationId, "Test")));
                    };

                    await Task.WhenAny(taskResult, Task.Delay(TimeSpan.FromSeconds(5)));

                    Assert.That(taskResult.IsFaulted, Is.False);
                    Assert.That(taskResult.IsCanceled, Is.False);
                    await taskResult;
                    Assert.That(taskResult.Status, Is.EqualTo(TaskStatus.RanToCompletion));
                }
            }
        }

        [Test]
        public async Task SendAsyncShouldUseStatictVersionInfo()
        {
            IRequestContext context = null;
            var endpoint = await Endpoint.ResolveAsync(TestConfig.ServerUri(), TestConfig.Log);
            using (var server = new FakeTcpServer(TestConfig.Log, endpoint.Value.Port))
            using (var conn = new Connection(endpoint, new ConnectionConfiguration(requestTimeout: TimeSpan.FromSeconds(1000), versionSupport: VersionSupport.Kafka10), log: TestConfig.Log))
            {
                server.OnBytesReceived = data =>
                {
                    context = KafkaDecoder.DecodeHeader(data);
                    var send = server.SendDataAsync(KafkaDecoder.EncodeResponseBytes(context, new FetchResponse()));
                };

                await conn.SendAsync(new FetchRequest(new FetchRequest.Topic("Foo", 0, 0)), CancellationToken.None);
                await TaskTest.WaitFor(() => context != null);

                Assert.That(context.ApiVersion.Value, Is.EqualTo(2));
            }
        }

        [Test]
        public async Task SendAsyncShouldTimeoutMultipleMessagesAtATime()
        {
            var endpoint = await Endpoint.ResolveAsync(TestConfig.ServerUri(), TestConfig.Log);
            using (var server = new FakeTcpServer(TestConfig.Log, endpoint.Value.Port))
            using (var conn = new Connection(endpoint, new ConnectionConfiguration(requestTimeout: TimeSpan.FromMilliseconds(100)), log: TestConfig.Log))
            {
                server.HasClientConnected.Wait(TimeSpan.FromSeconds(3));
                Assert.That(server.ConnectionEventcount, Is.EqualTo(1));

                var tasks = new[] {
                    conn.SendAsync(new MetadataRequest(), CancellationToken.None),
                    conn.SendAsync(new MetadataRequest(), CancellationToken.None),
                    conn.SendAsync(new MetadataRequest(), CancellationToken.None)
                };

                await TaskTest.WaitFor(() => tasks.All(t => t.IsFaulted));
                foreach (var task in tasks)
                {
                    Assert.That(task.IsFaulted, Is.True, "Task should have faulted.");
                    Assert.That(task.Exception.InnerException, Is.TypeOf<TimeoutException>(), "Task fault has wrong type.");
                }
            }
        }

        [Test]
        public async Task WriteAsyncShouldSendData()
        {
            var endpoint = await Endpoint.ResolveAsync(TestConfig.ServerUri(), TestConfig.Log);
            using (var server = new FakeTcpServer(TestConfig.Log, endpoint.Value.Port))
            using (var conn = new Connection(endpoint, new ConnectionConfiguration(requestTimeout: TimeSpan.FromSeconds(1000), versionSupport: VersionSupport.Kafka10), log: TestConfig.Log))
            {
                const int testData = 99;
                int result = 0;

                server.OnBytesReceived = data => result = data.ToInt32();

                var socket = await conn.ConnectAsync(CancellationToken.None);
                await conn.WriteBytesAsync(socket, 5, new ArraySegment<byte>(testData.ToBytes()), CancellationToken.None);
                await TaskTest.WaitFor(() => result > 0);
                Assert.That(result, Is.EqualTo(testData));
            }
        }

        [Test]
        public async Task WriteAsyncShouldAllowMoreThanOneWrite()
        {
            var endpoint = await Endpoint.ResolveAsync(TestConfig.ServerUri(), TestConfig.Log);
            using (var server = new FakeTcpServer(TestConfig.Log, endpoint.Value.Port))
            using (var conn = new Connection(endpoint, new ConnectionConfiguration(requestTimeout: TimeSpan.FromSeconds(1000), versionSupport: VersionSupport.Kafka10), log: TestConfig.Log))
            {
                const int testData = 99;
                var results = new List<byte>();

                server.OnBytesReceived = results.AddRange;

                var socket = await conn.ConnectAsync(CancellationToken.None);
                await Task.WhenAll(conn.WriteBytesAsync(socket, 5, new ArraySegment<byte>(testData.ToBytes()), CancellationToken.None), conn.WriteBytesAsync(socket, 6, new ArraySegment<byte>(testData.ToBytes()), CancellationToken.None));
                await TaskTest.WaitFor(() => results.Count >= 8);
                Assert.That(results.Count, Is.EqualTo(8));
            }
        }

        [Test]
        public async Task AsynchronousWriteAndReadShouldBeConsistent()
        {
            const int requests = 10;
            var expected = requests.Repeat(i => i).ToList();
            var readOnServer = new ConcurrentBag<int>();
            var readOnClient = new ConcurrentBag<int>();

            var endpoint = await Endpoint.ResolveAsync(TestConfig.ServerUri(), TestConfig.Log);
            using (var server = new FakeTcpServer(TestConfig.Log, endpoint.Value.Port))
            using (var conn = new ExplicitlyReadingConnection(endpoint, new ConnectionConfiguration(requestTimeout: TimeSpan.FromSeconds(1000), versionSupport: VersionSupport.Kafka10), log: TestConfig.Log))
            {
                server.OnBytesReceived = data => {
                    var d = data.Batch(4).Select(x => x.ToArray().ToInt32());
                    foreach (var item in d) {
                        readOnServer.Add(item);
                    }
                };
                var socket = await conn.ConnectAsync(CancellationToken.None);

                var clientWriteTasks = expected.Select(i => conn.WriteBytesAsync(socket, i, new ArraySegment<byte>(i.ToBytes()), CancellationToken.None));
                var clientReadTasks = expected.Select(
                    i =>
                    {
                        var b = new byte[4];
                        return conn.ReadBytesAsync(socket, b, b.Length, _ => { }, CancellationToken.None)
                                   .ContinueWith(t => readOnClient.Add(b.ToInt32()));
                    });
                var serverWriteTasks = expected.Select(i => server.SendDataAsync(i.ToBytes()));

                await Task.WhenAll(clientWriteTasks.Union(clientReadTasks).Union(serverWriteTasks));
                await TaskTest.WaitFor(() => readOnServer.Count == requests);
                Assert.That(readOnServer.Count, Is.EqualTo(requests), "not all writes propagated to the server in time");
                await TaskTest.WaitFor(() => readOnClient.Count == requests);
                Assert.That(readOnClient.Count, Is.EqualTo(requests), "not all reads happend on the client in time");
                var w = readOnServer.OrderBy(x => x);
                var r = readOnClient.OrderBy(x => x);

                for (var i = 0; i < requests; i++) {
                    Assert.That(w.ElementAt(i), Is.EqualTo(expected[i]));
                }
                for (var i = 0; i < requests; i++) {
                    Assert.That(r.ElementAt(i), Is.EqualTo(expected[i]));
                }
            }
        }

        [Test]
        public async Task WriteShouldHandleLargeVolumeSendAsynchronously([Values(1000, 5000)] int requests)
        {
            var readOnServer = new ConcurrentBag<int>();
            var endpoint = await Endpoint.ResolveAsync(TestConfig.ServerUri(), TestConfig.Log);
            using (var server = new FakeTcpServer(TestConfig.Log, endpoint.Value.Port))
            using (var conn = new ExplicitlyReadingConnection(endpoint, new ConnectionConfiguration(requestTimeout: TimeSpan.FromSeconds(1000), versionSupport: VersionSupport.Kafka10), log: TestConfig.Log))
            {
                server.OnBytesReceived = data =>
                {
                    var d = data.Batch(4).Select(x => x.ToArray().ToInt32());
                    foreach (var item in d) {
                        readOnServer.Add(item);
                    }
                };
                var socket = await conn.ConnectAsync(CancellationToken.None);
                var clientWriteTasks = Enumerable.Range(1, requests).Select(i => conn.WriteBytesAsync(socket, i, new ArraySegment<byte>(i.ToBytes()), CancellationToken.None));

                await Task.WhenAll(clientWriteTasks);
                await TaskTest.WaitFor(() => readOnServer.Count == requests);
                Assert.That(readOnServer.Count, Is.EqualTo(requests), "not all writes propagated to the server in time");
                Assert.That(readOnServer.OrderBy(x => x), Is.EqualTo(Enumerable.Range(1, requests)));
            }
        }

        [Test]
        public async Task WriteShouldCancelWhileSendingData()
        {
            var clientWriteAttempts = 0;
            var config = new ConnectionConfiguration(onWritingBytes: (e, payload) => Interlocked.Increment(ref clientWriteAttempts));
            var endpoint = await Endpoint.ResolveAsync(TestConfig.ServerUri(), TestConfig.Log);
            using (var server = new FakeTcpServer(TestConfig.Log, endpoint.Value.Port))
            using (var conn = new ExplicitlyReadingConnection(endpoint, config, log: TestConfig.Log))
            using (var token = new CancellationTokenSource())
            {
                var socket = await conn.ConnectAsync(token.Token);
                var write = conn.WriteBytesAsync(socket, 5, new ArraySegment<byte>(1.ToBytes()), token.Token);

                await TaskTest.WaitFor(() => server.ConnectionEventcount > 0);
                await TaskTest.WaitFor(() => clientWriteAttempts > 0);

                Assert.That(clientWriteAttempts, Is.EqualTo(1), "Socket should have attempted to write.");

                //create a buffer write that will take a long time
                var data = Enumerable.Range(0, 100000000).Select(b => (byte)b).ToArray();
                token.Cancel();
                var taskResult = conn.WriteBytesAsync(socket, 6, new ArraySegment<byte>(data), token.Token);
                await Task.WhenAny(taskResult, Task.Delay(TimeSpan.FromSeconds(5))).ConfigureAwait(false);

                Assert.That(taskResult.IsCanceled, Is.True, "Task should have cancelled.");
            }
        }

        #endregion Send Tests...

        private static byte[] CreateCorrelationMessage(int id)
        {
            return new KafkaWriter().Write(id).ToBytes();
        }
    }
}