// Copyright (c) 2012-2020 fo-dicom contributors.
// Licensed under the Microsoft Public License (MS-PL).

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using FellowOakDicom.Imaging.Codec;
using FellowOakDicom.Log;
using FellowOakDicom.Network;
using FellowOakDicom.Network.Client;
using FellowOakDicom.Network.Client.EventArguments;
using FellowOakDicom.Tests.Helpers;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Xunit;
using Xunit.Abstractions;

namespace FellowOakDicom.Tests.Network.Client
{

    [Collection("Network"), Trait("Category", "Network")]
    public class DicomClientTimeoutTest
    {
        #region Fields

        private readonly XUnitDicomLogger _logger;

        #endregion

        #region Constructors

        public DicomClientTimeoutTest(ITestOutputHelper testOutputHelper)
        {
            _logger = new XUnitDicomLogger(testOutputHelper)
                .IncludeTimestamps()
                .IncludeThreadId()
                .WithMinimumLevel(LogLevel.Debug);
        }

        #endregion

        #region Unit tests

        private IDicomServer CreateServer<T>(int port) where T : DicomService, IDicomServiceProvider
        {
            var server = DicomServerFactory.Create<T>(port);
            server.Logger = _logger.IncludePrefix(typeof(T).Name).WithMinimumLevel(LogLevel.Debug);
            return server;
        }

        private IDicomClient CreateClient(int port)
        {
            var client = DicomClientFactory.Create("127.0.0.1", port, false, "SCU", "ANY-SCP");
            client.Logger = _logger.IncludePrefix(typeof(DicomClient).Name).WithMinimumLevel(LogLevel.Debug);
            return client;
        }

        private IDicomClientFactory CreateClientFactory(INetworkManager networkManager)
        {
            return new DefaultDicomClientFactory(
                Setup.ServiceProvider.GetRequiredService<ILogManager>(),
                networkManager,
                Setup.ServiceProvider.GetRequiredService<ITranscoderManager>(),
                Setup.ServiceProvider.GetRequiredService<IOptions<DicomClientOptions>>(),
                Setup.ServiceProvider.GetRequiredService<IOptions<DicomServiceOptions>>());
        }

        [Fact]
        public async Task SendingFindRequestToServerThatNeverRespondsShouldTimeout()
        {
            var port = Ports.GetNext();
            using (CreateServer<NeverRespondingDicomServer>(port))
            {
                var client = CreateClient(port);

                client.ServiceOptions.RequestTimeout = TimeSpan.FromSeconds(2);

                var request = new DicomCFindRequest(DicomQueryRetrieveLevel.Patient)
                {
                    Dataset = new DicomDataset
                    {
                        {DicomTag.PatientID, "PAT123"}
                    },
                    OnResponseReceived = (req, res) => throw new Exception("Did not expect a response"),
                };

                DicomRequest.OnTimeoutEventArgs eventArgsFromRequestTimeout = null;
                request.OnTimeout += (sender, args) => eventArgsFromRequestTimeout = args;
                RequestTimedOutEventArgs eventArgsFromDicomClientRequestTimedOut = null;
                client.RequestTimedOut += (sender, args) => eventArgsFromDicomClientRequestTimedOut = args;

                await client.AddRequestAsync(request).ConfigureAwait(false);

                var sendTask = client.SendAsync();
                var sendTimeoutCancellationTokenSource = new CancellationTokenSource();
                var sendTimeout = Task.Delay(TimeSpan.FromSeconds(10), sendTimeoutCancellationTokenSource.Token);

                var winner = await Task.WhenAny(sendTask, sendTimeout).ConfigureAwait(false);

                sendTimeoutCancellationTokenSource.Cancel();
                sendTimeoutCancellationTokenSource.Dispose();

                Assert.Equal(winner, sendTask);
                Assert.NotNull(eventArgsFromRequestTimeout);
                Assert.NotNull(eventArgsFromDicomClientRequestTimedOut);
                Assert.Equal(request, eventArgsFromDicomClientRequestTimedOut.Request);
                Assert.Equal(client.ServiceOptions.RequestTimeout, eventArgsFromDicomClientRequestTimedOut.Timeout);
            }
        }

