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
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using TechnitiumLibrary.IO;
using TechnitiumLibrary.Net.Dns.EDnsOptions;
using TechnitiumLibrary.Net.Dns.ResourceRecords;

namespace TechnitiumLibrary.Net.Dns
{
    public enum DnsOpcode : byte
    {
        StandardQuery = 0,
        InverseQuery = 1,
        ServerStatusRequest = 2,
        Notify = 4,
        Update = 5,
        DnsStatefulOperations = 6
    }

    public enum DnsResponseCode : ushort
    {
        NoError = 0,
        FormatError = 1,
        ServerFailure = 2,
        NxDomain = 3,
        NotImplemented = 4,
        Refused = 5,
        YXDomain = 6,
        YXRRSet = 7,
        NXRRSet = 8,
        NotAuth = 9,
        NotZone = 10,
        BADVERS = 16,
        BADCOOKIE = 23
    }

    public sealed class DnsDatagram
    {
        #region variables

        public const ushort EDNS_DEFAULT_UDP_PAYLOAD_SIZE = 1232;
        public const ushort EDNS_MAX_UDP_PAYLOAD_SIZE = 4096;

        const int MAX_XFR_RESPONSE_SIZE = 16384; //since the compressed name pointer offset can only address 16384 bytes in datagram

        DnsDatagramMetadata _metadata;
        DnsDatagramEdns _edns;
        List<EDnsExtendedDnsErrorOptionData> _dnsClientExtendedErrors;
        bool _shadowHideECSOption;
        EDnsClientSubnetOptionData _shadowECSOption;

        int _size = -1;
        byte[] _parsedDatagramUnsigned;

        ushort _ID;

        byte _QR;
        DnsOpcode _OPCODE;
        byte _AA;
        byte _TC;
        byte _RD;
        byte _RA;
        byte _Z;
        byte _AD;
        byte _CD;
        DnsResponseCode _RCODE;

        IReadOnlyList<DnsQuestionRecord> _question;
        IReadOnlyList<DnsResourceRecord> _answer;
        IReadOnlyList<DnsResourceRecord> _authority;
        IReadOnlyList<DnsResourceRecord> _additional;

        Exception _parsingException;

        DnsDatagram _nextDatagram; //used for TCP XFR multiple messages

        #endregion

        #region constructor

        private DnsDatagram()
        { }

        public DnsDatagram(ushort ID, bool isResponse, DnsOpcode OPCODE, bool authoritativeAnswer, bool truncation, bool recursionDesired, bool recursionAvailable, bool authenticData, bool checkingDisabled, DnsResponseCode RCODE, IReadOnlyList<DnsQuestionRecord> question, IReadOnlyList<DnsResourceRecord> answer = null, IReadOnlyList<DnsResourceRecord> authority = null, IReadOnlyList<DnsResourceRecord> additional = null, ushort udpPayloadSize = ushort.MinValue, EDnsHeaderFlags ednsFlags = EDnsHeaderFlags.None, IReadOnlyList<EDnsOption> options = null)
        {
            _ID = ID;

            if (isResponse)
                _QR = 1;

            _OPCODE = OPCODE;

            if (authoritativeAnswer)
                _AA = 1;

            if (truncation)
                _TC = 1;

            if (recursionDesired)
                _RD = 1;

            if (recursionAvailable)
                _RA = 1;

            if (authenticData)
                _AD = 1;

            if (checkingDisabled)
                _CD = 1;

            _RCODE = RCODE;

            _question = question;
            _answer = answer;
            _authority = authority;
            _additional = additional;

            if (_question is null)
                _question = Array.Empty<DnsQuestionRecord>();

            if (_answer is null)
                _answer = Array.Empty<DnsResourceRecord>();

            if (_authority is null)
                _authority = Array.Empty<DnsResourceRecord>();

            if (_additional is null)
            {
                if (udpPayloadSize < 512)
                {
                    _additional = Array.Empty<DnsResourceRecord>();
                }
                else
                {
                    _additional = new DnsResourceRecord[] { DnsDatagramEdns.GetOPTFor(udpPayloadSize, RCODE, 0, ednsFlags, options) };
                    _edns = new DnsDatagramEdns(udpPayloadSize, RCODE, 0, ednsFlags, options);
                }
            }
            else if (_additional.Count == 0)
            {
                if (udpPayloadSize >= 512)
                {
                    _additional = new DnsResourceRecord[] { DnsDatagramEdns.GetOPTFor(udpPayloadSize, RCODE, 0, ednsFlags, options) };
                    _edns = new DnsDatagramEdns(udpPayloadSize, RCODE, 0, ednsFlags, options);
                }
            }
            else
            {
                if (udpPayloadSize < 512)
                {
                    _edns = DnsDatagramEdns.ReadOPTFrom(_additional, RCODE);
                }
                else
                {
                    DnsResourceRecord[] newAdditional = new DnsResourceRecord[_additional.Count + 1];

                    for (int i = 0; i < _additional.Count; i++)
                    {
                        DnsResourceRecord record = _additional[i];

                        if (record.Type == DnsResourceRecordType.OPT)
                            throw new InvalidOperationException("DnsDatagram already contains an OPT record.");

                        newAdditional[i] = record;
                    }

                    newAdditional[_additional.Count] = DnsDatagramEdns.GetOPTFor(udpPayloadSize, RCODE, 0, ednsFlags, options);

                    _additional = newAdditional;
                    _edns = new DnsDatagramEdns(udpPayloadSize, RCODE, 0, ednsFlags, options);
                }
            }
        }

        #endregion

        #region static

        public static DnsDatagram ReadFrom(Stream s)
        {
            if (s.Position > 0)
                s = new OffsetStream(s, s.Position, s.Length - s.Position); //for handling datagram compression pointer offsets correctly

            DnsDatagram datagram = new DnsDatagram();

            datagram._ID = ReadUInt16NetworkOrder(s);

            int lB = s.ReadByte();
            if (lB < 0)
                throw new EndOfStreamException();

            datagram._QR = Convert.ToByte((lB & 0x80) >> 7);
            datagram._OPCODE = (DnsOpcode)Convert.ToByte((lB & 0x78) >> 3);
            datagram._AA = Convert.ToByte((lB & 0x4) >> 2);
            datagram._TC = Convert.ToByte((lB & 0x2) >> 1);
            datagram._RD = Convert.ToByte(lB & 0x1);

            int rB = s.ReadByte();
            if (rB < 0)
                throw new EndOfStreamException();

            datagram._RA = Convert.ToByte((rB & 0x80) >> 7);
            datagram._Z = Convert.ToByte((rB & 0x40) >> 6);
            datagram._AD = Convert.ToByte((rB & 0x20) >> 5);
            datagram._CD = Convert.ToByte((rB & 0x10) >> 4);
            datagram._RCODE = (DnsResponseCode)(rB & 0xf);

            ushort QDCOUNT = ReadUInt16NetworkOrder(s);
            ushort ANCOUNT = ReadUInt16NetworkOrder(s);
            ushort NSCOUNT = ReadUInt16NetworkOrder(s);
            ushort ARCOUNT = ReadUInt16NetworkOrder(s);

            try
            {
                if (QDCOUNT == 0)
                {
                    datagram._question = Array.Empty<DnsQuestionRecord>();
                }
                else
                {
                    DnsQuestionRecord[] question = new DnsQuestionRecord[QDCOUNT];

                    for (int i = 0; i < question.Length; i++)
                        question[i] = new DnsQuestionRecord(s);

                    datagram._question = question;
                }

                if (ANCOUNT == 0)
                {
                    datagram._answer = Array.Empty<DnsResourceRecord>();
                }
                else
                {
                    DnsResourceRecord[] answer = new DnsResourceRecord[ANCOUNT];

                    for (int i = 0; i < answer.Length; i++)
                        answer[i] = new DnsResourceRecord(s);

                    datagram._answer = answer;
                }

                if (NSCOUNT == 0)
                {
                    datagram._authority = Array.Empty<DnsResourceRecord>();
                }
                else
                {
                    DnsResourceRecord[] authority = new DnsResourceRecord[NSCOUNT];

                    for (int i = 0; i < authority.Length; i++)
                        authority[i] = new DnsResourceRecord(s);

                    datagram._authority = authority;
                }

                if (ARCOUNT == 0)
                {
                    datagram._additional = Array.Empty<DnsResourceRecord>();
                }
                else
                {
                    DnsResourceRecord[] additional = new DnsResourceRecord[ARCOUNT];

                    for (int i = 0; i < additional.Length; i++)
                        additional[i] = new DnsResourceRecord(s);

                    datagram._additional = additional;
                }
            }
            catch (Exception ex)
            {
                datagram._parsingException = ex;

                if (datagram._question is null)
                    datagram._question = Array.Empty<DnsQuestionRecord>();

                if (datagram._answer is null)
                    datagram._answer = Array.Empty<DnsResourceRecord>();

                if (datagram._authority is null)
                    datagram._authority = Array.Empty<DnsResourceRecord>();

                if (datagram._additional is null)
                    datagram._additional = Array.Empty<DnsResourceRecord>();
            }

            datagram._size = Convert.ToInt32(s.Position);

            if (datagram.IsSigned)
            {
                //get unsigned datagram for TSIG verification
                DnsResourceRecord tsigRecord = datagram._additional[datagram._additional.Count - 1];
                byte[] buffer = new byte[tsigRecord.DatagramOffset];

                s.Position = 0;
                s.ReadExactly(buffer, 0, buffer.Length);
                s.Position = datagram._size;

                BinaryPrimitives.WriteUInt16BigEndian(buffer.AsSpan(10, 2), Convert.ToUInt16(ARCOUNT - 1)); //originalARCOUNT
                BinaryPrimitives.WriteUInt16BigEndian(buffer.AsSpan(0, 2), (tsigRecord.RDATA as DnsTSIGRecordData).OriginalID); //originalID

                datagram._parsedDatagramUnsigned = buffer;
            }

            datagram._edns = DnsDatagramEdns.ReadOPTFrom(datagram._additional, datagram._RCODE);

            return datagram;
        }

