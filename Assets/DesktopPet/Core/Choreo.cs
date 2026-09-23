using System;

namespace DesktopPet.Core
{
    /// <summary>
    /// 행동별 연출 계획을 만들고(Build*), 경과 시간으로 그 순간의 화면 상태를 계산한다(Evaluate).
    /// 모든 수치는 논리 픽셀. 0 = 방석 중앙.
    /// </summary>
    public static class Choreo
    {
        public const double AreaHalf = 100;      // 캐릭터가 돌아다니는 범위 (방석 기준 좌우)
        public const double WalkSpeed = 40;      // 논리 px / 초
        public const double CarrySpeed = 24;     // 과자를 끌 때
        public const double ViewHalfWidth = 190; // 창 폭의 절반 (소품이 창 밖으로 나가는 기준)

        public const double LookDuration = 3.0;
        public const double DropLookDuration = 2.0;
        public const double StretchDuration = 2.4;
        public const double DozeDuration = 8.0;
        public const double ClickReactDuration = 1.0;
        public const double WakeReactDuration = 1.6;

        // ---------- 기본 동작 ----------

        public static ActionPlan Hold(ActionKind kind, string clip, double duration, double x, bool faceLeft)
        {
            var p = new ActionPlan { Kind = kind };
            p.Segments.Add(new Segment { Duration = duration, Clip = clip, FromX = x, ToX = x, FaceLeft = faceLeft });
            return p;
        }

        public static ActionPlan Drag(double x, bool faceLeft)
        {
            var p = Hold(ActionKind.Drag, ClipNames.Drag, double.PositiveInfinity, x, faceLeft);
            p.OpenEnded = true;
            return p;
        }

        public static ActionPlan Walk(double x0, double target)
        {
            var p = new ActionPlan { Kind = ActionKind.Walk };
            AddWalk(p, x0, target, ClipNames.Walk, x0 > target);
            return p;
        }

        public static double PickWalkTarget(double x0, Random rng)
        {
            for (int i = 0; i < 8; i++)
            {
                double t = -AreaHalf + rng.NextDouble() * AreaHalf * 2;
                if (Math.Abs(t - x0) >= 30) return Math.Round(t, 1);
            }
            return x0 > 0 ? -AreaHalf * 0.5 : AreaHalf * 0.5;
        }

        public static ActionPlan Doze(double x0, bool faceLeft)
        {
            var p = new ActionPlan { Kind = ActionKind.Doze };
            AddWalk(p, x0, 0, ClipNames.Walk, faceLeft);
            bool face = p.Segments.Count > 0 ? p.EndFaceLeft : faceLeft;
            p.Segments.Add(new Segment { Duration = DozeDuration, Clip = ClipNames.Doze, FromX = 0, ToX = 0, FaceLeft = face });
            return p;
        }

        public static ActionPlan Sleep(double duration, double x, bool faceLeft)
        {
            return Hold(ActionKind.Sleep, ClipNames.Sleep, duration, x, faceLeft);
        }

        // ---------- 생활 장면 ----------

        /// <summary>과자 옮겨 먹기: 과자가 나타남 → 걸어감 → 붙잡음 → 방석까지 끌고 옴 → 먹음 → 끝.</summary>
        public static ActionPlan SnackScene(double x0, bool faceLeft, Random rng)
        {
            var p = new ActionPlan { Kind = ActionKind.SnackScene, TextVariant = rng.Next(2) };
            int s = rng.Next(2) == 0 ? -1 : 1;
            double snackX = s * 140;
            double standX = s * 112;
            const double hold = 28; // 과자와 캐릭터 사이 간격

            // 1) 과자가 톡 나타나고 그쪽을 본다
            p.Segments.Add(new Segment
            {
                Duration = 1.2, Clip = ClipNames.Idle, FromX = x0, ToX = x0, FaceLeft = s < 0,
                Snack = new PropKey { FromX = snackX, ToX = snackX, FromScale = 0.6, ToScale = 1 }
            });
            // 2) 과자 옆으로 걸어감
            AddWalk(p, x0, standX, ClipNames.Walk, s < 0, PropKey.Still(snackX), null);
            // 3) 돌아서서 뒤로 붙잡음 (snack_carry 그림 = 앞을 보고 걸으며 뒤쪽 과자를 끄는 모습)
            p.Segments.Add(new Segment
            {
                Duration = 0.8, Clip = ClipNames.SnackCarry, FromX = standX, ToX = standX, FaceLeft = s > 0,
                Snack = PropKey.Still(snackX)
            });
            // 4) 방석 쪽으로 끌고 옴 (과자는 캐릭터 뒤에서 따라옴)
            double carryDur = Math.Abs(standX) / CarrySpeed;
            p.Segments.Add(new Segment
            {
                Duration = carryDur, Clip = ClipNames.SnackCarry, FromX = standX, ToX = 0, FaceLeft = s > 0,
                Snack = PropKey.Move(standX + s * hold, s * hold)
            });
            // 5) 먹는다 (과자가 점점 작아짐)
            p.Segments.Add(new Segment
            {
                Duration = 6.0, Clip = ClipNames.SnackEat, FromX = 0, ToX = 0, FaceLeft = s < 0,
                Snack = new PropKey { FromX = s * hold, ToX = s * hold, FromScale = 1, ToScale = 0.25 }
            });
            // 6) 다 먹음
            p.Segments.Add(new Segment { Duration = 1.0, Clip = ClipNames.Idle, FromX = 0, ToX = 0, FaceLeft = s < 0 });
            return p;
        }

