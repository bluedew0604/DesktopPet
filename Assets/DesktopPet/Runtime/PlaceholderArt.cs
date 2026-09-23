using System;
using UnityEngine;
using DesktopPet.Core;

namespace DesktopPet
{
    /// <summary>
    /// 그림이 없을 때 쓰는 임시 그림을 코드로 그린다(동글동글한 모찌 캐릭터 + 소품).
    /// 실제 그림 파일을 넣으면 그 동작부터 자동으로 교체된다.
    /// </summary>
    public static class PlaceholderArt
    {
        public const int CharSize = 256;

        private struct Pose
        {
            public float Cx, Cy, Rx, Ry, Tilt;
            public int Eyes;         // 0 뜸, 1 감음, 2 반쯤, 3 동그랗게 놀람
            public float LookX, LookY;
            public int Mouth;        // 0 없음, 1 작은 입, 2 벌린 입(o), 3 오물오물
            public float FootL, FootR, FootY;
            public bool Paw;
            public float PawX, PawY;
            public float Cos, Sin; // 기울기 (미리 계산)
        }

        private static readonly Color Body = new Color(0.97f, 0.91f, 0.80f, 1f);
        private static readonly Color Line = new Color(0.45f, 0.34f, 0.27f, 1f);
        private static readonly Color Eye = new Color(0.20f, 0.15f, 0.13f, 1f);
        private static readonly Color Blush = new Color(0.95f, 0.58f, 0.56f, 0.55f);
        private static readonly Color MouthCol = new Color(0.45f, 0.20f, 0.18f, 1f);

        public static int DefaultFrameCount(string clip)
        {
            switch (clip)
            {
                case ClipNames.Stretch: return 3;
                case ClipNames.Walk: return 4;
                case ClipNames.Drag: return 1;
                case ClipNames.CushionFix: return 3;
                case ClipNames.BallPlay: return 3;
                default: return 2;
            }
        }

        public static Texture2D Character(string clip, int frame)
        {
            var p = PoseFor(clip, frame);
            var px = new Color[CharSize * CharSize];

            // 그릴 부분만 계산 (몸·발·앞발을 감싸는 사각형). 나머지는 투명.
            float r = Mathf.Max(p.Rx, p.Ry) + 8f;
            float minX = p.Cx - r, maxX = p.Cx + r, minY = p.Cy - r, maxY = p.Cy + r;
            minX = Mathf.Min(minX, p.Cx - 60f); maxX = Mathf.Max(maxX, p.Cx + 60f);
            minY = Mathf.Min(minY, p.FootY - 16f); maxY = Mathf.Max(maxY, p.FootY + Mathf.Max(p.FootL, p.FootR) + 16f);
            if (p.Paw)
            {
                float reach = Mathf.Sqrt(p.PawX * p.PawX + p.PawY * p.PawY) + 20f;
                minX = Mathf.Min(minX, p.Cx - reach); maxX = Mathf.Max(maxX, p.Cx + reach);
                minY = Mathf.Min(minY, p.Cy - reach); maxY = Mathf.Max(maxY, p.Cy + reach);
            }
            int x0 = Mathf.Clamp((int)minX, 0, CharSize), x1 = Mathf.Clamp((int)maxX + 1, 0, CharSize);
            int y0 = Mathf.Clamp((int)minY, 0, CharSize), y1 = Mathf.Clamp((int)maxY + 1, 0, CharSize);

            for (int y = y0; y < y1; y++)
                for (int x = x0; x < x1; x++)
                    px[y * CharSize + x] = Shade(p, x + 0.5f, y + 0.5f);

            return MakeTexture(px, CharSize, CharSize, "placeholder_" + clip + "_" + frame, Line);
        }

        private static Pose Base()
        {
            return new Pose { Cx = 128, Cy = 96, Rx = 76, Ry = 68, FootY = 16 };
        }