        public static async Task<DnsDatagram> ReadFromTcpAsync(Stream stream, int bufferSize = 4096, CancellationToken cancellationToken = default)
        {
            using (MemoryStream mS = new MemoryStream(bufferSize))
            {
                return await ReadFromTcpAsync(stream, mS, cancellationToken);
            }
        }

        public static async Task<DnsDatagram> ReadFromTcpAsync(Stream stream, MemoryStream sharedBuffer, CancellationToken cancellationToken = default)
        {
            //read tcp datagram length
            int length = await ReadUInt16NetworkOrderAsync(stream, cancellationToken);

            //read datagram from source to buffer
            sharedBuffer.SetLength(0);
            await stream.CopyToAsync(sharedBuffer, Math.Min(4096, length), length, cancellationToken);
            sharedBuffer.Position = 0;

            //parse datagram from buffer
            return ReadFrom(sharedBuffer);
        }

        internal static async Task<ushort> ReadUInt16NetworkOrderAsync(Stream s, CancellationToken cancellationToken = default)
        {
            byte[] b = await s.ReadExactlyAsync(2, cancellationToken);
            return BinaryPrimitives.ReadUInt16BigEndian(b);
        }

        internal static ushort ReadUInt16NetworkOrder(Stream s)
        {
            Span<byte> b = stackalloc byte[2];
            s.ReadExactly(b);
            return BinaryPrimitives.ReadUInt16BigEndian(b);
        }

        internal static void WriteUInt16NetworkOrder(ushort value, Stream s)
        {
            Span<byte> b = stackalloc byte[2];
            BinaryPrimitives.WriteUInt16BigEndian(b, value);
            s.Write(b);
        }

        internal static uint ReadUInt32NetworkOrder(Stream s)
        {
            Span<byte> b = stackalloc byte[4];
            s.ReadExactly(b);
            return BinaryPrimitives.ReadUInt32BigEndian(b);
        }

        internal static void WriteUInt32NetworkOrder(uint value, Stream s)
        {
            Span<byte> b = stackalloc byte[4];
            BinaryPrimitives.WriteUInt32BigEndian(b, value);
            s.Write(b);
        }

        internal static ulong ReadUInt48NetworkOrder(Stream s)
        {
            Span<byte> b = stackalloc byte[8];
            s.ReadExactly(b.Slice(2, 6));
            return BinaryPrimitives.ReadUInt64BigEndian(b);
        }

        internal static void WriteUInt48NetworkOrder(ulong value, Stream s)
        {
            Span<byte> b = stackalloc byte[8];
            BinaryPrimitives.WriteUInt64BigEndian(b, value);
            s.Write(b.Slice(2, 6));
        }

        internal static byte[] ConvertToUInt48NetworkOrder(ulong value)
        {
            Span<byte> b = stackalloc byte[8];
            BinaryPrimitives.WriteUInt64BigEndian(b, value);

            byte[] t = new byte[6];
            b.Slice(2, 6).CopyTo(t);

            return t;
        }

        public static void SerializeDomainName(ReadOnlySpan<char> domain, Stream s, List<DnsDomainOffset> domainEntries = null, bool isEmailAddress = false)
        {
            Span<byte> labelBytes = stackalloc byte[63];
            int labelBytesLength;

            ReadOnlySpan<char> label;
            int i;

            while (!domain.IsEmpty)
            {
                if (domainEntries is not null)
                {
                    //search domain list
                    foreach (DnsDomainOffset domainEntry in domainEntries)
                    {
                        if (domain.Equals(domainEntry.Domain.Span, StringComparison.OrdinalIgnoreCase))
                        {
                            //found matching domain offset for compression
                            ushort pointer = 0xC000;
                            pointer |= domainEntry.Offset;

                            Span<byte> pointerBytes = stackalloc byte[2];
                            BinaryPrimitives.WriteUInt16BigEndian(pointerBytes, pointer);

                            //write pointer
                            s.Write(pointerBytes);
                            return;
                        }
                    }

                    domainEntries.Add(new DnsDomainOffset(Convert.ToUInt16(s.Position), domain));
                }

                if (isEmailAddress)
                {
                    i = domain.IndexOf('@');
                    if (i < 0)
                        i = domain.IndexOf('.');

                    isEmailAddress = false;
                }
                else
                {
                    i = domain.IndexOf('.');
                }

                if (i < 0)
                {
                    label = domain;
                    domain = Array.Empty<char>();
                }
                else
                {
                    label = domain.Slice(0, i);
                    domain = domain.Slice(i + 1);
                }

                if (!Encoding.ASCII.TryGetBytes(label, labelBytes, out labelBytesLength))
                    throw new DnsClientException("Invalid domain name: label cannot exceed 63 bytes.");

                s.WriteByte((byte)labelBytesLength);
                s.Write(labelBytes.Slice(0, labelBytesLength));
            }

            s.WriteByte(0);
        }

        public static string DeserializeDomainName(Stream s, int maxDepth = 10, bool ignoreMissingNullTermination = false, bool isEmailAddress = false)
        {
            if (maxDepth < 0)
                throw new DnsClientException("Error while reading domain name: max depth for decompression reached");

            int labelLength = s.ReadByte();
            if (labelLength < 0)
                throw new EndOfStreamException();

            Span<char> domain = stackalloc char[256];
            Span<byte> buffer = stackalloc byte[63];
            int domainPosition = 0;

            while (labelLength > 0)
            {
                if ((labelLength & 0xC0) == 0xC0)
                {
                    int secondByte = s.ReadByte();
                    if (secondByte < 0)
                        throw new EndOfStreamException();

                    int offset = ((labelLength << 8) | secondByte) & 0x3FFF;
                    long currentPosition = s.Position;
                    s.Position = offset;

                    string domainSuffix = DeserializeDomainName(s, maxDepth - 1, ignoreMissingNullTermination, isEmailAddress);
                    if (domainSuffix.Length > 0)
                    {
                        domainSuffix.AsSpan().CopyTo(domain.Slice(domainPosition));
                        domainPosition += domainSuffix.Length;
                        domain[domainPosition++] = '.';
                    }

                    s.Position = currentPosition;
                    break;
                }

                Span<byte> label = buffer.Slice(0, labelLength);
                s.ReadExactly(label);

                if (!Encoding.ASCII.TryGetChars(label, domain.Slice(domainPosition), out _))
                    throw new DnsClientException("Error while reading domain name: length cannot exceed 255 bytes.");

                domainPosition += labelLength;

                if (isEmailAddress)
                {
                    domain[domainPosition++] = '@';
                    isEmailAddress = false;
                }
                else
                {
                    domain[domainPosition++] = '.';
                }

                if (ignoreMissingNullTermination && (s.Length == s.Position))
                    break; //option to ignore for buggy DHCP clients

                labelLength = s.ReadByte();
                if (labelLength < 0)
                    throw new EndOfStreamException();
            }

            if (domainPosition > 0)
                domainPosition--;

            return new string(domain.Slice(0, domainPosition));
        }

        public static int GetSerializeDomainNameLength(string domain)
        {
            if (domain.Length == 0)
                return 1;

            return domain.Length + 2;
        }

        internal static string EncodeCharacterString(string value)
        {
            return "\"" + value.Replace("\"", "\\\"") + "\"";
        }

        internal static string DecodeCharacterString(string value)
        {
            if (value.StartsWith('\"') && value.EndsWith('\"'))
                value = value.Substring(1, value.Length - 2).Replace("\\\"", "\"");

            return value;
        }

        #endregion

        #region internal

        internal DnsDatagram CloneHeadersAndQuestions()
        {
            DnsQuestionRecord[] clonedQuestion = new DnsQuestionRecord[_question.Count];

            for (int i = 0; i < _question.Count; i++)
                clonedQuestion[i] = _question[i].Clone();

            DnsDatagram datagram = new DnsDatagram(_ID, _QR == 1, _OPCODE, _AA == 1, _TC == 1, _RD == 1, _RA == 1, _AD == 1, _CD == 1, _RCODE, clonedQuestion, _answer, _authority, _additional);

            datagram._metadata = _metadata;
            datagram._edns = _edns;
            datagram._dnsClientExtendedErrors = _dnsClientExtendedErrors;
            datagram._shadowHideECSOption = _shadowHideECSOption;
            datagram._shadowECSOption = _shadowECSOption;
            datagram.Tag = Tag;

            return datagram;
        }

        internal void AddDnsClientExtendedError(EDnsExtendedDnsErrorCode errorCode, string extraText = null)
        {
            AddDnsClientExtendedError(new EDnsExtendedDnsErrorOptionData(errorCode, extraText));
        }