        [Fact]
        public async Task SendingMoveRequestToServerThatNeverRespondsShouldTimeout()
        {
            var port = Ports.GetNext();
            using (CreateServer<NeverRespondingDicomServer>(port))
            {
                var client = CreateClient(port);

                client.ServiceOptions.RequestTimeout = TimeSpan.FromSeconds(2);

                var request = new DicomCMoveRequest("another-AE", "study123")
                {
                    OnResponseReceived = (req, res) => throw new Exception("Did not expect a response")
                };

                DicomRequest.OnTimeoutEventArgs onTimeoutEventArgs = null;
                request.OnTimeout += (sender, args) => onTimeoutEventArgs = args;
                RequestTimedOutEventArgs eventArgsFromDicomClientRequestTimedOut = null;
                client.RequestTimedOut += (sender, args) => eventArgsFromDicomClientRequestTimedOut = args;

                await client.AddRequestAsync(request).ConfigureAwait(false);

                var sendTask = client.SendAsync();
                var sendTimeoutCancellationTokenSource = new CancellationTokenSource();
                var sendTimeout = Task.Delay(TimeSpan.FromSeconds(10), sendTimeoutCancellationTokenSource.Token);

                var winner = await Task.WhenAny(sendTask, sendTimeout).ConfigureAwait(false);

                sendTimeoutCancellationTokenSource.Cancel();
                sendTimeoutCancellationTokenSource.Dispose();

                Assert.Equal(winner, sendTask);
                Assert.NotNull(onTimeoutEventArgs);
                Assert.NotNull(eventArgsFromDicomClientRequestTimedOut);
                Assert.Equal(request, eventArgsFromDicomClientRequestTimedOut.Request);
                Assert.Equal(client.ServiceOptions.RequestTimeout, eventArgsFromDicomClientRequestTimedOut.Timeout);
            }
        }

        [Fact]
        public async Task SendingFindRequestToServerThatSendsPendingResponsesWithinTimeoutShouldNotTimeout()
        {
            var port = Ports.GetNext();
            using (CreateServer<FastPendingResponsesDicomServer>(port))
            {
                var client = CreateClient(port);

                client.ServiceOptions.RequestTimeout = TimeSpan.FromSeconds(2);

                DicomCFindResponse lastResponse = null;
                var request = new DicomCFindRequest(DicomQueryRetrieveLevel.Patient)
                {
                    Dataset = new DicomDataset
                    {
                        {DicomTag.PatientID, "PAT123"}
                    },
                    OnResponseReceived = (req, res) => lastResponse = res
                };

                DicomRequest.OnTimeoutEventArgs onTimeoutEventArgs = null;
                request.OnTimeout += (sender, args) => onTimeoutEventArgs = args;

                await client.AddRequestAsync(request).ConfigureAwait(false);

                var sendTask = client.SendAsync();
                var sendTimeoutCancellationTokenSource = new CancellationTokenSource();
                var sendTimeout = Task.Delay(TimeSpan.FromSeconds(10), sendTimeoutCancellationTokenSource.Token);

                var winner = await Task.WhenAny(sendTask, sendTimeout).ConfigureAwait(false);

                sendTimeoutCancellationTokenSource.Cancel();
                sendTimeoutCancellationTokenSource.Dispose();

                Assert.Equal(winner, sendTask);
                Assert.NotNull(lastResponse);
                Assert.Equal(lastResponse.Status, DicomStatus.Success);
                Assert.Null(onTimeoutEventArgs);
            }
        }

        [Fact]
        public async Task SendingMoveRequestToServerThatSendsPendingResponsesWithinTimeoutShouldNotTimeout()
        {
            var port = Ports.GetNext();
            using (CreateServer<FastPendingResponsesDicomServer>(port))
            {
                var client = CreateClient(port);

                client.ServiceOptions.RequestTimeout = TimeSpan.FromSeconds(2);

                DicomCMoveResponse lastResponse = null;
                var request = new DicomCMoveRequest("another-AE", "study123")
                {
                    OnResponseReceived = (req, res) => lastResponse = res
                };

                DicomRequest.OnTimeoutEventArgs onTimeoutEventArgs = null;
                request.OnTimeout += (sender, args) => onTimeoutEventArgs = args;

                await client.AddRequestAsync(request).ConfigureAwait(false);

                var sendTask = client.SendAsync();
                var sendTimeoutCancellationTokenSource = new CancellationTokenSource();
                var sendTimeout = Task.Delay(TimeSpan.FromSeconds(10), sendTimeoutCancellationTokenSource.Token);

                var winner = await Task.WhenAny(sendTask, sendTimeout).ConfigureAwait(false);

                sendTimeoutCancellationTokenSource.Cancel();
                sendTimeoutCancellationTokenSource.Dispose();

                Assert.Equal(winner, sendTask);
                Assert.NotNull(lastResponse);
                Assert.Equal(lastResponse.Status, DicomStatus.Success);
                Assert.Null(onTimeoutEventArgs);
            }
        }

