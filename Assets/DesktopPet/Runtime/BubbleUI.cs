using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;

namespace DesktopPet
{
    /// <summary>말풍선 · 오늘의 일기 창 · 잠잘 때 떠오르는 z. 크기 단위는 논리 px(화면 배율 자동 반영).</summary>
    public sealed class BubbleUI
    {
        private const float BubbleMaxWidth = 200f;
        private const float DiaryMaxWidth = 360f;
        private const int FontSize = 14;

        private readonly Canvas _canvas;
        private readonly CanvasScaler _scaler;
        private readonly Font _font;

        private readonly RectTransform _bubble;
        private readonly Text _bubbleText;
        private readonly RectTransform _tail;
        private float _bubbleUntil;

        private readonly RectTransform _diary;
        private readonly Text _diaryText;
        private float _diaryUntil;

        private readonly List<Text> _zs = new List<Text>();
        private float _scale = 1f;

        public BubbleUI(Transform parent)
        {
            var go = new GameObject("UI");
            go.transform.SetParent(parent, false);
            _canvas = go.AddComponent<Canvas>();
            _canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            _canvas.sortingOrder = 100;
            _scaler = go.AddComponent<CanvasScaler>();
            _scaler.uiScaleMode = CanvasScaler.ScaleMode.ConstantPixelSize;
            _scaler.scaleFactor = 1f;

            _font = Font.CreateDynamicFontFromOSFont(new[] { "Malgun Gothic", "맑은 고딕", "Segoe UI", "Arial" }, FontSize);

            var bg = MakeRoundedSprite();

            _bubble = MakePanel("Bubble", bg, out _bubbleText, new Vector2(0.5f, 0f));
            _tail = new GameObject("Tail", typeof(RectTransform)).GetComponent<RectTransform>();
            _tail.SetParent(_bubble, false);
            var tailImg = _tail.gameObject.AddComponent<Image>();
            tailImg.sprite = MakeTailSprite();
            _tail.anchorMin = _tail.anchorMax = new Vector2(0.5f, 0f);
            _tail.pivot = new Vector2(0.5f, 1f);
            _tail.sizeDelta = new Vector2(14f, 9f);
            _tail.anchoredPosition = new Vector2(0f, 2f);
            _bubble.gameObject.SetActive(false);

            _diary = MakePanel("Diary", bg, out _diaryText, new Vector2(0.5f, 1f));
            _diaryText.lineSpacing = 1.15f;
            _diary.gameObject.SetActive(false);

            for (int i = 0; i < 3; i++)
            {
                var z = MakeText("Z" + i, _canvas.transform, FontSize + i * 3);
                z.text = "z";
                z.fontStyle = FontStyle.Bold;
                z.color = new Color(0.35f, 0.40f, 0.62f, 1f);
                z.rectTransform.anchorMin = z.rectTransform.anchorMax = Vector2.zero;
                z.rectTransform.sizeDelta = new Vector2(30f, 30f);
                z.gameObject.SetActive(false);
                _zs.Add(z);
            }
        }

        public void SetScale(float dpiScale)
        {
            _scale = dpiScale <= 0 ? 1f : dpiScale;
            _scaler.scaleFactor = _scale;
        }

        public void ShowBubble(string text, float seconds)
        {
            Layout(_bubble, _bubbleText, text, BubbleMaxWidth);
            _bubble.gameObject.SetActive(true);
            _bubbleUntil = Time.unscaledTime + seconds;
        }

        public void ShowDiary(string text, float seconds)
        {
            Layout(_diary, _diaryText, text, DiaryMaxWidth);
            _diary.gameObject.SetActive(true);
            _diaryUntil = Time.unscaledTime + seconds;
        }

        public bool DiaryVisible { get { return _diary.gameObject.activeSelf; } }

        public void HideDiary() { _diary.gameObject.SetActive(false); }

        public void HideAll()
        {
            _bubble.gameObject.SetActive(false);
            _diary.gameObject.SetActive(false);
            foreach (var z in _zs) z.gameObject.SetActive(false);
        }

        /// <summary>headScreen: 머리 꼭대기의 화면 좌표(물리 px, 왼쪽 아래 0).</summary>
        public void Tick(Vector2 headScreen, bool sleeping, bool hidden, float screenW, float screenH)
        {
            if (hidden) { HideAll(); return; }
            float now = Time.unscaledTime;
            float cw = screenW / _scale, ch = screenH / _scale;
            Vector2 head = headScreen / _scale;

            if (_bubble.gameObject.activeSelf)
            {
                if (now > _bubbleUntil) _bubble.gameObject.SetActive(false);
                else
                {
                    float halfW = _bubble.sizeDelta.x * 0.5f;
                    float x = Mathf.Clamp(head.x, halfW + 4f, cw - halfW - 4f);
                    float y = Mathf.Min(head.y + 14f, ch - _bubble.sizeDelta.y - 4f);
                    _bubble.anchoredPosition = new Vector2(x, y);
                    _tail.anchoredPosition = new Vector2(Mathf.Clamp(head.x - x, -halfW + 12f, halfW - 12f), 2f);
                }
            }

            if (_diary.gameObject.activeSelf)
            {
                if (now > _diaryUntil) _diary.gameObject.SetActive(false);
                else _diary.anchoredPosition = new Vector2(cw * 0.5f, ch - 6f);
            }

            for (int i = 0; i < _zs.Count; i++)
            {
                var z = _zs[i];
                if (!sleeping) { if (z.gameObject.activeSelf) z.gameObject.SetActive(false); continue; }
                if (!z.gameObject.activeSelf) z.gameObject.SetActive(true);
                float t = Mathf.Repeat(now / 2.4f + i / 3f, 1f);
                z.rectTransform.anchoredPosition = head + new Vector2(18f + t * 16f + i * 4f, -10f + t * 34f);
                var c = z.color;
                c.a = Mathf.Sin(t * Mathf.PI);
                z.color = c;
            }
        }