        internal void AddDnsClientExtendedError(IReadOnlyCollection<EDnsExtendedDnsErrorOptionData> dnsErrors)
        {
            if (_dnsClientExtendedErrors is null)
                _dnsClientExtendedErrors = new List<EDnsExtendedDnsErrorOptionData>();

            foreach (EDnsExtendedDnsErrorOptionData dnsError in dnsErrors)
            {
                if (!_dnsClientExtendedErrors.Contains(dnsError))
                    _dnsClientExtendedErrors.Add(dnsError);
            }
        }

        internal void AddDnsClientExtendedError(EDnsExtendedDnsErrorOptionData dnsError)
        {
            if (_dnsClientExtendedErrors is null)
                _dnsClientExtendedErrors = new List<EDnsExtendedDnsErrorOptionData>();

            if (!_dnsClientExtendedErrors.Contains(dnsError))
                _dnsClientExtendedErrors.Add(dnsError);
        }

        internal void AddDnsClientExtendedErrorFrom(DnsDatagram datagram)
        {
            //copy errors from OPT
            if (datagram._edns is not null)
            {
                foreach (EDnsOption option in datagram._edns.Options)
                {
                    if (option.Code == EDnsOptionCode.EXTENDED_DNS_ERROR)
                        AddDnsClientExtendedError(option.Data as EDnsExtendedDnsErrorOptionData);
                }
            }

            //copy generated errors
            if (datagram._dnsClientExtendedErrors is not null)
            {
                foreach (EDnsExtendedDnsErrorOptionData dnsError in datagram._dnsClientExtendedErrors)
                    AddDnsClientExtendedError(dnsError);
            }
        }

        internal void SetShadowEDnsClientSubnetOption(EDnsClientSubnetOptionData shadowECSOption)
        {
            _shadowECSOption = shadowECSOption;
        }

        #endregion

        #region public

        public DnsDatagram Clone(IReadOnlyList<DnsResourceRecord> answer = null, IReadOnlyList<DnsResourceRecord> authority = null, IReadOnlyList<DnsResourceRecord> additional = null)
        {
            if (answer is null)
                answer = _answer;

            if (authority is null)
                authority = _authority;

            if (additional is null)
                additional = _additional;

            DnsDatagram datagram = new DnsDatagram(_ID, _QR == 1, _OPCODE, _AA == 1, _TC == 1, _RD == 1, _RA == 1, _AD == 1, _CD == 1, _RCODE, _question, answer, authority, additional);

            datagram._metadata = _metadata;

            if (additional is null)
                datagram._edns = _edns;
            else if (additional.Count == 0)
                datagram._edns = null;
            else
                datagram._edns = DnsDatagramEdns.ReadOPTFrom(additional, _RCODE);

            datagram._dnsClientExtendedErrors = _dnsClientExtendedErrors;
            datagram._shadowHideECSOption = _shadowHideECSOption;
            datagram._shadowECSOption = _shadowECSOption;

            datagram._nextDatagram = _nextDatagram;

            datagram.Tag = Tag;

            return datagram;
        }

        public DnsDatagram CloneWithoutEDns()
        {
            if (_edns is null)
                return this;

            IReadOnlyList<DnsResourceRecord> newAdditional;

            if (_additional.Count == 1)
            {
                newAdditional = Array.Empty<DnsResourceRecord>();
            }
            else
            {
                List<DnsResourceRecord> newAdditionalList = new List<DnsResourceRecord>(_additional.Count - 1);

                foreach (DnsResourceRecord record in _additional)
                {
                    if (record.Type == DnsResourceRecordType.OPT)
                        continue;

                    newAdditionalList.Add(record);
                }

                newAdditional = newAdditionalList;
            }

            return Clone(null, null, newAdditional);
        }

        public DnsDatagram CloneWithoutEDnsClientSubnet()
        {
            if ((_edns is null) || (_edns.Options.Count == 0))
                return this;

            List<DnsResourceRecord> newAdditional = new List<DnsResourceRecord>(_additional.Count);

            foreach (DnsResourceRecord record in _additional)
            {
                if (record.Type != DnsResourceRecordType.OPT)
                    newAdditional.Add(record);
            }

            IReadOnlyList<EDnsOption> options;

            if ((_edns.Options.Count == 1) && (_edns.Options[0].Code == EDnsOptionCode.EDNS_CLIENT_SUBNET))
            {
                options = Array.Empty<EDnsOption>();
            }
            else
            {
                List<EDnsOption> newOptions = new List<EDnsOption>(_edns.Options.Count);

                foreach (EDnsOption option in _edns.Options)
                {
                    if (option.Code != EDnsOptionCode.EDNS_CLIENT_SUBNET)
                        newOptions.Add(option);
                }

                options = newOptions;
            }

            newAdditional.Add(DnsDatagramEdns.GetOPTFor(_edns.UdpPayloadSize, _edns.ExtendedRCODE, _edns.Version, _edns.Flags, options));

            return Clone(null, null, newAdditional);
        }

        public DnsDatagram CloneWithoutGlueRecords()
        {
            if (_additional.Count == 0)
                return this;

            IReadOnlyList<DnsResourceRecord> newAdditional = null;

            for (int i = _additional.Count - 1; i > -1; i--)
            {
                DnsResourceRecord record = _additional[i];

                if (record.Type == DnsResourceRecordType.OPT)
                {
                    newAdditional = new DnsResourceRecord[] { record };
                    break;
                }
            }

            return Clone(null, null, newAdditional);
        }

        public EDnsClientSubnetOptionData GetEDnsClientSubnetOption(bool noShadow = false)
        {
            if (!noShadow)
            {
                if (_shadowHideECSOption)
                    return null;

                if (_shadowECSOption is not null)
                    return _shadowECSOption;
            }

            if (_edns is null)
                return null;

            foreach (EDnsOption eDnsOption in _edns.Options)
            {
                if (eDnsOption.Code == EDnsOptionCode.EDNS_CLIENT_SUBNET)
                    return eDnsOption.Data as EDnsClientSubnetOptionData;
            }

            return null;
        }

        public void SetShadowEDnsClientSubnetOption(NetworkAddress eDnsClientSubnet, bool advancedForwardingClientSubnet = false)
        {
            if (advancedForwardingClientSubnet)
                _shadowECSOption = new EDnsClientSubnetOptionData(eDnsClientSubnet.PrefixLength, eDnsClientSubnet.PrefixLength, eDnsClientSubnet.Address) { AdvancedForwardingClientSubnet = true };
            else
                _shadowECSOption = new EDnsClientSubnetOptionData(eDnsClientSubnet.PrefixLength, 0, eDnsClientSubnet.Address);
        }

        public void ShadowHideEDnsClientSubnetOption()
        {
            _shadowHideECSOption = true;
        }

        public bool IsBlockedResponse()
        {
            if ((_RD == 0) || (_AA == 1))
                return false;

            if (_edns is not null)
            {
                foreach (EDnsOption option in _edns.Options)
                {
                    if ((option.Code == EDnsOptionCode.EXTENDED_DNS_ERROR) && (option.Data is EDnsExtendedDnsErrorOptionData ede))
                    {
                        switch (ede.InfoCode)
                        {
                            case EDnsExtendedDnsErrorCode.Blocked:
                            case EDnsExtendedDnsErrorCode.Censored:
                            case EDnsExtendedDnsErrorCode.Filtered:
                            case EDnsExtendedDnsErrorCode.Prohibited:
                                return true;
                        }
                    }
                }
            }

            if (_dnsClientExtendedErrors is not null)
            {
                foreach (EDnsExtendedDnsErrorOptionData ede in _dnsClientExtendedErrors)
                {
                    if (ede.InfoCode == EDnsExtendedDnsErrorCode.Blocked)
                        return true;
                }
            }

            if ((_RCODE == DnsResponseCode.NoError) && (_question.Count > 0) && (_answer.Count > 0))
            {
                switch (_question[0].Type)
                {
                    case DnsResourceRecordType.A:
                        {
                            DnsResourceRecord lastRR = _answer[_answer.Count - 1];
                            return (lastRR.Type == DnsResourceRecordType.A) && (lastRR.RDATA is DnsARecordData aRecord) && aRecord.Address.Equals(IPAddress.Any);
                        }

                    case DnsResourceRecordType.AAAA:
                        {
                            DnsResourceRecord lastRR = _answer[_answer.Count - 1];
                            return (lastRR.Type == DnsResourceRecordType.AAAA) && (lastRR.RDATA is DnsAAAARecordData aaaaRecord) && aaaaRecord.Address.Equals(IPAddress.IPv6Any);
                        }
                }
            }

            return false;
        }

        public void SetMetadata(NameServerAddress server, double rtt = 0.0)
        {
            if (_metadata is not null)
                throw new InvalidOperationException("Cannot overwrite existing Metadata.");

            _metadata = new DnsDatagramMetadata(server, _size, rtt);
        }

        public void SetIdentifier(ushort id)
        {
            _ID = id;
        }

        public void SetRandomIdentifier()
        {
            Span<byte> buffer = stackalloc byte[2];
            RandomNumberGenerator.Fill(buffer);

            _ID = BitConverter.ToUInt16(buffer);
        }

