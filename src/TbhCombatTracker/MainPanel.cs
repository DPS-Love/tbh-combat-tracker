using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;

namespace TbhCombatTracker
{
    /// <summary>
    /// 战斗记录主面板，形制参考 ACT 的主窗口：左边是分段列表，右边是选中那段的
    /// 战斗员表格、每秒数值曲线、选中角色的拆分表和饼图。
    ///
    /// 数据源是一个 <see cref="Session"/>：本局的实时会话，或者从日志文件导入、重新解析出来的会话。
    /// 两者走同一个解析器，界面完全一样。
    ///
    /// 这个构建里 IMGUI 的滚动视图被裁剪了（BeginScrollView / EndScrollView 是会抛异常的桩），
    /// 列表滚动是自己按鼠标滚轮算的，只画看得见的那几行。
    ///
    /// 【生存路径】从 TrackerBehaviour.OnGUI 直接调过来，这里不能引用任何游戏类型。
    /// </summary>
    internal static class MainPanel
    {
        public static bool Visible;

        private const int WindowId = 0x7B4F;           // 和浮窗、明细窗口错开
        private const float W = 800f, H = 506f;
        private const float Pad = 10f;
        private const float TitleH = 17f;
        private const float ToolbarY = 21f, ToolbarH = 19f;
        private const float BodyY = 48f;
        private const float ListW = 250f;
        private const float RowH = 18f;
        private const float RightX = Pad + ListW + 10f;
        private const float RightW = W - RightX - Pad;
        private const float TableRowH = 16f;
        private const int MinTableRows = 3, MaxTableRows = 7;
        private const float MinChartH = 64f;
        private const float BreakRowH = 15f;
        private const int BreakRows = 8;
        private const float PieSize = 120f;
        /// <summary>拆分区（维度页签 + 表头 + 8 行 + "另有 n 项"）固定贴在底部，曲线填满表格和它之间。</summary>
        private const float BreakdownH = 22f + BreakRowH * (BreakRows + 2);

        private static Rect _rect = new Rect(24f, 150f, W, H);

        // ---- 数据源 ----
        private static Session _imported;                  // null = 看本局
        private static volatile Session _importResult;     // 后台解析完放这里，主线程取走
        private static volatile string _importError;
        private static volatile bool _importing;
        private static string _importingName;

        // ---- 选择状态 ----
        private static Encounter _selected;                // 本局时 null = 跟着实时的当前段
        private static TrackerView _view = TrackerView.Outgoing;
        private static int _source;                        // 选中的来源；这一段里没有它就自动看第一名
        private static bool _hasSource;
        private static Dimension _dim = Dimension.Skill;
        private static int _listTop;

        // ---- 导入列表 ----
        private static bool _pickerOpen;
        private static List<FileInfo> _files = new List<FileInfo>();
        private static int _fileTop;

        private static string _status;
        private static float _statusUntil;

        private static readonly PieChart Pie = new PieChart();
        private static readonly LineChart Chart = new LineChart();
        private static double _chartPeak;

        private static GUIStyle _text, _textRight, _small, _smallRight, _bold, _head, _headRight,
                                _center, _centerSmall, _plain, _tab, _live;
        private static Texture2D _white;
        private static bool _stylesReady;
        private static int _failures;

        private static readonly Color Unknown = new Color(0.62f, 0.62f, 0.62f);

        public static Rect CurrentRect => _rect;

        public static bool HitTest(Vector2 clientPoint)
        {
            if (!Visible) return false;
            try { return _rect.Contains(clientPoint / Overlay.CurrentScale); }
            catch { return false; }
        }

        public static void Toggle() => Visible = !Visible;

        // ================================================================== 绘制入口

        public static void Draw()
        {
            if (!Visible) return;
            PollImport();

            var oldMatrix = GUI.matrix;
            try
            {
                EnsureStyles();

                var scale = Overlay.CurrentScale;
                if (Math.Abs(scale - 1f) > 0.001f)
                    GUI.matrix = Matrix4x4.TRS(Vector3.zero, Quaternion.identity, new Vector3(scale, scale, 1f));

                _rect.width = W;
                _rect.height = H;
                _rect = GUI.Window(WindowId, _rect, (GUI.WindowFunction)DrawWindow, Strings.MainTitle);

                // 别拖到够不着的地方去
                var sw = Screen.width / scale;
                var sh = Screen.height / scale;
                _rect.x = Mathf.Clamp(_rect.x, 80f - _rect.width, Mathf.Max(0f, sw - 80f));
                _rect.y = Mathf.Clamp(_rect.y, 0f, Mathf.Max(0f, sh - 30f));
            }
            catch (Exception e)
            {
                Fail(e);
            }
            finally
            {
                GUI.matrix = oldMatrix;
            }
        }

