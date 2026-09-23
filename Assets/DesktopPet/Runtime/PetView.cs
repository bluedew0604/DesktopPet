using UnityEngine;
using DesktopPet.Core;

namespace DesktopPet
{
    /// <summary>
    /// 엔진이 계산한 Frame을 화면에 그린다. 월드 좌표 1 = 창의 물리 픽셀 1.
    /// 크기 기준(논리 px): 캐릭터 캔버스 높이 150, 방석 폭 170, 과자 폭 46, 종이공 폭 36.
    /// </summary>
    public sealed class PetView
    {
        public float CharacterHeight = 150f;
        public float CushionWidth = 170f;
        public float SnackWidth = 46f;
        public float BallWidth = 36f;
        public float GroundY = 8f;

        private readonly ArtLibrary _art;
        private readonly Transform _root;
        private readonly SpriteRenderer _body, _cushion, _snack, _ball;
        private readonly Transform _ballSpin;

        private float _scale = 1f;
        private float _baseX, _groundY;
        private Vector2 _offset;
        private bool _hidden;

        public Transform Body { get { return _body.transform; } }

        public PetView(Transform parent, ArtLibrary art)
        {
            _art = art;
            _root = new GameObject("PetView").transform;
            _root.SetParent(parent, false);

            _cushion = MakeRenderer("Cushion", _root, 0);
            _snack = MakeRenderer("Snack", _root, 5);
            _body = MakeRenderer("Character", _root, 10);

            var ballRoot = new GameObject("BallRoot").transform;
            ballRoot.SetParent(_root, false);
            _ballSpin = new GameObject("BallSpin").transform;
            _ballSpin.SetParent(ballRoot, false);
            _ball = MakeRenderer("Ball", _ballSpin, 12);
        }

        private static SpriteRenderer MakeRenderer(string name, Transform parent, int order)
        {
            var go = new GameObject(name);
            go.transform.SetParent(parent, false);
            var sr = go.AddComponent<SpriteRenderer>();
            sr.sortingOrder = order;
            return sr;
        }

        /// <summary>창 크기/배율이 바뀔 때마다 호출.</summary>
        public void Layout(float screenWidth, float dpiScale, Vector2 editorOffset)
        {
            _scale = dpiScale <= 0 ? 1f : dpiScale;
            _baseX = screenWidth * 0.5f;
            _groundY = GroundY * _scale;
            _offset = editorOffset;
        }

        public void SetHidden(bool hidden)
        {
            _hidden = hidden;
            _body.enabled = _cushion.enabled = !hidden;
            if (hidden) { _snack.enabled = false; _ball.enabled = false; }
        }

        /// <summary>press: 0~1 (누르고 있으면 살짝 눌림), time: 연출용 시간.</summary>
        public void Apply(Frame f, float press, float time)
        {
            if (_hidden) return;

            // ---- 방석 ----
            var cushionSprite = f.CushionMessy ? _art.CushionMessy : _art.Cushion;
            _cushion.sprite = cushionSprite;
            float cushionK = cushionSprite != null ? CushionWidth * _scale / cushionSprite.rect.width : 1f;
            _cushion.transform.localPosition = new Vector3(_baseX + _offset.x, _groundY - 2f * _scale + _offset.y, 0f);
            _cushion.transform.localScale = new Vector3(cushionK, cushionK, 1f);

            // 방석 위에 있으면 살짝 올라가 앉음
            float cushionTop = 0f;
            if (cushionSprite != null)
                cushionTop = _art.GetOpaqueRect(cushionSprite).yMax * cushionK * 0.55f;
            float onCushion = 1f - Mathf.Clamp01((Mathf.Abs((float)f.X) - 55f) / 25f);
            float lift = cushionTop * onCushion;

            // ---- 캐릭터 ----
            int count = _art.FrameCount(f.Clip);
            int idx = ClipTable.FrameIndex(f.Clip, f.ClipTime, f.ClipDuration, count);
            var sprite = _art.GetFrame(f.Clip, idx);
            _body.sprite = sprite;

            float k = sprite != null ? CharacterHeight * _scale / sprite.rect.height : 1f;
            bool flip = f.FaceLeft && ClipTable.IsDirectional(f.Clip);

            // 살아있는 느낌: 숨쉬기 / 걸을 때 통통 / 누르면 눌림
            float sx = 1f, sy = 1f, bob = 0f;
            if (f.Clip == ClipNames.Idle || f.Clip == ClipNames.Doze || f.Clip == ClipNames.Sleep)
            {
                float period = f.Clip == ClipNames.Sleep ? 4.2f : 3.2f;
                float b = Mathf.Sin(time * Mathf.PI * 2f / period) * 0.012f;
                sy += b; sx -= b * 0.5f;
            }
            else if (f.Clip == ClipNames.Walk || f.Clip == ClipNames.SnackCarry)
            {
                bob = Mathf.Abs(Mathf.Sin((float)f.ClipTime * Mathf.PI * 4f)) * 1.5f * _scale;
            }
            if (press > 0f) { sy *= 1f - 0.06f * press; sx *= 1f + 0.04f * press; }

            _body.transform.localPosition = new Vector3(_baseX + (float)f.X * _scale + _offset.x, _groundY + lift + bob + _offset.y, 0f);
            _body.transform.localScale = new Vector3(k * sx * (flip ? -1f : 1f), k * sy, 1f);

            // ---- 과자 ----
            ApplyProp(_snack, _art.Snack, f.Snack, SnackWidth, null);
            // ---- 종이공 (굴러가며 회전) ----
            ApplyProp(_ball, _art.Ball, f.Ball, BallWidth, _ballSpin);
        }