        public void SetDnssecStatusForAllRecords(DnssecStatus dnssecStatus)
        {
            foreach (DnsResourceRecord record in _answer)
                record.SetDnssecStatus(dnssecStatus);

            foreach (DnsResourceRecord record in _authority)
                record.SetDnssecStatus(dnssecStatus);

            if (dnssecStatus == DnssecStatus.Disabled)
            {
                foreach (DnsResourceRecord record in _additional)
                    record.SetDnssecStatus(dnssecStatus);
            }
            else
            {
                foreach (DnsResourceRecord record in _additional)
                {
                    if (record.Type == DnsResourceRecordType.OPT)
                        record.SetDnssecStatus(DnssecStatus.Indeterminate);
                    else
                        record.SetDnssecStatus(dnssecStatus);
                }
            }
        }

        public DnsResourceRecord GetLastAnswerRecord()
        {
            if (_question.Count == 0)
                return null;

            DnsQuestionRecord question = _question[0];
            DnsResourceRecord lastAnswer = null;
            string name = question.Name;

            foreach (DnsResourceRecord record in _answer)
            {
                if (!record.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
                    continue;

                if (record.Type == question.Type)
                {
                    lastAnswer = record;
                }
                else if (record.Type == DnsResourceRecordType.CNAME)
                {
                    lastAnswer = record;
                    name = (record.RDATA as DnsCNAMERecordData).Domain;
                }
            }

            if (lastAnswer is null)
                return _answer[_answer.Count - 1];

            return lastAnswer;
        }

        public bool IsFirstAuthoritySOA()
        {
            DnsResourceRecord firstAuthority = FindFirstAuthorityRecord();
            return (firstAuthority is not null) && (firstAuthority.Type == DnsResourceRecordType.SOA);
        }

        public DnsResourceRecordType FindFirstAuthorityType()
        {
            DnsResourceRecord firstAuthority = FindFirstAuthorityRecord();
            if (firstAuthority is null)
                return DnsResourceRecordType.Unknown;

            return firstAuthority.Type;
        }

        public DnsResourceRecord FindFirstAuthorityRecord()
        {
            foreach (DnsResourceRecord record in _authority)
            {
                switch (record.Type)
                {
                    case DnsResourceRecordType.SOA:
                    case DnsResourceRecordType.NS:
                        return record;
                }
            }

            if (_authority.Count > 0)
                return _authority[0];

            return null;
        }

        public void WriteTo(Stream s)
        {
            if (s.Position > 0)
                s = new OffsetStream(s, s.Position); //for handling datagram compression pointer offsets correctly

            WriteUInt16NetworkOrder(_ID, s);
            s.WriteByte(Convert.ToByte((_QR << 7) | ((byte)_OPCODE << 3) | (_AA << 2) | (_TC << 1) | _RD));
            s.WriteByte(Convert.ToByte((_RA << 7) | (_Z << 6) | (_AD << 5) | (_CD << 4) | (byte)((int)_RCODE & 0xf)));
            WriteUInt16NetworkOrder(Convert.ToUInt16(_question.Count), s);
            WriteUInt16NetworkOrder(Convert.ToUInt16(_answer.Count), s);
            WriteUInt16NetworkOrder(Convert.ToUInt16(_authority.Count), s);
            WriteUInt16NetworkOrder(Convert.ToUInt16(_additional.Count), s);

            List<DnsDomainOffset> domainEntries = new List<DnsDomainOffset>(1);

            foreach (DnsQuestionRecord question in _question)
                question.WriteTo(s, domainEntries);

            foreach (DnsResourceRecord answer in _answer)
                answer.WriteTo(s, domainEntries);

            foreach (DnsResourceRecord authority in _authority)
                authority.WriteTo(s, domainEntries);

            foreach (DnsResourceRecord additional in _additional)
                additional.WriteTo(s, domainEntries);
        }

        public async Task WriteToTcpAsync(Stream s, int bufferSize = 4096, CancellationToken cancellationToken = default)
        {
            using (MemoryStream mS = new MemoryStream(bufferSize))
            {
                await WriteToTcpAsync(s, mS, cancellationToken);
            }
        }

        public async Task WriteToTcpAsync(Stream s, MemoryStream sharedBuffer, CancellationToken cancellationToken = default)
        {
            DnsDatagram current = this;
            long datagramLength;

            do
            {
                sharedBuffer.SetLength(0);
                sharedBuffer.Position = 2;
                current.WriteTo(sharedBuffer);

                datagramLength = sharedBuffer.Length - 2L;
                if (datagramLength > ushort.MaxValue)
                {
                    //truncate and write
                    IReadOnlyList<DnsResourceRecord> additional = null;

                    if (_edns is not null)
                        additional = new DnsResourceRecord[] { DnsDatagramEdns.GetOPTFor(_edns.UdpPayloadSize, _edns.ExtendedRCODE, _edns.Version, _edns.Flags, _edns.Options) };

                    DnsDatagram truncted = new DnsDatagram(_ID, _QR == 1, _OPCODE, _AA == 1, true, _RD == 1, _RA == 1, _AD == 1, _CD == 1, _RCODE, _question, Array.Empty<DnsResourceRecord>(), Array.Empty<DnsResourceRecord>(), additional);
                    await truncted.WriteToTcpAsync(s, sharedBuffer, cancellationToken);
                    break;
                }

                sharedBuffer.Position = 0;
                WriteUInt16NetworkOrder(Convert.ToUInt16(datagramLength), sharedBuffer);

                sharedBuffer.Position = 0;
                await sharedBuffer.CopyToAsync(s, Math.Min(4096, (int)sharedBuffer.Length), cancellationToken);

                current = current._nextDatagram;
            }
            while (current is not null);
        }

        public void SerializeTo(Utf8JsonWriter jsonWriter)
        {
            jsonWriter.WriteStartObject();

            if (_metadata is not null)
            {
                jsonWriter.WritePropertyName("Metadata");
                _metadata.SerializeTo(jsonWriter);
            }

            if (_edns is not null)
            {
                jsonWriter.WritePropertyName("EDNS");
                _edns.SerializeTo(jsonWriter);
            }

            if (_dnsClientExtendedErrors is not null)
            {
                jsonWriter.WritePropertyName("DnsClientExtendedErrors");
                jsonWriter.WriteStartArray();

                foreach (EDnsExtendedDnsErrorOptionData ee in _dnsClientExtendedErrors)
                    ee.SerializeTo(jsonWriter);

                jsonWriter.WriteEndArray();
            }

            jsonWriter.WriteNumber("Identifier", _ID);
            jsonWriter.WriteBoolean("IsResponse", IsResponse);
            jsonWriter.WriteString("OPCODE", _OPCODE.ToString());
            jsonWriter.WriteBoolean("AuthoritativeAnswer", AuthoritativeAnswer);
            jsonWriter.WriteBoolean("Truncation", Truncation);
            jsonWriter.WriteBoolean("RecursionDesired", RecursionDesired);
            jsonWriter.WriteBoolean("RecursionAvailable", RecursionAvailable);
            jsonWriter.WriteNumber("Z", _Z);
            jsonWriter.WriteBoolean("AuthenticData", AuthenticData);
            jsonWriter.WriteBoolean("CheckingDisabled", CheckingDisabled);
            jsonWriter.WriteString("RCODE", RCODE.ToString());
            jsonWriter.WriteNumber("QDCOUNT", _question.Count);
            jsonWriter.WriteNumber("ANCOUNT", _answer.Count);
            jsonWriter.WriteNumber("NSCOUNT", _authority.Count);
            jsonWriter.WriteNumber("ARCOUNT", _additional.Count);

            jsonWriter.WritePropertyName("Question");
            jsonWriter.WriteStartArray();

            foreach (DnsQuestionRecord question in _question)
                question.SerializeTo(jsonWriter);

            jsonWriter.WriteEndArray();

            jsonWriter.WritePropertyName("Answer");
            jsonWriter.WriteStartArray();

            foreach (DnsResourceRecord answer in _answer)
                answer.SerializeTo(jsonWriter);

            jsonWriter.WriteEndArray();

            jsonWriter.WritePropertyName("Authority");
            jsonWriter.WriteStartArray();

            foreach (DnsResourceRecord authority in _authority)
                authority.SerializeTo(jsonWriter);

            jsonWriter.WriteEndArray();

            jsonWriter.WritePropertyName("Additional");
            jsonWriter.WriteStartArray();

            foreach (DnsResourceRecord additional in _additional)
                additional.SerializeTo(jsonWriter);

            jsonWriter.WriteEndArray();

            jsonWriter.WriteEndObject();
        }

        public DnsDatagram Split()
        {
            if (!IsResponse)
                throw new InvalidOperationException("Cannot split this datagram: datagram must be a response.");

            if (!IsZoneTransfer)
                throw new InvalidOperationException("Cannot split this datagram: datagram must be for a zone transfer.");

            if (IsSigned)
                throw new InvalidOperationException("Cannot split this datagram: datagram must not be signed.");

            if (_answer.Count == 0)
                return this;

            DnsDatagram first = null;
            DnsDatagram last = null;
            int iQD = 0;
            int iAN = 0;
            int iNS = 0;
            int iAR = 0;

            do
            {
                int datagramSize = 12; //init with header size

                IReadOnlyList<DnsQuestionRecord> question = null;
                IReadOnlyList<DnsResourceRecord> answer = null;
                IReadOnlyList<DnsResourceRecord> authority = null;
                IReadOnlyList<DnsResourceRecord> additional = null;

                if (iQD < _question.Count)
                {
                    datagramSize += _question[0].UncompressedLength;
                    question = _question;
                    iQD = _question.Count;
                }

                if (iAN < _answer.Count)
                {
                    List<DnsResourceRecord> list = new List<DnsResourceRecord>();

                    for (; (iAN < _answer.Count) && (datagramSize < MAX_XFR_RESPONSE_SIZE); iAN++)
                    {
                        DnsResourceRecord record = _answer[iAN];

                        datagramSize += record.UncompressedLength;
                        list.Add(record);
                    }

                    answer = list;
                }

                if ((iNS < _authority.Count) && (datagramSize < MAX_XFR_RESPONSE_SIZE))
                {
                    List<DnsResourceRecord> list = new List<DnsResourceRecord>();

                    for (; (iNS < _authority.Count) && (datagramSize < MAX_XFR_RESPONSE_SIZE); iNS++)
                    {
                        DnsResourceRecord record = _authority[iNS];

                        datagramSize += record.UncompressedLength;
                        list.Add(record);
                    }

                    authority = list;
                }

                if ((iAR < _additional.Count) && (datagramSize < MAX_XFR_RESPONSE_SIZE))
                {
                    List<DnsResourceRecord> list = new List<DnsResourceRecord>();

                    for (; (iAR < _additional.Count) && (datagramSize < MAX_XFR_RESPONSE_SIZE); iAR++)
                    {
                        DnsResourceRecord record = _additional[iAR];

                        datagramSize += record.UncompressedLength;
                        list.Add(record);
                    }

                    _additional = list;
                }

                DnsDatagram datagram = new DnsDatagram(_ID, _QR == 1, _OPCODE, _AA == 1, _TC == 1, _RD == 1, _RA == 1, _AD == 1, _CD == 1, _RCODE, question, answer, authority, additional);

                if (first is null)
                {
                    first = datagram;
                    first._metadata = _metadata;
                    first.Tag = Tag;

                    last = datagram;
                }
                else
                {
                    last._nextDatagram = datagram;
                    last = datagram;
                }
            }
            while ((iAN < _answer.Count) || (iNS < _authority.Count) || (iAR < _additional.Count));

            return first;
        }

        public DnsDatagram Join()
        {
            if (!IsResponse)
                throw new InvalidOperationException("Cannot join this datagram: datagram must be a response.");

            if (!IsZoneTransfer)
                throw new InvalidOperationException("Cannot join this datagram: datagram must be for a zone transfer.");

            if (IsSigned)
                throw new InvalidOperationException("Cannot join this datagram: datagram must not be signed.");

            if (_nextDatagram is null)
                return this;

            List<DnsResourceRecord> answer = new List<DnsResourceRecord>(_answer.Count * 2);
            DnsDatagram current = this;
            int size = 0;

            do
            {
                size += current._size;
                answer.AddRange(current._answer);

                current = current._nextDatagram;
            }
            while (current is not null);

            DnsDatagram joinedDatagram = new DnsDatagram(_ID, _QR == 1, _OPCODE, _AA == 1, _TC == 1, _RD == 1, _RA == 1, _AD == 1, _CD == 1, _RCODE, _question, answer, _authority, _additional);
            joinedDatagram._size = size;
            joinedDatagram.SetMetadata(_metadata.NameServer, _metadata.RoundTripTime);
            joinedDatagram.Tag = Tag;

            return joinedDatagram;
        }

        public DnsDatagram SignRequest(TsigKey key, ushort fudge)
        {
            if (IsResponse)
                throw new InvalidOperationException("Cannot sign this datagram: datagram must be a request.");

            ulong timeSigned = Convert.ToUInt64((DateTime.UtcNow - DateTime.UnixEpoch).TotalSeconds);
            DnsTsigError error = DnsTsigError.NoError;
            byte[] otherData = Array.Empty<byte>();

            byte[] mac = ComputeTsigRequestMac(key.KeyName, key.AlgorithmName, timeSigned, fudge, error, otherData, key.SharedSecret, 0);

            DnsResourceRecord tsigRecord = new DnsResourceRecord(key.KeyName, DnsResourceRecordType.TSIG, DnsClass.ANY, 0, new DnsTSIGRecordData(key.AlgorithmName, timeSigned, fudge, mac, _ID, error, otherData));

            IReadOnlyList<DnsResourceRecord> additional;

            if (_additional.Count == 0)
            {
                additional = new DnsResourceRecord[] { tsigRecord };
            }
            else
            {
                List<DnsResourceRecord> list = new List<DnsResourceRecord>(_additional.Count + 1);

                foreach (DnsResourceRecord record in _additional)
                {
                    if (record.Type == DnsResourceRecordType.TSIG)
                        throw new InvalidOperationException("Cannot sign this datagram: a TSIG record already exists.");

                    list.Add(record);
                }

                list.Add(tsigRecord);

                additional = list;
            }

            DnsDatagram signedDatagram = new DnsDatagram(_ID, _QR == 1, _OPCODE, _AA == 1, _TC == 1, _RD == 1, _RA == 1, _AD == 1, _CD == 1, _RCODE, _question, _answer, _authority, additional);
            signedDatagram._metadata = _metadata;
            signedDatagram.Tag = Tag;

            return signedDatagram;
        }

        public bool VerifySignedRequest(IReadOnlyDictionary<string, TsigKey> keys, out DnsDatagram unsignedRequest, out DnsDatagram errorResponse)
        {
            if (IsResponse)
                throw new InvalidOperationException("Cannot verify this datagram: datagram must be a request.");

            if (!IsSigned)
                throw new InvalidOperationException("Cannot verify this datagram: datagram is not signed.");

            DnsResourceRecord tsigRecord = _additional[_additional.Count - 1];
            DnsTSIGRecordData tsig = tsigRecord.RDATA as DnsTSIGRecordData;

            //verify
            ushort expectedMacSize = GetTsigMacSize(tsig.AlgorithmName);

            //Key Check
            if ((expectedMacSize == ushort.MinValue) || (keys is null) || !keys.TryGetValue(tsigRecord.Name.ToLowerInvariant(), out TsigKey key) || !key.AlgorithmName.Equals(tsig.AlgorithmName, StringComparison.OrdinalIgnoreCase))
            {
                unsignedRequest = null;

                //unsigned error response
                DnsTSIGRecordData errorTsig = new DnsTSIGRecordData(tsig.AlgorithmName, tsig.TimeSigned, tsig.Fudge, Array.Empty<byte>(), tsig.OriginalID, DnsTsigError.BADKEY, Array.Empty<byte>());
                DnsResourceRecord errorTsigRecord = new DnsResourceRecord(tsigRecord.Name, DnsResourceRecordType.TSIG, DnsClass.ANY, 0, errorTsig);
                errorResponse = new DnsDatagram(_ID, true, _OPCODE, _AA == 1, _TC == 1, _RD == 1, _RA == 1, _AD == 1, _CD == 1, DnsResponseCode.NotAuth, _question, null, null, new DnsResourceRecord[] { errorTsigRecord });

                return false;
            }

            //MAC Check
            if ((tsig.MAC.Length > expectedMacSize) || (tsig.MAC.Length < Math.Max(10, expectedMacSize / 2)))
            {
                unsignedRequest = null;

                //unsigned error response
                errorResponse = new DnsDatagram(_ID, true, _OPCODE, _AA == 1, _TC == 1, _RD == 1, _RA == 1, _AD == 1, _CD == 1, DnsResponseCode.FormatError, _question);

                return false;
            }

            byte[] computedMac = ComputeTsigRequestMac(key.KeyName, key.AlgorithmName, tsig.TimeSigned, tsig.Fudge, tsig.Error, tsig.OtherData, key.SharedSecret, tsig.MAC.Length);
            if (!BinaryNumber.Equals(computedMac, tsig.MAC))
            {
                unsignedRequest = null;

                //unsigned error response
                DnsTSIGRecordData errorTsig = new DnsTSIGRecordData(key.AlgorithmName, tsig.TimeSigned, tsig.Fudge, Array.Empty<byte>(), tsig.OriginalID, DnsTsigError.BADSIG, Array.Empty<byte>());
                DnsResourceRecord errorTsigRecord = new DnsResourceRecord(key.KeyName, DnsResourceRecordType.TSIG, DnsClass.ANY, 0, errorTsig);
                errorResponse = new DnsDatagram(_ID, true, _OPCODE, _AA == 1, _TC == 1, _RD == 1, _RA == 1, _AD == 1, _CD == 1, DnsResponseCode.NotAuth, _question, null, null, new DnsResourceRecord[] { errorTsigRecord });

                return false;
            }

            //Check time values
            DateTime startTime = DateTime.UnixEpoch.AddSeconds(tsig.TimeSigned - tsig.Fudge);
            DateTime endTime = DateTime.UnixEpoch.AddSeconds(tsig.TimeSigned + tsig.Fudge);
            DateTime utcNow = DateTime.UtcNow;

            if ((utcNow < startTime) || (utcNow > endTime))
            {
                unsignedRequest = null;

                //signed error response
                errorResponse = new DnsDatagram(_ID, true, _OPCODE, _AA == 1, _TC == 1, _RD == 1, _RA == 1, _AD == 1, _CD == 1, DnsResponseCode.NotAuth, _question);
                errorResponse = errorResponse.SignResponse(this, keys, DnsTsigError.BADTIME);

                return false;
            }

            //Check truncation policy
            if (tsig.MAC.Length < expectedMacSize)
            {
                unsignedRequest = null;

                //signed error response
                errorResponse = new DnsDatagram(_ID, true, _OPCODE, _AA == 1, _TC == 1, _RD == 1, _RA == 1, _AD == 1, _CD == 1, DnsResponseCode.NotAuth, _question);
                errorResponse = errorResponse.SignResponse(this, keys, DnsTsigError.BADTRUNC);

                return false;
            }

            //return
            IReadOnlyList<DnsResourceRecord> additional;

            if (_additional.Count == 1)
            {
                additional = Array.Empty<DnsResourceRecord>();
            }
            else
            {
                List<DnsResourceRecord> list = new List<DnsResourceRecord>(_additional.Count - 1);

                for (int i = 0; i < _additional.Count - 1; i++)
                {
                    DnsResourceRecord record = _additional[i];

                    if (record.Type == DnsResourceRecordType.TSIG)
                    {
                        //contains extra TSIG
                        unsignedRequest = null;

                        //unsigned error response
                        errorResponse = new DnsDatagram(_ID, true, _OPCODE, _AA == 1, _TC == 1, _RD == 1, _RA == 1, _AD == 1, _CD == 1, DnsResponseCode.FormatError, _question);

                        return false;
                    }

                    list.Add(record);
                }

                additional = list;
            }

            unsignedRequest = new DnsDatagram(tsig.OriginalID, _QR == 1, _OPCODE, _AA == 1, _TC == 1, _RD == 1, _RA == 1, _AD == 1, _CD == 1, _RCODE, _question, _answer, _authority, additional);
            unsignedRequest._metadata = _metadata;
            unsignedRequest.Tag = Tag;

            errorResponse = null;
            return true;
        }

        public DnsDatagram SignResponse(DnsDatagram signedRequest, IReadOnlyDictionary<string, TsigKey> keys, DnsTsigError error = DnsTsigError.NoError)
        {
            if (!IsResponse)
                throw new InvalidOperationException("Cannot sign this datagram: datagram must be a response.");

            if (!signedRequest.IsSigned)
                throw new InvalidOperationException("Cannot sign this datagram: a signed request datagram is expected.");

            DnsResourceRecord requestTsigRecord = signedRequest._additional[signedRequest._additional.Count - 1];
            DnsTSIGRecordData requestTsig = requestTsigRecord.RDATA as DnsTSIGRecordData;

            ulong timeSigned;
            byte[] otherData;

            switch (error)
            {
                case DnsTsigError.BADTIME:
                    timeSigned = requestTsig.TimeSigned;
                    otherData = ConvertToUInt48NetworkOrder(Convert.ToUInt64((DateTime.UtcNow - DateTime.UnixEpoch).TotalSeconds));
                    break;

                default:
                    timeSigned = Convert.ToUInt64((DateTime.UtcNow - DateTime.UnixEpoch).TotalSeconds);
                    otherData = Array.Empty<byte>();
                    break;
            }

            if ((keys is null) || !keys.TryGetValue(requestTsigRecord.Name.ToLowerInvariant(), out TsigKey key) || !key.AlgorithmName.Equals(requestTsig.AlgorithmName, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Cannot sign this datagram: key not found or algorithm mismatch.");

            byte[] mac = ComputeTsigResponseMac(requestTsigRecord, key.KeyName, key.AlgorithmName, timeSigned, requestTsig.Fudge, error, otherData, key.SharedSecret, 0);

            DnsTSIGRecordData tsig = new DnsTSIGRecordData(key.AlgorithmName, timeSigned, requestTsig.Fudge, mac, requestTsig.OriginalID, error, otherData);
            DnsResourceRecord tsigRecord = new DnsResourceRecord(key.KeyName, DnsResourceRecordType.TSIG, DnsClass.ANY, 0, tsig);

            IReadOnlyList<DnsResourceRecord> additional;

            if (_additional.Count == 0)
            {
                additional = new DnsResourceRecord[] { tsigRecord };
            }
            else
            {
                List<DnsResourceRecord> list = new List<DnsResourceRecord>(_additional.Count + 1);

                foreach (DnsResourceRecord record in _additional)
                {
                    if (record.Type == DnsResourceRecordType.TSIG)
                        throw new InvalidOperationException("Cannot sign this datagram: a TSIG record already exists.");

                    list.Add(record);
                }

                list.Add(tsigRecord);

                additional = list;
            }

            DnsDatagram signedDatagram = new DnsDatagram(signedRequest._ID, _QR == 1, _OPCODE, _AA == 1, _TC == 1, _RD == 1, _RA == 1, _AD == 1, _CD == 1, _RCODE, _question, _answer, _authority, additional);
            signedDatagram._metadata = _metadata;
            signedDatagram.Tag = Tag;

            //sign next messages
            if (_nextDatagram is not null)
            {
                DnsDatagram current = _nextDatagram;
                DnsTSIGRecordData priorTsig = tsig;
                DnsDatagram lastSigned = signedDatagram;

                while (current is not null)
                {
                    byte[] currentMac = ComputeTsigNextResponseMac(priorTsig, new DnsDatagram[] { current }, timeSigned, requestTsig.Fudge, key.SharedSecret, 0);

                    DnsTSIGRecordData currentTsig = new DnsTSIGRecordData(key.AlgorithmName, timeSigned, requestTsig.Fudge, currentMac, requestTsig.OriginalID, error, otherData);
                    DnsResourceRecord currentTsigRecord = new DnsResourceRecord(key.KeyName, DnsResourceRecordType.TSIG, DnsClass.ANY, 0, currentTsig);

                    IReadOnlyList<DnsResourceRecord> currentAdditional;

                    if (current._additional.Count == 0)
                    {
                        currentAdditional = new DnsResourceRecord[] { currentTsigRecord };
                    }
                    else
                    {
                        List<DnsResourceRecord> list = new List<DnsResourceRecord>(current._additional.Count + 1);

                        foreach (DnsResourceRecord record in current._additional)
                        {
                            if (record.Type == DnsResourceRecordType.TSIG)
                                throw new InvalidOperationException("Cannot sign this datagram: a TSIG record already exists.");

                            list.Add(record);
                        }

                        list.Add(currentTsigRecord);

                        currentAdditional = list;
                    }

                    DnsDatagram signedCurrent = new DnsDatagram(signedRequest._ID, _QR == 1, _OPCODE, _AA == 1, _TC == 1, _RD == 1, _RA == 1, _AD == 1, _CD == 1, _RCODE, current._question, current._answer, current._authority, currentAdditional);

                    current = current._nextDatagram;
                    priorTsig = currentTsig;
                    lastSigned._nextDatagram = signedCurrent;
                    lastSigned = signedCurrent;
                }
            }

            return signedDatagram;
        }

        public bool VerifySignedResponse(DnsDatagram signedRequest, TsigKey key, out DnsDatagram unsignedResponse, out bool requestFailed, out DnsResponseCode rCode, out DnsTsigError error)
        {
            if (!IsResponse)
                throw new InvalidOperationException("Cannot verify this datagram: datagram must be a response.");

            if (!IsSigned)
            {
                //datagram not signed; discard it
                unsignedResponse = null;
                requestFailed = false;
                rCode = DnsResponseCode.FormatError;
                error = DnsTsigError.NoError;
                return false;
            }

            DnsResourceRecord tsigRecord = _additional[_additional.Count - 1];
            DnsTSIGRecordData tsig = tsigRecord.RDATA as DnsTSIGRecordData;

            //verify
            ushort expectedMacSize = GetTsigMacSize(tsig.AlgorithmName);

            //Key Check
            if ((expectedMacSize == ushort.MinValue) || !key.KeyName.Equals(tsigRecord.Name, StringComparison.OrdinalIgnoreCase) || !key.AlgorithmName.Equals(tsig.AlgorithmName, StringComparison.OrdinalIgnoreCase))
            {
                unsignedResponse = null;
                requestFailed = false;
                rCode = DnsResponseCode.NotAuth;
                error = DnsTsigError.BADKEY;
                return false;
            }

            //check unsigned TSIG response
            if (tsig.MAC.Length == 0)
            {
                switch (_RCODE)
                {
                    case DnsResponseCode.FormatError:
                    case DnsResponseCode.NotAuth:
                        unsignedResponse = null;
                        requestFailed = true;
                        rCode = _RCODE;
                        error = tsig.Error;
                        return false;
                }
            }

            //MAC Check
            if ((tsig.MAC.Length > expectedMacSize) || (tsig.MAC.Length < Math.Max(10, expectedMacSize / 2)))
            {
                unsignedResponse = null;
                requestFailed = false;
                rCode = DnsResponseCode.FormatError;
                error = DnsTsigError.NoError;
                return false;
            }

            DnsResourceRecord requestTsigRecord = signedRequest._additional[signedRequest._additional.Count - 1];

            byte[] computedMac = ComputeTsigResponseMac(requestTsigRecord, key.KeyName, key.AlgorithmName, tsig.TimeSigned, tsig.Fudge, tsig.Error, tsig.OtherData, key.SharedSecret, tsig.MAC.Length);
            if (!BinaryNumber.Equals(computedMac, tsig.MAC))
            {
                unsignedResponse = null;
                requestFailed = false;
                rCode = DnsResponseCode.NotAuth;
                error = DnsTsigError.BADSIG;
                return false;
            }

            //check signed TSIG response
            switch (_RCODE)
            {
                case DnsResponseCode.FormatError:
                case DnsResponseCode.NotAuth:
                    unsignedResponse = null;
                    requestFailed = true;
                    rCode = _RCODE;
                    error = tsig.Error;
                    return false;
            }

            //Check time values
            DateTime utcNow = DateTime.UtcNow;
            DateTime startTime = DateTime.UnixEpoch.AddSeconds(tsig.TimeSigned - tsig.Fudge);
            DateTime endTime = DateTime.UnixEpoch.AddSeconds(tsig.TimeSigned + tsig.Fudge);

            if ((utcNow < startTime) || (utcNow > endTime))
            {
                unsignedResponse = null;
                requestFailed = false;
                rCode = DnsResponseCode.NotAuth;
                error = DnsTsigError.BADTIME;
                return false;
            }

            //Check truncation policy
            if (tsig.MAC.Length < expectedMacSize)
            {
                unsignedResponse = null;
                requestFailed = false;
                rCode = DnsResponseCode.NotAuth;
                error = DnsTsigError.BADTRUNC;
                return false;
            }

            //get unsigned datagram
            IReadOnlyList<DnsResourceRecord> additional;

            if (_additional.Count == 1)
            {
                additional = Array.Empty<DnsResourceRecord>();
            }
            else
            {
                List<DnsResourceRecord> list = new List<DnsResourceRecord>(_additional.Count - 1);

                for (int i = 0; i < _additional.Count - 1; i++)
                {
                    DnsResourceRecord record = _additional[i];

                    if (record.Type == DnsResourceRecordType.TSIG)
                    {
                        //contains extra TSIG
                        unsignedResponse = null;
                        requestFailed = false;
                        rCode = DnsResponseCode.FormatError;
                        error = DnsTsigError.NoError;
                        return false;
                    }

                    list.Add(record);
                }

                additional = list;
            }

            unsignedResponse = new DnsDatagram(tsig.OriginalID, _QR == 1, _OPCODE, _AA == 1, _TC == 1, _RD == 1, _RA == 1, _AD == 1, _CD == 1, _RCODE, _question, _answer, _authority, additional);
            unsignedResponse._metadata = _metadata;
            unsignedResponse.Tag = Tag;

            //verify next messages
            if (_nextDatagram is not null)
            {
                DnsDatagram unsignedPrior = unsignedResponse;
                DnsDatagram current = _nextDatagram;
                DnsTSIGRecordData priorTsig = tsig;
                List<DnsDatagram> dnsMessages = new List<DnsDatagram>();

                while (current is not null)
                {
                    dnsMessages.Add(current);

                    if (!current.IsSigned)
                    {
                        if (dnsMessages.Count < 100)
                            continue; //MUST accept up to 99 intermediary messages without a TSIG

                        unsignedResponse = null;
                        requestFailed = false;
                        rCode = DnsResponseCode.FormatError;
                        error = DnsTsigError.NoError;
                        return false;
                    }

                    DnsResourceRecord currentTsigRecord = current._additional[current._additional.Count - 1];
                    DnsTSIGRecordData currentTsig = currentTsigRecord.RDATA as DnsTSIGRecordData;

                    //verify
                    //MAC Check
                    if ((currentTsig.MAC.Length > expectedMacSize) || (currentTsig.MAC.Length < Math.Max(10, expectedMacSize / 2)))
                    {
                        unsignedResponse = null;
                        requestFailed = false;
                        rCode = DnsResponseCode.FormatError;
                        error = DnsTsigError.NoError;
                        return false;
                    }

                    byte[] currentComputedMac = ComputeTsigNextResponseMac(priorTsig, dnsMessages, currentTsig.TimeSigned, currentTsig.Fudge, key.SharedSecret, currentTsig.MAC.Length);
                    if (!BinaryNumber.Equals(currentComputedMac, currentTsig.MAC))
                    {
                        unsignedResponse = null;
                        requestFailed = false;
                        rCode = DnsResponseCode.NotAuth;
                        error = DnsTsigError.BADSIG;
                        return false;
                    }

                    //Check time values
                    DateTime currentStartTime = DateTime.UnixEpoch.AddSeconds(currentTsig.TimeSigned - currentTsig.Fudge);
                    DateTime currentEndTime = DateTime.UnixEpoch.AddSeconds(currentTsig.TimeSigned + currentTsig.Fudge);

                    if ((utcNow < currentStartTime) || (utcNow > currentEndTime))
                    {
                        unsignedResponse = null;
                        requestFailed = false;
                        rCode = DnsResponseCode.NotAuth;
                        error = DnsTsigError.BADTIME;
                        return false;
                    }

                    //Check truncation policy
                    if (currentTsig.MAC.Length < expectedMacSize)
                    {
                        unsignedResponse = null;
                        requestFailed = false;
                        rCode = DnsResponseCode.NotAuth;
                        error = DnsTsigError.BADTRUNC;
                        return false;
                    }

                    IReadOnlyList<DnsResourceRecord> currentAdditional;

                    if (_additional.Count == 1)
                    {
                        currentAdditional = Array.Empty<DnsResourceRecord>();
                    }
                    else
                    {
                        List<DnsResourceRecord> list = new List<DnsResourceRecord>(_additional.Count - 1);

                        for (int i = 0; i < _additional.Count - 1; i++)
                        {
                            DnsResourceRecord record = _additional[i];

                            if (record.Type == DnsResourceRecordType.TSIG)
                            {
                                //contains extra TSIG
                                unsignedResponse = null;
                                requestFailed = false;
                                rCode = DnsResponseCode.FormatError;
                                error = DnsTsigError.NoError;
                                return false;
                            }

                            list.Add(record);
                        }

                        currentAdditional = list;
                    }

                    DnsDatagram unsignedCurrentResponse = new DnsDatagram(currentTsig.OriginalID, _QR == 1, _OPCODE, _AA == 1, _TC == 1, _RD == 1, _RA == 1, _AD == 1, _CD == 1, _RCODE, current._question, current._answer, current._authority, currentAdditional);

                    unsignedPrior._nextDatagram = unsignedCurrentResponse;
                    unsignedPrior = unsignedCurrentResponse;
                    current = current._nextDatagram;
                    priorTsig = currentTsig;
                    dnsMessages.Clear();
                }

                if (dnsMessages.Count > 0)
                {
                    //last message was unsigned
                    unsignedResponse = null;
                    requestFailed = false;
                    rCode = DnsResponseCode.FormatError;
                    error = DnsTsigError.NoError;
                    return false;
                }
            }

            //return
            requestFailed = false;
            rCode = DnsResponseCode.NoError;
            error = DnsTsigError.NoError;
            return true;
        }

        #endregion

        #region private

        private byte[] ComputeTsigRequestMac(string keyName, string algorithmName, ulong timeSigned, ushort fudge, DnsTsigError error, byte[] otherData, string sharedSecret, int truncationLength)
        {
            using (MemoryStream mS = new MemoryStream(256))
            {
                //write DNS Message (request)
                if (_parsedDatagramUnsigned is null)
                {
                    if (IsSigned)
                        throw new InvalidOperationException();

                    WriteTo(mS); //client created request
                }
                else
                {
                    if (!IsSigned)
                        throw new InvalidOperationException();

                    mS.Write(_parsedDatagramUnsigned, 0, _parsedDatagramUnsigned.Length); //server received request
                }

                //write TSIG Variables (request)
                SerializeDomainName(keyName.ToLowerInvariant(), mS); //NAME
                WriteUInt16NetworkOrder((ushort)DnsClass.ANY, mS); //CLASS
                WriteUInt32NetworkOrder(0u, mS); //TTL
                SerializeDomainName(algorithmName.ToLowerInvariant(), mS); //Algorithm Name
                WriteUInt48NetworkOrder(timeSigned, mS); //Time Signed
                WriteUInt16NetworkOrder(fudge, mS); //Fudge
                WriteUInt16NetworkOrder((ushort)error, mS); //Error
                WriteUInt16NetworkOrder(Convert.ToUInt16(otherData.Length), mS); //Other Len
                mS.Write(otherData); //Other Data

                //compute mac
                mS.Position = 0;
                return ComputeTsigMac(algorithmName, sharedSecret, truncationLength, mS);
            }
        }

        private byte[] ComputeTsigResponseMac(DnsResourceRecord requestTsigRecord, string keyName, string algorithmName, ulong timeSigned, ushort fudge, DnsTsigError error, byte[] otherData, string sharedSecret, int truncationLength)
        {
            DnsTSIGRecordData requestTsig = requestTsigRecord.RDATA as DnsTSIGRecordData;

            using (MemoryStream mS = new MemoryStream(256))
            {
                //write Request MAC
                WriteUInt16NetworkOrder(Convert.ToUInt16(requestTsig.MAC.Length), mS);
                mS.Write(requestTsig.MAC);

                //write DNS Message (response)
                if (_parsedDatagramUnsigned is null)
                {
                    if (IsSigned)
                        throw new InvalidOperationException();

                    WriteTo(mS); //server created response
                }
                else
                {
                    if (!IsSigned)
                        throw new InvalidOperationException();

                    mS.Write(_parsedDatagramUnsigned, 0, _parsedDatagramUnsigned.Length); //client received response
                }

                //write TSIG Variables (response)
                SerializeDomainName(keyName.ToLowerInvariant(), mS); //NAME
                WriteUInt16NetworkOrder((ushort)DnsClass.ANY, mS); //CLASS
                WriteUInt32NetworkOrder(0u, mS); //TTL
                SerializeDomainName(algorithmName.ToLowerInvariant(), mS); //Algorithm Name
                WriteUInt48NetworkOrder(timeSigned, mS); //Time Signed
                WriteUInt16NetworkOrder(fudge, mS); //Fudge
                WriteUInt16NetworkOrder((ushort)error, mS); //Error
                WriteUInt16NetworkOrder(Convert.ToUInt16(otherData.Length), mS); //Other Len
                mS.Write(otherData); //Other Data

                //compute mac
                mS.Position = 0;
                return ComputeTsigMac(algorithmName, sharedSecret, truncationLength, mS);
            }
        }

        private static byte[] ComputeTsigNextResponseMac(DnsTSIGRecordData priorTsig, IReadOnlyList<DnsDatagram> dnsMessages, ulong timeSigned, ushort fudge, string sharedSecret, int truncationLength)
        {
            using (MemoryStream mS = new MemoryStream(256))
            {
                //write Prior MAC (running)
                WriteUInt16NetworkOrder(Convert.ToUInt16(priorTsig.MAC.Length), mS);
                mS.Write(priorTsig.MAC);

                //write DNS Messages (any unsigned messages since the last TSIG)
                foreach (DnsDatagram dnsMessage in dnsMessages)
                {
                    if (dnsMessage._parsedDatagramUnsigned is null)
                    {
                        if (dnsMessage.IsSigned)
                            throw new InvalidOperationException();

                        dnsMessage.WriteTo(mS); //server created response
                    }
                    else
                    {
                        if (!dnsMessage.IsSigned)
                            throw new InvalidOperationException();

                        mS.Write(dnsMessage._parsedDatagramUnsigned, 0, dnsMessage._parsedDatagramUnsigned.Length); //client received response
                    }
                }

                //write TSIG Timers (current message)
                WriteUInt48NetworkOrder(timeSigned, mS); //Time Signed
                WriteUInt16NetworkOrder(fudge, mS); //Fudge

                //compute mac
                mS.Position = 0;
                return ComputeTsigMac(priorTsig.AlgorithmName, sharedSecret, truncationLength, mS);
            }
        }

        private static ushort GetTsigMacSize(string algorithmName)
        {
            switch (algorithmName.ToLowerInvariant())
            {
                case DnsTSIGRecordData.ALGORITHM_NAME_HMAC_MD5:
                    return 16;

                case DnsTSIGRecordData.ALGORITHM_NAME_HMAC_SHA1:
                    return 20;

                case DnsTSIGRecordData.ALGORITHM_NAME_HMAC_SHA256:
                    return 32;

                case DnsTSIGRecordData.ALGORITHM_NAME_HMAC_SHA256_128:
                    return 16;

                case DnsTSIGRecordData.ALGORITHM_NAME_HMAC_SHA384:
                    return 48;

                case DnsTSIGRecordData.ALGORITHM_NAME_HMAC_SHA384_192:
                    return 24;

                case DnsTSIGRecordData.ALGORITHM_NAME_HMAC_SHA512:
                    return 64;

                case DnsTSIGRecordData.ALGORITHM_NAME_HMAC_SHA512_256:
                    return 32;

                default:
                    return ushort.MinValue;
            }
        }

        private static byte[] ComputeTsigMac(string algorithmName, string sharedSecret, int truncationLength, Stream s)
        {
            byte[] key;

            try
            {
                key = Convert.FromBase64String(sharedSecret);
            }
            catch (FormatException)
            {
                key = Encoding.UTF8.GetBytes(sharedSecret);
            }

            byte[] mac;

            switch (algorithmName.ToLowerInvariant())
            {
                case DnsTSIGRecordData.ALGORITHM_NAME_HMAC_MD5:
                    using (HMAC hmac = new HMACMD5(key))
                    {
                        mac = hmac.ComputeHash(s);
                    }
                    break;

                case DnsTSIGRecordData.ALGORITHM_NAME_HMAC_SHA1:
                    using (HMAC hmac = new HMACSHA1(key))
                    {
                        mac = hmac.ComputeHash(s);
                    }
                    break;

                case DnsTSIGRecordData.ALGORITHM_NAME_HMAC_SHA256:
                    using (HMAC hmac = new HMACSHA256(key))
                    {
                        mac = hmac.ComputeHash(s);
                    }
                    break;

                case DnsTSIGRecordData.ALGORITHM_NAME_HMAC_SHA256_128:
                    using (HMAC hmac = new HMACSHA256(key))
                    {
                        mac = hmac.ComputeHash(s);
                    }

                    if (truncationLength < 1)
                        truncationLength = 16;

                    break;

                case DnsTSIGRecordData.ALGORITHM_NAME_HMAC_SHA384:
                    using (HMAC hmac = new HMACSHA384(key))
                    {
                        mac = hmac.ComputeHash(s);
                    }
                    break;

                case DnsTSIGRecordData.ALGORITHM_NAME_HMAC_SHA384_192:
                    using (HMAC hmac = new HMACSHA384(key))
                    {
                        mac = hmac.ComputeHash(s);
                    }

                    if (truncationLength < 1)
                        truncationLength = 24;

                    break;

                case DnsTSIGRecordData.ALGORITHM_NAME_HMAC_SHA512:
                    using (HMAC hmac = new HMACSHA512(key))
                    {
                        mac = hmac.ComputeHash(s);
                    }
                    break;

                case DnsTSIGRecordData.ALGORITHM_NAME_HMAC_SHA512_256:
                    using (HMAC hmac = new HMACSHA512(key))
                    {
                        mac = hmac.ComputeHash(s);
                    }

                    if (truncationLength < 1)
                        truncationLength = 32;

                    break;

                default:
                    throw new NotSupportedException("TSIG algorithm is not supported: " + algorithmName);
            }

            if ((truncationLength > 0) && (truncationLength < mac.Length))
            {
                byte[] truncatedMac = new byte[truncationLength];

                Buffer.BlockCopy(mac, 0, truncatedMac, 0, truncatedMac.Length);

                return truncatedMac;
            }

            return mac;
        }

        #endregion

        #region properties

        public DnsDatagramMetadata Metadata
        { get { return _metadata; } }

        public DnsDatagramEdns EDNS
        { get { return _edns; } }

        public IReadOnlyList<EDnsExtendedDnsErrorOptionData> DnsClientExtendedErrors
        {
            get
            {
                if (_dnsClientExtendedErrors is null)
                    return Array.Empty<EDnsExtendedDnsErrorOptionData>();

                return _dnsClientExtendedErrors;
            }
        }

        public ushort Identifier
        { get { return _ID; } }

        public bool IsResponse
        { get { return _QR == 1; } }

        public DnsOpcode OPCODE
        { get { return _OPCODE; } }

        public bool AuthoritativeAnswer
        { get { return _AA == 1; } }

        public bool Truncation
        { get { return _TC == 1; } }

        public bool RecursionDesired
        { get { return _RD == 1; } }

        public bool RecursionAvailable
        { get { return _RA == 1; } }

        public byte Z
        { get { return _Z; } }

        public bool AuthenticData
        { get { return _AD == 1; } }

        public bool CheckingDisabled
        { get { return _CD == 1; } }

        public DnsResponseCode RCODE
        {
            get
            {
                if (_edns is not null)
                    return _edns.ExtendedRCODE;

                return _RCODE;
            }
        }

        public IReadOnlyList<DnsQuestionRecord> Question
        { get { return _question; } }

        public IReadOnlyList<DnsResourceRecord> Answer
        { get { return _answer; } }

        public IReadOnlyList<DnsResourceRecord> Authority
        { get { return _authority; } }

        public IReadOnlyList<DnsResourceRecord> Additional
        { get { return _additional; } }

        public bool IsSigned
        { get { return (_additional.Count > 0) && (_additional[_additional.Count - 1].Type == DnsResourceRecordType.TSIG); } }

        public DnsTsigError TsigError
        {
            get
            {
                if ((_additional.Count > 0) && (_additional[_additional.Count - 1].RDATA is DnsTSIGRecordData tsig))
                    return tsig.Error;

                return DnsTsigError.NoError;
            }
        }

        public string TsigKeyName
        {
            get
            {
                if ((_additional.Count > 0) && (_additional[_additional.Count - 1].Type == DnsResourceRecordType.TSIG))
                    return _additional[_additional.Count - 1].Name;

                return null;
            }
        }

        public bool IsZoneTransfer
        { get { return (_question.Count > 0) && ((_question[0].Type == DnsResourceRecordType.IXFR) || (_question[0].Type == DnsResourceRecordType.AXFR)); } }

        public Exception ParsingException
        { get { return _parsingException; } }

        public DnsDatagram NextDatagram
        {
            get { return _nextDatagram; }
            set
            {
                if (_nextDatagram is not null)
                    throw new InvalidOperationException("Cannot overwrite next datagram.");

                _nextDatagram = value;
            }
        }

        public bool DnssecOk
        {
            get
            {
                if (_edns is null)
                    return false;

                return _edns.Flags.HasFlag(EDnsHeaderFlags.DNSSEC_OK);
            }
        }

        public object Tag { get; set; }

        #endregion
    }
}
