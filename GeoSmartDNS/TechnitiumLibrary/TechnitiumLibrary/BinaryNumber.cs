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
using System.Security.Cryptography;

namespace TechnitiumLibrary
{
    public class BinaryNumber : IEquatable<BinaryNumber>, IComparable<BinaryNumber>
    {
        #region variables

        readonly byte[] _value;

        #endregion

        #region constructor

        public BinaryNumber(byte[] value)
        {
            _value = value;
        }

        public BinaryNumber(Stream s)
            : this(new BinaryReader(s))
        { }

        public BinaryNumber(BinaryReader bR)
        {
            _value = bR.ReadBytes(bR.Read7BitEncodedInt());
        }

        #endregion

        #region static

        public static BinaryNumber GenerateRandomNumber160()
        {
            return new BinaryNumber(RandomNumberGenerator.GetBytes(20));
        }

        public static BinaryNumber GenerateRandomNumber256()
        {
            return new BinaryNumber(RandomNumberGenerator.GetBytes(32));
        }

        public static BinaryNumber MaxValueNumber160()
        {
            return new BinaryNumber(new byte[] { 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF });
        }

        public static BinaryNumber Clone(byte[] buffer, int offset, int count)
        {
            byte[] value = new byte[count];
            Buffer.BlockCopy(buffer, offset, value, 0, count);

            return new BinaryNumber(value);
        }

        public static bool Equals(byte[] value1, byte[] value2)
        {
            if (ReferenceEquals(value1, value2))
                return true;

            if ((value1 == null) || (value2 == null))
                return false;

            if (value1.Length != value2.Length)
                return false;

            for (int i = 0; i < value1.Length; i++)
            {
                if (value1[i] != value2[i])
                    return false;
            }

            return true;
        }

        public static BinaryNumber Parse(string value)
        {
            return new BinaryNumber(Convert.FromHexString(value));
        }

        #endregion

        #region public

        public BinaryNumber Clone()
        {
            byte[] value = new byte[_value.Length];
            Buffer.BlockCopy(_value, 0, value, 0, _value.Length);
            return new BinaryNumber(value);
        }

        public bool Equals(BinaryNumber obj)
        {
            if (obj is null)
                return false;

            return Equals(_value, obj._value);
        }

        public override bool Equals(object obj)
        {
            return Equals(obj as BinaryNumber);
        }

        public override int GetHashCode()
        {
            return HashCode.Combine(_value);
        }

        public int CompareTo(BinaryNumber other)
        {
            if (this._value.Length != other._value.Length)
                throw new ArgumentException("Operand value length not equal.");

            for (int i = 0; i < this._value.Length; i++)
            {
                if (this._value[i] > other._value[i])
                    return 1;

                if (this._value[i] < other._value[i])
                    return -1;
            }

            return 0;
        }

        public override string ToString()
        {
            return Convert.ToHexString(_value).ToLower();
        }

        public void WriteTo(Stream s)
        {
            s.WriteByte(Convert.ToByte(_value.Length));
            s.Write(_value, 0, _value.Length);
        }

        #endregion

        #region operators

        public static bool operator ==(BinaryNumber b1, BinaryNumber b2)
        {
            if (ReferenceEquals(b1, b2))
                return true;

            return b1.Equals(b2);
        }

        public static bool operator !=(BinaryNumber b1, BinaryNumber b2)
        {
            if (ReferenceEquals(b1, b2))
                return false;

            return !b1.Equals(b2);
        }

        public static BinaryNumber operator |(BinaryNumber b1, BinaryNumber b2)
        {
            if (b1._value.Length != b2._value.Length)
                throw new ArgumentException("Operand value length not equal.");

            byte[] value = new byte[b1._value.Length];

            for (int i = 0; i < value.Length; i++)
                value[i] = (byte)(b1._value[i] | b2._value[i]);

            return new BinaryNumber(value);
        }