        [Fact]
        public async Task SendingFindRequestToServerThatSendsPendingResponsesTooSlowlyShouldTimeout()
        {
            var port = Ports.GetNext();
            using (CreateServer<SlowPendingResponsesDicomServer>(port))
            {
                var client = CreateClient(port);

                client.ServiceOptions.RequestTimeout = TimeSpan.FromSeconds(2);

                var request = new DicomCFindRequest(DicomQueryRetrieveLevel.Patient)
                {
                    Dataset = new DicomDataset
                    {
                        {DicomTag.PatientID, "PAT123"}
                    }
                };

                DicomRequest.OnTimeoutEventArgs onTimeoutEventArgs = null;
                request.OnTimeout += (sender, args) => onTimeoutEventArgs = args;
                RequestTimedOutEventArgs eventArgsFromDicomClientRequestTimedOut = null;
                client.RequestTimedOut += (sender, args) => eventArgsFromDicomClientRequestTimedOut = args;

                await client.AddRequestAsync(request).ConfigureAwait(false);

                var sendTask = client.SendAsync();
                var sendTimeoutCancellationTokenSource = new CancellationTokenSource();
                var sendTimeout = Task.Delay(TimeSpan.FromSeconds(10), sendTimeoutCancellationTokenSource.Token);

                var winner = await Task.WhenAny(sendTask, sendTimeout).ConfigureAwait(false);

                sendTimeoutCancellationTokenSource.Cancel();
                sendTimeoutCancellationTokenSource.Dispose();

                Assert.Equal(winner, sendTask);
                Assert.NotNull(onTimeoutEventArgs);
                Assert.NotNull(eventArgsFromDicomClientRequestTimedOut);
                Assert.Equal(request, eventArgsFromDicomClientRequestTimedOut.Request);
                Assert.Equal(client.ServiceOptions.RequestTimeout, eventArgsFromDicomClientRequestTimedOut.Timeout);
            }
        }

        [Fact]
        public async Task SendingMoveRequestToServerThatSendsPendingResponsesTooSlowlyShouldTimeout()
        {
            var port = Ports.GetNext();
            using (CreateServer<SlowPendingResponsesDicomServer>(port))
            {
                var client = CreateClient(port);

                client.ServiceOptions.RequestTimeout = TimeSpan.FromSeconds(2);

                var request = new DicomCMoveRequest("another-AE", "study123");

                DicomRequest.OnTimeoutEventArgs onTimeoutEventArgs = null;
                request.OnTimeout += (sender, args) => onTimeoutEventArgs = args;
                RequestTimedOutEventArgs eventArgsFromDicomClientRequestTimedOut = null;
                client.RequestTimedOut += (sender, args) => eventArgsFromDicomClientRequestTimedOut = args;

                await client.AddRequestAsync(request).ConfigureAwait(false);

                var sendTask = client.SendAsync();
                var sendTimeoutCancellationTokenSource = new CancellationTokenSource();
                var sendTimeout = Task.Delay(TimeSpan.FromSeconds(10), sendTimeoutCancellationTokenSource.Token);

                var winner = await Task.WhenAny(sendTask, sendTimeout).ConfigureAwait(false);

                sendTimeoutCancellationTokenSource.Cancel();
                sendTimeoutCancellationTokenSource.Dispose();

                Assert.Equal(winner, sendTask);
                Assert.NotNull(onTimeoutEventArgs);
                Assert.NotNull(eventArgsFromDicomClientRequestTimedOut);
                Assert.Equal(request, eventArgsFromDicomClientRequestTimedOut.Request);
                Assert.Equal(client.ServiceOptions.RequestTimeout, eventArgsFromDicomClientRequestTimedOut.Timeout);
            }
        }

        [Fact]
        public async Task SendingLargeFileUsingVeryShortResponseTimeoutShouldSucceed()
        {
            var port = Ports.GetNext();
            using (CreateServer<InMemoryDicomCStoreProvider>(port))
            {
                TimeSpan streamWriteTimeout = TimeSpan.FromMilliseconds(10);
                var clientFactory = CreateClientFactory(new VerySlowNetworkManager(streamWriteTimeout));
                var client = clientFactory.Create("127.0.0.1", port, false, "SCU", "ANY-SCP");
                client.Logger = _logger.IncludePrefix(typeof(DicomClient).Name).WithMinimumLevel(LogLevel.Debug);
                client.ServiceOptions.RequestTimeout = TimeSpan.FromSeconds(2);
                client.ServiceOptions.MaxPDULength = 16 * 1024; // 16 KB

                DicomResponse response = null;

                // Size = 5 192 KB, one PDU = 16 KB, so this will result in 325 PDUs
                // If stream timeout = 50ms, then total time to send will be 3s 250ms
                var request = new DicomCStoreRequest(TestData.Resolve("10200904.dcm"))
                {
                    OnResponseReceived = (req, res) => response = res,
                };
                await client.AddRequestAsync(request).ConfigureAwait(false);

                var sendTask = client.SendAsync();
                var sendTimeoutCancellationTokenSource = new CancellationTokenSource();
                var sendTimeout = Task.Delay(TimeSpan.FromSeconds(10), sendTimeoutCancellationTokenSource.Token);

                var winner = await Task.WhenAny(sendTask, sendTimeout).ConfigureAwait(false);

                sendTimeoutCancellationTokenSource.Cancel();
                sendTimeoutCancellationTokenSource.Dispose();

                Assert.Equal(winner, sendTask);

                Assert.NotNull(response);

                Assert.Equal(DicomStatus.Success, response.Status);
            }
        }