        private static void DrawWindow(int id)
        {
            // GUI.Window 的回调是跨 IL2CPP 边界调过来的，异常必须在这里截住
            try
            {
                DrawBody();
                _failures = 0;
            }
            catch (Exception e)
            {
                Fail(e);
            }
        }

        /// <summary>连续画失败就关掉面板，别每帧刷异常；浮窗不受影响，按热键或「记录」可以重新打开。</summary>
        private static void Fail(Exception e)
        {
            if (++_failures < 3) return;
            _failures = 0;
            Visible = false;
            Mod.Log.Error($"战斗记录面板连续绘制失败，已关闭：{e}");
        }

        private static void DrawBody()
        {
            var live = _imported == null;
            var session = _imported ?? DamageTracker.Live;

            // 面板盖在桌面上，底下什么都有，要一块够暗的底才看得清
            Fill(new Rect(0f, TitleH, _rect.width, _rect.height - TitleH), new Color(0.06f, 0.06f, 0.08f, 0.94f));

            var rows = Rows(session);
            var enc = Resolve(session, live, rows);

            DrawToolbar(session, live, enc);
            DrawList(new Rect(Pad, BodyY, ListW, _rect.height - BodyY - Pad), session, live, rows, enc);

            var right = new Rect(RightX, BodyY, RightW, _rect.height - BodyY - Pad);
            if (_pickerOpen) DrawPicker(right);
            else if (enc == null) Label(new Rect(right.x, right.y + 24f, right.width, 18f), Strings.NoEncounters, _small);
            else DrawEncounter(right, session, live, enc);

            // 只在标题栏上能拖：面板里到处是可点的行，整窗拖会和点击打架
            GUI.DragWindow(new Rect(0f, 0f, 10000f, TitleH + 2f));
        }

        // ================================================================== 数据

        /// <summary>列表里的段，最新的在上面；本局进行中的那段排第一。</summary>
        private static List<Encounter> Rows(Session s)
        {
            var list = new List<Encounter>(s.Encounters.Count + 1);
            if (s.Current != null) list.Add(s.Current);
            for (var i = s.Encounters.Count - 1; i >= 0; i--) list.Add(s.Encounters[i]);
            return list;
        }

        private static Encounter Resolve(Session s, bool live, List<Encounter> rows)
        {
            if (rows.Count == 0) return null;
            if (live)
            {
                if (_selected == null) return s.Current;
                if (s.Encounters.Contains(_selected)) return _selected;
                _selected = null;   // 选中的那段被挤出保留上限了，回到实时
                return s.Current;
            }
            if (_selected == null || !rows.Contains(_selected)) _selected = rows[0];
            return _selected;
        }

        // ================================================================== 工具栏

        private static void DrawToolbar(Session session, bool live, Encounter enc)
        {
            var x = _rect.width - Pad;
            if (Button(ref x, 22f, "×")) Visible = false;
            if (Button(ref x, 74f, Strings.BtnExportCsv))
            {
                var path = DamageTracker.ExportCsv(enc, live ? "live" : "import");
                SetStatus(path != null ? Strings.Exported(Path.GetFileName(path)) : Strings.ExportFailed);
            }
            if (Button(ref x, 70f, Strings.BtnLogFolder)) OpenFolder();

            GUI.enabled = !_importing;
            if (Button(ref x, 56f, Strings.BtnImport))
            {
                _pickerOpen = !_pickerOpen;
                if (_pickerOpen) RefreshFiles();
            }
            GUI.enabled = true;

            if (!live && Button(ref x, 78f, Strings.BtnBackToLive))
            {
                _imported = null;
                _selected = null;
                _listTop = 0;
            }

            // 左边剩下的地方给状态：正在解析、导入结果、导出到了哪
            string status = null;
            if (_importing) status = Strings.Parsing(_importingName);
            else if (_status != null && Time.unscaledTime < _statusUntil) status = _status;
            if (status != null) Label(new Rect(Pad, ToolbarY, x - Pad - 6f, ToolbarH), status, _small);
        }

        /// <summary>从右往左排按钮。</summary>
        private static bool Button(ref float right, float width, string label)
        {
            right -= width;
            var clicked = GUI.Button(new Rect(right, ToolbarY, width, ToolbarH), label);
            right -= 4f;
            return clicked;
        }

