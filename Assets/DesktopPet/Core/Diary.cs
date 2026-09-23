using System;
using System.Collections.Generic;
using System.Globalization;

namespace DesktopPet.Core
{
    public sealed class DiaryEntry
    {
        public string Key;      // 같은 날 같은 Key는 한 번만 (반복 방지)
        public int Priority;    // 생활장면 3 > 누가 깨움/옮김 2 > 잠 1
        public string Time;     // "HH:mm"
        public string Text;
    }

    public sealed class DiaryDay
    {
        public string Date; // "yyyy-MM-dd"
        public readonly List<DiaryEntry> Entries = new List<DiaryEntry>();
    }

    /// <summary>
    /// 하루 최대 세 줄 일기. 실제로 끝까지 일어난 일만 기록한다(중단된 장면, 앱이 꺼져 있던 동안의 일은 쓰지 않음).
    /// 세 줄이 찼을 때 더 중요한 일이 생기면 가장 덜 중요한 줄(그중 가장 최근)을 밀어낸다.
    /// </summary>
    public sealed class DiaryBook
    {
        public const int MaxLinesPerDay = 3;
        public const int KeepDays = 30;

        private readonly List<DiaryDay> _days = new List<DiaryDay>();
        public IReadOnlyList<DiaryDay> Days { get { return _days; } }
        public int Version { get; private set; }

        public static string DateKey(DateTime local)
        {
            return local.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        }

        public DiaryDay GetDay(DateTime local)
        {
            return Find(DateKey(local));
        }

        public DiaryDay Find(string date)
        {
            for (int i = 0; i < _days.Count; i++) if (_days[i].Date == date) return _days[i];
            return null;
        }

        /// <summary>기록했으면 true.</summary>
        public bool Add(DateTime local, string key, int priority, string text)
        {
            if (string.IsNullOrEmpty(text)) return false;
            string date = DateKey(local);
            var day = Find(date);
            if (day == null)
            {
                day = new DiaryDay { Date = date };
                _days.Add(day);
                _days.Sort((a, b) => string.CompareOrdinal(a.Date, b.Date));
            }

            for (int i = 0; i < day.Entries.Count; i++)
                if (day.Entries[i].Key == key) return false; // 같은 일은 하루 한 줄

            var entry = new DiaryEntry
            {
                Key = key,
                Priority = priority,
                Time = local.ToString("HH:mm", CultureInfo.InvariantCulture),
                Text = text
            };

            if (day.Entries.Count >= MaxLinesPerDay)
            {
                int victim = -1;
                for (int i = 0; i < day.Entries.Count; i++)
                {
                    if (victim < 0 || day.Entries[i].Priority <= day.Entries[victim].Priority) victim = i;
                }
                if (victim < 0 || day.Entries[victim].Priority >= priority) return false;
                day.Entries.RemoveAt(victim);
            }

            day.Entries.Add(entry); // 시간 순서 유지 (항상 지금 시각에 추가됨)
            Prune(local);
            Version++;
            return true;
        }

        public void Prune(DateTime localToday)
        {
            string oldest = DateKey(localToday.Date.AddDays(-(KeepDays - 1)));
            int before = _days.Count;
            _days.RemoveAll(d => string.CompareOrdinal(d.Date, oldest) < 0);
            if (_days.Count != before) Version++;
        }

        public void LoadFrom(IEnumerable<DiaryDay> days)
        {
            _days.Clear();
            foreach (var d in days)
            {
                if (d == null || string.IsNullOrEmpty(d.Date)) continue;
                var copy = new DiaryDay { Date = d.Date };
                foreach (var e in d.Entries)
                {
                    if (e == null || string.IsNullOrEmpty(e.Text)) continue;
                    if (copy.Entries.Count >= MaxLinesPerDay) break;
                    copy.Entries.Add(e);
                }
                if (Find(copy.Date) == null) _days.Add(copy);
            }
            _days.Sort((a, b) => string.CompareOrdinal(a.Date, b.Date));
            Version++;
        }
    }
}