        [Fact]
        public async Task SendingLargeFileUsingVeryShortResponseTimeoutAndSendingTakesTooLongShouldFail()
        {
            var port = Ports.GetNext();
            using (CreateServer<InMemoryDicomCStoreProvider>(port))
            {
                TimeSpan streamWriteTimeout = TimeSpan.FromMilliseconds(1500);
                var clientFactory = CreateClientFactory(new VerySlowNetworkManager(streamWriteTimeout));
                var client = clientFactory.Create("127.0.0.1", port, false, "SCU", "ANY-SCP");
                client.Logger = _logger.IncludePrefix(typeof(DicomClient).Name).WithMinimumLevel(LogLevel.Debug);
                client.ServiceOptions.RequestTimeout = TimeSpan.FromSeconds(1);
                client.ServiceOptions.MaxPDULength = 16 * 1024;

                DicomResponse response = null;
                DicomRequest.OnTimeoutEventArgs onTimeoutEventArgs = null;

                // Size = 5 192 KB, one PDU = 16 KB, so this will result in 325 PDUs
                // If stream timeout = 1500ms, then total time to send will be 325 * 1500 = 487.5 seconds
                var request = new DicomCStoreRequest(TestData.Resolve("10200904.dcm"))
                {
                    OnResponseReceived = (req, res) => response = res,
                    OnTimeout = (sender, args) => onTimeoutEventArgs = args
                };

                RequestTimedOutEventArgs eventArgsFromDicomClientRequestTimedOut = null;
                client.RequestTimedOut += (sender, args) => eventArgsFromDicomClientRequestTimedOut = args;
                await client.AddRequestAsync(request).ConfigureAwait(false);

                var sendTask = client.SendAsync();
                var sendTimeoutCancellationTokenSource = new CancellationTokenSource();
                var sendTimeout = Task.Delay(TimeSpan.FromSeconds(10), sendTimeoutCancellationTokenSource.Token);

                var winner = await Task.WhenAny(sendTask, sendTimeout).ConfigureAwait(false);

                sendTimeoutCancellationTokenSource.Cancel();
                sendTimeoutCancellationTokenSource.Dispose();

                Assert.Same(winner, sendTask);
                Assert.Null(response);
                Assert.NotNull(onTimeoutEventArgs);
                Assert.NotNull(eventArgsFromDicomClientRequestTimedOut);
                Assert.Equal(request, eventArgsFromDicomClientRequestTimedOut.Request);
                Assert.Equal(client.ServiceOptions.RequestTimeout, eventArgsFromDicomClientRequestTimedOut.Timeout);
            }
        }

