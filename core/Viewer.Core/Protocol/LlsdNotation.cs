// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;

namespace Viewer.Core.Protocol;

/// <summary>
/// A reader for LLSD notation (the text form of LLSD, as the LLSD specification on the Second Life wiki describes it),
/// giving JSON: maps become objects, arrays arrays, integers and reals numbers, booleans booleans, undef null, and
/// UUIDs, strings, URIs and dates strings; binary becomes a base64 string. A real that JSON cannot hold (nan, inf) becomes
/// null. Written for the region's GLTF override messages: LibreMetaverse 3.1.6's notation parser throws on an empty array
/// followed by more entries and silently drops a map's entries after an empty map, which are the shapes an override
/// message uses to clear overrides. Bounded: nesting deeper than <see cref="MaxDepth"/> or any malformed input throws
/// <see cref="FormatException"/>.
/// </summary>
public static class LlsdNotation
{
    public const int MaxDepth = 32;

    public static JsonNode? Parse(string text)
    {
        int i = 0;
        var v = Value(text, ref i, 0);
        Skip(text, ref i);
        if (i != text.Length)
            throw new FormatException("trailing characters after the value at " + i);
        return v;
    }

    private static void Skip(string s, ref int i)
    {
        while (i < s.Length && char.IsWhiteSpace(s[i]))
            i++;
    }

    private static char Next(string s, ref int i)
    {
        Skip(s, ref i);
        if (i >= s.Length)
            throw new FormatException("unexpected end of input");
        return s[i];
    }

    private static void Expect(string s, ref int i, char c)
    {
        if (Next(s, ref i) != c)
            throw new FormatException($"expected '{c}' at {i}");
        i++;
    }

    private static JsonNode? Value(string s, ref int i, int depth)
    {
        if (depth > MaxDepth)
            throw new FormatException("nested deeper than " + MaxDepth);
        char c = Next(s, ref i);
        switch (c)
        {
            case '!': i++; return null;
            case '{': return Map(s, ref i, depth);
            case '[': return Array(s, ref i, depth);
            case '\'' or '"': return JsonValue.Create(Quoted(s, ref i));
            case 's' when i + 1 < s.Length && s[i + 1] == '(': return JsonValue.Create(Sized(s, ref i));
            case 'i': i++; return JsonValue.Create(long.Parse(Token(s, ref i, "+-0123456789"), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture));
            case 'r': i++; return Real(Token(s, ref i, "+-.0123456789eEnaifNAIF"));
            case 'u': i++; return JsonValue.Create(Guid.Parse(Take(s, ref i, 36)).ToString());
            case 'l' or 'd': i++; return JsonValue.Create(Quoted(s, ref i));
            case 'b': return JsonValue.Create(Binary(s, ref i));
            default: return Boolean(s, ref i);
        }
    }

    private static JsonObject Map(string s, ref int i, int depth)
    {
        i++;   // '{'
        var o = new JsonObject();
        if (Next(s, ref i) == '}') { i++; return o; }
        while (true)
        {
            char k = Next(s, ref i);
            string key = k is '\'' or '"' ? Quoted(s, ref i) : k == 's' ? Sized(s, ref i) : throw new FormatException("a map key must be a string at " + i);
            Expect(s, ref i, ':');
            o[key] = Value(s, ref i, depth + 1);   // a repeated key keeps the last value
            char n = Next(s, ref i);
            i++;
            if (n == '}') return o;
            if (n != ',') throw new FormatException("expected ',' or '}' at " + (i - 1));
        }
    }

    private static JsonArray Array(string s, ref int i, int depth)
    {
        i++;   // '['
        var a = new JsonArray();
        if (Next(s, ref i) == ']') { i++; return a; }
        while (true)
        {
            a.Add(Value(s, ref i, depth + 1));
            char n = Next(s, ref i);
            i++;
            if (n == ']') return a;
            if (n != ',') throw new FormatException("expected ',' or ']' at " + (i - 1));
        }
    }

    private static string Token(string s, ref int i, string allowed)
    {
        int start = i;
        while (i < s.Length && allowed.Contains(s[i]))
            i++;
        if (i == start)
            throw new FormatException("expected a number at " + start);
        return s[start..i];
    }

