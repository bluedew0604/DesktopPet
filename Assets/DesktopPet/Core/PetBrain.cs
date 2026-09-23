using System;
using System.Collections.Generic;

namespace DesktopPet.Core
{
    public sealed class PetConfig
    {
        public double DecisionMin = 30, DecisionMax = 90;       // 한가할 때 다음 행동까지
        public double SceneMin = 360, SceneMax = 720;           // 생활 장면 간격 (6~12분)
        public double RestMin = 1080, RestMax = 1680;           // 졸기 간격 (18~28분)
        public double NightRestMin = 480, NightRestMax = 720;   // 새벽(1~6시)엔 더 자주 졸음
        public double SleepMin = 240, SleepMax = 540;           // 잠 길이 (4~9분)
        public double NightSleepMin = 480, NightSleepMax = 900;
        public double SceneCooldown = 900;                      // 같은 장면 재등장 최소 간격
        public double LongGapSeconds = 10;                      // 이보다 긴 실행 공백 = 절전/멈춤으로 봄
        public double ClickCooldown = 0.4;                      // 연속 클릭 묶음
        public double BubbleMinGap = 90;                        // 말풍선 최소 간격
        public double IdleHintSeconds = 600;                    // (선택 기능) 입력 없음 판단 기준
        public double SceneOverlayReact = 0.6;                  // 장면 중 클릭하면 잠깐 반응
    }

    /// <summary>
    /// 캐릭터의 판단 엔진. Unity에 의존하지 않는 순수 C#.
    /// 우선순위: 사용자 명령(숨김) > 클릭·드래그(즉시) > 진행 중 행동 > 졸림/수면 > 한가할 때 자율 행동.
    /// 시간(now)은 초 단위 단조 증가 시계, local은 현재 날짜/시각(일기 날짜·새벽 판단용).
    /// </summary>
    public sealed class PetBrain
    {
        private readonly PetConfig _cfg;
        private readonly Random _rng;
        private readonly DiaryBook _diary;
        private readonly Queue<PetEvent> _events = new Queue<PetEvent>();
        private readonly Dictionary<ActionKind, double> _lastUsed = new Dictionary<ActionKind, double>();

        private bool _init;
        private double _prev;
        private double _nextDecision, _nextScene, _nextRest;
        private double _lastRestEnd = double.NegativeInfinity;
        private double _lastClickReaction = double.NegativeInfinity;
        private double _lastBubble = double.NegativeInfinity;
        private double _overlayStart, _overlayUntil = double.NegativeInfinity;

        private ActionPlan _current;
        private double _curStart, _curEnd;
        private double _idleSince;
        private ActionKind _lastScene = ActionKind.None;
        private ActionKind _lastBasic = ActionKind.None;

        public LifeState Life { get; private set; }
        public bool Quiet { get; private set; }
        public bool Hidden { get; private set; }
        public bool Dragging { get; private set; }
        public bool IdleHintEnabled { get; set; }
        public bool CushionMessy { get; private set; }
        public double PosX { get; private set; }
        public bool FaceLeft { get; private set; }

        public int StartedCount { get; private set; }
        public int CompletedCount { get; private set; }
        public int InterruptedCount { get; private set; }
        public int ClockAnomalies { get; private set; }

        public DiaryBook Diary { get { return _diary; } }
        public ActionKind Current { get { return _current == null ? ActionKind.None : _current.Kind; } }
        public ActionPlan CurrentPlan { get { return _current; } }
        public PetConfig Config { get { return _cfg; } }

        public PetBrain(int seed, PetConfig cfg = null, DiaryBook diary = null)
        {
            _rng = new Random(seed);
            _cfg = cfg ?? new PetConfig();
            _diary = diary ?? new DiaryBook();
            Life = LifeState.Awake;
        }

