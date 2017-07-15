﻿using Microsoft.AspNetCore.Hosting.Server;
using System.Linq;
using System;
using Microsoft.AspNetCore.Http.Features;
using System.Net.Sockets;
using System.Net;
using System.Threading.Tasks;
using System.Collections.Generic;
using System.Threading;
using System.Collections.Concurrent;
using System.Text;
using System.Buffers;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using System.IO;
using Microsoft.AspNetCore.Hosting.Internal;
using TCPServer.ServerImplemetation;

namespace TCPServer
{
	class Server : IServer
	{
		public Server(IServiceProvider serviceProvider, ServerOptions options)
		{
			if(serviceProvider == null)
				throw new ArgumentNullException(nameof(serviceProvider));
			if(options == null)
				throw new ArgumentNullException(nameof(options));
			_Options = options;

			var serverAddressesFeature = new ServerAddressesFeature();
			serverAddressesFeature.Addresses.Add(new UriBuilder("http", options.EndPoint.Address.ToString(), options.EndPoint.Port).Uri.AbsoluteUri);

			Features.Set<IHttpRequestFeature>(new HttpRequestFeature());
			Features.Set<IHttpResponseFeature>(new HttpResponseFeature());
			Features.Set<IServerAddressesFeature>(serverAddressesFeature);
			Features.Set<IServiceProvidersFeature>(new ServiceProvidersFeature() { RequestServices = serviceProvider });
		}


		private readonly ServerOptions _Options;
		public ServerOptions Options
		{
			get
			{
				return _Options;
			}
		}

		public IFeatureCollection Features { get; } = new FeatureCollection();

		CancellationTokenSource _Stopped = new CancellationTokenSource();

		public void Dispose()
		{
			if(!_Stopped.IsCancellationRequested)
			{
				_Stopped.Cancel();
				_AcceptLoopStopped.Wait();
				if(_ListeningSocket != null)
					_ListeningSocket.AsSafeDisposable().Dispose();
				_ListeningEndPoint = null;
			}
		}


		IPEndPoint _ListeningEndPoint;
		public IPEndPoint ListeningEndPoint
		{
			get
			{
				return _ListeningEndPoint;
			}
		}
		Socket _ListeningSocket;

		object l = new object();
		public void Start<TContext>(IHttpApplication<TContext> application)
		{
			lock(l)
			{
				if(_ListeningSocket != null)
					throw new InvalidOperationException("The server is already started");
				var socket = Options.CreateSocket();
				_ListeningSocket = socket;
				_ListeningEndPoint = (IPEndPoint)_ListeningSocket.LocalEndPoint;
				var unused = StartAsync(socket, application);
			}
		}


		private async Task StartAsync<TContext>(Socket socket, IHttpApplication<TContext> application)
		{
			try
			{
				while(true)
				{
					var client = await socket.AcceptAsync(_Stopped.Token).ConfigureAwait(false);
					_Clients.TryAdd(client, client);
					ListenClient(client, application);
					_Stopped.Token.ThrowIfCancellationRequested();
				}
			}
			catch(OperationCanceledException)
			{
				if(!_Stopped.IsCancellationRequested)
					throw;
			}
			finally
			{
				_AcceptLoopStopped.Set();
			}
		}

		private async Task ListenClient<TContext>(Socket client, IHttpApplication<TContext> application)
		{
			try
			{
				var networkStream = new NetworkStream(client, false);
				while(true)
				{
					using(var stream = new TCPStream(networkStream)
					{
						ArrayPool = Options.ArrayPool,
						Cancellation = _Stopped.Token,
						MaxArrayLength = Options.MaxBytesArrayLength
					})
					{
						var request = await TCPRequest.Parse(stream).ConfigureAwait(false);

						var context = (HostingApplication.Context)(object)application.CreateContext(Features);

						context.HttpContext = new TCPContext(request, Features, new TCPConnectionInfo(client, _ListeningEndPoint));
						await application.ProcessRequestAsync((TContext)(object)context);

						var response = (TCPResponse)context.HttpContext.Response;
						try
						{
							response.OnStarting();
							await stream.WriteVarIntAsync((ulong)response.StatusCode).ConfigureAwait(false);

							if(Options.IncludeHeaders)
							{
								await stream.WriteVarIntAsync((ulong)response.Headers.Count).ConfigureAwait(false);
								foreach(var header in response.Headers)
								{
									await stream.WriteStringAsync(header.Key).ConfigureAwait(false);
									await stream.WriteStringAsync(header.Value).ConfigureAwait(false);
								}
							}
							
							await stream.WriteVarIntAsync((ulong)response.Body.Length);
							response.Body.Position = 0;
							await response.Body.CopyToAsync(networkStream).ConfigureAwait(false);
							await networkStream.FlushAsync().ConfigureAwait(false);
						}
						finally
						{
							response.OnCompleted();
						}
					}
				}

			}
			catch(OperationCanceledException)
			{
				if(!_Stopped.IsCancellationRequested)
					throw;
			}
			finally
			{
				DisconnectClient(client);
			}
		}

		private void DisconnectClient(Socket client)
		{
			client.AsSafeDisposable().Dispose();
			Socket unused;
			_Clients.TryRemove(client, out unused);
		}

		ManualResetEventSlim _AcceptLoopStopped = new ManualResetEventSlim(false);
		ConcurrentDictionary<Socket, Socket> _Clients = new ConcurrentDictionary<Socket, Socket>();



	}
}