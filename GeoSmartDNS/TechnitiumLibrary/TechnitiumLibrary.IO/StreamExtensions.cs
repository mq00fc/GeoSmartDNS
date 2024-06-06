﻿/*
Technitium Library
Copyright (C) 2024  Shreyas Zare (shreyas@technitium.com)

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
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace TechnitiumLibrary.IO
{
    public static class StreamExtensions
    {
        public static byte ReadByteValue(this Stream s)
        {
            Span<byte> buffer = stackalloc byte[1];

            if (s.Read(buffer) < 1)
                throw new EndOfStreamException();

            return buffer[0];
        }

        public static async Task<byte> ReadByteValueAsync(this Stream s, CancellationToken cancellationToken = default)
        {
            byte[] buffer = new byte[1];

            if ((await s.ReadAsync(buffer, cancellationToken)) < 1)
                throw new EndOfStreamException();

            return buffer[0];
        }

        public static byte[] ReadExactly(this Stream s, int count)
        {
            byte[] buffer = new byte[count];
            s.ReadExactly(buffer, 0, count);

            return buffer;
        }

        public static async Task<byte[]> ReadExactlyAsync(this Stream s, int count, CancellationToken cancellationToken = default)
        {
            byte[] buffer = new byte[count];
            await s.ReadExactlyAsync(buffer, 0, count, cancellationToken);

            return buffer;
        }

        public static string ReadShortString(this Stream s)
        {
            return ReadShortString(s, Encoding.UTF8);
        }

        public static string ReadShortString(this Stream s, Encoding encoding)
        {
            return encoding.GetString(s.ReadExactly(s.ReadByteValue()));
        }

        public static void WriteShortString(this Stream s, string value)
        {
            WriteShortString(s, value, Encoding.UTF8);
        }

        public static void WriteShortString(this Stream s, string value, Encoding encoding)
        {
            byte[] buffer = encoding.GetBytes(value);
            if (buffer.Length > 255)
                throw new ArgumentOutOfRangeException(nameof(value), "Parameter 'value' exceeded max length of 255 bytes.");

            s.WriteByte(Convert.ToByte(buffer.Length));
            s.Write(buffer);
        }

        public static void CopyTo(this Stream s, Stream destination, int bufferSize, int length)
        {
            if (length < 1)
                return;

            if (length < bufferSize)
                bufferSize = length;

            byte[] buffer = new byte[bufferSize];
            int bytesRead;

            while (length > 0)
            {
                if (length < bufferSize)
                    bufferSize = length;

                bytesRead = s.Read(buffer, 0, bufferSize);
                if (bytesRead < 1)
                    throw new EndOfStreamException();

                destination.Write(buffer, 0, bytesRead);
                length -= bytesRead;
            }
        }

        public static async Task CopyToAsync(this Stream s, Stream destination, int bufferSize, int length, CancellationToken cancellationToken = default)
        {
            if (length < 1)
                return;

            if (length < bufferSize)
                bufferSize = length;

            byte[] buffer = new byte[bufferSize];
            int bytesRead;

            while (length > 0)
            {
                if (length < bufferSize)
                    bufferSize = length;

                bytesRead = await s.ReadAsync(buffer.AsMemory(0, bufferSize), cancellationToken);
                if (bytesRead < 1)
                    throw new EndOfStreamException();

                await destination.WriteAsync(buffer.AsMemory(0, bytesRead), cancellationToken);
                length -= bytesRead;
            }
        }
    }
}
