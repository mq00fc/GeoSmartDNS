﻿/*
Technitium Library
Copyright (C) 2023  Shreyas Zare (shreyas@technitium.com)

This program is free software: you can redistribute it and/or modify
it under the terms of the GNU General Public License as published by
the Free Software Foundation, either version 3 of the License, or
(at your option) any later version.

This program is distributed in the hope that it will be useful,
but WITHOUT ANY WARRANTY; without even the implied warranty of
MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
GNU General Public License for more details.

You should have received a copy of the GNU General Public License
along with this program.  If not, see <http://www.gnu.org/licenses/>.

*/

using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;

namespace TechnitiumLibrary.Net.Proxy
{
    public enum NetProxyType : byte
    {
        None = 0,
        Http = 1,
        Socks5 = 2
    }

    public abstract class NetProxy : IWebProxy, IProxyServerConnectionManager
    {
        #region variables

        internal readonly static NetProxy NONE = new NoProxy();

        readonly NetProxyType _type;

        protected readonly EndPoint _proxyEP;
        protected readonly NetworkCredential _credential;

        protected NetProxy _viaProxy;
        IReadOnlyCollection<NetProxyBypassItem> _bypassList = new List<NetProxyBypassItem> { new NetProxyBypassItem("127.0.0.0/8"), new NetProxyBypassItem("169.254.0.0/16"), new NetProxyBypassItem("fe80::/10"), new NetProxyBypassItem("::1"), new NetProxyBypassItem("localhost") };

        HttpProxyServer _httpProxyServer;

        #endregion

        #region constructor

        protected NetProxy(NetProxyType type, EndPoint proxyEP, NetworkCredential credential = null)
        {
            _type = type;
            _proxyEP = proxyEP;
            _credential = credential;
        }

        #endregion

        #region static

        public static NetProxy CreateHttpProxy(string address, int port = 8080, NetworkCredential credential = null)
        {
            return new HttpProxy(EndPointExtensions.GetEndPoint(address, port), credential);
        }

        public static NetProxy CreateHttpProxy(EndPoint proxyEP, NetworkCredential credential = null)
        {
            return new HttpProxy(proxyEP, credential);
        }

        public static NetProxy CreateSystemHttpProxy()
        {
            IWebProxy proxy = WebRequest.DefaultWebProxy;
            if (proxy == null)
                return null; //no proxy configured

            Uri testUri = new Uri("https://www.google.com/");

            if (proxy.IsBypassed(testUri))
                return null; //no proxy configured

            Uri proxyAddress = proxy.GetProxy(testUri);
            if (proxyAddress.Equals(testUri))
                return null; //no proxy configured

            return new HttpProxy(EndPointExtensions.GetEndPoint(proxyAddress.Host, proxyAddress.Port), proxy.Credentials.GetCredential(proxyAddress, "BASIC"));
        }

        public static NetProxy CreateSocksProxy(string address, int port = 1080, NetworkCredential credential = null)
        {
            return new SocksProxy(EndPointExtensions.GetEndPoint(address, port), credential);
        }

        public static NetProxy CreateSocksProxy(EndPoint proxyEP, NetworkCredential credential = null)
        {
            return new SocksProxy(proxyEP, credential);
        }

        public static NetProxy CreateProxy(NetProxyType type, string address, int port, string username, string password)
        {
            return CreateProxy(type, address, port, string.IsNullOrEmpty(username) ? null : new NetworkCredential(username, password));
        }

        public static NetProxy CreateProxy(NetProxyType type, string address, int port, NetworkCredential credential = null)
        {
            switch (type)
            {
                case NetProxyType.Http:
                    return new HttpProxy(EndPointExtensions.GetEndPoint(address, port), credential);

                case NetProxyType.Socks5:
                    return new SocksProxy(EndPointExtensions.GetEndPoint(address, port), credential);

                default:
                    throw new NotSupportedException("Proxy type not supported.");
            }
        }

        public static NetProxy CreateProxy(NetProxyType type, EndPoint proxyEP, NetworkCredential credential = null)
        {
            switch (type)
            {
                case NetProxyType.Http:
                    return new HttpProxy(proxyEP, credential);

                case NetProxyType.Socks5:
                    return new SocksProxy(proxyEP, credential);

                default:
                    throw new NotSupportedException("Proxy type not supported.");
            }
        }

        #endregion

        #region protected

        protected static async Task<Socket> GetTcpConnectionAsync(EndPoint ep, CancellationToken cancellationToken)
        {
            if (ep.AddressFamily == AddressFamily.Unspecified)
                ep = await ep.GetIPEndPointAsync();

            Socket socket = new Socket(ep.AddressFamily, SocketType.Stream, ProtocolType.Tcp);

            await socket.ConnectAsync(ep, cancellationToken);

            socket.NoDelay = true;

            return socket;
        }

