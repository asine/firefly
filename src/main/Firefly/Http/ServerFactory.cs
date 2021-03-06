﻿using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using Firefly.Utils;

// ReSharper disable AccessToModifiedClosure

namespace Firefly.Http
{
    using AppDelegate = Func<IDictionary<string, object>, Task>;

    public class ServerFactory 
    {
        private readonly IFireflyService _services;

        public ServerFactory()
            : this(new FireflyService())
        {
        }

        public ServerFactory(IServerTrace trace)
            : this(new FireflyService {Trace = trace})
        {
        }

        public ServerFactory(IFireflyService services)
        {
            _services = services;
        }

        public IDisposable Create(AppDelegate app, int port)
        {
            return Create(app, new IPEndPoint(IPAddress.Any, port));
        }

        public IDisposable Create(AppDelegate app, int port, string hostname)
        {
            var ipAddress = Dns.GetHostAddresses(hostname).First();
            return Create(app, new IPEndPoint(ipAddress, port));
        }

        public IDisposable Create(AppDelegate app, EndPoint endpoint)
        {
            _services.Trace.Event(TraceEventType.Start, TraceMessage.ServerFactory);

            var listenSocket = new Socket(endpoint.AddressFamily, SocketType.Stream, ProtocolType.IP);
            listenSocket.Bind(endpoint);
            listenSocket.Listen(-1);

            WaitCallback connectionExecute = connection =>
            {
                _services.Trace.Event(TraceEventType.Verbose, TraceMessage.ServerFactoryConnectionExecute);
                ((Connection)connection).Execute();
            };

            var stop = false;
            var acceptEvent = new SocketAsyncEventArgs();
            Action accept = () =>
            {
                while (!stop)
                {
                    _services.Trace.Event(TraceEventType.Verbose, TraceMessage.ServerFactoryAcceptAsync);

                    if (listenSocket.AcceptAsync(acceptEvent))
                    {
                        return;
                    }

                    _services.Trace.Event(TraceEventType.Verbose, TraceMessage.ServerFactoryAcceptCompletedSync);

                    if (acceptEvent.SocketError != SocketError.Success)
                    {
                        _services.Trace.Event(TraceEventType.Error, TraceMessage.ServerFactoryAcceptSocketError);
                    }

                    if (acceptEvent.SocketError == SocketError.Success &&
                        acceptEvent.AcceptSocket != null)
                    {
                        ThreadPool.QueueUserWorkItem(
                            connectionExecute,
                            new Connection(
                                _services,
                                app,
                                new SocketWrapper(acceptEvent.AcceptSocket),
                                OnDisconnect));
                    }
                    acceptEvent.AcceptSocket = null;
                }
            };
            acceptEvent.Completed += (_, __) =>
            {
                _services.Trace.Event(TraceEventType.Verbose, TraceMessage.ServerFactoryAcceptCompletedAsync);

                if (acceptEvent.SocketError == SocketError.Success &&
                    acceptEvent.AcceptSocket != null)
                {
                    ThreadPool.QueueUserWorkItem(
                        connectionExecute,
                        new Connection(
                            _services,
                            app,
                            new SocketWrapper(acceptEvent.AcceptSocket),
                            OnDisconnect));
                }
                acceptEvent.AcceptSocket = null;
                accept();
            };
            accept();

            return new Disposable(
                () =>
                {
                    _services.Trace.Event(TraceEventType.Stop, TraceMessage.ServerFactory);

                    stop = true;
                    listenSocket.Close();
                    acceptEvent.Dispose();
                });
        }

        private static void OnDisconnect(ISocket obj)
        {
            obj.Close();
        }
    }
}