    private static string Take(string s, ref int i, int n)
    {
        if (i + n > s.Length)
            throw new FormatException("unexpected end of input");
        string t = s.Substring(i, n);
        i += n;
        return t;
    }

    private static JsonNode? Real(string t)
    {
        if (!double.TryParse(t, NumberStyles.Float, CultureInfo.InvariantCulture, out double d))
            return t.ToLowerInvariant().TrimStart('+', '-') is "nan" or "inf" or "infinity" ? null : throw new FormatException("not a real: " + t);
        return double.IsFinite(d) ? JsonValue.Create(d) : null;
    }

    private static string Quoted(string s, ref int i)
    {
        char q = Next(s, ref i);
        if (q is not ('\'' or '"'))
            throw new FormatException("expected a quoted string at " + i);
        i++;
        var b = new StringBuilder();
        while (true)
        {
            if (i >= s.Length)
                throw new FormatException("unterminated string");
            char c = s[i++];
            if (c == q)
                return b.ToString();
            if (c != '\\')
            {
                b.Append(c);
                continue;
            }
            if (i >= s.Length)
                throw new FormatException("unterminated escape");
            char e = s[i++];
            switch (e)
            {
                case 'n': b.Append('\n'); break;
                case 't': b.Append('\t'); break;
                case 'r': b.Append('\r'); break;
                case 'a': b.Append('\a'); break;
                case 'b': b.Append('\b'); break;
                case 'f': b.Append('\f'); break;
                case 'v': b.Append('\v'); break;
                case 'x': b.Append((char)Convert.ToByte(Take(s, ref i, 2), 16)); break;
                default: b.Append(e); break;   // \\, \', \" and any other character stand for themselves
            }
        }
    }

    /// <summary>s(N)"..." : exactly N characters between the quotes, taken as they are.</summary>
    private static string Sized(string s, ref int i)
    {
        i++;   // 's'
        int n = Length(s, ref i);
        char q = Next(s, ref i);
        if (q is not ('\'' or '"'))
            throw new FormatException("expected a quote after a sized string's length at " + i);
        i++;
        string t = Take(s, ref i, n);
        if (i >= s.Length || s[i] != q)
            throw new FormatException("a sized string's closing quote is missing at " + i);
        i++;
        return t;
    }

    private static int Length(string s, ref int i)
    {
        Expect(s, ref i, '(');
        int n = int.Parse(Token(s, ref i, "0123456789"), CultureInfo.InvariantCulture);
        Expect(s, ref i, ')');
        if (n < 0 || n > s.Length)
            throw new FormatException("a length longer than the input");
        return n;
    }

    /// <summary>b16"hex", b64"base64" or b(N)"raw", as base64.</summary>
    private static string Binary(string s, ref int i)
    {
        i++;   // 'b'
        if (i < s.Length && s[i] == '(')
        {
            int n = Length(s, ref i);
            char q = Next(s, ref i);
            i++;
            var raw = Take(s, ref i, n);
            if (i >= s.Length || s[i] != q)
                throw new FormatException("a sized binary's closing quote is missing at " + i);
            i++;
            return Convert.ToBase64String(Encoding.Latin1.GetBytes(raw));
        }
        string radix = Token(s, ref i, "0123456789");
        string body = Quoted(s, ref i);
        return radix switch
        {
            "16" => Convert.ToBase64String(Convert.FromHexString(body)),
            "64" => Convert.ToBase64String(Convert.FromBase64String(body)),
            _ => throw new FormatException("binary base " + radix + " is not supported"),
        };
    }

    private static JsonNode Boolean(string s, ref int i)
    {
        foreach (var (word, value) in new[] { ("true", true), ("TRUE", true), ("false", false), ("FALSE", false), ("1", true), ("0", false), ("t", true), ("T", true), ("f", false), ("F", false) })
            if (string.CompareOrdinal(s, i, word, 0, word.Length) == 0)
            {
                i += word.Length;
                return JsonValue.Create(value);
            }
        throw new FormatException($"unexpected '{s[i]}' at {i}");
    }
}