        /// <summary>방석 정리: 옆으로 비킴 → 방석을 밀어 정리 → 확인 → 다시 올라앉음 (가끔 다시 비뚤어짐).</summary>
        public static ActionPlan CushionScene(double x0, bool faceLeft, Random rng)
        {
            bool messyAgain = rng.NextDouble() < 0.35;
            var p = new ActionPlan { Kind = ActionKind.CushionScene, OutcomeCushionMessy = messyAgain, TextVariant = messyAgain ? 1 : 0 };
            int s = x0 > 5 ? 1 : (x0 < -5 ? -1 : (rng.Next(2) == 0 ? -1 : 1));
            double side = s * 70;
            bool faceCenter = s > 0; // 오른쪽에 서 있으면 왼쪽(방석)을 봄

            AddWalk(p, x0, side, ClipNames.Walk, faceLeft);
            p.Segments.Add(new Segment { Duration = 1.8, Clip = ClipNames.CushionFix, FromX = side, ToX = side, FaceLeft = faceCenter, CushionOverride = 1 });
            p.Segments.Add(new Segment { Duration = 1.4, Clip = ClipNames.CushionFix, FromX = side, ToX = side, FaceLeft = faceCenter, CushionOverride = 0 });
            p.Segments.Add(new Segment { Duration = 1.0, Clip = ClipNames.Idle, FromX = side, ToX = side, FaceLeft = faceCenter, CushionOverride = 0 });
            int before = p.Segments.Count;
            AddWalk(p, side, 0, ClipNames.Walk, faceCenter);
            for (int i = before; i < p.Segments.Count; i++) p.Segments[i].CushionOverride = 0;
            p.Segments.Add(new Segment { Duration = 1.2, Clip = ClipNames.Idle, FromX = 0, ToX = 0, FaceLeft = p.EndFaceLeft, CushionOverride = messyAgain ? 1 : 0 });
            return p;
        }

        /// <summary>종이공 놀이: 공이 굴러옴 → 쫓아감 → 툭 침 → 또 쫓아감 → 세게 쳐서 창 밖으로 → 흥미 잃고 복귀.</summary>
        public static ActionPlan PlayScene(double x0, bool faceLeft, Random rng)
        {
            var p = new ActionPlan { Kind = ActionKind.PlayScene, TextVariant = rng.Next(2) };
            int s = rng.Next(2) == 0 ? -1 : 1;
            bool towardBall = s < 0;
            double ballOut = s * (ViewHalfWidth + 60);

            p.Segments.Add(new Segment
            {
                Duration = 1.2, Clip = ClipNames.Idle, FromX = x0, ToX = x0, FaceLeft = towardBall,
                Ball = new PropKey { FromX = s * 175, ToX = s * 120, BounceHeight = 10, BounceCount = 2 }
            });
            AddWalk(p, x0, s * 90, ClipNames.Walk, towardBall, null, PropKey.Still(s * 120));
            p.Segments.Add(new Segment
            {
                Duration = 1.5, Clip = ClipNames.BallPlay, FromX = s * 90, ToX = s * 90, FaceLeft = towardBall,
                Ball = new PropKey { FromX = s * 120, ToX = s * 150, BounceHeight = 8, BounceCount = 2 }
            });
            AddWalk(p, s * 90, s * 120, ClipNames.Walk, towardBall, null, PropKey.Still(s * 150));
            p.Segments.Add(new Segment
            {
                Duration = 1.5, Clip = ClipNames.BallPlay, FromX = s * 120, ToX = s * 120, FaceLeft = towardBall,
                Ball = new PropKey { FromX = s * 150, ToX = ballOut, BounceHeight = 12, BounceCount = 3 }
            });
            p.Segments.Add(new Segment { Duration = 1.4, Clip = ClipNames.Look, FromX = s * 120, ToX = s * 120, FaceLeft = towardBall });
            AddWalk(p, s * 120, 0, ClipNames.Walk, towardBall);
            p.Segments.Add(new Segment { Duration = 0.8, Clip = ClipNames.Idle, FromX = 0, ToX = 0, FaceLeft = p.EndFaceLeft });
            return p;
        }

        // ---------- 공통 ----------

        private static void AddWalk(ActionPlan p, double from, double to, string clip, bool faceIfStill)
        {
            AddWalk(p, from, to, clip, faceIfStill, null, null);
        }