        protected abstract Task<Socket> ConnectAsync(EndPoint remoteEP, Socket viaSocket);

        #endregion

        #region public

        public virtual Uri GetProxy(Uri destination)
        {
            if (IsBypassed(destination))
                return destination;

            if (_httpProxyServer == null)
                _httpProxyServer = new HttpProxyServer(this);

            return new Uri("http://" + _httpProxyServer.LocalEndPoint.ToString());
        }

        public bool IsBypassed(Uri host)
        {
            return IsBypassed(EndPointExtensions.GetEndPoint(host.Host, host.Port));
        }

        public bool IsBypassed(EndPoint ep)
        {
            foreach (NetProxyBypassItem bypassItem in _bypassList)
            {
                if (bypassItem.IsMatching(ep))
                    return true;
            }

            return false;
        }

        public async Task<bool> IsProxyAccessibleAsync(bool throwException = false, int timeout = 10000, CancellationToken cancellationToken = default)
        {
            try
            {
                using (Socket socket = await GetTcpConnectionAsync(_proxyEP, cancellationToken).WithTimeout(timeout))
                { }

                return true;
            }
            catch
            {
                if (throwException)
                    throw;

                return false;
            }
        }

        public virtual Task<bool> IsUdpAvailableAsync()
        {
            return Task.FromResult(false);
        }

        public async Task<Socket> ConnectAsync(string address, int port, CancellationToken cancellationToken = default)
        {
            return await ConnectAsync(EndPointExtensions.GetEndPoint(address, port), cancellationToken);
        }

        public async Task<Socket> ConnectAsync(EndPoint remoteEP, CancellationToken cancellationToken = default)
        {
            if (IsBypassed(remoteEP))
                return await GetTcpConnectionAsync(remoteEP, cancellationToken);

            if (_viaProxy == null)
                return await ConnectAsync(remoteEP, await GetTcpConnectionAsync(_proxyEP, cancellationToken));
            else
                return await ConnectAsync(remoteEP, await _viaProxy.ConnectAsync(_proxyEP, cancellationToken));
        }

        public virtual Task<IProxyServerBindHandler> GetBindHandlerAsync(AddressFamily family)
        {
            throw new NotSupportedException("Bind feature is not supported by the proxy protocol.");
        }

        public virtual Task<IProxyServerUdpAssociateHandler> GetUdpAssociateHandlerAsync(EndPoint localEP)
        {
            throw new NotSupportedException("UDP transport is not supported by the proxy protocol.");
        }

        public virtual Task<UdpTunnelProxy> CreateUdpTunnelProxyAsync(EndPoint remoteEP, CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException("UDP transport is not supported by the proxy protocol.");
        }

        public Task<TunnelProxy> CreateTunnelProxyAsync(string address, int port, bool enableSsl = false, bool ignoreCertificateErrors = false, CancellationToken cancellationToken = default)
        {
            return CreateTunnelProxyAsync(EndPointExtensions.GetEndPoint(address, port), enableSsl, ignoreCertificateErrors, cancellationToken);
        }

        public async Task<TunnelProxy> CreateTunnelProxyAsync(EndPoint remoteEP, bool enableSsl = false, bool ignoreCertificateErrors = false, CancellationToken cancellationToken = default)
        {
            return new TunnelProxy(await ConnectAsync(remoteEP, cancellationToken), remoteEP, enableSsl, ignoreCertificateErrors);
        }

        public virtual Task<int> UdpQueryAsync(ArraySegment<byte> request, ArraySegment<byte> response, EndPoint remoteEP, int timeout = 10000, int retries = 1, bool expBackoffTimeout = false, Func<int, bool> isResponseValid = null, CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException("UDP transport is not supported by the proxy protocol.");
        }

        #endregion

        #region properties

        public NetProxyType Type
        { get { return _type; } }

        public EndPoint ProxyEndPoint
        { get { return _proxyEP; } }

        public string Address
        { get { return _proxyEP.GetAddress(); } }

        public int Port
        { get { return _proxyEP.GetPort(); } }

        public NetworkCredential Credential
        { get { return _credential; } }

        ICredentials IWebProxy.Credentials
        {
            get { return _credential; }
            set { throw new NotImplementedException(); }
        }

        public NetProxy ViaProxy
        {
            get { return _viaProxy; }
            set { _viaProxy = value; }
        }

        public IReadOnlyCollection<NetProxyBypassItem> BypassList
        {
            get { return _bypassList; }
            set
            {
                if (value is null)
                    _bypassList = Array.Empty<NetProxyBypassItem>();
                else
                    _bypassList = value;
            }
        }

        #endregion

        class NoProxy : NetProxy
        {
            public NoProxy()
                : base(NetProxyType.None, null, null)
            { }

            protected override Task<Socket> ConnectAsync(EndPoint remoteEP, Socket viaSocket)
            {
                throw new NotImplementedException();
            }
        }
    }
}
