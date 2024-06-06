using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace GeoSmartDNS
{
    public class Domain_Attribute
    {
        public string Key { get; set; }
        public bool? BoolValue { get; set; }
        public long? IntValue { get; set; }
    }

    public class Domain
    {
        public DomainType Type { get; set; }
        public string Value { get; set; }
        public List<Domain_Attribute> Attributes { get; set; } = new List<Domain_Attribute>();
    }

    public class GeoSite
    {
        public string CountryCode { get; set; }
        public List<Domain> Domains { get; set; } = new List<Domain>();
    }

    public class GeoSiteList
    {
        public List<GeoSite> Entries { get; set; } = new List<GeoSite>();
    }

    public enum DomainType
    {
        RootDomain = 0,
        Regex = 1,
        Plain = 2,
        Full = 3
    }


    public class GeoSiteParse
    {
        public static GeoSiteList ParseGeoSiteList(byte[] data)
        {
            GeoSiteList geoSiteList = new GeoSiteList();
            using (MemoryStream ms = new MemoryStream(data))
            {
                while (ms.Position < ms.Length)
                {
                    int tag = (int)ReadVarint(ms);
                    int fieldNumber = tag >> 3;
                    int wireType = tag & 0x7;

                    switch (fieldNumber)
                    {
                        case 1: // entry
                            geoSiteList.Entries.Add(ParseGeoSite(ms));
                            break;
                        default:
                            SkipField(ms, wireType);
                            break;
                    }
                }
            }
            return geoSiteList;
        }

        private static GeoSite ParseGeoSite(MemoryStream ms)
        {
            GeoSite geoSite = new GeoSite();
            long length = ReadVarint(ms);
            long end = ms.Position + length;

            while (ms.Position < end)
            {
                int tag = (int)ReadVarint(ms);
                int fieldNumber = tag >> 3;
                int wireType = tag & 0x7;

                switch (fieldNumber)
                {
                    case 1: // country_code
                        geoSite.CountryCode = ReadString(ms);
                        break;
                    case 2: // domain
                        geoSite.Domains.Add(ParseDomain(ms));
                        break;
                    default:
                        SkipField(ms, wireType);
                        break;
                }
            }
            return geoSite;
        }

        private static Domain ParseDomain(MemoryStream ms)
        {
            Domain domain = new Domain();
            long length = ReadVarint(ms);
            long end = ms.Position + length;

            while (ms.Position < end)
            {
                int tag = (int)ReadVarint(ms);
                int fieldNumber = tag >> 3;
                int wireType = tag & 0x7;

                switch (fieldNumber)
                {
                    case 1: // type
                        domain.Type = (DomainType)ReadVarint(ms);
                        break;
                    case 2: // value
                        domain.Value = ReadString(ms);
                        break;
                    case 3: // attribute
                        domain.Attributes.Add(ParseDomainAttribute(ms));
                        break;
                    default:
                        SkipField(ms, wireType);
                        break;
                }
            }
            return domain;
        }

        private static Domain_Attribute ParseDomainAttribute(MemoryStream ms)
        {
            Domain_Attribute attribute = new Domain_Attribute();
            long length = ReadVarint(ms);
            long end = ms.Position + length;

            while (ms.Position < end)
            {
                int tag = (int)ReadVarint(ms);
                int fieldNumber = tag >> 3;
                int wireType = tag & 0x7;

                switch (fieldNumber)
                {
                    case 1: // key
                        attribute.Key = ReadString(ms);
                        break;
                    case 2: // bool_value
                        attribute.BoolValue = ReadBool(ms);
                        break;
                    case 3: // int_value
                        attribute.IntValue = ReadVarint(ms);
                        break;
                    default:
                        SkipField(ms, wireType);
                        break;
                }
            }
            return attribute;
        }

        private static void SkipField(MemoryStream ms, int wireType)
        {
            switch (wireType)
            {
                case 0: // Varint
                    ReadVarint(ms);
                    break;
                case 1: // Fixed64
                    ms.Seek(8, SeekOrigin.Current);
                    break;
                case 2: // Length-delimited
                    long length = ReadVarint(ms);
                    ms.Seek(length, SeekOrigin.Current);
                    break;
                case 5: // Fixed32
                    ms.Seek(4, SeekOrigin.Current);
                    break;
                default:
                    throw new InvalidOperationException($"Unsupported wire type: {wireType}");
            }
        }

        private static long ReadVarint(MemoryStream ms)
        {
            long result = 0;
            int shift = 0;
            while (true)
            {
                byte b = (byte)ms.ReadByte();
                result |= (long)(b & 0x7F) << shift;
                if ((b & 0x80) == 0) break;
                shift += 7;
            }
            return result;
        }

        private static string ReadString(MemoryStream ms)
        {
            long length = ReadVarint(ms);
            byte[] buffer = new byte[length];
            ms.Read(buffer, 0, (int)length);
            return System.Text.Encoding.UTF8.GetString(buffer);
        }

        private static bool ReadBool(MemoryStream ms)
        {
            return ms.ReadByte() != 0;
        }
    }

}