        /// <summary>저장된 상태 복원. 첫 Tick 전에 부른다.</summary>
        public void Restore(bool cushionMessy, bool quiet, bool idleHint)
        {
            CushionMessy = cushionMessy;
            Quiet = quiet;
            IdleHintEnabled = idleHint;
        }

        public bool TryDequeueEvent(out PetEvent e)
        {
            if (_events.Count > 0) { e = _events.Dequeue(); return true; }
            e = default(PetEvent);
            return false;
        }

        // =====================================================================
        // 매 프레임
        // =====================================================================

        public void Tick(double now, DateTime local, double? userIdleSeconds)
        {
            if (!_init) { Init(now, local); return; }

            if (double.IsNaN(now) || double.IsInfinity(now) || now < _prev)
            {
                ClockAnomalies++;
                return; // 시계가 이상하면 이번 프레임은 아무것도 안 한다
            }

            double gap = now - _prev;
            _prev = now;

            if (gap > _cfg.LongGapSeconds)
            {
                HandleLongGap(now, local);
                return;
            }

            if (Hidden) return;

            if (_current != null)
            {
                if (_current.OpenEnded) return;
                if (now < _curEnd) return;
                Complete(now, local);
                if (_current != null) return; // 이어지는 행동(졸기→잠 등)이 시작됨
            }

            Decide(now, local, userIdleSeconds);
        }

        private void Init(double now, DateTime local)
        {
            _init = true;
            _prev = now;
            _idleSince = now;
            _nextDecision = now + Rand(_cfg.DecisionMin * 0.5, _cfg.DecisionMax * 0.5);
            _nextScene = now + Rand(_cfg.SceneMin, _cfg.SceneMax);
            _nextRest = now + RestInterval(local);
            // 첫 tick은 스케줄만 배치하고 아무 행동도 시작하지 않는다.
        }

        private void EnsureInit(double now, DateTime local)
        {
            if (!_init) Init(now, local);
        }

        private void Decide(double now, DateTime local, double? idle)
        {
            if (Dragging) return;

            bool restDue = now >= _nextRest;
            if (!restDue && IdleHintEnabled && idle.HasValue && idle.Value >= _cfg.IdleHintSeconds
                && now - _lastRestEnd >= 300)
                restDue = true;

            if (restDue)
            {
                StartAction(Choreo.Doze(PosX, FaceLeft), now);
                _nextRest = now + RestInterval(local); // 잠에서 깰 때 다시 잡힌다
                return;
            }

            if (Quiet) return;
            if (now < _nextDecision) return;

            ActionPlan plan = null;
            if (now >= _nextScene)
            {
                plan = PickScene(now);
                if (plan != null) _nextScene = now + plan.TotalDuration + Rand(_cfg.SceneMin, _cfg.SceneMax);
            }
            if (plan == null) plan = PickBasic(now);

            if (plan != null) StartAction(plan, now);
            _nextDecision = (plan != null ? now + plan.TotalDuration : now) + Rand(_cfg.DecisionMin, _cfg.DecisionMax);
        }

        private ActionPlan PickScene(double now)
        {
            var cands = new List<ActionKind>(3);
            var weights = new List<double>(3);
            AddSceneCandidate(cands, weights, ActionKind.SnackScene, 1.0, now);
            AddSceneCandidate(cands, weights, ActionKind.PlayScene, 1.0, now);
            if (CushionMessy) AddSceneCandidate(cands, weights, ActionKind.CushionScene, 1.2, now);
            if (cands.Count == 0) return null;

            if (cands.Count > 1)
            {
                int idx = cands.IndexOf(_lastScene); // 직전 장면은 연속으로 안 나오게
                if (idx >= 0) { cands.RemoveAt(idx); weights.RemoveAt(idx); }
            }

            var kind = cands[PickIndex(weights)];
            _lastScene = kind;
            switch (kind)
            {
                case ActionKind.SnackScene: return Choreo.SnackScene(PosX, FaceLeft, _rng);
                case ActionKind.CushionScene: return Choreo.CushionScene(PosX, FaceLeft, _rng);
                default: return Choreo.PlayScene(PosX, FaceLeft, _rng);
            }
        }

