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
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;

namespace TechnitiumLibrary.Net.Proxy
{
    public interface IProxyServerConnectionManager
    {
        Task<Socket> ConnectAsync(EndPoint remoteEP, CancellationToken cancellationToken = default);

        Task<IProxyServerBindHandler> GetBindHandlerAsync(AddressFamily family);

        Task<IProxyServerUdpAssociateHandler> GetUdpAssociateHandlerAsync(EndPoint localEP);
    }

    public interface IProxyServerExtendedConnectionManager : IProxyServerConnectionManager
    {
        Task<Socket> ConnectAsync(EndPoint remoteEP, string username, CancellationToken cancellationToken = default);
    }

    public interface IProxyServerBindHandler : IDisposable
    {
        Task<Socket> AcceptAsync();

        SocksProxyReplyCode ReplyCode { get; }

        EndPoint ProxyRemoteEndPoint { get; }

        EndPoint ProxyLocalEndPoint { get; }
    }

    public interface IProxyServerUdpAssociateHandler : IDisposable
    {
        Task<int> SendToAsync(ArraySegment<byte> buffer, EndPoint remoteEP, CancellationToken cancellationToken = default);

        Task<SocketReceiveFromResult> ReceiveFromAsync(ArraySegment<byte> buffer, CancellationToken cancellationToken = default);
    }
}
