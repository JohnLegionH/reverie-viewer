// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using System.Buffers.Binary;
using System.Xml;

namespace Viewer.Core.Protocol;

/// <summary>
/// A bounded read of LLSD (binary and XML) that runs before the data reaches LibreMetaverse's parser, which has no limit
/// on nesting and allocates a string or blob from its declared length before reading it. The check builds nothing: it only
/// walks the data and says whether it stays inside the limits. Nesting is limited to <see cref="MaxDepth"/> (the limit
/// LibreMetaverse 3.2.0 sets in its own parsers), every declared length and element count must fit in the bytes that remain,
/// and the number of values is limited to <see cref="MaxValues"/>. Notation LLSD has its own bounded reader
/// (<see cref="LlsdNotation"/>).
/// </summary>
internal static class LlsdGuard
{
    public const int MaxDepth = 128;
    public const int MaxValues = 4 * 1024 * 1024;

    /// <summary>Null when the binary LLSD body in <paramref name="body"/> (after its header line) is inside the limits, otherwise what is wrong with it.</summary>
    public static string? CheckBinary(ReadOnlySpan<byte> body)
    {
        var w = new BinaryWalker(body);
        return w.Walk(1);
    }

    /// <summary>Null when the XML LLSD document is inside the limits, otherwise what is wrong with it. A document type declaration is refused.</summary>
    public static string? CheckXml(string xml)
    {
        var settings = new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = 4 * 1024 * 1024 };
        try
        {
            using var reader = XmlReader.Create(new StringReader(xml), settings);
            int values = 0;
            while (reader.Read())
            {
                if (reader.NodeType != XmlNodeType.Element)
                    continue;
                // the llsd element is depth 0, so an element's depth is the nesting depth of the value it holds
                if (reader.Depth > MaxDepth) return "nesting deeper than " + MaxDepth;
                if (++values > MaxValues) return "too many elements";
            }
            return null;
        }
        catch (XmlException ex) { return "not well-formed XML (" + ex.Message.Split('.')[0] + ")"; }
    }

    private ref struct BinaryWalker(ReadOnlySpan<byte> data)
    {
        private readonly ReadOnlySpan<byte> _d = data;
        private int _pos;
        private int _values;

        private readonly int Remaining => _d.Length - _pos;

        /// <summary>A length prefix that fits in what remains after it.</summary>
        private bool Length(out int len)
        {
            len = 0;
            if (Remaining < 4) return false;
            len = BinaryPrimitives.ReadInt32BigEndian(_d.Slice(_pos, 4));
            _pos += 4;
            return len >= 0 && len <= Remaining;
        }

        public string? Walk(int depth)
        {
            if (depth > MaxDepth) return "nesting deeper than " + MaxDepth;
            if (++_values > MaxValues) return "too many values";
            if (Remaining < 1) return "truncated";
            char type = (char)_d[_pos++];
            switch (type)
            {
                case '!': case '1': case '0': return null;
                case 'i': return Skip(4);
                case 'r': case 'd': return Skip(8);
                case 'u': return Skip(16);
                case 's': case 'b': case 'l':
                    if (!Length(out int n)) return "declared length beyond the data";
                    _pos += n;
                    return null;
                case '[':
                {
                    // every element takes at least one byte, so a larger count is a lie
                    if (!Length(out int count)) return "array count beyond the data";
                    for (int i = 0; i < count; i++)
                        if (Walk(depth + 1) is { } why) return why;
                    return Expect(']');
                }
                case '{':
                {
                    // every entry takes at least a key marker, a length and a value
                    if (!Length(out int count)) return "map count beyond the data";
                    for (int i = 0; i < count; i++)
                    {
                        if (Remaining < 1 || _d[_pos] != (byte)'k') return "map key expected";
                        _pos++;
                        if (!Length(out int k)) return "key length beyond the data";
                        _pos += k;
                        if (Walk(depth + 1) is { } why) return why;
                    }
                    return Expect('}');
                }
                default: return $"unknown type byte 0x{(byte)type:X2}";
            }
        }

        private string? Skip(int n)
        {
            if (Remaining < n) return "truncated";
            _pos += n;
            return null;
        }

        private string? Expect(char c)
        {
            if (Remaining < 1 || _d[_pos] != (byte)c) return $"missing '{c}'";
            _pos++;
            return null;
        }
    }
}