        private void AddSceneCandidate(List<ActionKind> c, List<double> w, ActionKind k, double weight, double now)
        {
            if (IsOffCooldown(k, _cfg.SceneCooldown, now)) { c.Add(k); w.Add(weight); }
        }

        private ActionPlan PickBasic(double now)
        {
            var kinds = new List<ActionKind>(3);
            var weights = new List<double>(3);
            if (IsOffCooldown(ActionKind.LookAround, 15, now)) { kinds.Add(ActionKind.LookAround); weights.Add(1.4); }
            if (IsOffCooldown(ActionKind.Stretch, 120, now)) { kinds.Add(ActionKind.Stretch); weights.Add(0.8); }
            if (IsOffCooldown(ActionKind.Walk, 20, now)) { kinds.Add(ActionKind.Walk); weights.Add(1.0); }
            if (kinds.Count == 0) return null;
            for (int i = 0; i < kinds.Count; i++) if (kinds[i] == _lastBasic) weights[i] *= 0.3;

            var k = kinds[PickIndex(weights)];
            _lastBasic = k;
            switch (k)
            {
                case ActionKind.LookAround:
                    return Choreo.Hold(ActionKind.LookAround, ClipNames.Look, Choreo.LookDuration, PosX, FaceLeft);
                case ActionKind.Stretch:
                    return Choreo.Hold(ActionKind.Stretch, ClipNames.Stretch, Choreo.StretchDuration, PosX, FaceLeft);
                default:
                    return Choreo.Walk(PosX, Choreo.PickWalkTarget(PosX, _rng));
            }
        }

        // =====================================================================
        // 사용자 입력 (즉시 처리, 자율 추첨과 경쟁하지 않음)
        // =====================================================================

        /// <summary>클릭. 반응이 시작됐으면 true (같은 호출 안에서 곧바로 Evaluate에 반영된다).</summary>
        public bool Click(double now, DateTime local)
        {
            EnsureInit(now, local);
            if (Hidden || Dragging) return false;

            if (Life != LifeState.Awake)
            {
                bool wasAsleep = Life == LifeState.Asleep;
                EndCurrent(now, EndReason.Interrupted);
                Life = LifeState.Awake;
                _lastClickReaction = now;
                _lastRestEnd = now;
                if (wasAsleep) WriteDiary(local, "WOKEN", 2, Texts.Woken);
                StartAction(Choreo.Hold(ActionKind.WakeReact, ClipNames.React, Choreo.WakeReactDuration, PosX, FaceLeft), now);
                Bubble(now, "!?", 1.6, true);
                _nextRest = now + RestInterval(local);
                _nextDecision = now + Choreo.WakeReactDuration + Rand(_cfg.DecisionMin, _cfg.DecisionMax);
                return true;
            }

            if (now - _lastClickReaction < _cfg.ClickCooldown) return false; // 연속 클릭은 하나로 묶음
            _lastClickReaction = now;

            if (_current != null && ActionInfo.IsScene(_current.Kind))
            {
                // 장면은 클릭으로 끊지 않고, 잠깐 돌아보는 반응만 겹쳐 보여준다.
                _overlayStart = now;
                _overlayUntil = now + _cfg.SceneOverlayReact;
                return true;
            }

            StartAction(Choreo.Hold(ActionKind.ClickReact, ClipNames.React, Choreo.ClickReactDuration, PosX, FaceLeft), now);
            _nextDecision = Math.Max(_nextDecision, now + Choreo.ClickReactDuration + 5);
            return true;
        }

        public void DragStart(double now, DateTime local)
        {
            EnsureInit(now, local);
            if (Hidden || Dragging) return;
            EndCurrent(now, EndReason.Interrupted);
            Life = LifeState.Awake;
            Dragging = true;
            _overlayUntil = double.NegativeInfinity;
            StartAction(Choreo.Drag(PosX, FaceLeft), now);
        }

