using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace PokeLab.Intelligence.Data
{
    /// <summary>
    /// A small read-only JSON reader.
    ///
    /// UnityEngine.JsonUtility would cover most of this, but it cannot represent an
    /// absent value distinctly from a default, refuses top-level arrays, and — the reason
    /// that actually decided it — only exists inside the player. Keeping the parser plain
    /// C# lets the export artefacts be verified against Python from a console harness
    /// without a Unity install, which is how the forest port is proven correct.
    /// </summary>
    public sealed class JsonValue
    {
        public enum Kind { Null, Bool, Number, String, Array, Object }

        public Kind Type { get; private set; }

        private bool _bool;
        private double _number;
        private string _string;
        private List<JsonValue> _array;
        private Dictionary<string, JsonValue> _object;

        private static readonly JsonValue NullValue = new JsonValue { Type = Kind.Null };

        public int Count => _array?.Count ?? _object?.Count ?? 0;

        /// <summary>Missing keys yield a null value rather than throwing, so optional
        /// fields read naturally as <c>value["Maybe"].AsInt(0)</c>.</summary>
        public JsonValue this[string key]
        {
            get
            {
                if (_object != null && _object.TryGetValue(key, out var found)) return found;
                return NullValue;
            }
        }

        public JsonValue this[int index] =>
            _array != null && index >= 0 && index < _array.Count ? _array[index] : NullValue;

        public bool IsNull => Type == Kind.Null;

        public bool AsBool(bool fallback = false) => Type switch
        {
            Kind.Bool => _bool,
            Kind.Number => _number != 0d,
            _ => fallback,
        };

        public double AsDouble(double fallback = 0d) => Type == Kind.Number ? _number : fallback;
        public float AsFloat(float fallback = 0f) => Type == Kind.Number ? (float)_number : fallback;
        public int AsInt(int fallback = 0) => Type == Kind.Number ? (int)Math.Round(_number) : fallback;
        public string AsString(string fallback = null) => Type == Kind.String ? _string : fallback;

        public IReadOnlyList<JsonValue> AsArray() => _array ?? (IReadOnlyList<JsonValue>)Array.Empty<JsonValue>();

        public int[] AsIntArray()
        {
            if (_array == null) return Array.Empty<int>();
            var result = new int[_array.Count];
            for (var i = 0; i < result.Length; i++) result[i] = _array[i].AsInt();
            return result;
        }

        public float[] AsFloatArray()
        {
            if (_array == null) return Array.Empty<float>();
            var result = new float[_array.Count];
            for (var i = 0; i < result.Length; i++) result[i] = _array[i].AsFloat();
            return result;
        }

        public string[] AsStringArray()
        {
            if (_array == null) return Array.Empty<string>();
            var result = new string[_array.Count];
            for (var i = 0; i < result.Length; i++) result[i] = _array[i].AsString(string.Empty);
            return result;
        }

        public static JsonValue Parse(string text)
        {
            if (string.IsNullOrEmpty(text)) throw new FormatException("empty JSON document");
            var index = 0;
            // A UTF-8 BOM survives some editors and decoders; skip it rather than fail on
            // it. Written as an escape because the raw character is invisible in a diff.
            if (text[0] == '\uFEFF') index = 1;
            var value = ParseValue(text, ref index);
            SkipWhitespace(text, ref index);
            if (index != text.Length) throw new FormatException($"trailing JSON content at {index}");
            return value;
        }

        private static JsonValue ParseValue(string text, ref int index)
        {
            SkipWhitespace(text, ref index);
            if (index >= text.Length) throw new FormatException("unexpected end of JSON");

            switch (text[index])
            {
                case '{': return ParseObject(text, ref index);
                case '[': return ParseArray(text, ref index);
                case '"': return new JsonValue { Type = Kind.String, _string = ParseString(text, ref index) };
                case 't': Expect(text, ref index, "true"); return new JsonValue { Type = Kind.Bool, _bool = true };
                case 'f': Expect(text, ref index, "false"); return new JsonValue { Type = Kind.Bool, _bool = false };
                case 'n': Expect(text, ref index, "null"); return NullValue;
                default: return ParseNumber(text, ref index);
            }
        }

        private static JsonValue ParseObject(string text, ref int index)
        {
            var result = new JsonValue { Type = Kind.Object, _object = new Dictionary<string, JsonValue>(StringComparer.Ordinal) };
            index++; // '{'
            SkipWhitespace(text, ref index);
            if (index < text.Length && text[index] == '}') { index++; return result; }

            while (true)
            {
                SkipWhitespace(text, ref index);
                var key = ParseString(text, ref index);
                SkipWhitespace(text, ref index);
                if (index >= text.Length || text[index] != ':') throw new FormatException($"expected ':' at {index}");
                index++;
                result._object[key] = ParseValue(text, ref index);
                SkipWhitespace(text, ref index);
                if (index >= text.Length) throw new FormatException("unterminated object");
                if (text[index] == ',') { index++; continue; }
                if (text[index] == '}') { index++; return result; }
                throw new FormatException($"expected ',' or '}}' at {index}");
            }
        }

        private static JsonValue ParseArray(string text, ref int index)
        {
            var result = new JsonValue { Type = Kind.Array, _array = new List<JsonValue>() };
            index++; // '['
            SkipWhitespace(text, ref index);
            if (index < text.Length && text[index] == ']') { index++; return result; }

            while (true)
            {
                result._array.Add(ParseValue(text, ref index));
                SkipWhitespace(text, ref index);
                if (index >= text.Length) throw new FormatException("unterminated array");
                if (text[index] == ',') { index++; continue; }
                if (text[index] == ']') { index++; return result; }
                throw new FormatException($"expected ',' or ']' at {index}");
            }
        }

        private static JsonValue ParseNumber(string text, ref int index)
        {
            var start = index;
            if (index < text.Length && (text[index] == '-' || text[index] == '+')) index++;
            while (index < text.Length)
            {
                var c = text[index];
                if (char.IsDigit(c) || c == '.' || c == 'e' || c == 'E' || c == '+' || c == '-') index++;
                else break;
            }
            var span = text.Substring(start, index - start);
            if (!double.TryParse(span, NumberStyles.Float, CultureInfo.InvariantCulture, out var number))
                throw new FormatException($"bad number '{span}' at {start}");
            return new JsonValue { Type = Kind.Number, _number = number };
        }

        private static string ParseString(string text, ref int index)
        {
            if (index >= text.Length || text[index] != '"') throw new FormatException($"expected string at {index}");
            index++;
            var builder = new StringBuilder();
            while (index < text.Length)
            {
                var c = text[index++];
                if (c == '"') return builder.ToString();
                if (c != '\\') { builder.Append(c); continue; }

                if (index >= text.Length) break;
                var escape = text[index++];
                switch (escape)
                {
                    case '"': builder.Append('"'); break;
                    case '\\': builder.Append('\\'); break;
                    case '/': builder.Append('/'); break;
                    case 'b': builder.Append('\b'); break;
                    case 'f': builder.Append('\f'); break;
                    case 'n': builder.Append('\n'); break;
                    case 'r': builder.Append('\r'); break;
                    case 't': builder.Append('\t'); break;
                    case 'u':
                        if (index + 4 > text.Length) throw new FormatException("truncated \\u escape");
                        builder.Append((char)ushort.Parse(text.Substring(index, 4), NumberStyles.HexNumber,
                                                         CultureInfo.InvariantCulture));
                        index += 4;
                        break;
                    default: throw new FormatException($"unknown escape '\\{escape}'");
                }
            }
            throw new FormatException("unterminated string");
        }

        private static void Expect(string text, ref int index, string literal)
        {
            if (index + literal.Length > text.Length || string.CompareOrdinal(text, index, literal, 0, literal.Length) != 0)
                throw new FormatException($"expected '{literal}' at {index}");
            index += literal.Length;
        }

        private static void SkipWhitespace(string text, ref int index)
        {
            while (index < text.Length)
            {
                var c = text[index];
                if (c == ' ' || c == '\t' || c == '\n' || c == '\r') index++;
                else break;
            }
        }
    }
}