        // ------------------------------------------------------------

        private void Layout(RectTransform panel, Text text, string content, float maxWidth)
        {
            const float padX = 12f, padY = 8f;
            text.text = content;
            text.horizontalOverflow = HorizontalWrapMode.Wrap;
            float natural = text.preferredWidth; // 줄바꿈 없이 필요한 폭
            float w = Mathf.Min(natural + 2f, maxWidth - padX * 2f);
            text.rectTransform.sizeDelta = new Vector2(w, 10f);
            float h = text.preferredHeight;       // 그 폭에서 줄바꿈했을 때 높이
            text.rectTransform.sizeDelta = new Vector2(w, h);
            panel.sizeDelta = new Vector2(w + padX * 2f, h + padY * 2f);
        }

        private RectTransform MakePanel(string name, Sprite bg, out Text text, Vector2 pivot)
        {
            var go = new GameObject(name, typeof(RectTransform));
            go.transform.SetParent(_canvas.transform, false);
            var rt = go.GetComponent<RectTransform>();
            rt.anchorMin = rt.anchorMax = Vector2.zero;
            rt.pivot = pivot;
            var img = go.AddComponent<Image>();
            img.sprite = bg;
            img.type = Image.Type.Sliced;
            text = MakeText("Text", rt, FontSize);
            text.rectTransform.anchorMin = text.rectTransform.anchorMax = new Vector2(0.5f, 0.5f);
            text.rectTransform.pivot = new Vector2(0.5f, 0.5f);
            text.rectTransform.anchoredPosition = Vector2.zero;
            return rt;
        }

        private Text MakeText(string name, Transform parent, int size)
        {
            var go = new GameObject(name, typeof(RectTransform));
            go.transform.SetParent(parent, false);
            var t = go.AddComponent<Text>();
            t.font = _font;
            t.fontSize = size;
            t.color = new Color(0.30f, 0.22f, 0.18f, 1f);
            t.alignment = TextAnchor.MiddleLeft;
            t.supportRichText = false;
            t.raycastTarget = false;
            return t;
        }

        private static Sprite MakeRoundedSprite()
        {
            const int s = 48, r = 14;
            var tex = new Texture2D(s, s, TextureFormat.RGBA32, false);
            var fill = new Color(1f, 0.99f, 0.96f, 1f);
            var line = new Color(0.55f, 0.45f, 0.38f, 1f);
            var px = new Color[s * s];
            for (int y = 0; y < s; y++)
            {
                for (int x = 0; x < s; x++)
                {
                    float cx = Mathf.Clamp(x + 0.5f, r, s - r);
                    float cy = Mathf.Clamp(y + 0.5f, r, s - r);
                    float d = Vector2.Distance(new Vector2(x + 0.5f, y + 0.5f), new Vector2(cx, cy)) - r; // 둥근 사각형 거리
                    float outer = Mathf.Clamp01(0.5f - d);
                    float inner = Mathf.Clamp01(0.5f - (d + 2f));
                    Color c = Color.Lerp(line, fill, inner);
                    c.a = outer;
                    px[y * s + x] = c;
                }
            }
            tex.SetPixels(px);
            tex.Apply(false, false);
            tex.wrapMode = TextureWrapMode.Clamp;
            return Sprite.Create(tex, new Rect(0, 0, s, s), new Vector2(0.5f, 0.5f), 100f, 0, SpriteMeshType.FullRect, new Vector4(r + 2, r + 2, r + 2, r + 2));
        }

        private static Sprite MakeTailSprite()
        {
            const int w = 28, h = 18;
            var tex = new Texture2D(w, h, TextureFormat.RGBA32, false);
            var fill = new Color(1f, 0.99f, 0.96f, 1f);
            var line = new Color(0.55f, 0.45f, 0.38f, 1f);
            var px = new Color[w * h];
            for (int y = 0; y < h; y++)
            {
                for (int x = 0; x < w; x++)
                {
                    // 위가 넓고 아래가 뾰족한 삼각형
                    float half = (y + 0.5f) / h * (w * 0.5f);
                    float d = Mathf.Abs(x + 0.5f - w * 0.5f) - half;
                    float outer = Mathf.Clamp01(0.5f - d);
                    float inner = Mathf.Clamp01(0.5f - (d + 2.5f));
                    Color c = Color.Lerp(line, fill, inner);
                    c.a = outer;
                    if (y >= h - 3) { c = fill; c.a = outer; } // 위쪽(말풍선과 붙는 부분)은 테두리 없음
                    px[y * w + x] = c;
                }
            }
            tex.SetPixels(px);
            tex.Apply(false, false);
            return Sprite.Create(tex, new Rect(0, 0, w, h), new Vector2(0.5f, 1f), 100f);
        }
    }
}
