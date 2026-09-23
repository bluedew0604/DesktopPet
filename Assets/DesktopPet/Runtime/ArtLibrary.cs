using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using UnityEngine;
using DesktopPet.Core;

namespace DesktopPet
{
    /// <summary>
    /// 그림 불러오기. StreamingAssets/Art 폴더의 PNG를 파일 이름 규칙으로 찾아서 쓴다.
    ///   캐릭터: {동작}_{번호}.png  (예: idle_01.png, walk_03.png)
    ///   소품:   prop_cushion.png, prop_cushion_messy.png, prop_snack.png, prop_ball.png
    ///   트레이: tray_icon.png
    /// 없는 동작은 비슷한 동작 그림으로 대신하고, 캐릭터 그림이 하나도 없으면 코드로 그린 임시 그림을 쓴다.
    /// 빌드된 게임 폴더(DesktopPet_Data/StreamingAssets/Art)에 넣어도 똑같이 동작한다.
    /// </summary>
    public sealed class ArtLibrary
    {
        public Sprite Cushion { get; private set; }
        public Sprite CushionMessy { get; private set; }
        public Sprite Snack { get; private set; }
        public Sprite Ball { get; private set; }
        public Texture2D TrayTexture { get; private set; }
        public Rect TrayRect { get; private set; }
        public bool UsingPlaceholderCharacter { get; private set; }
        public string Report { get; private set; }
        public string Folder { get; private set; }

        private readonly Dictionary<string, Sprite[]> _clips = new Dictionary<string, Sprite[]>();
        private readonly Dictionary<Sprite, Rect> _opaque = new Dictionary<Sprite, Rect>();
        private readonly List<Texture2D> _owned = new List<Texture2D>();
        private readonly List<Sprite> _sprites = new List<Sprite>();
        private readonly List<string> _pending = new List<string>();          // 아직 안 그린 임시 그림 동작
        private readonly List<Sprite> _pendingFrames = new List<Sprite>();    // 지금 그리는 중인 동작의 프레임

        /// <summary>임시 그림을 아직 그리는 중이면 true. 매 프레임 한 장씩 그린다(그동안 없는 동작은 기본 자세로 보임).</summary>
        public bool Loading { get { return _pending.Count > 0; } }

        public void ContinueLoading()
        {
            if (_pending.Count == 0) return;
            string clip = _pending[0];
            int n = PlaceholderArt.DefaultFrameCount(clip);
            _pendingFrames.Add(MakeSprite(PlaceholderArt.Character(clip, _pendingFrames.Count), new Vector2(0.5f, 0f)));
            if (_pendingFrames.Count >= n)
            {
                _clips[clip] = _pendingFrames.ToArray();
                _pendingFrames.Clear();
                _pending.RemoveAt(0);
            }
        }

        private static readonly Regex FramePattern = new Regex(@"^([a-z_]+?)_(\d+)$", RegexOptions.Compiled);

        /// <summary>동작별 대체 순서. 그 동작 그림이 없으면 앞에서부터 있는 걸 쓴다.</summary>
        private static readonly Dictionary<string, string[]> Fallbacks = new Dictionary<string, string[]>
        {
            { ClipNames.Look, new[] { ClipNames.Idle } },
            { ClipNames.Stretch, new[] { ClipNames.Idle } },
            { ClipNames.Walk, new[] { ClipNames.Idle } },
            { ClipNames.Doze, new[] { ClipNames.Sleep, ClipNames.Idle } },
            { ClipNames.Sleep, new[] { ClipNames.Doze, ClipNames.Idle } },
            { ClipNames.React, new[] { ClipNames.Idle } },
            { ClipNames.Drag, new[] { ClipNames.React, ClipNames.Idle } },
            { ClipNames.SnackCarry, new[] { ClipNames.Walk, ClipNames.Idle } },
            { ClipNames.SnackEat, new[] { ClipNames.Idle } },
            { ClipNames.CushionFix, new[] { ClipNames.Walk, ClipNames.Idle } },
            { ClipNames.BallPlay, new[] { ClipNames.Walk, ClipNames.Idle } },
        };

