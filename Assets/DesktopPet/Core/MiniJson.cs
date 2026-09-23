using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace DesktopPet.Core
{
    /// <summary>
    /// 저장 파일용 최소 JSON 읽기/쓰기. (Unity JsonUtility에 의존하지 않아 엔진 밖에서도 테스트 가능)
    /// 객체 → Dictionary&lt;string, object&gt;, 배열 → List&lt;object&gt;, 숫자 → double.
    /// </summary>
    public static class MiniJson
    {
        public static object Parse(string json)
        {
            if (json == null) throw new FormatException("null json");
            var p = new Parser(json);
            object v = p.ParseValue();
            p.SkipWs();
            if (!p.End) throw new FormatException("trailing characters at " + p.Pos);
            return v;
        }

        public static string Serialize(object value)
        {
            var sb = new StringBuilder();
            Write(sb, value, 0);
            return sb.ToString();
        }

        private static void Write(StringBuilder sb, object v, int indent)
        {
            if (v == null) { sb.Append("null"); return; }
            var s = v as string;
            if (s != null) { WriteString(sb, s); return; }
            if (v is bool) { sb.Append((bool)v ? "true" : "false"); return; }
            if (v is double || v is float || v is int || v is long)
            {
                double d = Convert.ToDouble(v, CultureInfo.InvariantCulture);
                if (double.IsNaN(d) || double.IsInfinity(d)) d = 0;
                sb.Append(d.ToString("R", CultureInfo.InvariantCulture));
                return;
            }
            var dict = v as IDictionary<string, object>;
            if (dict != null)
            {
                sb.Append('{');
                bool first = true;
                foreach (var kv in dict)
                {
                    if (!first) sb.Append(',');
                    first = false;
                    sb.Append('\n').Append(' ', (indent + 1) * 2);
                    WriteString(sb, kv.Key);
                    sb.Append(": ");
                    Write(sb, kv.Value, indent + 1);
                }
                if (!first) sb.Append('\n').Append(' ', indent * 2);
                sb.Append('}');
                return;
            }
            var list = v as System.Collections.IEnumerable;
            if (list != null)
            {
                sb.Append('[');
                bool first = true;
                foreach (var item in list)
                {
                    if (!first) sb.Append(',');
                    first = false;
                    sb.Append('\n').Append(' ', (indent + 1) * 2);
                    Write(sb, item, indent + 1);
                }
                if (!first) sb.Append('\n').Append(' ', indent * 2);
                sb.Append(']');
                return;
            }
            WriteString(sb, v.ToString());
        }

        private static void WriteString(StringBuilder sb, string s)
        {
            sb.Append('"');
            foreach (char c in s)
            {
                switch (c)
                {
                    case '"': sb.Append("\\\""); break;
                    case '\\': sb.Append("\\\\"); break;
                    case '\n': sb.Append("\\n"); break;
                    case '\r': sb.Append("\\r"); break;
                    case '\t': sb.Append("\\t"); break;
                    default:
                        if (c < 0x20) sb.Append("\\u").Append(((int)c).ToString("x4"));
                        else sb.Append(c);
                        break;
                }
            }
            sb.Append('"');
        }

        private sealed class Parser
        {
            private readonly string _s;
            public int Pos;
            public Parser(string s) { _s = s; }
            public bool End { get { return Pos >= _s.Length; } }

            public void SkipWs()
            {
                while (Pos < _s.Length && char.IsWhiteSpace(_s[Pos])) Pos++;
            }

            public object ParseValue()
            {
                SkipWs();
                if (End) throw new FormatException("unexpected end");
                char c = _s[Pos];
                if (c == '{') return ParseObject();
                if (c == '[') return ParseArray();
                if (c == '"') return ParseString();
                if (c == 't') { Expect("true"); return true; }
                if (c == 'f') { Expect("false"); return false; }
                if (c == 'n') { Expect("null"); return null; }
                return ParseNumber();
            }

            private void Expect(string word)
            {
                if (string.CompareOrdinal(_s, Pos, word, 0, word.Length) != 0) throw new FormatException("bad literal at " + Pos);
                Pos += word.Length;
            }

            private Dictionary<string, object> ParseObject()
            {
                var d = new Dictionary<string, object>();
                Pos++; SkipWs();
                if (!End && _s[Pos] == '}') { Pos++; return d; }
                while (true)
                {
                    SkipWs();
                    if (End || _s[Pos] != '"') throw new FormatException("expected key at " + Pos);
                    string key = ParseString();
                    SkipWs();
                    if (End || _s[Pos] != ':') throw new FormatException("expected ':' at " + Pos);
                    Pos++;
                    d[key] = ParseValue();
                    SkipWs();
                    if (End) throw new FormatException("unterminated object");
                    if (_s[Pos] == ',') { Pos++; continue; }
                    if (_s[Pos] == '}') { Pos++; return d; }
                    throw new FormatException("expected ',' or '}' at " + Pos);
                }
            }

            private List<object> ParseArray()
            {
                var l = new List<object>();
                Pos++; SkipWs();
                if (!End && _s[Pos] == ']') { Pos++; return l; }
                while (true)
                {
                    l.Add(ParseValue());
                    SkipWs();
                    if (End) throw new FormatException("unterminated array");
                    if (_s[Pos] == ',') { Pos++; continue; }
                    if (_s[Pos] == ']') { Pos++; return l; }
                    throw new FormatException("expected ',' or ']' at " + Pos);
                }
            }

            private string ParseString()
            {
                var sb = new StringBuilder();
                Pos++;
                while (true)
                {
                    if (End) throw new FormatException("unterminated string");
                    char c = _s[Pos++];
                    if (c == '"') return sb.ToString();
                    if (c != '\\') { sb.Append(c); continue; }
                    if (End) throw new FormatException("bad escape");
                    char e = _s[Pos++];
                    switch (e)
                    {
                        case '"': sb.Append('"'); break;
                        case '\\': sb.Append('\\'); break;
                        case '/': sb.Append('/'); break;
                        case 'b': sb.Append('\b'); break;
                        case 'f': sb.Append('\f'); break;
                        case 'n': sb.Append('\n'); break;
                        case 'r': sb.Append('\r'); break;
                        case 't': sb.Append('\t'); break;
                        case 'u':
                            if (Pos + 4 > _s.Length) throw new FormatException("bad unicode escape");
                            sb.Append((char)int.Parse(_s.Substring(Pos, 4), NumberStyles.HexNumber, CultureInfo.InvariantCulture));
                            Pos += 4;
                            break;
                        default: throw new FormatException("bad escape char");
                    }
                }
            }

            private double ParseNumber()
            {
                int start = Pos;
                while (Pos < _s.Length && "+-0123456789.eE".IndexOf(_s[Pos]) >= 0) Pos++;
                if (Pos == start) throw new FormatException("unexpected char at " + Pos);
                return double.Parse(_s.Substring(start, Pos - start), NumberStyles.Float, CultureInfo.InvariantCulture);
            }
        }
    }
}
