using System;
using UnityEngine;


namespace TbhCombatTracker
{
    /// <summary>
    /// 盯着游戏的关卡状态，把**原始信号**变成事件交给 <see cref="DamageTracker"/>：
    ///   1. <c>UI_Stage.text_StageName</c> 显示的关卡名变了
    ///   2. <c>StageManager.b_StageStart</c> 变了
    ///   3. <c>StageManager.stageState</c>（关卡内的波次状态）变了
    ///
    /// 这里**不做分段决定**——什么时候切段、段叫什么名字，是 <see cref="CombatParser"/> 按这些信号算的。
    /// 信号原样进日志，所以以后分段规则改了，旧日志重新导入也按新规则切。
    ///
    /// 三个都是 <c>[SerializeField]</c> 的原名，混淆器动不了，抗游戏更新。
    /// 波次状态每波都变好几次，只进战斗日志，不再刷到 LogOutput.log。
    /// </summary>
    internal static class StageWatcher
    {
        private static GStageManager _mgr;
        private static GUiStage _ui;
        private static float _nextLookup;
        private static bool _disabled;

        private static GStageState _lastState = GStageState.NONE;
        private static bool _hasState;
        private static bool _lastStageStart;
        private static bool _hasStageStart;
        private static string _lastStageName;

        public static void Tick()
        {
            if (_disabled) return;

            try
            {
                Acquire();

                // 关卡名在标志之前：同一帧里两个都变时，解析器按这个顺序处理（反过来它也能自己纠正）
                WatchStageName();
                WatchStageStartFlag();
                WatchStageState();
            }
            catch (Exception e)
            {
                _disabled = true;
                Mod.Log.Warning($"读取关卡信号出错，已停用关卡分段（退回按空闲时间分段）：{e}");
                try { Mod.Config.SegmentByStage.Value = false; } catch { /* 无所谓 */ }
                DamageTracker.DisableStageSegmentation();
            }
        }

        private static void WatchStageName()
        {
            var name = ReadStageName();
            if (string.IsNullOrEmpty(name) || name == _lastStageName) return;

            var prev = _lastStageName;
            _lastStageName = name;
            Mod.Log.Msg($"[stage] 关卡名 '{prev}' -> '{name}'");
            DamageTracker.StageName(name);
        }

        private static void WatchStageStartFlag()
        {
            if (_mgr == null) return;

            var flag = _mgr.b_StageStart;
            if (_hasStageStart && flag == _lastStageStart) return;

            var prev = _hasStageStart && _lastStageStart;
            _lastStageStart = flag;
            _hasStageStart = true;
            Mod.Log.Msg($"[stage] b_StageStart {prev} -> {flag}");
            DamageTracker.StageStart(flag);
        }

        private static void WatchStageState()
        {
            if (_mgr == null) return;

            var state = _mgr.stageState;
            if (_hasState && state == _lastState) return;

            _lastState = state;
            _hasState = true;
            DamageTracker.StageWave(state.ToString());
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

            // 走 FindObjectOfType 而不是单例基类的静态属性：
            // Il2CppInterop 对泛型基类静态成员的支持不稳，这条路更实在。
            if (_mgr == null) _mgr = UnityEngine.Object.FindObjectOfType<GStageManager>();
            if (_ui == null) _ui = UnityEngine.Object.FindObjectOfType<GUiStage>();
        }
    }
}