        public void Load()
        {
            Unload();
            Folder = Path.Combine(Application.streamingAssetsPath, "Art");
            var report = new StringBuilder();
            var userFrames = new Dictionary<string, SortedDictionary<int, Texture2D>>();
            var props = new Dictionary<string, Texture2D>();

            if (Directory.Exists(Folder))
            {
                foreach (var path in Directory.GetFiles(Folder, "*.png"))
                {
                    string name = Path.GetFileNameWithoutExtension(path).ToLowerInvariant();
                    if (name.StartsWith("prop_") || name == "tray_icon")
                    {
                        var t = LoadPng(path, report);
                        if (t != null) props[name] = t;
                        continue;
                    }
                    var m = FramePattern.Match(name);
                    if (!m.Success) { report.AppendLine("이름 규칙과 달라서 무시: " + Path.GetFileName(path)); continue; }
                    string clip = m.Groups[1].Value;
                    if (Array.IndexOf(ClipNames.All, clip) < 0) { report.AppendLine("모르는 동작 이름이라 무시: " + Path.GetFileName(path)); continue; }
                    var tex = LoadPng(path, report);
                    if (tex == null) continue;
                    SortedDictionary<int, Texture2D> frames;
                    if (!userFrames.TryGetValue(clip, out frames)) userFrames[clip] = frames = new SortedDictionary<int, Texture2D>();
                    frames[int.Parse(m.Groups[2].Value)] = tex;
                }
            }
            else
            {
                report.AppendLine("그림 폴더 없음: " + Folder);
            }

            // ---- 캐릭터 ----
            UsingPlaceholderCharacter = userFrames.Count == 0;
            var own = new Dictionary<string, Sprite[]>();
            _pending.Clear();
            if (UsingPlaceholderCharacter)
            {
                // 시작을 빠르게: 기본 자세만 지금 그리고, 나머지 동작은 ContinueLoading()에서 한 장씩 그린다.
                int n = PlaceholderArt.DefaultFrameCount(ClipNames.Idle);
                var idle = new Sprite[n];
                for (int i = 0; i < n; i++) idle[i] = MakeSprite(PlaceholderArt.Character(ClipNames.Idle, i), new Vector2(0.5f, 0f));
                own[ClipNames.Idle] = idle;
                foreach (var clip in ClipNames.All)
                    if (clip != ClipNames.Idle) _pending.Add(clip);
                _pendingFrames.Clear();
                report.AppendLine("캐릭터 그림이 없어 임시 그림 사용");
            }
            else
            {
                foreach (var kv in userFrames)
                {
                    var list = new List<Sprite>();
                    foreach (var f in kv.Value) list.Add(MakeSprite(f.Value, new Vector2(0.5f, 0f)));
                    own[kv.Key] = list.ToArray();
                    report.AppendLine("캐릭터 " + kv.Key + ": " + list.Count + "장");
                }
            }

            Sprite[] firstAvailable = null;
            foreach (var clip in ClipNames.All)
                if (own.ContainsKey(clip)) { firstAvailable = own[clip]; break; }

            foreach (var clip in ClipNames.All)
            {
                Sprite[] frames;
                if (own.TryGetValue(clip, out frames)) { _clips[clip] = frames; continue; }
                string[] chain;
                string used = null;
                if (Fallbacks.TryGetValue(clip, out chain))
                {
                    foreach (var alt in chain)
                        if (own.TryGetValue(alt, out frames)) { used = alt; break; }
                }
                if (frames == null) { frames = firstAvailable; used = "첫 번째 그림"; }
                _clips[clip] = frames;
                if (!UsingPlaceholderCharacter) report.AppendLine("캐릭터 " + clip + ": 그림 없음 → " + used + " 사용");
            }

            // ---- 소품 ----
            Cushion = PropSprite(props, "prop_cushion", () => PlaceholderArt.Cushion(false), report);
            CushionMessy = props.ContainsKey("prop_cushion_messy")
                ? PropSprite(props, "prop_cushion_messy", null, report)
                : (props.ContainsKey("prop_cushion") ? Cushion : MakeSprite(PlaceholderArt.Cushion(true), new Vector2(0.5f, 0f)));
            Snack = PropSprite(props, "prop_snack", PlaceholderArt.Snack, report);
            Ball = PropSprite(props, "prop_ball", PlaceholderArt.Ball, report);

            // ---- 트레이 아이콘 ----
            Texture2D tray;
            if (props.TryGetValue("tray_icon", out tray))
            {
                TrayTexture = tray;
                TrayRect = new Rect(0, 0, tray.width, tray.height);
                report.AppendLine("트레이 아이콘: tray_icon.png");
            }
            else
            {
                var idle = _clips[ClipNames.Idle][0];
                TrayTexture = idle.texture;
                TrayRect = GetOpaqueRect(idle);
            }

            Report = report.ToString();
        }

