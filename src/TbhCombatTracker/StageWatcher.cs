using System;
using UnityEngine;

using GStageManager = TaskbarHero.StageManager;
using GStageState = TaskbarHero.EStageState;
using GUiStage = TaskbarHero.UI_Stage;

namespace TbhCombatTracker
{
    /// <summary>
    /// 按关卡自动分段统计。
    ///
    /// 【踩过的坑】最早用 <c>EStageState</c> 进入 <c>MONSTERSPAWN</c> 当分段点，结果是
    /// **每波刷怪都切一次**——这个状态机描述的是关卡内的波次循环
    /// （MONSTERSPAWN → BATTLE → MONSTERSPAWN → …），不是关卡边界。
    /// 佐证：<c>UI_Stage</c> 里有 <c>StageWaveIconSliderController</c>，波次确实是关卡的子单位。
    ///
    /// 现在改用两个**关卡级**信号，任一触发即切段（带去抖，避免同时触发切两次）：
    ///   1. <c>UI_Stage.text_StageName</c> 显示的关卡名变化——顺带拿来当面板标题
    ///   2. <c>StageManager.b_StageStart</c> 由 false 变 true
    ///
    /// 两个都是 <c>[SerializeField]</c> 的原名，混淆器动不了，抗游戏更新。
    /// 三个信号的每次跃迁都会记一条日志，方便核对到底哪个才是真正的关卡边界。
    /// </summary>
    internal static class StageWatcher
    {
        /// <summary>切段后的静默期，防止两个信号同时触发切出一个空段。</summary>
        private const float DebounceSeconds = 1.5f;

        private static GStageManager _mgr;
        private static GUiStage _ui;
        private static float _nextLookup;
        private static bool _disabled;

        private static GStageState _lastState = GStageState.NONE;
        private static bool _hasState;
        private static bool _lastStageStart;
        private static bool _hasStageStart;
        private static string _lastStageName;

        private static int _stageCount;
        private static float _lastSegmentAt = float.NegativeInfinity;

        public static void Tick()
        {
            if (_disabled || Mod.Config?.SegmentByStage?.Value != true) return;

            try
            {
                Acquire();

                WatchStageState();          // 只记日志，不再拿来切段
                var byName = WatchStageName();
                var byFlag = WatchStageStartFlag();

                if (byName != null || byFlag)
                    Segment(byName);
            }
            catch (Exception e)
            {
                _disabled = true;
                Mod.Log.Warning($"关卡分段出错，已停用（退回按空闲时间分段）：{e}");
                try { Mod.Config.SegmentByStage.Value = false; } catch { /* 无所谓 */ }
            }
        }

        /// <summary>波次状态：只用于观察，不作为分段依据。</summary>
        private static void WatchStageState()
        {
            if (_mgr == null) return;

            var state = _mgr.stageState;
            if (_hasState && state == _lastState) return;

            var prev = _hasState ? _lastState : GStageState.NONE;
            _lastState = state;
            _hasState = true;
            Mod.Log.Msg($"[stage] 波次状态 {prev} -> {state}");
        }

        /// <summary>关卡名变了就是换关卡；返回新名字，没变返回 null。</summary>
        private static string WatchStageName()
        {
            var name = ReadStageName();
            if (string.IsNullOrEmpty(name) || name == _lastStageName) return null;

            var prev = _lastStageName;
            _lastStageName = name;
            Mod.Log.Msg($"[stage] 关卡名 '{prev}' -> '{name}'");

            // 第一次读到名字时不切段——那只是我们刚拿到引用，不代表换关了
            return prev == null ? null : name;
        }

        /// <summary>b_StageStart 由 false 变 true 视为进入新关卡。</summary>
        private static bool WatchStageStartFlag()
        {
            if (_mgr == null) return false;

            var flag = _mgr.b_StageStart;
            if (_hasStageStart && flag == _lastStageStart) return false;

            var prev = _hasStageStart && _lastStageStart;
            var first = !_hasStageStart;
            _lastStageStart = flag;
            _hasStageStart = true;
            Mod.Log.Msg($"[stage] b_StageStart {prev} -> {flag}");

            return !first && !prev && flag;
        }

        private static void Segment(string stageName)
        {
            var now = Time.realtimeSinceStartup;
            if (now - _lastSegmentAt < DebounceSeconds) return;
            _lastSegmentAt = now;

            _stageCount++;
            var label = string.IsNullOrEmpty(stageName) ? $"关卡 #{_stageCount}" : stageName;
            DamageTracker.BeginStage(label);
            Mod.Log.Msg($"[stage] === 开始统计 {label} ===");
        }

        private static string ReadStageName()
        {
            if (_ui == null) return null;
            try
            {
                var tmp = _ui.text_StageName;
                return tmp == null ? null : tmp.text;
            }
            catch
            {
                return null;
            }
        }

        private static void Acquire()
        {
            if (_mgr != null && _ui != null) return;

            // 场景切换会把引用换掉，拿不到时隔一会儿再找，别每帧 FindObjectOfType
            var now = Time.realtimeSinceStartup;
            if (now < _nextLookup) return;
            _nextLookup = now + 2f;

            // 走 FindObjectOfType 而不是单例基类 nu<T> 的静态属性：
            // Il2CppInterop 对泛型基类静态成员的支持不稳，这条路更实在。
            if (_mgr == null) _mgr = UnityEngine.Object.FindObjectOfType<GStageManager>();
            if (_ui == null) _ui = UnityEngine.Object.FindObjectOfType<GUiStage>();
        }
    }
}