        [Theory]
        [InlineData(/* number of reqs: */ 6, /* max requests per assoc: */ 1, /* async ops invoked: */ 1)]
        [InlineData(/* number of reqs: */ 6, /* max requests per assoc: */ 6, /* async ops invoked: */ 1)]
        [InlineData(/* number of reqs: */ 6, /* max requests per assoc: */ 2, /* async ops invoked: */ 1)]
        [InlineData(/* number of reqs: */ 6, /* max requests per assoc: */ 1, /* async ops invoked: */ 2)]
        [InlineData(/* number of reqs: */ 6, /* max requests per assoc: */ 6, /* async ops invoked: */ 6)]
        public async Task ShouldSendAllRequestsEvenThoughTheyAllTimeOut(int numberOfRequests, int maximumRequestsPerAssociation, int asyncOpsInvoked)
        {
            var options = new
            {
                Requests = numberOfRequests,
                TimeBetweenRequests = TimeSpan.FromMilliseconds(100),
                MaxRequestsPerAssoc = maximumRequestsPerAssociation,
            };

            var port = Ports.GetNext();
            using (CreateServer<NeverRespondingDicomServer>(port))
            {
                var client = CreateClient(port);
                client.NegotiateAsyncOps(asyncOpsInvoked);

                // Ensure the client is quite impatient
                client.ServiceOptions.RequestTimeout = TimeSpan.FromMilliseconds(200);

                var testLogger = _logger.IncludePrefix("Test");
                testLogger.Info($"Beginning {options.Requests} parallel requests with {options.MaxRequestsPerAssoc} requests / association");

                var requests = new List<DicomRequest>();
                for (var i = 1; i <= options.Requests; i++)
                {
                    var request = new DicomCFindRequest(DicomQueryRetrieveLevel.Study);

                    requests.Add(request);
                    await client.AddRequestAsync(request).ConfigureAwait(false);

                    if (i < options.Requests)
                    {
                        testLogger.Info($"Waiting {options.TimeBetweenRequests.TotalMilliseconds}ms between requests");
                        await Task.Delay(options.TimeBetweenRequests);
                        testLogger.Info($"Waited {options.TimeBetweenRequests.TotalMilliseconds}ms, moving on to next request");
                    }
                }

                var timedOutRequests = new List<DicomRequest>();
                client.RequestTimedOut += (sender, args) => { timedOutRequests.Add(args.Request); };

                var sendTask = client.SendAsync();
                var sendTimeoutCancellationTokenSource = new CancellationTokenSource();
                var sendTimeout = Task.Delay(TimeSpan.FromMinutes(1), sendTimeoutCancellationTokenSource.Token);

                var winner = await Task.WhenAny(sendTask, sendTimeout).ConfigureAwait(false);

                sendTimeoutCancellationTokenSource.Cancel();
                sendTimeoutCancellationTokenSource.Dispose();

                if (winner != sendTask)
                    throw new Exception("DicomClient.SendAsync timed out");

                Assert.Equal(requests.OrderBy(m => m.MessageID), timedOutRequests.OrderBy(m => m.MessageID));
            }
        }

        #endregion

        #region Support classes

        internal class VerySlowNetworkManager : DesktopNetworkManager
        {
            private readonly TimeSpan _streamWriteTimeout;

            public VerySlowNetworkManager(TimeSpan streamWriteTimeout)
            {
                _streamWriteTimeout = streamWriteTimeout;
            }

            protected internal override INetworkStream CreateNetworkStreamImpl(string host, int port, bool useTls, bool noDelay, bool ignoreSslPolicyErrors,
                int millisecondsTimeout)
            {
                return new VerySlowDesktopNetworkStreamDecorator(
                    _streamWriteTimeout,
                    new DesktopNetworkStream(host, port, useTls, noDelay, ignoreSslPolicyErrors, millisecondsTimeout)
                );
            }
        }

        private class VerySlowDesktopNetworkStreamDecorator : INetworkStream
        {
            private readonly TimeSpan _streamWriteTimeout;
            private readonly DesktopNetworkStream _desktopNetworkStream;

            public VerySlowDesktopNetworkStreamDecorator(TimeSpan streamWriteTimeout, DesktopNetworkStream desktopNetworkStream)
            {
                _streamWriteTimeout = streamWriteTimeout;
                _desktopNetworkStream = desktopNetworkStream;
            }

            public void Dispose()
            {
                _desktopNetworkStream.Dispose();
            }

            public string RemoteHost => _desktopNetworkStream.RemoteHost;

            public string LocalHost => _desktopNetworkStream.LocalHost;

            public int RemotePort => _desktopNetworkStream.RemotePort;

            public int LocalPort => _desktopNetworkStream.LocalPort;

            public Stream AsStream()
            {
                return new VerySlowStreamDecorator(_streamWriteTimeout, (NetworkStream) _desktopNetworkStream.AsStream());
            }
        }

        private class VerySlowStreamDecorator : Stream
        {
            private readonly TimeSpan _streamWriteTimeout;
            private readonly NetworkStream _inner;

            public VerySlowStreamDecorator(TimeSpan streamWriteTimeout, NetworkStream inner)
            {
                _streamWriteTimeout = streamWriteTimeout;
                _inner = inner ?? throw new ArgumentNullException(nameof(inner));
            }

            public override void Flush() => _inner.Flush();

            public override long Seek(long offset, SeekOrigin origin) => _inner.Seek(offset, origin);

            public override void SetLength(long value) => _inner.SetLength(value);

            public override int Read(byte[] buffer, int offset, int count) => _inner.Read(buffer, offset, count);

            public override void Write(byte[] buffer, int offset, int count)
            {
                Thread.Sleep(_streamWriteTimeout);

                _inner.Write(buffer, offset, count);
            }

            public override bool CanRead => _inner.CanRead;