        public int FrameCount(string clip)
        {
            Sprite[] f;
            return _clips.TryGetValue(clip ?? ClipNames.Idle, out f) && f != null ? f.Length : 0;
        }

        public Sprite GetFrame(string clip, int index)
        {
            Sprite[] f;
            if (clip == null || !_clips.TryGetValue(clip, out f) || f == null || f.Length == 0)
                f = _clips[ClipNames.Idle];
            if (index < 0) index = 0;
            return f[index % f.Length];
        }

        /// <summary>그림에서 실제로 색이 칠해진 영역(픽셀 좌표). 클릭 판정에 쓴다.</summary>
        public Rect GetOpaqueRect(Sprite s)
        {
            Rect r;
            if (s != null && _opaque.TryGetValue(s, out r)) return r;
            return s != null ? s.rect : new Rect(0, 0, 1, 1);
        }

        public void Unload()
        {
            foreach (var sp in _sprites) if (sp != null) UnityEngine.Object.Destroy(sp);
            _sprites.Clear();
            foreach (var t in _owned) if (t != null) UnityEngine.Object.Destroy(t);
            _owned.Clear();
            _clips.Clear();
            _opaque.Clear();
        }

        // ------------------------------------------------------------

        private Sprite PropSprite(Dictionary<string, Texture2D> props, string key, Func<Texture2D> placeholder, StringBuilder report)
        {
            Texture2D t;
            if (props.TryGetValue(key, out t))
            {
                report.AppendLine("소품 " + key + ": 그림 사용");
                return MakeSprite(t, new Vector2(0.5f, 0f));
            }
            return placeholder != null ? MakeSprite(placeholder(), new Vector2(0.5f, 0f)) : null;
        }

        private Texture2D LoadPng(string path, StringBuilder report)
        {
            try
            {
                var tex = new Texture2D(2, 2, TextureFormat.RGBA32, true);
                if (!tex.LoadImage(File.ReadAllBytes(path), false))
                {
                    UnityEngine.Object.Destroy(tex);
                    report.AppendLine("PNG를 읽지 못함: " + Path.GetFileName(path));
                    return null;
                }
                tex.name = Path.GetFileNameWithoutExtension(path);
                _owned.Add(tex);
                BleedAlphaEdges(tex);
                tex.filterMode = FilterMode.Trilinear;
                tex.wrapMode = TextureWrapMode.Clamp;
                tex.Apply(true, false);
                return tex;
            }
            catch (Exception ex)
            {
                report.AppendLine("PNG 읽기 오류 " + Path.GetFileName(path) + ": " + ex.Message);
                return null;
            }
        }