        // ================================================================== 左侧：分段列表

        private static void DrawList(Rect r, Session session, bool live, List<Encounter> rows, Encounter selected)
        {
            Fill(r, new Color(1f, 1f, 1f, 0.03f));

            // 列表标题说明数据来源：本局，还是导入的哪份日志
            var count = session.Encounters.Count + (session.Current != null && !session.Current.IsEmpty ? 1 : 0);
            Label(new Rect(r.x + 6f, r.y, r.width - 12f, 18f),
                  live ? Strings.LiveSession(count) : Strings.ImportedSession(ShortName(session.SourcePath), count), _head);

            var area = new Rect(r.x, r.y + 20f, r.width, r.height - 20f - 22f);
            var visible = Mathf.Max(1, (int)(area.height / RowH));
            _listTop = Wheel(area, _listTop, rows.Count - visible);

            if (rows.Count == 0)
            {
                Label(new Rect(area.x + 6f, area.y + 4f, area.width - 12f, RowH), Strings.NoEncounters, _small);
            }

            for (var i = 0; i < visible && _listTop + i < rows.Count; i++)
            {
                var e = rows[_listTop + i];
                var row = new Rect(area.x, area.y + i * RowH, area.width, RowH);
                var isLiveRow = live && e == session.Current;

                if (e == selected) Fill(row, new Color(0.26f, 0.46f, 0.78f, 0.55f));
                else if (row.Contains(Event.current.mousePosition)) Fill(row, new Color(1f, 1f, 1f, 0.07f));

                Label(new Rect(row.x + 6f, row.y, 40f, RowH), ClockOf(session, e), _small);
                Label(new Rect(row.x + 46f, row.y, row.width - 100f, RowH), Strings.EncounterTitle(e), _text);
                if (isLiveRow)
                    Label(new Rect(row.xMax - 52f, row.y, 46f, RowH), Strings.LiveTag, _live);
                else
                    Label(new Rect(row.xMax - 52f, row.y, 46f, RowH), Dur(e.DurationSeconds), _smallRight);

                if (GUI.Button(row, "", _plain))
                {
                    _selected = isLiveRow ? null : e;
                }
            }

            // 底部：翻页 + 位置
            var foot = new Rect(r.x, r.yMax - 20f, r.width, 20f);
            if (GUI.Button(new Rect(foot.x + 4f, foot.y + 1f, 26f, 18f), "^")) _listTop = Mathf.Max(0, _listTop - visible);
            if (GUI.Button(new Rect(foot.x + 32f, foot.y + 1f, 26f, 18f), "v"))
                _listTop = Mathf.Min(Mathf.Max(0, rows.Count - visible), _listTop + visible);
            var shownTo = Mathf.Min(rows.Count, _listTop + visible);
            var foot2 = rows.Count == 0 ? "" : $"{_listTop + 1}–{shownTo} / {rows.Count}";
            if (live && session.DroppedEncounters > 0 && shownTo == rows.Count)
                foot2 = Strings.DroppedHint(session.DroppedEncounters);
            Label(new Rect(foot.x + 64f, foot.y, foot.width - 70f, 20f), foot2, _smallRight);
        }

        // ================================================================== 右侧：一段的统计