        private static Pose PoseFor(string clip, int f)
        {
            var p = Base();
            switch (clip)
            {
                case ClipNames.Idle:
                    if (f % 2 == 1) { p.Ry *= 0.975f; p.Rx *= 1.015f; p.Cy -= 1.5f; }
                    break;
                case ClipNames.Look:
                    p.LookX = f % 2 == 0 ? -1f : 1f;
                    break;
                case ClipNames.Stretch:
                    if (f == 0) { p.Ry *= 0.9f; p.Rx *= 1.06f; p.Cy -= 6; p.Eyes = 1; }
                    else if (f == 1) { p.Ry *= 1.17f; p.Rx *= 0.9f; p.Cy += 11; p.Eyes = 1; p.Mouth = 2; }
                    else { p.Eyes = 1; p.Mouth = 1; }
                    break;
                case ClipNames.Walk:
                    p.Tilt = new[] { -5f, 0f, 5f, 0f }[f % 4];
                    p.Cy += (f % 2 == 1) ? 5 : 0;
                    p.FootL = f % 4 == 0 ? 7 : 0;
                    p.FootR = f % 4 == 2 ? 7 : 0;
                    p.LookX = 0.6f;
                    break;
                case ClipNames.Doze:
                    p.Eyes = 2;
                    p.Tilt = f % 2 == 0 ? 3 : 8;
                    p.Cy -= f % 2 == 0 ? 0 : 2;
                    break;
                case ClipNames.Sleep:
                    p.Eyes = 1;
                    p.Ry *= f % 2 == 0 ? 0.84f : 0.86f;
                    p.Rx *= 1.1f;
                    p.Cy -= 10;
                    break;
                case ClipNames.React:
                    p.Eyes = 3; p.Mouth = 2;
                    if (f % 2 == 0) { p.Ry *= 0.86f; p.Rx *= 1.12f; p.Cy -= 8; }
                    else { p.Cy += 18; p.Ry *= 1.05f; p.FootL = p.FootR = 10; }
                    break;
                case ClipNames.Drag:
                    p.Eyes = 3; p.Mouth = 1;
                    p.Ry *= 1.14f; p.Rx *= 0.9f; p.Cy += 16;
                    p.FootY = 8;
                    break;
                case ClipNames.SnackCarry:
                    p.Tilt = 8; p.LookX = 0.6f; p.Mouth = 1;
                    p.Paw = true; p.PawX = -84; p.PawY = -26;
                    p.FootL = f % 2 == 0 ? 6 : 0; p.FootR = f % 2 == 1 ? 6 : 0;
                    break;
                case ClipNames.SnackEat:
                    p.LookX = 0.6f; p.Paw = true; p.PawX = 66; p.PawY = -18;
                    if (f % 2 == 0) p.Mouth = 2; else { p.Mouth = 3; p.Rx *= 1.04f; }
                    break;
                case ClipNames.CushionFix:
                    p.Tilt = new[] { 6f, 11f, 6f }[f % 3]; p.LookX = 0.7f; p.Mouth = 1;
                    p.Paw = true; p.PawX = f % 3 == 1 ? 94 : 84; p.PawY = f % 3 == 1 ? -30 : -24;
                    break;
                case ClipNames.BallPlay:
                    p.LookX = 0.8f;
                    if (f % 3 == 0) { p.Ry *= 0.9f; p.Cy -= 6; }
                    else if (f % 3 == 1) { p.Tilt = 8; p.Paw = true; p.PawX = 88; p.PawY = 8; p.Mouth = 2; }
                    else { p.Tilt = 3; p.Paw = true; p.PawX = 72; p.PawY = -22; }
                    break;
            }
            float a = -p.Tilt * Mathf.Deg2Rad;
            p.Cos = Mathf.Cos(a);
            p.Sin = Mathf.Sin(a);
            return p;
        }