        public void DragEnd(double now, DateTime local)
        {
            if (!Dragging) return;
            Dragging = false;
            EndCurrent(now, EndReason.Completed);
            CushionMessy = true; // 통째로 옮겨지면서 방석이 흐트러짐
            WriteDiary(local, "MOVED", 2, Texts.Moved);
            StartAction(Choreo.Hold(ActionKind.DropLook, ClipNames.Look, Choreo.DropLookDuration, PosX, FaceLeft), now);
            Bubble(now, "여긴…?", 2.0, false);
            _nextDecision = now + Choreo.DropLookDuration + Rand(_cfg.DecisionMin * 0.5, _cfg.DecisionMax * 0.5);
        }

        /// <summary>조용히 모드: 자발적인 움직임·말풍선을 멈춘다. 졸기/잠과 클릭 반응은 유지.</summary>
        public void SetQuiet(bool quiet, double now, DateTime local)
        {
            EnsureInit(now, local);
            if (quiet == Quiet) return;
            Quiet = quiet;
            if (quiet)
            {
                if (_current != null && (ActionInfo.IsScene(_current.Kind) || _current.Kind == ActionKind.Walk
                    || _current.Kind == ActionKind.LookAround || _current.Kind == ActionKind.Stretch))
                    EndCurrent(now, EndReason.Interrupted);
            }
            else
            {
                _nextDecision = now + Rand(5, 15);
            }
        }

        /// <summary>숨김: 진행 중이던 일은 중단(완료로 기록 안 함), 숨어 있는 동안은 아무 행동도 만들지 않는다.</summary>
        public void SetHidden(bool hidden, double now, DateTime local)
        {
            EnsureInit(now, local);
            if (hidden == Hidden) return;
            if (hidden)
            {
                Dragging = false;
                EndCurrent(now, EndReason.Interrupted);
                Life = LifeState.Awake;
                Hidden = true;
            }
            else
            {
                Hidden = false;
                _idleSince = now;
                _nextDecision = now + Rand(3, 10);
                _nextScene = Math.Max(_nextScene, now + _cfg.SceneMin * 0.5);
                _nextRest = now + RestInterval(local);
            }
        }

        /// <summary>디버그/테스트용: 특정 행동을 바로 시작.</summary>
        public void DebugStart(ActionKind kind, double now, DateTime local)
        {
            EnsureInit(now, local);
            if (Hidden || Dragging) return;
            ActionPlan p;
            switch (kind)
            {
                case ActionKind.SnackScene: p = Choreo.SnackScene(PosX, FaceLeft, _rng); break;
                case ActionKind.PlayScene: p = Choreo.PlayScene(PosX, FaceLeft, _rng); break;
                case ActionKind.CushionScene: CushionMessy = true; p = Choreo.CushionScene(PosX, FaceLeft, _rng); break;
                case ActionKind.Doze: p = Choreo.Doze(PosX, FaceLeft); break;
                case ActionKind.Sleep: p = Choreo.Sleep(SleepDuration(local), PosX, FaceLeft); break;
                case ActionKind.Walk: p = Choreo.Walk(PosX, Choreo.PickWalkTarget(PosX, _rng)); break;
                case ActionKind.Stretch: p = Choreo.Hold(kind, ClipNames.Stretch, Choreo.StretchDuration, PosX, FaceLeft); break;
                default: p = Choreo.Hold(ActionKind.LookAround, ClipNames.Look, Choreo.LookDuration, PosX, FaceLeft); break;
            }
            StartAction(p, now);
        }

        // =====================================================================
        // 행동 시작/종료
        // =====================================================================

