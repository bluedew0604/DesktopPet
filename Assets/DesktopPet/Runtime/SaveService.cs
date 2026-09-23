using System;
using System.IO;
using System.Text;
using UnityEngine;
using DesktopPet.Core;

namespace DesktopPet
{
    /// <summary>
    /// 초 단위 단조 증가 시계. 절전(PC 잠자기) 중에는 멈추는 타이머가 있어서,
    /// 벽시계가 크게 건너뛴 만큼을 더해 "긴 공백"을 엔진이 알아챌 수 있게 한다.
    /// </summary>
    public sealed class HostClock
    {
        private bool _init;
        private double _lastMono, _offset;
        private DateTime _lastWall;

        public double Now()
        {
            double mono = Time.realtimeSinceStartupAsDouble;
            DateTime wall = DateTime.UtcNow;
            if (_init)
            {
                double dm = mono - _lastMono;
                double dw = (wall - _lastWall).TotalSeconds;
                if (dw - dm > 10.0) _offset += dw - dm;
            }
            _init = true;
            _lastMono = mono;
            _lastWall = wall;
            return mono + _offset;
        }
    }

    /// <summary>설정/일기 저장. 실패해도 게임은 계속 돈다(다음 저장 때 다시 시도).</summary>
    public sealed class SaveService
    {
        public string Folder { get; private set; }
        public string LastError { get; private set; }
        public string LastNote { get; private set; }

        private readonly string _settingsPath, _diaryPath, _diaryTextPath;

        public SaveService(string folder)
        {
            Folder = folder;
            _settingsPath = Path.Combine(folder, "settings.json");
            _diaryPath = Path.Combine(folder, "diary.json");
            _diaryTextPath = Path.Combine(folder, "일기.txt");
        }

        public PetSettings LoadSettings(out bool firstRun)
        {
            firstRun = !File.Exists(_settingsPath) && !File.Exists(_settingsPath + ".bak");
            string note;
            string text = SafeFile.ReadWithRecovery(_settingsPath, s => PetStorage.SettingsFromJson(s), out note);
            if (note != null) { LastNote = "settings: " + note; Debug.LogWarning("[DesktopPet] " + LastNote); }
            return text != null ? PetStorage.SettingsFromJson(text) : new PetSettings();
        }

        public DiaryBook LoadDiary()
        {
            var book = new DiaryBook();
            string note;
            string text = SafeFile.ReadWithRecovery(_diaryPath, s => PetStorage.DiaryFromJson(s), out note);
            if (note != null) { LastNote = "diary: " + note; Debug.LogWarning("[DesktopPet] " + LastNote); }
            if (text != null) book.LoadFrom(PetStorage.DiaryFromJson(text));
            book.Prune(DateTime.Now);
            return book;
        }

        public bool SaveSettings(PetSettings s)
        {
            string err;
            bool ok = SafeFile.TryWriteAtomic(_settingsPath, PetStorage.SettingsToJson(s), out err);
            if (!ok) { LastError = err; Debug.LogWarning("[DesktopPet] 설정 저장 실패: " + err); }
            return ok;
        }

        public bool SaveDiary(DiaryBook book)
        {
            string err;
            bool ok = SafeFile.TryWriteAtomic(_diaryPath, PetStorage.DiaryToJson(book), out err);
            if (!ok) { LastError = err; Debug.LogWarning("[DesktopPet] 일기 저장 실패: " + err); return false; }
            SafeFile.TryWriteAtomic(_diaryTextPath, ToReadableText(book), out err); // 사람이 읽기 편한 사본
            return true;
        }

        public static string ToReadableText(DiaryBook book)
        {
            var sb = new StringBuilder();
            for (int i = book.Days.Count - 1; i >= 0; i--)
            {
                var day = book.Days[i];
                sb.AppendLine("[" + day.Date + "]");
                foreach (var e in day.Entries) sb.AppendLine("  " + e.Time + "  " + e.Text);
                sb.AppendLine();
            }
            return sb.ToString();
        }
    }
}
