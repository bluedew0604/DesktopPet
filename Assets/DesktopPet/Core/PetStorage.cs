using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;

namespace DesktopPet.Core
{
    public sealed class PetSettings
    {
        public bool Quiet;
        public bool IdleHint;          // 선택 기능: 마지막 입력 이후 경과시간 참고 (기본 꺼짐)
        public bool CushionMessy;
        public bool HasPosition;
        public double PosFromRight;    // 작업영역 오른쪽 끝 ~ 창 오른쪽 끝 (논리 px)
        public double PosFromBottom;   // 작업영역 아래쪽 끝 ~ 창 아래쪽 끝 (논리 px)
    }

    /// <summary>설정/일기를 JSON으로 변환. 모르는 값이나 깨진 값은 기본값으로 둔다.</summary>
    public static class PetStorage
    {
        public const int FormatVersion = 1;

        public static string SettingsToJson(PetSettings s)
        {
            var d = new Dictionary<string, object>
            {
                { "version", FormatVersion },
                { "quiet", s.Quiet },
                { "idleHint", s.IdleHint },
                { "cushionMessy", s.CushionMessy },
                { "hasPosition", s.HasPosition },
                { "posFromRight", s.PosFromRight },
                { "posFromBottom", s.PosFromBottom }
            };
            return MiniJson.Serialize(d);
        }

        public static PetSettings SettingsFromJson(string json)
        {
            var d = MiniJson.Parse(json) as Dictionary<string, object>;
            if (d == null) throw new FormatException("settings root is not an object");
            return new PetSettings
            {
                Quiet = GetBool(d, "quiet"),
                IdleHint = GetBool(d, "idleHint"),
                CushionMessy = GetBool(d, "cushionMessy"),
                HasPosition = GetBool(d, "hasPosition"),
                PosFromRight = GetNum(d, "posFromRight"),
                PosFromBottom = GetNum(d, "posFromBottom")
            };
        }

        public static string DiaryToJson(DiaryBook book)
        {
            var days = new List<object>();
            foreach (var day in book.Days)
            {
                var entries = new List<object>();
                foreach (var e in day.Entries)
                {
                    entries.Add(new Dictionary<string, object>
                    {
                        { "key", e.Key ?? "" },
                        { "priority", e.Priority },
                        { "time", e.Time ?? "" },
                        { "text", e.Text ?? "" }
                    });
                }
                days.Add(new Dictionary<string, object> { { "date", day.Date }, { "entries", entries } });
            }
            return MiniJson.Serialize(new Dictionary<string, object> { { "version", FormatVersion }, { "days", days } });
        }

        public static List<DiaryDay> DiaryFromJson(string json)
        {
            var root = MiniJson.Parse(json) as Dictionary<string, object>;
            if (root == null) throw new FormatException("diary root is not an object");
            var result = new List<DiaryDay>();
            object daysObj;
            if (!root.TryGetValue("days", out daysObj)) return result;
            var days = daysObj as List<object>;
            if (days == null) throw new FormatException("days is not an array");
            foreach (var o in days)
            {
                var dd = o as Dictionary<string, object>;
                if (dd == null) continue;
                string date = GetStr(dd, "date");
                DateTime parsed;
                if (!DateTime.TryParseExact(date, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out parsed)) continue;
                var day = new DiaryDay { Date = date };
                object entsObj;
                if (dd.TryGetValue("entries", out entsObj) && entsObj is List<object>)
                {
                    foreach (var eo in (List<object>)entsObj)
                    {
                        var ed = eo as Dictionary<string, object>;
                        if (ed == null) continue;
                        string text = GetStr(ed, "text");
                        if (string.IsNullOrEmpty(text)) continue;
                        day.Entries.Add(new DiaryEntry
                        {
                            Key = GetStr(ed, "key"),
                            Priority = (int)GetNum(ed, "priority"),
                            Time = GetStr(ed, "time"),
                            Text = text
                        });
                    }
                }
                result.Add(day);
            }
            return result;
        }

        private static bool GetBool(Dictionary<string, object> d, string k)
        {
            object v;
            return d.TryGetValue(k, out v) && v is bool && (bool)v;
        }

        private static double GetNum(Dictionary<string, object> d, string k)
        {
            object v;
            if (d.TryGetValue(k, out v) && v is double)
            {
                double x = (double)v;
                if (!double.IsNaN(x) && !double.IsInfinity(x)) return x;
            }
            return 0;
        }

        private static string GetStr(Dictionary<string, object> d, string k)
        {
            object v;
            return d.TryGetValue(k, out v) ? v as string : null;
        }
    }

    /// <summary>
    /// 안전한 파일 저장: 임시파일에 먼저 쓰고 교체(직전 파일은 .bak으로 보관).
    /// 읽을 때 본 파일이 깨졌으면 .bak으로 복구하고, 깨진 파일은 .corrupt로 옮겨 둔다.
    /// 어떤 경우에도 예외를 밖으로 던지지 않는다(저장 실패가 앱 전체를 멈추지 않게).
    /// </summary>
    public static class SafeFile
    {
        private static readonly Encoding Utf8NoBom = new UTF8Encoding(false);

        public static bool TryWriteAtomic(string path, string content, out string error)
        {
            error = null;
            string tmp = path + ".tmp";
            string bak = path + ".bak";
            try
            {
                string dir = Path.GetDirectoryName(path);
                if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

                using (var fs = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None))
                {
                    var bytes = Utf8NoBom.GetBytes(content);
                    fs.Write(bytes, 0, bytes.Length);
                    fs.Flush(true);
                }

                if (File.Exists(path))
                {
                    try
                    {
                        File.Replace(tmp, path, bak);
                    }
                    catch (Exception)
                    {
                        File.Copy(path, bak, true);
                        File.Copy(tmp, path, true);
                        File.Delete(tmp);
                    }
                }
                else
                {
                    File.Move(tmp, path);
                }
                return true;
            }
            catch (Exception ex)
            {
                error = ex.GetType().Name + ": " + ex.Message;
                try { if (File.Exists(tmp)) File.Delete(tmp); } catch (Exception) { }
                return false;
            }
        }

        /// <summary>validate가 예외 없이 통과하는 내용을 돌려준다. 없으면 null.</summary>
        public static string ReadWithRecovery(string path, Action<string> validate, out string note)
        {
            note = null;
            string primary = TryRead(path, validate);
            if (primary != null) return primary;

            bool primaryExisted = File.Exists(path);
            string backup = TryRead(path + ".bak", validate);

            if (primaryExisted)
            {
                try
                {
                    string stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
                    File.Move(path, path + ".corrupt-" + stamp);
                    note = "본 파일이 손상되어 " + (backup != null ? "백업으로 복구함" : "기본값으로 시작함");
                }
                catch (Exception ex)
                {
                    note = "손상 파일 이동 실패: " + ex.Message;
                }
            }
            else if (backup != null)
            {
                note = "본 파일이 없어 백업으로 복구함";
            }
            return backup;
        }

        private static string TryRead(string p, Action<string> validate)
        {
            try
            {
                if (!File.Exists(p)) return null;
                string text = File.ReadAllText(p, Utf8NoBom);
                validate(text);
                return text;
            }
            catch (Exception)
            {
                return null;
            }
        }
    }
}