        private static void AddWalk(ActionPlan p, double from, double to, string clip, bool faceIfStill, PropKey snack, PropKey ball)
        {
            double dist = Math.Abs(to - from);
            if (dist < 1e-6) return;
            double dur = Math.Max(0.6, dist / WalkSpeed);
            p.Segments.Add(new Segment
            {
                Duration = dur, Clip = clip, FromX = from, ToX = to,
                FaceLeft = to < from ? true : (to > from ? false : faceIfStill),
                Snack = snack, Ball = ball
            });
        }

        /// <summary>계획의 경과시간 t에서의 화면 상태.</summary>
        public static Frame Evaluate(ActionPlan plan, double t, bool worldCushionMessy)
        {
            var f = new Frame { Action = plan.Kind, CushionMessy = worldCushionMessy };
            if (plan.Segments.Count == 0)
            {
                f.Clip = ClipNames.Idle;
                return f;
            }
            if (t < 0) t = 0;

            double acc = 0;
            Segment seg = plan.Segments[plan.Segments.Count - 1];
            double local = 0;
            for (int i = 0; i < plan.Segments.Count; i++)
            {
                var s = plan.Segments[i];
                if (t < acc + s.Duration || i == plan.Segments.Count - 1)
                {
                    seg = s;
                    local = t - acc;
                    break;
                }
                acc += s.Duration;
            }

            double u = double.IsInfinity(seg.Duration) || seg.Duration <= 0 ? 0 : Clamp01(local / seg.Duration);
            f.Clip = seg.Clip;
            f.ClipTime = local;
            f.ClipDuration = seg.Duration;
            f.X = Lerp(seg.FromX, seg.ToX, u);
            f.FaceLeft = seg.FaceLeft;
            if (seg.CushionOverride >= 0) f.CushionMessy = seg.CushionOverride == 1;
            f.Snack = EvalProp(seg.Snack, u);
            f.Ball = EvalProp(seg.Ball, u);
            return f;
        }

        private static PropState EvalProp(PropKey k, double u)
        {
            if (k == null) return new PropState { Visible = false, Scale = 1 };
            double y = 0;
            if (k.BounceHeight > 0 && k.BounceCount > 0)
                y = k.BounceHeight * Math.Abs(Math.Sin(u * Math.PI * k.BounceCount)) * (1 - 0.6 * u);
            return new PropState
            {
                Visible = true,
                X = Lerp(k.FromX, k.ToX, u),
                Y = y,
                Scale = Lerp(k.FromScale, k.ToScale, u)
            };
        }

        public static double Lerp(double a, double b, double u) { return a + (b - a) * u; }
        public static double Clamp01(double v) { return v < 0 ? 0 : (v > 1 ? 1 : v); }
    }

    /// <summary>클립별 재생 방식. Loop는 초당 프레임으로 반복, Span은 구간 길이에 프레임을 고르게 나눠 한 번 재생.</summary>
    public static class ClipTable
    {
        public static int FrameIndex(string clip, double clipTime, double clipDuration, int frameCount)
        {
            if (frameCount <= 1) return 0;
            if (clipTime < 0) clipTime = 0;
            bool span;
            double fps = FpsFor(clip, out span);
            if (span && clipDuration > 0 && !double.IsInfinity(clipDuration))
            {
                int idx = (int)Math.Floor(clipTime / clipDuration * frameCount);
                return idx < 0 ? 0 : (idx >= frameCount ? frameCount - 1 : idx);
            }
            long n = (long)Math.Floor(clipTime * fps);
            return (int)(n % frameCount);
        }

        public static double FpsFor(string clip, out bool span)
        {
            span = false;
            switch (clip)
            {
                case ClipNames.Idle: return 1.25;
                case ClipNames.Look: span = true; return 1;
                case ClipNames.Stretch: span = true; return 1;
                case ClipNames.React: span = true; return 1;
                case ClipNames.Walk: return 8;
                case ClipNames.Doze: return 1.5;
                case ClipNames.Sleep: return 0.7;
                case ClipNames.Drag: return 4;
                case ClipNames.SnackCarry: return 6;
                case ClipNames.SnackEat: return 3;
                case ClipNames.CushionFix: return 3;
                case ClipNames.BallPlay: return 5;
                default: return 2;
            }
        }

        /// <summary>좌우 반전해서 방향을 표현하는 클립(이동·소품 관련). 나머지는 정면 그림이라 뒤집지 않는다.</summary>
        public static bool IsDirectional(string clip)
        {
            return clip == ClipNames.Walk || clip == ClipNames.SnackCarry || clip == ClipNames.SnackEat
                || clip == ClipNames.CushionFix || clip == ClipNames.BallPlay;
        }
    }

    /// <summary>클릭/드래그 판정. 임계값은 논리 8px, 화면 배율을 곱해 물리 픽셀로 비교한다.</summary>
    public static class DragMath
    {
        public const double ThresholdLogical = 8.0;

        public static bool IsDrag(double dxPhysical, double dyPhysical, double dpiScale)
        {
            if (dpiScale <= 0) dpiScale = 1;
            double t = ThresholdLogical * dpiScale;
            return dxPhysical * dxPhysical + dyPhysical * dyPhysical >= t * t;
        }
    }
}