        private void ApplyProp(SpriteRenderer r, Sprite sprite, PropState p, float width, Transform spin)
        {
            if (!p.Visible || sprite == null) { r.enabled = false; return; }
            r.enabled = true;
            r.sprite = sprite;
            float k = width * _scale / sprite.rect.width * (float)p.Scale;
            var pos = new Vector3(_baseX + (float)p.X * _scale + _offset.x, _groundY + (float)p.Y * _scale + _offset.y, 0f);

            if (spin == null)
            {
                r.transform.localPosition = pos;
                r.transform.localScale = new Vector3(k, k, 1f);
                return;
            }

            // 공은 가운데를 축으로 굴러간다: 부모를 바닥 위치에, 회전축을 공 중심에 둔다.
            float halfH = sprite.rect.height * k * 0.5f;
            spin.parent.localPosition = pos;
            spin.localPosition = new Vector3(0f, halfH, 0f);
            float radius = Mathf.Max(1f, width * 0.5f);
            spin.localRotation = Quaternion.Euler(0f, 0f, -(float)p.X / radius * Mathf.Rad2Deg);
            r.transform.localPosition = new Vector3(0f, -halfH, 0f);
            r.transform.localScale = new Vector3(k, k, 1f);
        }

        /// <summary>월드 좌표가 캐릭터 그림(칠해진 영역 + 여유)에 닿는지.</summary>
        public bool HitTest(Vector2 world, float marginLogical)
        {
            if (_hidden || _body.sprite == null) return false;
            var s = _body.sprite;
            Vector3 local = _body.transform.InverseTransformPoint(new Vector3(world.x, world.y, 0f));
            float lx = local.x + s.rect.width * 0.5f; // 스프라이트 픽셀 좌표로 (피벗 = 아래 가운데)
            float ly = local.y;
            float k = Mathf.Abs(_body.transform.localScale.y);
            float margin = k > 0 ? marginLogical * _scale / k : 0f;
            var r = _art.GetOpaqueRect(s);
            return lx >= r.xMin - margin && lx <= r.xMax + margin && ly >= r.yMin - margin && ly <= r.yMax + margin;
        }

        /// <summary>머리 꼭대기(월드 좌표). 말풍선 위치 기준.</summary>
        public Vector2 HeadWorld()
        {
            var s = _body.sprite;
            if (s == null) return new Vector2(_baseX, _groundY + CharacterHeight * _scale);
            var r = _art.GetOpaqueRect(s);
            Vector3 top = _body.transform.TransformPoint(new Vector3(r.center.x - s.rect.width * 0.5f, r.yMax, 0f));
            return new Vector2(top.x, top.y);
        }
    }
}