        private static void DrawEncounter(Rect r, Session session, bool live, Encounter enc)
        {
            var y = r.y;
            var isLiveCurrent = live && enc == session.Current;

            // ---- 摘要 ----
            var d = enc.DurationSeconds;
            var outTotal = enc.OutgoingTotal;
            Label(new Rect(r.x, y, r.width, 18f),
                  $"{Strings.EncounterTitle(enc)}   {Dur(d)}   " +
                  Strings.SummaryStats(Overlay.Short(outTotal), Overlay.Short(d > 0 ? outTotal / d : 0d),
                                       Overlay.Short(enc.IncomingTotal), Overlay.Short(enc.HealingTotal)),
                  _bold);
            y += 22f;

            // ---- 视图 ----
            var views = new[] { TrackerView.Outgoing, TrackerView.Incoming, TrackerView.Healing };
            for (var i = 0; i < views.Length; i++)
                if (Tab(new Rect(r.x + i * 62f, y, 58f, 18f), Strings.ViewLabel(views[i]), _view == views[i]))
                    _view = views[i];
            if (isLiveCurrent)
                Label(new Rect(r.xMax - 200f, y, 200f, 18f), Strings.LiveHint, _smallRight);
            y += 22f;

            // ---- 战斗员表格 ----
            var bucket = enc.Bucket(_view);
            var rows = bucket.Values.OrderByDescending(x => x.Total).ToList();
            var total = rows.Sum(x => x.Total);

            SourceStats sel = null;
            if (_hasSource) bucket.TryGetValue(_source, out sel);
            if (sel == null && rows.Count > 0) sel = rows[0];

            // 表格按这一段三个视图里人最多的那个留行（切视图时布局不跳），曲线填满表格和拆分区之间
            var tableRows = Mathf.Clamp(Mathf.Max(enc.Outgoing.Count, Mathf.Max(enc.Incoming.Count, enc.Healing.Count)),
                                        MinTableRows, MaxTableRows);
            DrawTable(new Rect(r.x, y, r.width, TableRowH * (tableRows + 1)), rows, total, sel, tableRows);
            y += TableRowH * (tableRows + 1) + 6f;

            // ---- 曲线 ----
            var breakdownTop = r.yMax - BreakdownH;
            var chartH = Mathf.Max(MinChartH, breakdownTop - 8f - y);
            DrawChart(new Rect(r.x, y, r.width, chartH), enc, rows, sel, isLiveCurrent);
            y += chartH + 8f;

            // ---- 选中角色的拆分 ----
            DrawBreakdown(new Rect(r.x, y, r.width, r.yMax - y), sel);
        }

        private static void DrawTable(Rect r, List<SourceStats> rows, double total, SourceStats sel, int maxRows)
        {
            var heal = _view == TrackerView.Healing;

            // 列：名字 | 总量 | 占比 | 每秒 | 暴击 | 次数 | 最高（治疗：名字 | 总量 | 占比 | 每秒 | 次数 | 主要来源）
            var hy = r.y;
            Label(new Rect(r.x + 8f, hy, 118f, TableRowH), Strings.ColName, _head);
            Label(new Rect(r.x + 126f, hy, 72f, TableRowH), Strings.ColTotal, _headRight);
            Label(new Rect(r.x + 198f, hy, 54f, TableRowH), Strings.ColShare, _headRight);
            Label(new Rect(r.x + 252f, hy, 72f, TableRowH), Strings.ColPerSec, _headRight);
            if (heal)
            {
                Label(new Rect(r.x + 324f, hy, 56f, TableRowH), Strings.ColHits, _headRight);
                Label(new Rect(r.x + 396f, hy, 124f, TableRowH), Strings.ColTopSource, _head);
            }
            else
            {
                Label(new Rect(r.x + 324f, hy, 56f, TableRowH), Strings.ColCrit, _headRight);
                Label(new Rect(r.x + 380f, hy, 64f, TableRowH), Strings.ColHits, _headRight);
                Label(new Rect(r.x + 444f, hy, 76f, TableRowH), Strings.ColMax, _headRight);
            }

            if (rows.Count == 0)
            {
                Label(new Rect(r.x + 8f, hy + TableRowH + 2f, r.width - 16f, TableRowH), Strings.EmptyHint(_view), _small);
                return;
            }

            for (var i = 0; i < maxRows && i < rows.Count; i++)
            {
                var s = rows[i];
                var row = new Rect(r.x, hy + (i + 1) * TableRowH, r.width, TableRowH);

                if (s == sel) Fill(row, new Color(0.26f, 0.46f, 0.78f, 0.45f));
                else if (row.Contains(Event.current.mousePosition)) Fill(row, new Color(1f, 1f, 1f, 0.06f));

                // 职业色条 + 占比条
                Fill(new Rect(row.x, row.y + 2f, 4f, row.height - 4f), ColorOf(s));
                var share = total > 0d ? s.Total / total : 0d;
                Fill(new Rect(row.x + 6f, row.yMax - 2f, (row.width - 6f) * (float)share, 2f), With(ColorOf(s), 0.8f));

                Label(new Rect(row.x + 8f, row.y, 118f, TableRowH), Strings.SourceName(s), _text);
                Label(new Rect(row.x + 126f, row.y, 72f, TableRowH), Overlay.Short(s.Total), _textRight);
                Label(new Rect(row.x + 198f, row.y, 54f, TableRowH), Pct(share), _textRight);
                Label(new Rect(row.x + 252f, row.y, 72f, TableRowH), Overlay.Short(s.Dps), _textRight);
                if (heal)
                {
                    Label(new Rect(row.x + 324f, row.y, 56f, TableRowH), Count(s.Hits), _textRight);
                    var top = s.TopHealKind;
                    Label(new Rect(row.x + 396f, row.y, 124f, TableRowH), top >= 0 ? Healing.KindName(top) : "—", _text);
                }
                else
                {
                    Label(new Rect(row.x + 324f, row.y, 56f, TableRowH), s.Hits > 0 ? Pct(s.CritRate) : "—", _textRight);
                    Label(new Rect(row.x + 380f, row.y, 64f, TableRowH), Count(s.Hits), _textRight);
                    Label(new Rect(row.x + 444f, row.y, 76f, TableRowH), Overlay.Short(s.MaxHit), _textRight);
                }

                if (GUI.Button(row, "", _plain))
                {
                    _source = s.InstanceId;
                    _hasSource = true;
                }
            }

            if (rows.Count > maxRows)
                Label(new Rect(r.x + 8f, hy + (maxRows + 1) * TableRowH - 2f, r.width - 16f, 12f),
                      Strings.MoreItems(rows.Count - maxRows), _small);
        }

