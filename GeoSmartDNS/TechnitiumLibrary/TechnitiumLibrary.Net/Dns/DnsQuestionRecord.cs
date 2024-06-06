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
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using TechnitiumLibrary.Net.Dns.ResourceRecords;

namespace TechnitiumLibrary.Net.Dns
{
    //DNS Query Name Minimisation to Improve Privacy
    //https://datatracker.ietf.org/doc/html/draft-ietf-dnsop-rfc7816bis-04

    //Use of Bit 0x20 in DNS Labels to Improve Transaction Identity
    //https://datatracker.ietf.org/doc/html/draft-vixie-dnsext-dns0x20-00

    public class DnsQuestionRecord
    {
        #region variables

        string _name;
        readonly DnsResourceRecordType _type;
        readonly DnsClass _class;

        //QNAME Minimization
        const int MAX_MINIMISE_COUNT = 10;
        string _zoneCut;
        string _minimizedName;

        #endregion  

        #region constructor

        public DnsQuestionRecord(string name, DnsResourceRecordType type, DnsClass @class, bool validateName = true)
        {
            if (DnsClient.IsDomainNameUnicode(name))
                name = DnsClient.ConvertDomainNameToAscii(name);

            if (validateName)
                DnsClient.IsDomainNameValid(name, true);

            _name = name;
            _type = type;
            _class = @class;
        }

        public DnsQuestionRecord(IPAddress ip, DnsClass @class)
        {
            _name = ip.GetReverseDomain();
            _type = DnsResourceRecordType.PTR;
            _class = @class;
        }

        public DnsQuestionRecord(Stream s)
        {
            _name = DnsDatagram.DeserializeDomainName(s);
            _type = (DnsResourceRecordType)DnsDatagram.ReadUInt16NetworkOrder(s);
            _class = (DnsClass)DnsDatagram.ReadUInt16NetworkOrder(s);
        }

        public DnsQuestionRecord(JsonElement jsonQuestionRecord)
        {
            _name = jsonQuestionRecord.GetProperty("name").GetString().TrimEnd('.');
            _type = (DnsResourceRecordType)jsonQuestionRecord.GetProperty("type").GetUInt16();
            _class = DnsClass.IN;
        }

        #endregion

        #region private

        private static string RandomizeName(string name)
        {
            if (string.IsNullOrEmpty(name))
                return name;

            byte[] asciiName = Encoding.ASCII.GetBytes(name);
            Span<byte> r = stackalloc byte[asciiName.Length];

            RandomNumberGenerator.Fill(r);

            for (int i = 0; i < asciiName.Length; i++)
            {
                if (((asciiName[i] >= 97) && (asciiName[i] <= 122)) || ((asciiName[i] >= 65) && (asciiName[i] <= 90)))
                {
                    if ((r[i] & 0x1) > 0)
                        asciiName[i] |= 0x20;
                    else
                        asciiName[i] &= 0xDF;
                }
            }

            return Encoding.ASCII.GetString(asciiName);
        }

        private static string GetMinimizedName(string name, string zoneCut)
        {
            //www.example.com
            //com

            if ((zoneCut is not null) && (name.Length > (zoneCut.Length + 1)))
            {
                int i = name.LastIndexOf('.', name.Length - zoneCut.Length - 2);
                if (i < 0)
                    return name;

                //return minimized QNAME
                return name.Substring(i + 1);
            }

            return null;
        }

        #endregion

        #region internal

        internal void RandomizeName()
        {
            if (_minimizedName == null)
                _name = RandomizeName(_name);
            else
                _minimizedName = RandomizeName(_minimizedName);
        }

        internal void NormalizeName()
        {
            if (_minimizedName == null)
                _name = _name.ToLowerInvariant();
            else
                _minimizedName = _minimizedName.ToLowerInvariant();
        }

        internal DnsQuestionRecord Clone()
        {
            return new DnsQuestionRecord(_name, _type, _class) { _zoneCut = _zoneCut, _minimizedName = _minimizedName };
        }

        #endregion

        #region public

        public void WriteTo(Stream s, List<DnsDomainOffset> domainEntries)
        {
            if (_minimizedName == null)
            {
                DnsDatagram.SerializeDomainName(_name, s, domainEntries);
                DnsDatagram.WriteUInt16NetworkOrder((ushort)_type, s);
                DnsDatagram.WriteUInt16NetworkOrder((ushort)_class, s);
            }
            else
            {
                DnsDatagram.SerializeDomainName(_minimizedName, s, domainEntries);
                DnsDatagram.WriteUInt16NetworkOrder((ushort)MinimizedType, s);
                DnsDatagram.WriteUInt16NetworkOrder((ushort)_class, s);
            }
        }

        public override bool Equals(object obj)
        {
            if (obj is null)
                return false;

            if (ReferenceEquals(this, obj))
                return true;

            if (obj is DnsQuestionRecord other)
            {
                if (!_name.Equals(other._name, StringComparison.OrdinalIgnoreCase))
                    return false;

                if (_type != other._type)
                    return false;

                if (_class != other._class)
                    return false;

                return true;
            }

            return false;
        }

        public override int GetHashCode()
        {
            return HashCode.Combine(_name, _type, _class);
        }

        public override string ToString()
        {
            return _name.ToLowerInvariant() + ". " + _type.ToString() + " " + _class.ToString();
        }

        public void SerializeTo(Utf8JsonWriter jsonWriter)
        {
            jsonWriter.WriteStartObject();

            jsonWriter.WriteString("Name", _name);

            if (DnsClient.TryConvertDomainNameToUnicode(_name, out string nameIDN))
                jsonWriter.WriteString("NameIDN", nameIDN);

            jsonWriter.WriteString("Type", _type.ToString());
            jsonWriter.WriteString("Class", _class.ToString());

            jsonWriter.WriteEndObject();
        }

        #endregion

        #region properties

        public string Name
        { get { return _name; } }

        public DnsResourceRecordType Type
        { get { return _type; } }

        public DnsClass Class
        { get { return _class; } }

        internal string ZoneCut
        {
            get { return _zoneCut; }
            set
            {
                _zoneCut = value;
                _minimizedName = GetMinimizedName(_name, _zoneCut);
                if ((_minimizedName is null) || _minimizedName.StartsWith('_') || (_minimizedName.Split('.').Length > MAX_MINIMISE_COUNT))
                {
                    //auto disable QNAME minimization
                    _zoneCut = null;
                    _minimizedName = null;
                }
            }
        }

        internal string MinimizedName
        { get { return _minimizedName; } }

        internal DnsResourceRecordType MinimizedType
        {
            get
            {
                switch (_type)
                {
                    case DnsResourceRecordType.DS:
                    case DnsResourceRecordType.AAAA:
                        return _type;

                    default:
                        return DnsResourceRecordType.A;
                }
            }
        }

        public ushort UncompressedLength
        { get { return Convert.ToUInt16(_name.Length + 2 + 2 + 2); } }

        #endregion
    }
}