            public override bool CanSeek => _inner.CanSeek;

            public override bool CanWrite => _inner.CanWrite;

            public override long Length => _inner.Length;

            public override long Position
            {
                get => _inner.Position;
                set => _inner.Position = value;
            }

            public override Task CopyToAsync(Stream destination, int bufferSize, CancellationToken cancellationToken)
            {
                return _inner.CopyToAsync(destination, bufferSize, cancellationToken);
            }

            public override void Close()
            {
                _inner.Close();
            }

            protected override void Dispose(bool disposing)
            {
                _inner.Dispose();
                base.Dispose(disposing);
            }

            public override Task FlushAsync(CancellationToken cancellationToken)
            {
                return _inner.FlushAsync(cancellationToken);
            }

            public override IAsyncResult BeginRead(byte[] buffer, int offset, int count, AsyncCallback callback, object state)
            {
                return _inner.BeginRead(buffer, offset, count, callback, state);
            }

            public override int EndRead(IAsyncResult asyncResult)
            {
                return _inner.EndRead(asyncResult);
            }

            public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
            {
                return _inner.ReadAsync(buffer, offset, count, cancellationToken);
            }

            public override IAsyncResult BeginWrite(byte[] buffer, int offset, int count, AsyncCallback callback, object state)
            {
                return _inner.BeginWrite(buffer, offset, count, callback, state);
            }

            public override void EndWrite(IAsyncResult asyncResult)
            {
                _inner.EndWrite(asyncResult);
            }

            public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
            {
                Thread.Sleep(_streamWriteTimeout);

                return _inner.WriteAsync(buffer, offset, count, cancellationToken);
            }

            public override int ReadByte()
            {
                return _inner.ReadByte();
            }

            public override void WriteByte(byte value)
            {
                _inner.WriteByte(value);
            }

            public override bool CanTimeout => _inner.CanTimeout;
            public override int ReadTimeout => _inner.ReadTimeout;

            public override int WriteTimeout
            {
                get => _inner.WriteTimeout;
                set => _inner.WriteTimeout = value;
            }

            public override object InitializeLifetimeService()
            {
                return _inner.InitializeLifetimeService();
            }


            public override string ToString()
            {
                return _inner.ToString();
            }

            public override bool Equals(object obj)
            {
                return _inner.Equals(obj);
            }

            public override int GetHashCode()
            {
                return _inner.GetHashCode();
            }
        }

        private class InMemoryDicomCStoreProvider : DicomService, IDicomServiceProvider, IDicomCStoreProvider
        {
            public InMemoryDicomCStoreProvider(INetworkStream stream, Encoding fallbackEncoding, Logger log,
                ILogManager logManager, INetworkManager networkManager,
                ITranscoderManager transcoderManager) : base(stream, fallbackEncoding, log,
                logManager, networkManager, transcoderManager)
            {
            }

            public void OnReceiveAbort(DicomAbortSource source, DicomAbortReason reason)
            {
            }

            public void OnConnectionClosed(Exception exception)
            {
            }

            public Task OnReceiveAssociationRequestAsync(DicomAssociation association)
            {
                foreach (var presentationContext in association.PresentationContexts)
                {
                    foreach (var ts in presentationContext.GetTransferSyntaxes())
                    {
                        presentationContext.SetResult(DicomPresentationContextResult.Accept, ts);
                        break;
                    }
                }

                return SendAssociationAcceptAsync(association);
            }

            public Task OnReceiveAssociationReleaseRequestAsync()
            {
                return SendAssociationReleaseResponseAsync();
            }

            public Task<DicomCStoreResponse> OnCStoreRequestAsync(DicomCStoreRequest request)
                => Task.FromResult(new DicomCStoreResponse(request, DicomStatus.Success));

            public Task OnCStoreRequestExceptionAsync(string tempFileName, Exception e)
                => Task.CompletedTask;

        }

        private class NeverRespondingDicomServer : DicomService, IDicomServiceProvider, IDicomCFindProvider, IDicomCMoveProvider
        {
            private readonly ConcurrentBag<DicomRequest> _requests;

            public IEnumerable<DicomRequest> Requests => _requests;

            public NeverRespondingDicomServer(INetworkStream stream, Encoding fallbackEncoding,
                Logger log, ILogManager logManager, INetworkManager networkManager,
                ITranscoderManager transcoderManager) : base(stream, fallbackEncoding, log,
                logManager, networkManager, transcoderManager)
            {
                _requests = new ConcurrentBag<DicomRequest>();
            }

            public void OnReceiveAbort(DicomAbortSource source, DicomAbortReason reason)
            {
            }

            public void OnConnectionClosed(Exception exception)
            {
            }