        private static void DrawChart(Rect r, Encounter enc, List<SourceStats> rows, SourceStats sel, bool live)
        {
            Fill(r, new Color(0.11f, 0.11f, 0.14f, 0.95f));
            if (rows.Count == 0) return;

            var shown = rows.Take(6).ToList();
            if (sel != null && !shown.Contains(sel)) shown.Add(sel);

            var n = Math.Max(2, shown.Max(s => s.PerSecond.Count));
            var w = (int)r.width;
            var h = (int)r.height;
            var sig = string.Join("|", enc.Index, enc.StartTime.ToString("R", CultureInfo.InvariantCulture),
                                  (int)_view, sel?.InstanceId ?? 0, enc.TotalOf(_view).ToString("R", CultureInfo.InvariantCulture), n);

            if (Chart.Stale(sig, w, h, live))
            {
                var series = new List<LineChart.Series>(shown.Count);
                double peak = 0;
                foreach (var s in shown)
                {
                    var sm = Smooth(s.PerSecond, n, 5);
                    foreach (var v in sm) if (v > peak) peak = v;
                    series.Add(new LineChart.Series { Values = sm, Color = ColorOf(s), Bold = s == sel });
                }
                _chartPeak = peak;
                Chart.Render(series, w, h, peak * 1.12, sig);
            }

            if (Chart.Texture != null) GUI.DrawTexture(r, Chart.Texture);

            Label(new Rect(r.x + 6f, r.y + 2f, 220f, 14f), Strings.Peak(Overlay.Short(_chartPeak)), _small);
            Label(new Rect(r.xMax - 226f, r.y + 2f, 220f, 14f), Strings.ChartHint, _smallRight);
            Label(new Rect(r.x + 6f, r.yMax - 15f, 60f, 14f), "0:00", _small);
            Label(new Rect(r.xMax - 66f, r.yMax - 15f, 60f, 14f), Dur(enc.DurationSeconds), _smallRight);
        }