        private Sprite MakeSprite(Texture2D tex, Vector2 pivot)
        {
            if (!_owned.Contains(tex)) _owned.Add(tex);
            var s = Sprite.Create(tex, new Rect(0, 0, tex.width, tex.height), pivot, 1f, 0, SpriteMeshType.FullRect);
            s.name = tex.name;
            _sprites.Add(s);
            _opaque[s] = ComputeOpaqueRect(tex);
            return s;
        }

        private static Rect ComputeOpaqueRect(Texture2D tex)
        {
            var px = tex.GetPixels32();
            int w = tex.width, h = tex.height;
            int minX = w, minY = h, maxX = -1, maxY = -1;
            for (int y = 0; y < h; y++)
            {
                int row = y * w;
                for (int x = 0; x < w; x++)
                {
                    if (px[row + x].a > 25)
                    {
                        if (x < minX) minX = x;
                        if (x > maxX) maxX = x;
                        if (y < minY) minY = y;
                        if (y > maxY) maxY = y;
                    }
                }
            }
            if (maxX < 0) return new Rect(0, 0, w, h);
            return new Rect(minX, minY, maxX - minX + 1, maxY - minY + 1);
        }

        /// <summary>
        /// 투명한 픽셀의 색을 이웃 불투명 픽셀 색으로 채운다(알파는 그대로, 가장자리에서 4픽셀까지).
        /// 축소 표시할 때 가장자리에 검은/흰 테두리가 생기는 것을 막는다. 가장자리만 훑어서 빠르다.
        /// </summary>
        public static void BleedAlphaEdges(Texture2D tex)
        {
            var px = tex.GetPixels32();
            int w = tex.width, h = tex.height, n = px.Length;
            var filled = new bool[n];
            var queued = new bool[n];
            for (int i = 0; i < n; i++) filled[i] = px[i].a > 0;

            var frontier = new List<int>();
            for (int y = 0; y < h; y++)
            {
                for (int x = 0; x < w; x++)
                {
                    int i = y * w + x;
                    if (filled[i]) continue;
                    if ((x > 0 && filled[i - 1]) || (x < w - 1 && filled[i + 1]) || (y > 0 && filled[i - w]) || (y < h - 1 && filled[i + w]))
                    {
                        frontier.Add(i);
                        queued[i] = true;
                    }
                }
            }

            var colors = new List<Color32>();
            var next = new List<int>();
            for (int ring = 0; ring < 4 && frontier.Count > 0; ring++)
            {
                colors.Clear();
                foreach (int i in frontier)
                {
                    int x = i % w, y = i / w, r = 0, g = 0, b = 0, c = 0;
                    for (int oy = -1; oy <= 1; oy++)
                    {
                        int yy = y + oy;
                        if (yy < 0 || yy >= h) continue;
                        for (int ox = -1; ox <= 1; ox++)
                        {
                            int xx = x + ox;
                            if (xx < 0 || xx >= w) continue;
                            int j = yy * w + xx;
                            if (!filled[j]) continue;
                            r += px[j].r; g += px[j].g; b += px[j].b; c++;
                        }
                    }
                    colors.Add(c == 0 ? px[i] : new Color32((byte)(r / c), (byte)(g / c), (byte)(b / c), 0));
                }
                for (int k = 0; k < frontier.Count; k++) { px[frontier[k]] = colors[k]; filled[frontier[k]] = true; }

                next.Clear();
                foreach (int i in frontier)
                {
                    int x = i % w, y = i / w;
                    if (x > 0) Enqueue(i - 1, filled, queued, next);
                    if (x < w - 1) Enqueue(i + 1, filled, queued, next);
                    if (y > 0) Enqueue(i - w, filled, queued, next);
                    if (y < h - 1) Enqueue(i + w, filled, queued, next);
                }
                var t = frontier; frontier = next; next = t;
            }
            tex.SetPixels32(px);
        }

        private static void Enqueue(int i, bool[] filled, bool[] queued, List<int> list)
        {
            if (filled[i] || queued[i]) return;
            queued[i] = true;
            list.Add(i);
        }
    }
}