            public Task OnReceiveAssociationRequestAsync(DicomAssociation association)
            {
                foreach (var presentationContext in association.PresentationContexts)
                {
                    foreach (var ts in presentationContext.GetTransferSyntaxes())
                    {
                        presentationContext.SetResult(DicomPresentationContextResult.Accept, ts);
                        break;
                    }
                }

                return SendAssociationAcceptAsync(association);
            }

            public Task OnReceiveAssociationReleaseRequestAsync()
            {
                return SendAssociationReleaseResponseAsync();
            }

#if NETSTANDARD2_1 || NETCOREAPP3_0 || NETCOREAPP3_1
            public async IAsyncEnumerable<DicomCFindResponse> OnCFindRequestAsync(DicomCFindRequest request)
            {
                _requests.Add(request);
                yield break;
            }
#else
            public async Task<IEnumerable<Task<DicomCFindResponse>>> OnCFindRequestAsync(DicomCFindRequest request)
            {
                _requests.Add(request);
                return InternalOnCFindRequestAsync();

                IEnumerable<Task<DicomCFindResponse>> InternalOnCFindRequestAsync()
                {
                    yield break;
                }
            }
#endif

#if NETSTANDARD2_1 || NETCOREAPP3_0 || NETCOREAPP3_1
            public async IAsyncEnumerable<DicomCMoveResponse> OnCMoveRequestAsync(DicomCMoveRequest request)
            {
                _requests.Add(request);
                yield break;
            }
#else
            public async Task<IEnumerable<Task<DicomCMoveResponse>>> OnCMoveRequestAsync(DicomCMoveRequest request)
            {
                _requests.Add(request);
                return Enumerable.Empty<Task<DicomCMoveResponse>>();
            }
#endif
        }

        private class FastPendingResponsesDicomServer : DicomService, IDicomServiceProvider, IDicomCFindProvider, IDicomCMoveProvider
        {
            public FastPendingResponsesDicomServer(INetworkStream stream, Encoding fallbackEncoding, Logger log, ILogManager logManager, INetworkManager networkManager,
                ITranscoderManager transcoderManager) : base(stream, fallbackEncoding, log,
                logManager, networkManager, transcoderManager)
            {
            }

            public void OnReceiveAbort(DicomAbortSource source, DicomAbortReason reason)
            {
            }

            public void OnConnectionClosed(Exception exception)
            {
            }

            public Task OnReceiveAssociationRequestAsync(DicomAssociation association)
            {
                foreach (var presentationContext in association.PresentationContexts)
                {
                    foreach (var ts in presentationContext.GetTransferSyntaxes())
                    {
                        presentationContext.SetResult(DicomPresentationContextResult.Accept, ts);
                        break;
                    }
                }

                return SendAssociationAcceptAsync(association);
            }

            public Task OnReceiveAssociationReleaseRequestAsync()
            {
                return SendAssociationReleaseResponseAsync();
            }

#if NETSTANDARD2_1 || NETCOREAPP3_0 || NETCOREAPP3_1
            public async IAsyncEnumerable<DicomCFindResponse> OnCFindRequestAsync(DicomCFindRequest request)
            {
                await Task.Delay(1000);
                yield return new DicomCFindResponse(request, DicomStatus.Pending);
                await Task.Delay(1000);
                yield return new DicomCFindResponse(request, DicomStatus.Pending);
                await Task.Delay(1000);
                yield return new DicomCFindResponse(request, DicomStatus.Pending);
                await Task.Delay(1000);
                yield return new DicomCFindResponse(request, DicomStatus.Success);
            }
#else
            public async Task<IEnumerable<Task<DicomCFindResponse>>> OnCFindRequestAsync(DicomCFindRequest request)
            {
                return InternalOnCFindRequestAsync();

                IEnumerable<Task<DicomCFindResponse>> InternalOnCFindRequestAsync()
                {
                    Thread.Sleep(1000);
                    yield return Task.FromResult(new DicomCFindResponse(request, DicomStatus.Pending));
                    Thread.Sleep(1000);
                    yield return Task.FromResult(new DicomCFindResponse(request, DicomStatus.Pending));
                    Thread.Sleep(1000);
                    yield return Task.FromResult(new DicomCFindResponse(request, DicomStatus.Pending));
                    Thread.Sleep(1000);
                    yield return Task.FromResult(new DicomCFindResponse(request, DicomStatus.Success));
                }
            }
#endif

#if NETSTANDARD2_1 || NETCOREAPP3_0 || NETCOREAPP3_1
            public async IAsyncEnumerable<DicomCMoveResponse> OnCMoveRequestAsync(DicomCMoveRequest request)
            {
                await Task.Delay(1000);
                yield return new DicomCMoveResponse(request, DicomStatus.Pending);
                await Task.Delay(1000);
                yield return new DicomCMoveResponse(request, DicomStatus.Pending);
                await Task.Delay(1000);
                yield return new DicomCMoveResponse(request, DicomStatus.Pending);
                await Task.Delay(1000);
                yield return new DicomCMoveResponse(request, DicomStatus.Success);
            }
#else
            public async Task<IEnumerable<Task<DicomCMoveResponse>>> OnCMoveRequestAsync(DicomCMoveRequest request)
            {
                return InternalOnCMoveRequestAsync();

                IEnumerable<Task<DicomCMoveResponse>> InternalOnCMoveRequestAsync()
                {
                    Thread.Sleep(1000);
                    yield return Task.FromResult(new DicomCMoveResponse(request, DicomStatus.Pending));
                    Thread.Sleep(1000);
                    yield return Task.FromResult(new DicomCMoveResponse(request, DicomStatus.Pending));
                    Thread.Sleep(1000);
                    yield return Task.FromResult(new DicomCMoveResponse(request, DicomStatus.Pending));
                    Thread.Sleep(1000);
                    yield return Task.FromResult(new DicomCMoveResponse(request, DicomStatus.Success));
                }
            }
#endif
        }