        private static void DrawBreakdown(Rect r, SourceStats sel)
        {
            var y = r.y;
            Dimension dim;
            if (_view == TrackerView.Healing)
            {
                dim = Dimension.HealKind;
                Label(new Rect(r.x, y, 120f, 18f), Strings.TabHealSources, _bold);
            }
            else
            {
                if (_dim == Dimension.HealKind) _dim = Dimension.Skill;
                if (Tab(new Rect(r.x, y, 58f, 18f), Strings.TabSkills, _dim == Dimension.Skill)) _dim = Dimension.Skill;
                if (Tab(new Rect(r.x + 62f, y, 58f, 18f), Strings.TabTypes, _dim == Dimension.DamageType)) _dim = Dimension.DamageType;
                if (Tab(new Rect(r.x + 124f, y, 58f, 18f), Strings.TabElements, _dim == Dimension.Attribute)) _dim = Dimension.Attribute;
                dim = _dim;
            }

            if (sel != null)
                Label(new Rect(r.x + 190f, y, r.width - 190f, 18f),
                      $"{Strings.SourceName(sel)} — {Strings.ViewLabel(_view)}", _textRight);
            y += 22f;

            if (sel == null)
            {
                Label(new Rect(r.x, y, r.width, BreakRowH), Strings.SelectHint, _small);
                return;
            }

            var rows = Breakdown.Of(sel, dim);
            var tw = r.width - PieSize - 16f;
            var detailed = dim == Dimension.Skill;

            // 列：名称 | 总量 | 占比（技能再加：次数 | 暴击 | 最高）
            float cName = detailed ? 128f : tw - 150f, cTotal = detailed ? 64f : 90f, cShare = detailed ? 50f : 60f;
            Label(new Rect(r.x + 12f, y, cName - 12f, BreakRowH), Strings.ColItem, _head);
            Label(new Rect(r.x + cName, y, cTotal, BreakRowH), Strings.ColTotal, _headRight);
            Label(new Rect(r.x + cName + cTotal, y, cShare, BreakRowH), Strings.ColShare, _headRight);
            if (detailed)
            {
                var x0 = r.x + cName + cTotal + cShare;
                Label(new Rect(x0, y, 46f, BreakRowH), Strings.ColHits, _headRight);
                Label(new Rect(x0 + 46f, y, 44f, BreakRowH), Strings.ColCrit, _headRight);
                Label(new Rect(x0 + 90f, y, tw - (x0 + 90f - r.x), BreakRowH), Strings.ColMax, _headRight);
            }

            double sum = 0;
            foreach (var row in rows) sum += row.Value;

            if (rows.Count == 0)
                Label(new Rect(r.x + 12f, y + BreakRowH, tw - 12f, BreakRowH), Strings.NoBreakdown, _small);

            for (var i = 0; i < BreakRows && i < rows.Count; i++)
            {
                var b = rows[i];
                var ry = y + (i + 1) * BreakRowH;
                Fill(new Rect(r.x + 1f, ry + 4f, 7f, 7f), PieChart.ColorAt(i));
                Label(new Rect(r.x + 12f, ry, cName - 12f, BreakRowH), b.Label, _text);
                Label(new Rect(r.x + cName, ry, cTotal, BreakRowH), Overlay.Short(b.Value), _textRight);
                Label(new Rect(r.x + cName + cTotal, ry, cShare, BreakRowH), Pct(sum > 0 ? b.Value / sum : 0d), _textRight);
                if (detailed)
                {
                    var x0 = r.x + cName + cTotal + cShare;
                    Label(new Rect(x0, ry, 46f, BreakRowH), Count(b.Hits), _textRight);
                    Label(new Rect(x0 + 46f, ry, 44f, BreakRowH), b.Hits > 0 ? Pct((double)b.Crits / b.Hits) : "—", _textRight);
                    Label(new Rect(x0 + 90f, ry, tw - (x0 + 90f - r.x), BreakRowH), Overlay.Short(b.Max), _textRight);
                }
            }
            if (rows.Count > BreakRows)
                Label(new Rect(r.x + 12f, y + (BreakRows + 1) * BreakRowH, tw - 12f, BreakRowH),
                      Strings.MoreItems(rows.Count - BreakRows), _small);

            // ---- 饼图 ----
            var pie = new Rect(r.xMax - PieSize, y - 4f, PieSize, PieSize);
            GUI.DrawTexture(pie, Pie.Get(Breakdown.Slices(rows), (int)PieSize));
            Label(new Rect(pie.x, pie.y + PieSize * 0.5f - 15f, PieSize, 16f), Overlay.Short(sel.Total), _center);
            Label(new Rect(pie.x, pie.y + PieSize * 0.5f + 1f, PieSize, 14f), $"{Overlay.Short(sel.Dps)}/s", _centerSmall);
        }

        // ================================================================== 导入

        private static void DrawPicker(Rect r)
        {
            Fill(r, new Color(0.10f, 0.10f, 0.13f, 0.98f));
            Label(new Rect(r.x + 8f, r.y + 2f, r.width - 90f, 18f), Strings.PickLog, _bold);
            if (GUI.Button(new Rect(r.xMax - 70f, r.y + 2f, 64f, 18f), Strings.BtnCancel)) _pickerOpen = false;

            var area = new Rect(r.x, r.y + 26f, r.width, r.height - 26f);
            if (_files.Count == 0)
            {
                Label(new Rect(area.x + 8f, area.y + 4f, area.width - 16f, RowH), Strings.NoLogs, _small);
                return;
            }

            var visible = Mathf.Max(1, (int)(area.height / RowH));
            _fileTop = Wheel(area, _fileTop, _files.Count - visible);

            for (var i = 0; i < visible && _fileTop + i < _files.Count; i++)
            {
                var f = _files[_fileTop + i];
                var row = new Rect(area.x, area.y + i * RowH, area.width, RowH);
                if (row.Contains(Event.current.mousePosition)) Fill(row, new Color(1f, 1f, 1f, 0.08f));

                var current = string.Equals(f.FullName, EventLogWriter.CurrentPath, StringComparison.OrdinalIgnoreCase);
                Label(new Rect(row.x + 8f, row.y, row.width - 200f, RowH), f.Name + (current ? Strings.CurrentFileTag : ""), _text);
                Label(new Rect(row.xMax - 196f, row.y, 70f, RowH), Size(f), _smallRight);
                Label(new Rect(row.xMax - 120f, row.y, 112f, RowH),
                      f.LastWriteTime.ToString("MM-dd HH:mm", CultureInfo.InvariantCulture), _smallRight);

                if (GUI.Button(row, "", _plain)) StartImport(f);
            }
        }