        private void StartAction(ActionPlan plan, double now)
        {
            if (_current != null) EndCurrent(now, EndReason.Interrupted);
            _current = plan;
            _curStart = now;
            _curEnd = plan.OpenEnded ? double.PositiveInfinity : now + plan.TotalDuration;
            _lastUsed[plan.Kind] = now;
            StartedCount++;

            if (plan.Kind == ActionKind.Doze) Life = LifeState.Drowsy;
            else if (plan.Kind == ActionKind.Sleep) Life = LifeState.Asleep;
            else Life = LifeState.Awake;

            Emit(new PetEvent { Kind = PetEventKind.ActionStarted, Action = plan.Kind, Time = now });
        }

        private void EndCurrent(double now, EndReason reason)
        {
            if (_current == null) return;
            var plan = _current;
            if (reason == EndReason.Completed)
            {
                PosX = plan.OpenEnded ? Evaluate(now).X : plan.EndX;
                FaceLeft = plan.OpenEnded ? FaceLeft : plan.EndFaceLeft;
                CompletedCount++;
            }
            else
            {
                var f = Evaluate(now); // 끊긴 순간의 위치에서 그대로 멈춤 (순간이동 없음)
                PosX = f.X;
                FaceLeft = f.FaceLeft;
                InterruptedCount++;
            }
            _current = null;
            _idleSince = now;
            _overlayUntil = double.NegativeInfinity;
            Emit(new PetEvent { Kind = PetEventKind.ActionEnded, Action = plan.Kind, Reason = reason, Time = now });
        }

        private void Complete(double now, DateTime local)
        {
            var plan = _current;
            EndCurrent(now, EndReason.Completed);

            switch (plan.Kind)
            {
                case ActionKind.Doze:
                    StartAction(Choreo.Sleep(SleepDuration(local), PosX, FaceLeft), now);
                    break;

                case ActionKind.Sleep:
                    Life = LifeState.Awake;
                    _lastRestEnd = now;
                    CushionMessy = true; // 자고 나면 방석이 흐트러져 있음
                    WriteDiary(local, "SLEEP", 1, Texts.Slept);
                    _nextRest = now + RestInterval(local);
                    StartAction(Choreo.Hold(ActionKind.Stretch, ClipNames.Stretch, Choreo.StretchDuration, PosX, FaceLeft), now);
                    _nextDecision = now + Choreo.StretchDuration + Rand(_cfg.DecisionMin, _cfg.DecisionMax);
                    break;

                case ActionKind.SnackScene:
                    WriteDiary(local, "SNACK", 3, Texts.Snack[plan.TextVariant % Texts.Snack.Length]);
                    Bubble(now, "냠냠, 끝!", 2.5, false);
                    break;

                case ActionKind.CushionScene:
                    CushionMessy = plan.OutcomeCushionMessy;
                    WriteDiary(local, "CUSHION", 3, Texts.Cushion[plan.OutcomeCushionMessy ? 1 : 0]);
                    Bubble(now, plan.OutcomeCushionMessy ? "…에이." : "반듯!", 2.5, false);
                    break;

                case ActionKind.PlayScene:
                    WriteDiary(local, "PLAY", 3, Texts.Play[plan.TextVariant % Texts.Play.Length]);
                    Bubble(now, "시시해졌다.", 2.5, false);
                    break;

                default:
                    Life = LifeState.Awake;
                    break;
            }
        }

        private void HandleLongGap(double now, DateTime local)
        {
            // 앱이 멈춰 있던(절전 등) 동안의 일은 없던 일로 친다. 밀린 행동을 몰아서 재생하지도 않는다.
            if (_current != null && !_current.OpenEnded) EndCurrent(now, EndReason.Interrupted);
            if (!Dragging) Life = LifeState.Awake;
            _idleSince = now;
            _nextDecision = now + Rand(_cfg.DecisionMin, _cfg.DecisionMax);
            _nextScene = now + Rand(_cfg.SceneMin, _cfg.SceneMax);
            _nextRest = now + RestInterval(local);
        }

        // =====================================================================
        // 화면 상태
        // =====================================================================