        private class SlowPendingResponsesDicomServer : DicomService, IDicomServiceProvider, IDicomCFindProvider, IDicomCMoveProvider
        {
            public SlowPendingResponsesDicomServer(INetworkStream stream, Encoding fallbackEncoding, Logger log,
                ILogManager logManager, INetworkManager networkManager, ITranscoderManager transcoderManager) : base(
                stream, fallbackEncoding, log, logManager, networkManager, transcoderManager)
            {
            }

            private TimeSpan Delay => (UserState is TimeSpan span) ? span : TimeSpan.FromSeconds(4);

            public void OnReceiveAbort(DicomAbortSource source, DicomAbortReason reason)
            {
            }

            public void OnConnectionClosed(Exception exception)
            {
            }

            public Task OnReceiveAssociationRequestAsync(DicomAssociation association)
            {
                foreach (var presentationContext in association.PresentationContexts)
                {
                    foreach (var ts in presentationContext.GetTransferSyntaxes())
                    {
                        presentationContext.SetResult(DicomPresentationContextResult.Accept, ts);
                        break;
                    }
                }

                return SendAssociationAcceptAsync(association);
            }

            public Task OnReceiveAssociationReleaseRequestAsync()
            {
                return SendAssociationReleaseResponseAsync();
            }

#if NETSTANDARD2_1 || NETCOREAPP3_0 || NETCOREAPP3_1
            public async IAsyncEnumerable<DicomCFindResponse> OnCFindRequestAsync(DicomCFindRequest request)
            {
                yield return new DicomCFindResponse(request, DicomStatus.Pending);
                await Task.Delay(Delay);
                yield return new DicomCFindResponse(request, DicomStatus.Success);
            }
#else
            public async Task<IEnumerable<Task<DicomCFindResponse>>> OnCFindRequestAsync(DicomCFindRequest request)
            {
                return InternalOnCFindRequestAsync();

                IEnumerable<Task<DicomCFindResponse>> InternalOnCFindRequestAsync()
                {
                    yield return Task.FromResult(new DicomCFindResponse(request, DicomStatus.Pending));
                    Thread.Sleep(Delay);
                    yield return Task.FromResult(new DicomCFindResponse(request, DicomStatus.Success));
                }
            }
#endif

#if NETSTANDARD2_1 || NETCOREAPP3_0 || NETCOREAPP3_1
            public async IAsyncEnumerable<DicomCMoveResponse> OnCMoveRequestAsync(DicomCMoveRequest request)
            {
                yield return new DicomCMoveResponse(request, DicomStatus.Pending);
                await Task.Delay(Delay);
                yield return new DicomCMoveResponse(request, DicomStatus.Success);
            }
#else
            public async Task<IEnumerable<Task<DicomCMoveResponse>>> OnCMoveRequestAsync(DicomCMoveRequest request)
            {
                return InternalOnCMoveRequestAsync();

                IEnumerable<Task<DicomCMoveResponse>> InternalOnCMoveRequestAsync()
                {
                    yield return Task.FromResult(new DicomCMoveResponse(request, DicomStatus.Pending));
                    Thread.Sleep(Delay);
                    yield return Task.FromResult(new DicomCMoveResponse(request, DicomStatus.Success));
                }
            }
#endif
        }

        #endregion
    }
}