        private static void RefreshFiles()
        {
            _fileTop = 0;
            try
            {
                var dir = new DirectoryInfo(DamageTracker.LogDirectory);
                _files = dir.Exists
                    ? dir.GetFiles("*.tbhlog*").OrderByDescending(f => f.LastWriteTime).ToList()
                    : new List<FileInfo>();
            }
            catch (Exception e)
            {
                _files = new List<FileInfo>();
                SetStatus(Strings.ImportFailed(e.Message));
            }
        }

        /// <summary>后台线程读文件、重新解析；主线程在下一帧取结果。解析器是纯 C#，不碰任何 Unity 对象。</summary>
        private static void StartImport(FileInfo f)
        {
            if (_importing) return;
            _importing = true;
            _importingName = f.Name;
            _pickerOpen = false;

            var path = f.FullName;
            var current = string.Equals(path, EventLogWriter.CurrentPath, StringComparison.OrdinalIgnoreCase);
            if (current) EventLogWriter.FlushSoon();
            var options = DamageTracker.ImportOptions();   // 读配置要在主线程

            Task.Run(() =>
            {
                try
                {
                    if (current) Thread.Sleep(800);   // 等写日志的线程把最新的数据刷下去
                    _importResult = EventLogReader.Read(path, options);
                }
                catch (Exception e)
                {
                    _importError = e.GetType().Name + ": " + e.Message;
                }
                finally
                {
                    _importing = false;
                }
            });
        }

        private static void PollImport()
        {
            var result = _importResult;
            if (result != null)
            {
                _importResult = null;
                _imported = result;
                _selected = null;
                _listTop = 0;
                var current = string.Equals(result.SourcePath, EventLogWriter.CurrentPath, StringComparison.OrdinalIgnoreCase);
                SetStatus(Strings.ImportDone(result.Encounters.Count, result.EventCount) +
                          (current ? Strings.ImportCurrentFile : result.Truncated ? Strings.ImportTruncated : ""));
                Mod.Log.Msg($"已导入战斗日志 {result.SourcePath}：{result.Encounters.Count} 段，{result.EventCount} 条事件" +
                            $"（不认识 {result.UnknownLines}，坏行 {result.BadLines}{(result.Truncated ? "，末尾不完整" : "")}）");
            }

            var error = _importError;
            if (error != null)
            {
                _importError = null;
                SetStatus(Strings.ImportFailed(error));
                Mod.Log.Warning($"导入战斗日志失败：{error}");
            }
        }

        private static void OpenFolder()
        {
            var dir = DamageTracker.LogDirectory;
            try { Directory.CreateDirectory(dir); } catch { /* 打开时会报 */ }
            try
            {
                Application.OpenURL(new Uri(dir).AbsoluteUri);
                return;
            }
            catch { /* 被裁剪或不可用时走下面 */ }
            try { Process.Start(new ProcessStartInfo("explorer.exe", "\"" + dir + "\"") { UseShellExecute = true }); }
            catch (Exception e) { SetStatus(Strings.ImportFailed(e.Message)); }
        }

        private static void SetStatus(string s)
        {
            _status = s;
            _statusUntil = Time.unscaledTime + 8f;
        }

        // ================================================================== 小工具

        private static int Wheel(Rect area, int top, int max)
        {
            var e = Event.current;
            if (e != null && e.type == EventType.ScrollWheel && area.Contains(e.mousePosition))
            {
                top += e.delta.y > 0f ? 3 : -3;
                e.Use();
            }
            return Mathf.Clamp(top, 0, Mathf.Max(0, max));
        }

        /// <summary>每秒一个桶 → 5 秒滑动平均，曲线才不会被单次大伤害扎成刺。</summary>
        private static float[] Smooth(List<float> perSecond, int n, int window)
        {
            var result = new float[n];
            double acc = 0;
            for (var i = 0; i < n; i++)
            {
                acc += i < perSecond.Count ? perSecond[i] : 0f;
                var drop = i - window;
                if (drop >= 0 && drop < perSecond.Count) acc -= perSecond[drop];
                result[i] = (float)(acc / Math.Min(i + 1, window));
            }
            return result;
        }