        private static Color Shade(Pose p, float x, float y)
        {
            Color c = Color.clear;

            // 발 (몸 뒤)
            float fl = EllipseLocal(x - (p.Cx - 32), y - (p.FootY + p.FootL), 20, 11);
            float fr = EllipseLocal(x - (p.Cx + 32), y - (p.FootY + p.FootR), 20, 11);
            c = Over(c, Fill(fl - 3, Line));
            c = Over(c, Fill(fl, Body));
            c = Over(c, Fill(fr - 3, Line));
            c = Over(c, Fill(fr, Body));

            // 몸 (기울기 적용한 지역 좌표)
            float dx = x - p.Cx, dy = y - p.Cy;
            float lx = dx * p.Cos - dy * p.Sin;
            float ly = dx * p.Sin + dy * p.Cos;

            float body = EllipseLocal(lx, ly, p.Rx, p.Ry);
            c = Over(c, Fill(body - 3.5f, Line));
            c = Over(c, Fill(body, Body));

            // 볼
            float ex = 26 + p.LookX * 10, ey = 6 + p.LookY * 6;
            c = Over(c, Fill(EllipseLocal(lx - (-ex - 12), ly - (ey - 16), 11, 6), Blush));
            c = Over(c, Fill(EllipseLocal(lx - (ex + 12), ly - (ey - 16), 11, 6), Blush));

            // 눈
            for (int side = -1; side <= 1; side += 2)
            {
                float cx = side * 24 + p.LookX * 12;
                float cy = 10 + p.LookY * 6;
                switch (p.Eyes)
                {
                    case 0: c = Over(c, Fill(EllipseLocal(lx - cx, ly - cy, 7, 8.5f), Eye)); break;
                    case 1: c = Over(c, Fill(Capsule(lx, ly, cx - 8, cy, cx + 8, cy, 2.2f), Eye)); break;
                    case 2: c = Over(c, Fill(EllipseLocal(lx - cx, ly - (cy - 2), 7.5f, 3.2f), Eye)); break;
                    default:
                        c = Over(c, Fill(EllipseLocal(lx - cx, ly - cy, 9.5f, 10.5f), Eye));
                        c = Over(c, Fill(EllipseLocal(lx - (cx + 3), ly - (cy + 4), 3.2f, 3.2f), Color.white));
                        break;
                }
                if (p.Eyes == 0) c = Over(c, Fill(EllipseLocal(lx - (cx + 2.5f), ly - (cy + 3.5f), 2.3f, 2.3f), Color.white));
            }

            // 입
            float mx = p.LookX * 12, my = -8 + p.LookY * 4;
            switch (p.Mouth)
            {
                case 1: c = Over(c, Fill(Capsule(lx, ly, mx - 5, my, mx + 5, my, 1.8f), MouthCol)); break;
                case 2: c = Over(c, Fill(EllipseLocal(lx - mx, ly - (my - 2), 6.5f, 7.5f), MouthCol)); break;
                case 3: c = Over(c, Fill(Capsule(lx, ly, mx - 7, my - 1, mx + 7, my - 1, 2.6f), MouthCol)); break;
            }

            // 앞발
            if (p.Paw)
            {
                float pd = EllipseLocal(lx - p.PawX, ly - p.PawY, 13, 12);
                c = Over(c, Fill(pd - 3, Line));
                c = Over(c, Fill(pd, Body));
            }
            return c;
        }

        // ---------------- 소품 ----------------

        public static Texture2D Cushion(bool messy)
        {
            const int w = 256, h = 128;
            var px = new Color[w * h];
            var fill = new Color(0.56f, 0.71f, 0.86f, 1f);
            var line = new Color(0.30f, 0.42f, 0.56f, 1f);
            var light = new Color(0.72f, 0.83f, 0.93f, 1f);
            float tilt = messy ? 6f : 0f;
            for (int y = 0; y < h; y++)
            {
                for (int x = 0; x < w; x++)
                {
                    float fx = x + 0.5f, fy = y + 0.5f;
                    Color c = Color.clear;
                    float d = Ellipse(fx, fy, 128, 30, 114, 20, tilt);
                    c = Over(c, Fill(d - 3, line));
                    c = Over(c, Fill(d, fill));
                    c = Over(c, Fill(Ellipse(fx, fy, messy ? 112 : 128, messy ? 40 : 38, 80, 7, tilt), light));
                    if (messy)
                    {
                        float fold = Ellipse(fx, fy, 186, 36, 28, 11, -20);
                        c = Over(c, Fill(fold - 2.5f, line));
                        c = Over(c, Fill(fold, fill));
                    }
                    px[y * w + x] = c;
                }
            }
            return MakeTexture(px, w, h, messy ? "placeholder_cushion_messy" : "placeholder_cushion", line);
        }