        public static BinaryNumber operator &(BinaryNumber b1, BinaryNumber b2)
        {
            if (b1._value.Length != b2._value.Length)
                throw new ArgumentException("Operand value length not equal.");

            byte[] value = new byte[b1._value.Length];

            for (int i = 0; i < value.Length; i++)
                value[i] = (byte)(b1._value[i] & b2._value[i]);

            return new BinaryNumber(value);
        }

        public static BinaryNumber operator ^(BinaryNumber b1, BinaryNumber b2)
        {
            if (b1._value.Length != b2._value.Length)
                throw new ArgumentException("Operand value length not equal.");

            byte[] value = new byte[b1._value.Length];

            for (int i = 0; i < value.Length; i++)
                value[i] = (byte)(b1._value[i] ^ b2._value[i]);

            return new BinaryNumber(value);
        }

        public static BinaryNumber operator >>(BinaryNumber b1, int bitcount)
        {
            byte[] value = new byte[b1._value.Length];

            if (bitcount >= 8)
                Buffer.BlockCopy(b1._value, 0, value, bitcount / 8, value.Length - (bitcount / 8));
            else
                Buffer.BlockCopy(b1._value, 0, value, 0, value.Length);

            bitcount %= 8;

            if (bitcount > 0)
            {
                for (int i = value.Length - 1; i >= 0; i--)
                {
                    value[i] >>= bitcount;

                    if (i > 0)
                        value[i] |= (byte)(value[i - 1] << (8 - bitcount));
                }
            }

            return new BinaryNumber(value);
        }

        public static BinaryNumber operator <<(BinaryNumber b1, int bitcount)
        {
            byte[] value = new byte[b1._value.Length];

            if (bitcount >= 8)
                Buffer.BlockCopy(b1._value, bitcount / 8, value, 0, value.Length - (bitcount / 8));
            else
                Buffer.BlockCopy(b1._value, 0, value, 0, value.Length);

            bitcount %= 8;

            if (bitcount > 0)
            {
                for (int i = 0; i < value.Length; i++)
                {
                    value[i] <<= bitcount;

                    if (i < (value.Length - 1))
                        value[i] |= (byte)(value[i + 1] >> (8 - bitcount));
                }
            }

            return new BinaryNumber(value);
        }

        public static bool operator <(BinaryNumber b1, BinaryNumber b2)
        {
            if (b1._value.Length != b2._value.Length)
                throw new ArgumentException("Operand value length not equal.");

            for (int i = 0; i < b1._value.Length; i++)
            {
                if (b1._value[i] < b2._value[i])
                    return true;

                if (b1._value[i] > b2._value[i])
                    return false;
            }

            return false;
        }

        public static bool operator >(BinaryNumber b1, BinaryNumber b2)
        {
            if (b1._value.Length != b2._value.Length)
                throw new ArgumentException("Operand value length not equal.");

            for (int i = 0; i < b1._value.Length; i++)
            {
                if (b1._value[i] > b2._value[i])
                    return true;

                if (b1._value[i] < b2._value[i])
                    return false;
            }

            return false;
        }

        public static bool operator <=(BinaryNumber b1, BinaryNumber b2)
        {
            if (b1._value.Length != b2._value.Length)
                throw new ArgumentException("Operand value length not equal.");

            for (int i = 0; i < b1._value.Length; i++)
            {
                if (b1._value[i] < b2._value[i])
                    return true;

                if (b1._value[i] > b2._value[i])
                    return false;
            }

            return true;
        }

        public static bool operator >=(BinaryNumber b1, BinaryNumber b2)
        {
            if (b1._value.Length != b2._value.Length)
                throw new ArgumentException("Operand value length not equal.");

            for (int i = 0; i < b1._value.Length; i++)
            {
                if (b1._value[i] > b2._value[i])
                    return true;

                if (b1._value[i] < b2._value[i])
                    return false;
            }

            return true;
        }

        public static BinaryNumber operator ~(BinaryNumber b1)
        {
            BinaryNumber obj = b1.Clone();

            for (int i = 0; i < obj._value.Length; i++)
                obj._value[i] = (byte)~obj._value[i];

            return obj;
        }

        #endregion

        #region properties

        public byte[] Value
        { get { return _value; } }

        #endregion
    }
}