        private static Color ColorOf(SourceStats s) => s.InstanceId == 0 || s.ClassType == 0 ? Unknown : JobTable.ColorOf(s.ClassType);

        private static Color With(Color c, float a)
        {
            c.a = a;
            return c;
        }

        private static string ClockOf(Session s, Encounter e)
            => s.StartedAt.HasValue
                ? s.StartedAt.Value.AddSeconds(e.StartTime).ToLocalTime().ToString("HH:mm", CultureInfo.InvariantCulture)
                : "+" + Dur(e.StartTime);

        private static string Dur(double seconds)
        {
            var s = (int)Math.Round(Math.Max(0d, seconds));
            return s >= 3600
                ? $"{s / 3600}:{s / 60 % 60:00}:{s % 60:00}"
                : $"{s / 60}:{s % 60:00}";
        }

        private static string Pct(double v) => (v * 100d).ToString("0.0", CultureInfo.InvariantCulture) + "%";

        private static string Count(long n) => n.ToString("N0", CultureInfo.InvariantCulture);

        /// <summary>tbh-20260923-120438.tbhlog.gz → tbh-20260923-120438</summary>
        private static string ShortName(string path)
        {
            var name = Path.GetFileName(path ?? "?");
            var dot = name.IndexOf(".tbhlog", StringComparison.OrdinalIgnoreCase);
            return dot > 0 ? name.Substring(0, dot) : name;
        }

        private static string Size(FileInfo f)
        {
            try
            {
                var kb = f.Length / 1024d;
                return kb >= 1024d ? (kb / 1024d).ToString("0.0", CultureInfo.InvariantCulture) + " MB"
                                   : kb.ToString("0", CultureInfo.InvariantCulture) + " KB";
            }
            catch { return ""; }
        }

        private static void Fill(Rect r, Color c)
        {
            var prev = GUI.color;
            GUI.color = c;
            GUI.DrawTexture(r, _white);
            GUI.color = prev;
        }

        private static void Label(Rect r, string text, GUIStyle style) => GUI.Label(r, text, style);

        private static bool Tab(Rect r, string label, bool on)
        {
            Fill(r, on ? new Color(0.26f, 0.46f, 0.78f, 0.75f) : new Color(1f, 1f, 1f, 0.07f));
            return GUI.Button(r, label, _tab);
        }

        private static void EnsureStyles()
        {
            if (_stylesReady) return;

            _white = new Texture2D(1, 1, TextureFormat.RGBA32, false) { hideFlags = HideFlags.HideAndDontSave };
            _white.SetPixel(0, 0, Color.white);
            _white.Apply();

            var basis = new GUIStyle(GUI.skin.label)
            {
                fontSize = 11,
                alignment = TextAnchor.MiddleLeft,
                padding = new RectOffset(0, 0, 0, 0),
                margin = new RectOffset(0, 0, 0, 0),
                wordWrap = false,
                clipping = TextClipping.Clip,
            };
            basis.normal.textColor = new Color(0.93f, 0.93f, 0.95f);

            _text = basis;
            _textRight = new GUIStyle(basis) { alignment = TextAnchor.MiddleRight };
            _small = new GUIStyle(basis) { fontSize = 10 };
            _small.normal.textColor = new Color(0.66f, 0.66f, 0.70f);
            _smallRight = new GUIStyle(_small) { alignment = TextAnchor.MiddleRight };
            _bold = new GUIStyle(basis) { fontSize = 12, fontStyle = FontStyle.Bold };
            _head = new GUIStyle(basis) { fontSize = 10 };
            _head.normal.textColor = new Color(0.60f, 0.62f, 0.68f);
            _headRight = new GUIStyle(_head) { alignment = TextAnchor.MiddleRight };
            _center = new GUIStyle(basis) { fontSize = 13, fontStyle = FontStyle.Bold, alignment = TextAnchor.MiddleCenter };
            _centerSmall = new GUIStyle(_small) { alignment = TextAnchor.MiddleCenter };
            _tab = new GUIStyle(basis) { alignment = TextAnchor.MiddleCenter };
            _live = new GUIStyle(_small) { alignment = TextAnchor.MiddleRight, fontStyle = FontStyle.Bold };
            _live.normal.textColor = new Color(0.45f, 0.85f, 0.45f);
            _plain = new GUIStyle();   // 看不见的按钮：只要它的点击

            _stylesReady = true;
        }
    }
}