        public static Texture2D Snack()
        {
            const int s = 128;
            var px = new Color[s * s];
            var fill = new Color(0.86f, 0.65f, 0.37f, 1f);
            var line = new Color(0.50f, 0.33f, 0.20f, 1f);
            var chip = new Color(0.36f, 0.22f, 0.14f, 1f);
            float[,] chips = { { -16, 12 }, { 14, 18 }, { 4, -8 }, { -18, -14 }, { 22, -6 } };
            for (int y = 0; y < s; y++)
            {
                for (int x = 0; x < s; x++)
                {
                    float fx = x + 0.5f, fy = y + 0.5f;
                    Color c = Color.clear;
                    float d = Ellipse(fx, fy, 64, 46, 44, 40, 0);
                    c = Over(c, Fill(d - 3, line));
                    c = Over(c, Fill(d, fill));
                    for (int i = 0; i < chips.GetLength(0); i++)
                        c = Over(c, Fill(Ellipse(fx, fy, 64 + chips[i, 0], 46 + chips[i, 1], 5, 4.5f, 0), chip));
                    px[y * s + x] = c;
                }
            }
            return MakeTexture(px, s, s, "placeholder_snack", line);
        }

        public static Texture2D Ball()
        {
            const int s = 128;
            var px = new Color[s * s];
            var fill = new Color(0.97f, 0.96f, 0.93f, 1f);
            var line = new Color(0.58f, 0.56f, 0.52f, 1f);
            for (int y = 0; y < s; y++)
            {
                for (int x = 0; x < s; x++)
                {
                    float fx = x + 0.5f, fy = y + 0.5f;
                    Color c = Color.clear;
                    float d = Ellipse(fx, fy, 64, 44, 40, 38, 0);
                    c = Over(c, Fill(d - 3, line));
                    c = Over(c, Fill(d, fill));
                    c = Over(c, Fill(Capsule(fx, fy, 44, 54, 70, 40, 1.6f), line));
                    c = Over(c, Fill(Capsule(fx, fy, 70, 40, 84, 58, 1.6f), line));
                    c = Over(c, Fill(Capsule(fx, fy, 50, 26, 76, 22, 1.6f), line));
                    px[y * s + x] = c;
                }
            }
            return MakeTexture(px, s, s, "placeholder_ball", line);
        }

        // ---------------- 그리기 도구 ----------------

        private static float Ellipse(float x, float y, float cx, float cy, float rx, float ry, float tiltDeg)
        {
            float a = -tiltDeg * Mathf.Deg2Rad;
            float dx = x - cx, dy = y - cy;
            float lx = dx * Mathf.Cos(a) - dy * Mathf.Sin(a);
            float ly = dx * Mathf.Sin(a) + dy * Mathf.Cos(a);
            return EllipseLocal(lx, ly, rx, ry);
        }

        private static float EllipseLocal(float lx, float ly, float rx, float ry)
        {
            float k = Mathf.Sqrt((lx * lx) / (rx * rx) + (ly * ly) / (ry * ry));
            return (k - 1f) * Mathf.Min(rx, ry);
        }

        private static float Capsule(float x, float y, float ax, float ay, float bx, float by, float r)
        {
            float pax = x - ax, pay = y - ay, bax = bx - ax, bay = by - ay;
            float h = Mathf.Clamp01((pax * bax + pay * bay) / (bax * bax + bay * bay));
            float dx = pax - bax * h, dy = pay - bay * h;
            return Mathf.Sqrt(dx * dx + dy * dy) - r;
        }

        private static Color Fill(float dist, Color col)
        {
            float cov = Mathf.Clamp01(0.5f - dist);
            if (cov <= 0f) return Color.clear;
            col.a *= cov;
            return col;
        }

        private static Color Over(Color dst, Color src)
        {
            if (src.a <= 0f) return dst;
            float a = src.a + dst.a * (1f - src.a);
            if (a <= 0f) return Color.clear;
            Color o = (src * src.a + dst * dst.a * (1f - src.a)) / a;
            o.a = a;
            return o;
        }

        private static Texture2D MakeTexture(Color[] px, int w, int h, string name, Color edge)
        {
            // 완전 투명한 곳의 색을 테두리 색으로 채워서, 축소할 때 가장자리가 검게 번지지 않게 한다.
            edge.a = 0f;
            for (int i = 0; i < px.Length; i++) if (px[i].a <= 0f) px[i] = edge;
            var tex = new Texture2D(w, h, TextureFormat.RGBA32, true);
            tex.name = name;
            tex.SetPixels(px);
            tex.filterMode = FilterMode.Trilinear;
            tex.wrapMode = TextureWrapMode.Clamp;
            tex.Apply(true, false);
            return tex;
        }
    }
}