        public Frame Evaluate(double now)
        {
            if (_current == null)
            {
                return new Frame
                {
                    Clip = ClipNames.Idle,
                    ClipTime = Math.Max(0, now - _idleSince),
                    ClipDuration = double.PositiveInfinity,
                    X = PosX,
                    FaceLeft = FaceLeft,
                    CushionMessy = CushionMessy,
                    Action = ActionKind.None,
                    Snack = new PropState { Scale = 1 },
                    Ball = new PropState { Scale = 1 }
                };
            }

            double t = now - _curStart;
            if (!_current.OpenEnded) t = Math.Min(t, _current.TotalDuration);
            var f = Choreo.Evaluate(_current, t, CushionMessy);
            if (now < _overlayUntil && ActionInfo.IsScene(_current.Kind))
            {
                f.Clip = ClipNames.React;
                f.ClipTime = now - _overlayStart;
                f.ClipDuration = _cfg.SceneOverlayReact;
            }
            return f;
        }

        // =====================================================================
        // 보조
        // =====================================================================

        private void WriteDiary(DateTime local, string key, int priority, string text)
        {
            if (_diary.Add(local, key, priority, text))
                Emit(new PetEvent { Kind = PetEventKind.DiaryChanged, Text = text, Time = _prev });
        }

        private void Bubble(double now, string text, double duration, bool force)
        {
            if (Hidden) return;
            if (!force && Quiet) return;
            if (!force && now - _lastBubble < _cfg.BubbleMinGap) return;
            _lastBubble = now;
            Emit(new PetEvent { Kind = PetEventKind.Bubble, Text = text, Duration = duration, Time = now });
        }

        private void Emit(PetEvent e)
        {
            while (_events.Count >= 64) _events.Dequeue();
            _events.Enqueue(e);
        }

        private bool IsOffCooldown(ActionKind k, double cooldown, double now)
        {
            double t;
            return !_lastUsed.TryGetValue(k, out t) || now - t >= cooldown;
        }

        private int PickIndex(List<double> weights)
        {
            double total = 0;
            for (int i = 0; i < weights.Count; i++) total += weights[i];
            double roll = _rng.NextDouble() * total, acc = 0;
            for (int i = 0; i < weights.Count; i++) { acc += weights[i]; if (roll < acc) return i; }
            return weights.Count - 1;
        }

        private double Rand(double min, double max) { return min + _rng.NextDouble() * (max - min); }

        private static bool IsNight(DateTime local) { return local.Hour >= 1 && local.Hour < 6; }

        private double RestInterval(DateTime local)
        {
            return IsNight(local) ? Rand(_cfg.NightRestMin, _cfg.NightRestMax) : Rand(_cfg.RestMin, _cfg.RestMax);
        }

        private double SleepDuration(DateTime local)
        {
            return IsNight(local) ? Rand(_cfg.NightSleepMin, _cfg.NightSleepMax) : Rand(_cfg.SleepMin, _cfg.SleepMax);
        }

        public static class Texts
        {
            public static readonly string[] Snack =
            {
                "과자를 방석까지 끌고 와서 다 먹었다. 조금 무거웠다.",
                "과자 하나를 자리까지 옮겨 와서 냠냠 먹었다."
            };
            public static readonly string[] Cushion =
            {
                "흐트러진 방석을 반듯하게 정리했다.",
                "방석을 정리했는데 앉자마자 다시 비뚤어졌다."
            };
            public static readonly string[] Play =
            {
                "종이공을 쫓아다니다가 멀리 굴려 버리고 흥미를 잃었다.",
                "종이공을 몇 번 툭툭 치며 놀았다."
            };
            public const string Slept = "방석 위에서 한숨 푹 잤다.";
            public const string Woken = "자고 있는데 누가 콕 찔러서 깼다.";
            public const string Moved = "누가 나를 번쩍 들어서 다른 곳으로 옮겼다.";
        }
    }
}
