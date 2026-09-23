using System;
using System.Collections.Generic;

namespace DesktopPet.Core
{
    public enum LifeState { Awake, Drowsy, Asleep }

    public enum ActionKind
    {
        None,
        LookAround,
        Stretch,
        Walk,
        Doze,
        Sleep,
        ClickReact,
        WakeReact,
        Drag,
        DropLook,
        SnackScene,
        CushionScene,
        PlayScene
    }

    public enum EndReason { Completed, Interrupted }

    public enum PetEventKind { ActionStarted, ActionEnded, Bubble, DiaryChanged }

    public struct PetEvent
    {
        public PetEventKind Kind;
        public ActionKind Action;
        public EndReason Reason;
        public string Text;
        public double Duration;
        public double Time;
    }

    /// <summary>소품 하나의 한 순간 상태. Visible=false면 그리지 않는다.</summary>
    public struct PropState
    {
        public bool Visible;
        public double X;
        public double Y;
        public double Scale;
    }

    /// <summary>
    /// 한 순간의 화면 상태. 행동 + 경과시간만으로 계산되는 순수 결과라서,
    /// 행동이 중단되면 소품도 같이 사라진다(화면에 소품이 남는 버그가 구조적으로 생길 수 없다).
    /// 좌표 단위는 논리 픽셀, 0 = 방석 중앙, 오른쪽이 +.
    /// </summary>
    public struct Frame
    {
        public string Clip;
        public double ClipTime;
        public double ClipDuration;
        public double X;
        public bool FaceLeft;
        public bool CushionMessy;
        public PropState Snack;
        public PropState Ball;
        public ActionKind Action;
    }

    /// <summary>행동 계획의 한 구간. 캐릭터는 FromX→ToX로 선형 이동한다.</summary>
    public sealed class Segment
    {
        public double Duration;
        public string Clip;
        public double FromX;
        public double ToX;
        public bool FaceLeft;
        public int CushionOverride; // -1: 세계 상태 따름, 0: 반듯, 1: 흐트러짐
        public PropKey Snack;
        public PropKey Ball;

        public Segment()
        {
            CushionOverride = -1;
        }
    }

    public sealed class PropKey
    {
        public double FromX, ToX;
        public double FromScale = 1, ToScale = 1;
        public double BounceHeight;   // 0이면 안 튐
        public int BounceCount;

        public static PropKey Still(double x)
        {
            return new PropKey { FromX = x, ToX = x };
        }

        public static PropKey Move(double from, double to)
        {
            return new PropKey { FromX = from, ToX = to };
        }
    }

    public sealed class ActionPlan
    {
        public ActionKind Kind;
        public readonly List<Segment> Segments = new List<Segment>();
        public bool OpenEnded;          // 드래그처럼 끝 시각이 없는 행동
        public bool OutcomeCushionMessy; // 방석 장면이 끝났을 때의 방석 상태
        public int TextVariant;

        public double TotalDuration
        {
            get
            {
                double sum = 0;
                for (int i = 0; i < Segments.Count; i++) sum += Segments[i].Duration;
                return sum;
            }
        }

        public double EndX
        {
            get { return Segments.Count == 0 ? 0 : Segments[Segments.Count - 1].ToX; }
        }

        public bool EndFaceLeft
        {
            get { return Segments.Count == 0 ? false : Segments[Segments.Count - 1].FaceLeft; }
        }
    }

    public static class ClipNames
    {
        public const string Idle = "idle";
        public const string Look = "look";
        public const string Stretch = "stretch";
        public const string Walk = "walk";
        public const string Doze = "doze";
        public const string Sleep = "sleep";
        public const string React = "react";
        public const string Drag = "drag";
        public const string SnackCarry = "snack_carry";
        public const string SnackEat = "snack_eat";
        public const string CushionFix = "cushion_fix";
        public const string BallPlay = "ball_play";

        public static readonly string[] All =
        {
            Idle, Look, Stretch, Walk, Doze, Sleep, React, Drag, SnackCarry, SnackEat, CushionFix, BallPlay
        };
    }

    public static class ActionInfo
    {
        public static bool IsScene(ActionKind k)
        {
            return k == ActionKind.SnackScene || k == ActionKind.CushionScene || k == ActionKind.PlayScene;
        }

        public static bool IsBasic(ActionKind k)
        {
            return k == ActionKind.LookAround || k == ActionKind.Stretch || k == ActionKind.Walk
                || k == ActionKind.ClickReact || k == ActionKind.WakeReact || k == ActionKind.DropLook;
        }
    }
}
