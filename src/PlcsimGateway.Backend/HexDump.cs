/*********************************************************************
 * NetToPLCsim, Netzwerkanbindung fuer PLCSIM
 *
 * Copyright (C) 2011-2016 Thomas Wiens, th.wiens@gmx.de
 *
 * This file is part of NetToPLCsim.
 *
 * NetToPLCsim is free software: you can redistribute it and/or modify
 * it under the terms of the GNU Lesser General Public License as
 * published by the Free Software Foundation, either version 3 of the
 * License, or (at your option) any later version.
 /*********************************************************************/

using System;
using System.Text;
using PlcsimGateway.Backend.IsoOnTcp;

namespace PlcsimGateway.Backend.Diagnostics
{
    public class Utils
    {
        public static string HexDumpAll(byte[] bytes)
        {
            int len = bytes.Length;
            return HexDump(bytes, len);
        }

        public static string HexDump(byte[] bytes, int len)
        {
            if (bytes == null) return "<null>";

            StringBuilder result = new StringBuilder(((len + 15) / 16) * 78);
            char[] chars = new char[78];

            for (int i = 0; i < 75; i++) chars[i] = ' ';
            chars[76] = '\r';
            chars[77] = '\n';

            for (int i1 = 0; i1 < len; i1 += 16)
            {
                chars[0] = HexChar(i1 >> 28);
                chars[1] = HexChar(i1 >> 24);
                chars[2] = HexChar(i1 >> 20);
                chars[3] = HexChar(i1 >> 16);
                chars[4] = HexChar(i1 >> 12);
                chars[5] = HexChar(i1 >> 8);
                chars[6] = HexChar(i1 >> 4);
                chars[7] = HexChar(i1 >> 0);

                int offset1 = 11;
                int offset2 = 60;

                for (int i2 = 0; i2 < 16; i2++)
                {
                    if (i1 + i2 >= len)
                    {
                        chars[offset1] = ' ';
                        chars[offset1 + 1] = ' ';
                        chars[offset2] = ' ';
                    }
                    else
                    {
                        byte b = bytes[i1 + i2];
                        chars[offset1] = HexChar(b >> 4);
                        chars[offset1 + 1] = HexChar(b);
                        chars[offset2] = (b < 32 ? '·' : (char)b);
                    }
                    offset1 += (i2 == 8 ? 4 : 3);
                    offset2++;
                }
                result.Append(chars);
            }
            return result.ToString();
        }

        public static string HexString(byte[] bytes, int len)
        {
            if (bytes == null) return "<null>";

            int safeLen = Math.Min(Math.Max(len, 0), bytes.Length);

            StringBuilder result = new StringBuilder(safeLen * 2);
            for (int i = 0; i < safeLen; i++)
            {
                result.Append(HexChar(bytes[i] >> 4));
                result.Append(HexChar(bytes[i]));
            }

            return result.ToString();
        }

        public static string S7PayloadDumpLine(string direction, IsoSessionEvent sessionEvent, long pduIndex, int maxBytes)
        {
            byte[] payload = sessionEvent.Payload;
            int dumpedBytes = GetS7PayloadDumpByteCount(payload, maxBytes);
            return DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff")
                + " PAYLOAD"
                + " direction=" + direction
                + " session=" + sessionEvent.SessionId
                + " remote=" + sessionEvent.RemoteEndPoint
                + " protocol=" + TextOrDash(sessionEvent.ProtocolName)
                + " pduIndex=" + pduIndex
                + " payloadBytes=" + sessionEvent.PayloadLength
                + " dumpedBytes=" + dumpedBytes
                + " firstS7=" + sessionEvent.FirstS7Byte
                + " hex=" + HexString(payload, dumpedBytes);
        }

        private static int GetS7PayloadDumpByteCount(byte[] payload, int maxBytes)
        {
            if (payload == null)
            {
                return 0;
            }

            if (maxBytes <= 0)
            {
                return payload.Length;
            }

            return Math.Min(payload.Length, maxBytes);
        }

        private static string TextOrDash(string value)
        {
            return String.IsNullOrWhiteSpace(value) ? "-" : value;
        }

        private static char HexChar(int value)
        {
            value &= 0xF;
            if (value <= 9) return (char)('0' + value);
            else return (char)('A' + (value - 10));
        }
    }
}
