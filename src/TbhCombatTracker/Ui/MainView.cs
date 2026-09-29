using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;

namespace TbhCombatTracker.Ui
{
    /// <summary>
    /// 战斗记录窗口，形制参考 ACT 的主窗口：左边是本局每一段战斗的列表，右边是选中那段的
    /// 战斗员表格、每秒数值曲线、选中角色的拆分表和环形图。
    ///
    /// 数据源是一个 <see cref="Session"/>：本局的实时会话，或者从日志文件导入、重新解析出来的会话，两者界面一样。
    /// 本局的旧段可能已经卸载了详细数据（列表里灰色、带硬盘图标）：点开时从本局日志重新载入。
    /// </summary>
    internal sealed class MainView : Window
    {
        private const float W = 840f, H = 590f, Pad = 10f;
        private const float ToolbarY = TitleH + 1f, ToolbarH = 28f;
        private const float BodyY = ToolbarY + ToolbarH + 7f;
        private const float ListW = 268f, RowH = 24f, FootH = 32f;
        private const float RightX = Pad + ListW + 12f;
        private const float RightW = W - RightX - Pad;
        private const float SummaryH = 48f;
        private const float TableRowH = 20f;
        private const int MinTableRows = 3, MaxTableRows = 7;
        private const float BreakRowH = 17f;
        private const int BreakRows = 8;
        private const float DonutSize = 128f;
        private const float BreakH = 28f + BreakRowH * (BreakRows + 2);
        private const float MinChartH = 80f;

        // 表格列：名字 | 总量 | 占比 | 每秒 | 暴击 | 次数 | 最高（治疗：名字 | 总量 | 占比 | 每秒 | 次数 | 主要来源）
        private static readonly float[] Cols = { 146f, 80f, 58f, 80f, 58f, 62f, 56f };
        private static readonly float[] HealCols = { 146f, 80f, 58f, 80f, 62f, 114f };

        // ---- 数据源 ----
        private Session _imported;
        private volatile Session _importResult;
        private volatile string _importError;
        private volatile bool _importing;
        private string _importingName;

        // ---- 选择 ----
        /// <summary>选中那段的流水号；-1 = 跟着本局实时的当前段。</summary>
        private int _sel = -1;
        private TrackerView _view = TrackerView.Outgoing;
        private int _source;
        private bool _hasSource;
        private Dimension _dim = Dimension.Skill;
        private Dimension _healDimSel = Dimension.HealKind;

        /// <summary>右侧下半截看什么：曲线和拆分，还是逐条的原始事件。</summary>
        private bool _eventsMode;
        private static readonly Dimension[] DamageDims = { Dimension.Skill, Dimension.DamageType, Dimension.Attribute, Dimension.Target };
        private Encounter _shown;
        private bool _shownLive;
        private bool _waiting;

        private string _status;
        private float _statusUntil;
        private bool _picker;
        private List<FileInfo> _files = new List<FileInfo>();

        private struct Item
        {
            public EncounterRecord Rec;
            /// <summary>本局进行中的那段（列表第一行）。</summary>
            public Encounter Live;
        }

        private readonly List<Item> _items = new List<Item>();

        // ---- 节点 ----
        private readonly Label _titleIcon, _title;
        private readonly Button _settingsBtn, _closeBtn;
        private readonly Box _chip;
        private readonly Label _chipIcon, _chipText, _statusText;
        private readonly Button _chipClose, _importBtn, _folderBtn, _exportBtn;
        private readonly Hit _chipHit;

        private readonly Box _listBg, _listHover, _listSel, _listSelBar, _footLine;
        private readonly ListView _list;
        private readonly ListRow[] _rows;
        private readonly Button _pageUp, _pageDown;
        private readonly Label _range, _listEmpty;
        private int _hoverSlot = -1;

        private readonly Node _enc, _pickerNode, _loadingNode;

        // 摘要
        private readonly Box _livePill;
        private readonly Label _encTitle, _encStats, _liveText;
        private readonly Segmented _views;
        /// <summary>右侧空白处：点一下取消表格里的选中。最先登记，压在所有控件下面。</summary>
        private readonly Hit _paneHit;
        private readonly Box _rightBg;

        // 表格
        private readonly Label[] _heads = new Label[7];
        private readonly TableRow[] _table = new TableRow[MaxTableRows];
        private readonly Shape _tableShape;
        private readonly Box _tableHover, _tableSel;
        private readonly Label _tableEmpty, _tableMore;
        private int _tableHoverSlot = -1;

        // 曲线
        private readonly LineChart _chart;

        // 拆分
        private readonly Segmented _dims, _healDims;
        private readonly Label _breakWho, _breakEmpty, _breakRange;
        private readonly Label[] _breakHeads = new Label[6];
        /// <summary>拆分表：一屏 BreakRows 行，多的用滚轮翻。行对象是个池，跟着滚动位置换绑。</summary>
        private readonly ListView _breakList;
        private readonly BreakRow[] _breaks = new BreakRow[BreakRows + 1];
        private readonly DonutChart _donut;
        /// <summary>光标指着的拆分行对象（-1 = 没有）；它此刻绑的那一行和环形图上指着的那段互相高亮。</summary>
        private int _breakHoverSlot = -1;
        // 滚动时换绑行要用的：这次的行和合计、列宽、环上单独成段的行数、高亮项，以及滚动位置归零的依据
        private List<BreakdownRow> _breakData = new List<BreakdownRow>();
        private double _breakSum;
        private float[] _breakCols = new float[0];
        private float _breakW;
        private bool _breakDetailed, _breakHeal;
        private int _breakFolded, _breakHighlight = -1, _breakHoverRow = -1;
        private string _breakKey;

        // 曲线 + 拆分 / 逐条事件：两块轮流显示
        private readonly Node _statsArea, _eventsArea;
        private readonly Button _modeChart, _modeEvents;

        // 逐条事件
        private const float EvRowH = 18f;
        private static readonly float[] EvCols = { 54f, 98f, 108f, 106f, 74f, 34f, 66f };
        private static readonly float[] EvHealCols = { 54f, 110f, 110f, 150f, 90f };
        private readonly Label _evHeader, _evNote, _evEmpty;
        private readonly Label[] _evHeads = new Label[7];
        private readonly ListView _evList;
        private readonly EventRow[] _evRows;
        private readonly List<CombatEvent> _evLive = new List<CombatEvent>();
        private readonly List<CombatEvent> _evShown = new List<CombatEvent>();
        private int _evLiveIndex = -1;
        private Dictionary<int, UnitInfo> _evUnits;
        private Dictionary<int, AbilityInfo> _evAbilities;
        private double _evStart;
        private string _evKey;
        private bool _evWaiting;

        private sealed class EventRow
        {
            public Node Root;
            public Box Stripe;
            public Label[] Cells = new Label[7];
        }

        // 导入
        private readonly Label _pickTitle, _pickEmpty;
        private readonly Button _pickCancel;
        private readonly Box _pickBg, _pickHover;
        private readonly ListView _pickList;
        private readonly PickRow[] _pickRows;
        private int _pickHoverSlot = -1;

        // 载入中
        private readonly Label _loadingText;
        private readonly Button _retry;

        private sealed class ListRow
        {
            public Node Root;
            public Label Time, Title, Disk, Dur;
            public Hit Hit;
            public int Item = -1;
        }

        private sealed class TableRow
        {
            public Node Root;
            public Label[] Cells = new Label[7];
            public Hit Hit;
            public int SourceId;
        }

        private sealed class BreakRow
        {
            public Node Root;
            public Box Hover, Dot;
            public Label[] Cells = new Label[6];
            public Hit Hit;
            public int Item = -1;
        }

        private sealed class PickRow
        {
            public Node Root;
            public Label Name, Size, Date;
            public Hit Hit;
            public int Item = -1;
        }

        public MainView(Transform canvas) : base("main", canvas, W, H, Theme.WindowBg)
        {
            // ---- 标题栏 ----
            DragHandle(Body, new Rect(0f, 0f, W, TitleH));
            _titleIcon = new Label(Body, 15, Theme.Accent, TextAnchor.MiddleCenter, font: UiKit.IconFont ?? UiKit.TextFont, name: "Icon").NoWrap();
            _titleIcon.Text = Glyphs.Of(Glyphs.History);
            _title = new Label(Body, Theme.FontTitle, Theme.Text, bold: true, name: "Title");
            _settingsBtn = new Button(this, Body, Glyphs.Settings, null, UiRoot.OpenSettings, tip: () => Strings.TipSettings);
            _closeBtn = new Button(this, Body, Glyphs.Cancel, null, () => Show(false), tip: () => Strings.TipClose);

            // ---- 工具栏 ----
            _chip = new Box(Body, Theme.Panel, 11f, "Chip");
            _chipIcon = new Label(_chip, 12, Theme.TextDim, TextAnchor.MiddleCenter, font: UiKit.IconFont ?? UiKit.TextFont, name: "Icon").NoWrap();
            _chipIcon.Text = Glyphs.Of(Glyphs.Document);
            _chipText = new Label(_chip, Theme.FontSmall, Theme.Text, name: "Text").NoWrap();
            _chipHit = AddHit(_chip);
            _chipClose = new Button(this, _chip, Glyphs.Cancel, null, BackToLive, tip: () => Strings.TipBackToLive);
            _statusText = new Label(Body, Theme.FontSmall, Theme.TextDim, name: "Status");
            _exportBtn = new Button(this, Body, Glyphs.Export, "", Export, tip: () => Strings.TipExport);
            _folderBtn = new Button(this, Body, Glyphs.FolderOpen, "", OpenLogFolder, tip: () => Strings.TipLogFolder);
            _importBtn = new Button(this, Body, Glyphs.OpenFile, "", TogglePicker, tip: () => Strings.TipImport);

            // ---- 左侧列表 ----
            _listBg = new Box(Body, Theme.Panel, 6f, "ListBg");
            _list = new ListView(this, Body, RowH);
            _listHover = new Box(_list.Viewport, Theme.Hover, 4f, "Hover");
            _listSel = new Box(_list.Viewport, Theme.AccentSoft, 4f, "Selected");
            _listSelBar = new Box(_list.Viewport, Theme.Accent, 1f, "SelectedBar");
            _listHover.Active = _listSel.Active = _listSelBar.Active = false;
            _rows = new ListRow[26];
            for (var i = 0; i < _rows.Length; i++) _rows[i] = MakeListRow(i);
            _listEmpty = new Label(Body, Theme.FontSmall, Theme.TextDim, TextAnchor.MiddleCenter, name: "Empty");
            _footLine = new Box(Body, Theme.Divider, 0f, "FootLine");
            _pageUp = new Button(this, Body, Glyphs.ChevronUp, null, () => _list.Page(-1), ButtonStyle.Subtle, () => Strings.TipPageUp);
            _pageDown = new Button(this, Body, Glyphs.ChevronDown, null, () => _list.Page(1), ButtonStyle.Subtle, () => Strings.TipPageDown);
            _range = new Label(Body, Theme.FontSmall, Theme.TextDim, TextAnchor.MiddleRight, name: "Range").NoWrap();

            // ---- 右侧：一段的统计 ----
            _enc = new Node("Encounter", Body);
            _rightBg = new Box(_enc, Theme.Panel, 6f, "RightBg");
            _rightBg.Place(RightX - 8f, BodyY - 6f, RightW + 8f + Pad * 0.5f, H - BodyY - Pad + 6f);
            _paneHit = AddHit(_enc, new Rect(RightX, BodyY, RightW, H - BodyY - Pad));
            _paneHit.Click = () =>
            {
                if (!_hasSource) return;
                _hasSource = false;
                Dirty = true;
            };
            _encTitle = new Label(_enc, 15, Theme.Text, bold: true, name: "Title");
            _livePill = new Box(_enc, Theme.With(Theme.Live, 0.16f), 8f, "LivePill");
            _liveText = new Label(_livePill, Theme.FontTiny, Theme.Live, TextAnchor.MiddleCenter, bold: true, name: "Live").NoWrap();
            _encStats = new Label(_enc, Theme.FontSmall, Theme.TextDim, name: "Stats");
            _views = new Segmented(this, _enc, 3, i =>
            {
                _view = (TrackerView)i;
                Dirty = true;
            });

            for (var i = 0; i < _heads.Length; i++)
                _heads[i] = new Label(_enc, Theme.FontTiny, Theme.TextFaint, i == 0 ? TextAnchor.MiddleLeft : TextAnchor.MiddleRight, name: "Head").NoWrap();
            _tableHover = new Box(_enc, Theme.Hover, 4f, "TableHover");
            _tableSel = new Box(_enc, Theme.AccentSoft, 4f, "TableSel");
            _tableHover.Active = _tableSel.Active = false;
            _tableShape = new Shape(_enc, "TableBars");
            for (var i = 0; i < MaxTableRows; i++) _table[i] = MakeTableRow(i);
            _tableEmpty = new Label(_enc, Theme.FontSmall, Theme.TextDim, name: "TableEmpty");
            _tableMore = new Label(_enc, Theme.FontTiny, Theme.TextFaint, name: "TableMore");

            // 右上角两个图标切换下半截：曲线和拆分 / 逐条事件
            _modeChart = new Button(this, _enc, Glyphs.AreaChart, null, () => SetEventsMode(false), ButtonStyle.Tab, () => Strings.TipModeChart);
            _modeEvents = new Button(this, _enc, Glyphs.List, null, () => SetEventsMode(true), ButtonStyle.Tab, () => Strings.TipModeEvents);

            _statsArea = new Node("Stats", _enc);
            _statsArea.Place(0f, 0f, W, H);
            _chart = new LineChart(this, _statsArea);

            _dims = new Segmented(this, _statsArea, DamageDims.Length, i =>
            {
                _dim = DamageDims[i];
                Dirty = true;
            });
            _healDims = new Segmented(this, _statsArea, 2, i =>
            {
                _healDimSel = i == 0 ? Dimension.HealKind : Dimension.Target;
                Dirty = true;
            });
            _breakWho = new Label(_statsArea, Theme.FontSmall, Theme.TextDim, TextAnchor.MiddleRight, name: "Who");
            for (var i = 0; i < _breakHeads.Length; i++)
                _breakHeads[i] = new Label(_statsArea, Theme.FontTiny, Theme.TextFaint, i == 0 ? TextAnchor.MiddleLeft : TextAnchor.MiddleRight, name: "Head").NoWrap();
            _breakList = new ListView(this, _statsArea, BreakRowH);
            for (var i = 0; i < _breaks.Length; i++) _breaks[i] = MakeBreakRow(i);
            _breakEmpty = new Label(_statsArea, Theme.FontSmall, Theme.TextDim, name: "BreakEmpty");
            _breakRange = new Label(_statsArea, Theme.FontTiny, Theme.TextFaint, name: "BreakRange").NoWrap();
            _donut = new DonutChart(this, _statsArea, BreakRows);
            _donut.HoverChanged = () => Dirty = true;

            _eventsArea = new Node("Events", _enc);
            _eventsArea.Place(0f, 0f, W, H);
            _evHeader = new Label(_eventsArea, Theme.FontSmall, Theme.Text, name: "Header");
            _evNote = new Label(_eventsArea, Theme.FontTiny, Theme.TextFaint, TextAnchor.MiddleRight, name: "Note");
            for (var i = 0; i < _evHeads.Length; i++)
                _evHeads[i] = new Label(_eventsArea, Theme.FontTiny, Theme.TextFaint, name: "Head").NoWrap();
            _evList = new ListView(this, _eventsArea, EvRowH);
            _evRows = new EventRow[32];
            for (var i = 0; i < _evRows.Length; i++)
            {
                var r = new EventRow { Root = new Node("Event" + i, _evList.Viewport) };
                r.Stripe = new Box(r.Root, new Color(1f, 1f, 1f, 0.03f), 0f, "Stripe");
                for (var c = 0; c < r.Cells.Length; c++)
                    r.Cells[c] = new Label(r.Root, Theme.FontSmall, Theme.Text, name: "Cell").NoWrap();
                r.Root.Active = false;
                _evRows[i] = r;
            }
            _evEmpty = new Label(_eventsArea, Theme.FontSmall, Theme.TextDim, TextAnchor.MiddleCenter, name: "Empty");
            _eventsArea.Active = false;

            // ---- 右侧：导入 ----
            _pickerNode = new Node("Picker", Body);
            _pickBg = new Box(_pickerNode, Theme.Panel, 6f, "Bg");
            _pickTitle = new Label(_pickerNode, Theme.FontBody, Theme.Text, bold: true, name: "Title");
            _pickCancel = new Button(this, _pickerNode, (char)0, "", () => { _picker = false; Dirty = true; }, ButtonStyle.Subtle);
            _pickList = new ListView(this, _pickerNode, 28f);
            _pickHover = new Box(_pickList.Viewport, Theme.Hover, 4f, "Hover");
            _pickHover.Active = false;
            _pickRows = new PickRow[22];
            for (var i = 0; i < _pickRows.Length; i++) _pickRows[i] = MakePickRow(i);
            _pickEmpty = new Label(_pickerNode, Theme.FontSmall, Theme.TextDim, TextAnchor.MiddleCenter, name: "Empty");
            _pickerNode.Active = false;

            // ---- 右侧：从日志载入中 ----
            _loadingNode = new Node("Loading", Body);
            _loadingText = new Label(_loadingNode, Theme.FontBody, Theme.TextDim, TextAnchor.MiddleCenter, name: "Text");
            _retry = new Button(this, _loadingNode, Glyphs.Refresh, "", Retry, ButtonStyle.Subtle);
            _loadingNode.Active = false;

            _enc.Place(0f, 0f, W, H);
            _pickerNode.Place(0f, 0f, W, H);
            _loadingNode.Place(0f, 0f, W, H);
        }

        public override Vector2 DefaultPosition() => new Vector2(40f, 150f);

        public override void Show(bool on)
        {
            base.Show(on);
            if (on) RefreshInterval = 0.5f;
        }

        // ================================================================== 行对象

        private ListRow MakeListRow(int slot)
        {
            var r = new ListRow { Root = new Node("Row" + slot, _list.Viewport) };
            r.Time = new Label(r.Root, Theme.FontSmall, Theme.TextDim, name: "Time").NoWrap();
            r.Title = new Label(r.Root, Theme.FontBody, Theme.Text, name: "Title");
            r.Disk = new Label(r.Root, 11, Theme.TextFaint, TextAnchor.MiddleCenter, font: UiKit.IconFont ?? UiKit.TextFont, name: "Disk").NoWrap();
            r.Disk.Text = Glyphs.Of(Glyphs.OnDisk);
            r.Dur = new Label(r.Root, Theme.FontSmall, Theme.TextDim, TextAnchor.MiddleRight, name: "Dur").NoWrap();
            r.Hit = AddHit(r.Root);
            r.Hit.Click = () => SelectItem(r.Item);
            r.Hit.State = s =>
            {
                _hoverSlot = s == HitState.Normal ? (_hoverSlot == slot ? -1 : _hoverSlot) : slot;
                PlaceListHighlights();
            };
            r.Hit.Tip = () => r.Item >= 0 && r.Item < _items.Count && _items[r.Item].Rec != null && !_items[r.Item].Rec.Loaded
                              && !EncounterLoader.Has(_items[r.Item].Rec) ? Strings.TipStored : null;
            r.Root.Active = false;
            return r;
        }

        private TableRow MakeTableRow(int slot)
        {
            var r = new TableRow { Root = new Node("TableRow" + slot, _enc) };
            for (var c = 0; c < r.Cells.Length; c++)
                r.Cells[c] = new Label(r.Root, Theme.FontBody, Theme.Text, c == 0 ? TextAnchor.MiddleLeft : TextAnchor.MiddleRight, name: "Cell").NoWrap();
            r.Cells[0].T.horizontalOverflow = HorizontalWrapMode.Wrap;
            r.Hit = AddHit(r.Root);
            r.Hit.Click = () =>
            {
                if (_hasSource && _source == r.SourceId) _hasSource = false;
                else
                {
                    _source = r.SourceId;
                    _hasSource = true;
                }
                Dirty = true;
            };
            r.Hit.State = s =>
            {
                _tableHoverSlot = s == HitState.Normal ? (_tableHoverSlot == slot ? -1 : _tableHoverSlot) : slot;
                PlaceTableHover();
            };
            r.Root.Active = false;
            return r;
        }

        private BreakRow MakeBreakRow(int slot)
        {
            var r = new BreakRow { Root = new Node("BreakRow" + slot, _breakList.Viewport) };
            r.Hover = new Box(r.Root, Theme.Hover, 4f, "Hover");
            r.Hover.Active = false;
            r.Dot = new Box(r.Root, Theme.PaletteAt(slot), 3f, "Dot");
            for (var c = 0; c < r.Cells.Length; c++)
                r.Cells[c] = new Label(r.Root, Theme.FontSmall, Theme.Text, c == 0 ? TextAnchor.MiddleLeft : TextAnchor.MiddleRight, name: "Cell").NoWrap();
            r.Cells[0].T.horizontalOverflow = HorizontalWrapMode.Wrap;
            r.Hit = AddHit(r.Root);
            r.Hit.State = s =>
            {
                var hot = s != HitState.Normal ? slot : _breakHoverSlot == slot ? -1 : _breakHoverSlot;
                if (hot == _breakHoverSlot) return;
                _breakHoverSlot = hot;
                Dirty = true;
            };
            r.Root.Active = false;
            return r;
        }

        private PickRow MakePickRow(int slot)
        {
            var r = new PickRow { Root = new Node("File" + slot, _pickList.Viewport) };
            r.Name = new Label(r.Root, Theme.FontBody, Theme.Text, name: "Name");
            r.Size = new Label(r.Root, Theme.FontSmall, Theme.TextDim, TextAnchor.MiddleRight, name: "Size").NoWrap();
            r.Date = new Label(r.Root, Theme.FontSmall, Theme.TextDim, TextAnchor.MiddleRight, name: "Date").NoWrap();
            r.Hit = AddHit(r.Root);
            r.Hit.Click = () =>
            {
                if (r.Item >= 0 && r.Item < _files.Count) StartImport(_files[r.Item]);
            };
            r.Hit.State = s =>
            {
                _pickHoverSlot = s == HitState.Normal ? (_pickHoverSlot == slot ? -1 : _pickHoverSlot) : slot;
                PlacePickHover();
            };
            r.Root.Active = false;
            return r;
        }

        // ================================================================== 每帧

        public override void Tick(float dt)
        {
            FollowOpacity(Theme.WindowBg);
            _donut.Tick();
            if (EventPages.Poll() && _evWaiting)
            {
                _evWaiting = false;
                Dirty = true;
            }
            if (_evList.Tick(dt)) BindEvents();
            if (_breakList.Tick(dt)) BindBreaks();
            PollImport();
            if (_list.Tick(dt)) BindList();
            if (_pickList.Tick(dt)) BindPicker();
            _chart.Tick();

            // 后台载入完成（或失败）：换上数据 / 显示原因
            if (_waiting)
            {
                var item = Current();
                if (item.Rec == null || !EncounterLoader.IsLoading(item.Rec))
                {
                    _waiting = false;
                    Dirty = true;
                }
            }
        }

        // ================================================================== 重画

        public override void Refresh()
        {
            var live = _imported == null;
            var session = live ? DamageTracker.Live : _imported;
            RefreshInterval = live ? 0.5f : 0f;

            BuildItems(session, live);
            var item = Resolve(session, live, out var enc, out var loading);
            _shown = enc;
            _shownLive = live && item.Live != null;

            Chrome(live, session, enc);
            ListPane();

            _pickerNode.Active = _picker;
            _enc.Active = !_picker && enc != null;
            _loadingNode.Active = !_picker && enc == null;
            if (_picker) Picker();
            else if (enc != null) EncounterPane(enc, _shownLive, session);
            else LoadingPane(item, loading);
        }

        private void BuildItems(Session session, bool live)
        {
            _items.Clear();
            if (live && session.Current != null) _items.Add(new Item { Live = session.Current });
            for (var i = session.Records.Count - 1; i >= 0; i--) _items.Add(new Item { Rec = session.Records[i] });
        }

        private Item Current()
        {
            if (_sel < 0) return _items.Count > 0 && _items[0].Live != null ? _items[0] : default;
            foreach (var it in _items)
                if (it.Rec != null && it.Rec.Index == _sel) return it;
            return default;
        }

        /// <summary>选中的那段，和能拿到的详细数据（没载入时为 null）。</summary>
        private Item Resolve(Session session, bool live, out Encounter enc, out bool loading)
        {
            enc = null;
            loading = false;
            if (_items.Count == 0) return default;

            var item = Current();
            if (item.Rec == null && item.Live == null)
            {
                // 选中的那段不在了（换了数据源）：本局回到实时，导入的看最新一段
                item = _items[0];
                _sel = item.Live != null ? -1 : item.Rec.Index;
            }

            if (item.Live != null)
            {
                enc = item.Live;
                return item;
            }

            enc = EncounterLoader.Get(item.Rec);
            if (enc == null && live)
            {
                EncounterLoader.Request(session, item.Rec);
                loading = EncounterLoader.IsLoading(item.Rec);
                _waiting = loading;
            }
            return item;
        }

        private void SelectItem(int index)
        {
            if (index < 0 || index >= _items.Count) return;
            var it = _items[index];
            _sel = it.Live != null ? -1 : it.Rec.Index;
            _picker = false;
            Dirty = true;
        }

        // ================================================================== 标题栏和工具栏

        private void Chrome(bool live, Session session, Encounter enc)
        {
            _titleIcon.Place(10f, 0f, 22f, TitleH);
            _title.Text = Strings.MainTitle;
            _title.Place(36f, 0f, 300f, TitleH);
            _closeBtn.Place(W - 36f, 4f, 28f, 24f);
            _settingsBtn.Place(W - 66f, 4f, 28f, 24f);

            // 看导入的日志时，左上角标出是哪份文件（点 × 回到本局）；看本局时什么都不放
            _chip.Active = !live;
            var chipW = 0f;
            if (!live)
            {
                var name = ShortName(session.SourcePath);
                _chipText.Text = name;
                var textW = Mathf.Min(220f, Mathf.Ceil(_chipText.PreferredWidth));
                chipW = 26f + textW + 30f;
                _chip.Place(Pad, ToolbarY + 2f, chipW, 24f);
                _chipIcon.Place(6f, 0f, 20f, 24f);
                _chipText.Place(26f, 0f, textW + 2f, 24f);
                _chipClose.Place(chipW - 26f, 2f, 22f, 20f);
                _chipHit.Rect = _chip.InWindow;
                _chipHit.Tip = () => Strings.TipImportedChip(name);
            }

            // 右边的按钮
            var right = W - Pad;
            _exportBtn.SetText(Strings.BtnExportCsv);
            _exportBtn.Enabled = enc != null;
            var bw = _exportBtn.Measure();
            _exportBtn.Place(right - bw, ToolbarY + 2f, bw, 24f);
            right -= bw + 4f;
            _folderBtn.SetText(Strings.BtnLogFolder);
            bw = _folderBtn.Measure();
            _folderBtn.Place(right - bw, ToolbarY + 2f, bw, 24f);
            right -= bw + 4f;
            _importBtn.SetText(Strings.BtnImport);
            _importBtn.On = _picker;
            _importBtn.Enabled = !_importing;
            bw = _importBtn.Measure();
            _importBtn.Place(right - bw, ToolbarY + 2f, bw, 24f);
            right -= bw + 8f;

            string status = null;
            if (_importing) status = Strings.Parsing(_importingName);
            else if (_status != null && Time.unscaledTime < _statusUntil) status = _status;
            _statusText.Active = status != null;
            _statusText.Text = status;
            var sx = live ? Pad + 4f : Pad + chipW + 10f;
            _statusText.Place(sx, ToolbarY, Mathf.Max(0f, right - sx), ToolbarH);
        }

        // ================================================================== 左侧列表

        private void ListPane()
        {
            var paneH = H - BodyY - Pad;
            _listBg.Place(Pad, BodyY, ListW, paneH);
            _list.Place(Pad + 4f, BodyY + 4f, ListW - 8f, paneH - 8f - FootH);
            _list.Count = _items.Count;
            BindList();

            var fy = BodyY + paneH - FootH;
            _footLine.Place(Pad + 8f, fy, ListW - 16f, 1f);
            _pageUp.Place(Pad + 8f, fy + 5f, 28f, 22f);
            _pageDown.Place(Pad + 40f, fy + 5f, 28f, 22f);
            _range.Place(Pad + 72f, fy, ListW - 84f, FootH);

            _listEmpty.Active = _items.Count == 0;
            if (_items.Count == 0)
            {
                _listEmpty.Text = Strings.NoEncounters;
                _listEmpty.Place(Pad, BodyY + 20f, ListW, 24f);
            }
        }

        /// <summary>把行对象摆到当前滚动位置，绑上数据。滚动动画每帧都会调它。</summary>
        private void BindList()
        {
            var vw = _list.Viewport.W;
            var vr = _list.Viewport.InWindow;
            var session = _imported ?? DamageTracker.Live;
            for (var slot = 0; slot < _rows.Length; slot++)
            {
                var r = _rows[slot];
                var index = _list.ItemAt(slot, out var y);
                if (slot >= _list.PoolSize || index >= _items.Count)
                {
                    r.Root.Active = false;
                    r.Item = -1;
                    continue;
                }
                var it = _items[index];
                r.Item = index;
                r.Root.Active = true;
                r.Root.Place(0f, y, vw, RowH);
                var rr = r.Root.InWindow;
                // 被视口裁掉的那部分不该还能点；右边留给滚动条
                var top = Mathf.Max(rr.y, vr.y);
                var bottom = Mathf.Min(rr.yMax, vr.yMax);
                r.Hit.Rect = new Rect(rr.x, top, rr.width - 9f, Mathf.Max(0f, bottom - top));

                var isLive = it.Live != null;
                var rec = it.Rec;
                var stored = !isLive && !rec.Loaded && !EncounterLoader.Has(rec);
                var start = isLive ? it.Live.StartTime : rec.StartTime;

                r.Time.Text = Clock(session, start);
                r.Time.Place(8f, 0f, 40f, RowH);
                r.Title.Text = isLive ? Strings.EncounterTitle(it.Live) : Title(rec);
                r.Title.Color = stored ? Theme.TextFaint : Theme.Text;
                r.Title.Place(50f, 0f, vw - 50f - (stored ? 66f : 50f), RowH);
                r.Time.Color = stored ? Theme.TextFaint : Theme.TextDim;

                r.Disk.Active = stored;
                r.Disk.Place(vw - 62f, 0f, 16f, RowH);
                r.Dur.Text = Fmt.Dur(isLive ? it.Live.DurationSeconds : rec.DurationSeconds);
                r.Dur.Color = stored ? Theme.TextFaint : Theme.TextDim;
                r.Dur.Place(vw - 50f, 0f, 44f, RowH);
            }
            PlaceListHighlights();

            _list.VisibleRange(out var from, out var to);
            _range.Text = _items.Count == 0 ? "" : $"{from}–{to} / {_items.Count}";
            _list.PaintThumb();
        }

        private void PlaceListHighlights()
        {
            var vw = _list.Viewport.W;
            ListRow sel = null, hover = null;
            foreach (var r in _rows)
            {
                if (r.Item < 0 || !r.Root.Active) continue;
                var it = _items[r.Item];
                if ((it.Live != null && _sel < 0) || (it.Rec != null && it.Rec.Index == _sel)) sel = r;
            }
            if (_hoverSlot >= 0 && _hoverSlot < _rows.Length && _rows[_hoverSlot].Root.Active) hover = _rows[_hoverSlot];

            _listSel.Active = _listSelBar.Active = sel != null;
            if (sel != null)
            {
                _listSel.Place(0f, sel.Root.Y + 1f, vw - 6f, RowH - 2f);
                _listSelBar.Place(0f, sel.Root.Y + 5f, 2f, RowH - 10f);
            }
            _listHover.Active = hover != null && hover != sel;
            if (_listHover.Active) _listHover.Place(0f, hover.Root.Y + 1f, vw - 6f, RowH - 2f);
        }

        // ================================================================== 右侧：一段的统计

        private void EncounterPane(Encounter enc, bool isLive, Session session)
        {
            var y = BodyY;
            var d = enc.DurationSeconds;

            // ---- 摘要：实时的那段在关卡名右边标一个"实时" ----
            var titleMax = RightW - 270f - (isLive ? 50f : 0f);
            _encTitle.Text = Strings.EncounterTitle(enc);
            _encTitle.Place(RightX, y - 1f, titleMax, 26f);
            _livePill.Active = isLive;
            if (isLive)
            {
                _liveText.Text = Strings.LiveTag;
                var pw = Mathf.Ceil(_liveText.PreferredWidth) + 14f;
                var tw = Mathf.Min(titleMax, Mathf.Ceil(_encTitle.PreferredWidth));
                _livePill.Place(RightX + tw + 8f, y + 4f, pw, 17f);
                _liveText.Place(0f, 0f, pw, 17f);
            }
            _encStats.Text = Strings.SummaryStats(Fmt.Dur(d), Fmt.Short(enc.OutgoingTotal), Fmt.Short(d > 0 ? enc.OutgoingTotal / d : 0d),
                                                  Fmt.Short(enc.IncomingTotal), Fmt.Short(enc.HealingTotal));
            _encStats.Place(RightX, y + 25f, RightW - 260f, 18f);
            _views.SetLabels(Strings.ViewLabel(TrackerView.Outgoing), Strings.ViewLabel(TrackerView.Incoming), Strings.ViewLabel(TrackerView.Healing));
            _views.Select((int)_view);
            var vx = RightX + RightW - 3f * 58f - 4f;
            _views.Place(vx, y + 2f, 58f, 28f);
            _modeEvents.On = _eventsMode;
            _modeChart.On = !_eventsMode;
            _modeEvents.Place(vx - 12f - 30f, y + 3f, 30f, 26f);
            _modeChart.Place(vx - 12f - 62f, y + 3f, 30f, 26f);
            y += SummaryH;

            // ---- 表格 ----
            var bucket = enc.Bucket(_view);
            var rows = bucket.Values.OrderByDescending(x => x.Total).ToList();
            var total = 0d;
            foreach (var s in rows) total += s.Total;

            // 没选中任何人（或选中的人这一段 / 这个视图里没有）：表格不高亮、曲线不加粗，拆分看全体
            SourceStats sel = null;
            if (_hasSource) bucket.TryGetValue(_source, out sel);

            // 按三个视图里人最多的那个留行（切视图时布局不跳），曲线填满表格和拆分区之间
            var tableRows = Mathf.Clamp(Mathf.Max(enc.Outgoing.Count, Mathf.Max(enc.Incoming.Count, enc.Healing.Count)),
                                        MinTableRows, MaxTableRows);
            Table(y, rows, total, sel, tableRows);
            y += TableRowH * (tableRows + 1) + 8f;

            _statsArea.Active = !_eventsMode;
            _eventsArea.Active = _eventsMode;
            if (_eventsMode)
            {
                EventsPane(y, enc, isLive, session, sel);
                return;
            }

            // ---- 曲线 ----
            var breakTop = H - Pad - BreakH;
            var chartH = Mathf.Max(MinChartH, breakTop - 10f - y);
            _chart.Place(RightX, y, RightW, chartH);
            Chart(enc, rows, sel);
            y += chartH + 10f;

            // ---- 拆分 ----
            Breakdown(y, sel, rows, $"{session.SourcePath}|{enc.Index}|{enc.StartTime}");
        }

        private void SetEventsMode(bool on)
        {
            if (_eventsMode == on) return;
            _eventsMode = on;
            _evKey = null;   // 回来时重新定位滚动
            Dirty = true;
        }

        /// <summary>
        /// 逐条事件：这一段的原始事件按时间排，跟着当前视图（输出看伤害、承伤看挨打、治疗看恢复）
        /// 和表格里选中的人筛选。实时那段从内存里拿、跟着最新的往下滚；别的段从日志里读。
        /// </summary>
        private void EventsPane(float y, Encounter enc, bool isLive, Session session, SourceStats sel)
        {
            var x = RightX;
            var bottom = H - Pad;
            string wait = null;
            var capped = 0;
            IList<CombatEvent> source = null;

            if (isLive)
            {
                _evLiveIndex = DamageTracker.CopyCurrentEvents(_evLive, _evLiveIndex);
                source = _evLiveIndex == enc.Index ? _evLive : (IList<CombatEvent>)Array.Empty<CombatEvent>();
                _evUnits = DamageTracker.Live.Units;
                _evAbilities = DamageTracker.Live.Abilities;
                if (_evLive.Count >= DamageTracker.MaxCurrentEvents) capped = DamageTracker.MaxCurrentEvents;
            }
            else
            {
                var path = _imported != null ? _imported.SourcePath : DamageTracker.CanReload ? EventLogWriter.CurrentPath : null;
                if (path == null)
                {
                    wait = Strings.EventsNoLog;
                }
                else
                {
                    var page = EventPages.Get(path, enc.Index, enc.StartTime);
                    if (page == null)
                    {
                        EventPages.Request(path, enc.Index, enc.StartTime);
                        _evWaiting = true;
                        wait = Strings.EventsLoading;
                    }
                    else if (page.Error != null)
                    {
                        wait = page.Error == "notFound" ? Strings.EventsNotFound : Strings.LoadFailed(page.Error);
                    }
                    else
                    {
                        source = page.Events;
                        _evUnits = page.Units;
                        _evAbilities = page.Abilities;
                        if (page.Capped) capped = EventPages.MaxEvents;
                    }
                }
            }

            // 按视图和选中的人筛
            var kind = _view == TrackerView.Incoming ? EventKind.Taken : _view == TrackerView.Healing ? EventKind.Heal : EventKind.Damage;
            _evShown.Clear();
            if (source != null)
            {
                foreach (var e in source)
                {
                    if (e.Kind != kind) continue;
                    if (sel != null)
                    {
                        var owner = kind == EventKind.Heal ? (e.B != 0 ? e.B : e.A) : e.A;
                        if (owner != sel.InstanceId) continue;
                    }
                    _evShown.Add(e);
                }
            }
            _evStart = enc.StartTime;

            // 标题行：谁 · 视图 · 条数
            var who = sel != null ? Strings.SourceName(sel) : Strings.AllSources;
            _evHeader.Text = Strings.EventsHeader(who, Strings.ViewLabel(_view), _evShown.Count);
            _evHeader.Place(x + 2f, y, RightW * 0.6f, 22f);
            _evNote.Active = capped > 0;
            if (capped > 0)
            {
                _evNote.Text = Strings.EventsCapped(capped);
                _evNote.Place(x + RightW * 0.5f, y, RightW * 0.5f - 4f, 22f);
            }
            y += 24f;

            // 列头
            var heal = kind == EventKind.Heal;
            var cols = heal ? EvHealCols : EvCols;
            var heads = heal
                ? new[] { Strings.ColTime, Strings.ColCaster, Strings.ColRecipient, Strings.TabHealSources, Strings.ColAmount }
                : new[] { Strings.ColTime, Strings.ColSource, kind == EventKind.Taken ? Strings.ColVictim : Strings.ColTarget,
                          Strings.ColSkill, Strings.ColAmount, Strings.ColCrit, Strings.ColKind };
            var cx = x;
            for (var c = 0; c < _evHeads.Length; c++)
            {
                _evHeads[c].Active = c < heads.Length;
                if (c >= heads.Length) continue;
                _evHeads[c].Text = heads[c];
                _evHeads[c].Align = EvAlign(c, heal);
                _evHeads[c].Place(cx + 6f, y, cols[c] - 10f, 18f);
                cx += cols[c];
            }
            y += 20f;

            // 列表：换了段 / 视图 / 选中的人就重新定位——实时的看最新，结束的从头看
            var key = $"{session.SourcePath}|{enc.Index}|{enc.StartTime}|{(int)_view}|{sel?.InstanceId}";
            var follow = _evList.AtEnd;
            _evList.Place(x, y, RightW, Mathf.Max(0f, bottom - y));
            _evList.Count = _evShown.Count;
            if (key != _evKey)
            {
                _evKey = key;
                if (isLive) _evList.ScrollToEnd();
                else _evList.ScrollToTop();
            }
            else if (isLive && follow)
            {
                _evList.ScrollToEnd();
            }

            _evEmpty.Active = wait != null || _evShown.Count == 0;
            if (_evEmpty.Active)
            {
                _evEmpty.Text = wait ?? Strings.EventsEmpty;
                _evEmpty.Place(x, y + 30f, RightW, 24f);
            }
            BindEvents();
        }

        private static TextAnchor EvAlign(int col, bool heal)
            => col == 0 ? TextAnchor.MiddleLeft
             : heal ? (col == 4 ? TextAnchor.MiddleRight : TextAnchor.MiddleLeft)
             : col == 4 ? TextAnchor.MiddleRight : col == 5 ? TextAnchor.MiddleCenter : TextAnchor.MiddleLeft;

        /// <summary>把事件行摆到当前滚动位置、填上文字。滚动动画每帧都会调它。</summary>
        private void BindEvents()
        {
            if (!_eventsArea.Active) return;
            var vw = _evList.Viewport.W;
            var heal = _view == TrackerView.Healing;
            var cols = heal ? EvHealCols : EvCols;
            for (var slot = 0; slot < _evRows.Length; slot++)
            {
                var r = _evRows[slot];
                var index = _evList.ItemAt(slot, out var y);
                if (slot >= _evList.PoolSize || index >= _evShown.Count)
                {
                    r.Root.Active = false;
                    continue;
                }
                var e = _evShown[index];
                r.Root.Active = true;
                r.Root.Place(0f, y, vw, EvRowH);
                r.Stripe.Active = index % 2 == 1;
                r.Stripe.Place(0f, 0f, vw - 8f, EvRowH);

                string[] cells;
                if (e.Kind == EventKind.Heal)
                    cells = new[] { EvTime(e.T), EvUnit(e.B != 0 ? e.B : e.A), EvUnit(e.A), Healing.KindName(e.HealKind), EvAmount(e.Amount) };
                else if (e.Kind == EventKind.Taken)
                    cells = new[] { EvTime(e.T), EvUnit(e.B), EvUnit(e.A), EvSkill(e.Ability), EvAmount(e.Amount),
                                    e.Crit == 1 ? Strings.CritMark : "", EvKind(e) };
                else
                    cells = new[] { EvTime(e.T), EvUnit(e.A), EvUnit(e.B), EvSkill(e.Ability), EvAmount(e.Amount),
                                    e.Crit == 1 ? Strings.CritMark : "", EvKind(e) };

                var cx = 0f;
                for (var c = 0; c < r.Cells.Length; c++)
                {
                    var cell = r.Cells[c];
                    cell.Active = c < cells.Length;
                    if (c >= cells.Length) continue;
                    cell.Text = cells[c];
                    cell.Align = EvAlign(c, heal);
                    cell.Color = c == 0 ? Theme.TextDim : c == 5 ? Theme.Warning : c == 4 ? Theme.Text : c == 6 ? Theme.TextDim : Theme.Text;
                    cell.Place(cx + 6f, 0f, cols[c] - 10f, EvRowH);
                    cx += cols[c];
                }
            }
            _evList.PaintThumb();
        }

        private string EvTime(double t)
        {
            var s = Math.Max(0d, t - _evStart);
            return ((int)(s / 60d)).ToString(CultureInfo.InvariantCulture) + ":" + (s % 60d).ToString("00.0", CultureInfo.InvariantCulture);
        }

        private static string EvAmount(float v) => ((long)Math.Round(v)).ToString("N0", CultureInfo.InvariantCulture);

        private string EvUnit(int id)
        {
            if (id == 0) return Strings.UnknownSource;
            if (_evUnits != null && _evUnits.TryGetValue(id, out var u))
                return u.Kind == 'M' ? TbhCombatTracker.Breakdown.MonsterName(u.Key ?? u.Name) : (u.Name ?? u.Key ?? "#" + id);
            return "#" + id;
        }

        private string EvSkill(int aid)
        {
            if (aid <= 0) return "—";
            if (_evAbilities != null && _evAbilities.TryGetValue(aid, out var a))
                return Strings.SkillName(!string.IsNullOrEmpty(a.Name) ? a.Name : a.Key);
            return "#" + aid;
        }

        /// <summary>伤害类型 · 元素，比如"投射物 · 火焰"；这次伤害没有分类上下文时是空的。</summary>
        private static string EvKind(in CombatEvent e)
        {
            string type = null;
            if (e.DamageType > 0)
            {
                for (var bit = 0; bit < 7; bit++)
                {
                    if ((e.DamageType & (1 << bit)) == 0) continue;
                    type = DamageTracker.TypeName(bit + 1);
                    break;
                }
            }
            var attr = e.Attribute >= 0 ? DamageTracker.AttributeName(e.Attribute) : null;
            return type != null && attr != null ? type + "·" + attr : type ?? attr ?? "";
        }

        private void Table(float y, List<SourceStats> rows, double total, SourceStats sel, int maxRows)
        {
            var heal = _view == TrackerView.Healing;
            var cols = heal ? HealCols : Cols;
            var names = heal
                ? new[] { Strings.ColName, Strings.ColTotal, Strings.ColShare, Strings.ColPerSec, Strings.ColHits, Strings.ColTopSource }
                : new[] { Strings.ColName, Strings.ColTotal, Strings.ColShare, Strings.ColPerSec, Strings.ColCrit, Strings.ColHits, Strings.ColMax };

            var x = RightX;
            for (var c = 0; c < _heads.Length; c++)
            {
                _heads[c].Active = c < names.Length;
                if (c >= names.Length) continue;
                _heads[c].Text = names[c];
                // 治疗的最后一列（主要来源）是文字，左对齐
                _heads[c].Align = c == 0 || (heal && c == names.Length - 1) ? TextAnchor.MiddleLeft : TextAnchor.MiddleRight;
                var pad = c == 0 ? 12f : 0f;
                _heads[c].Place(x + pad + (heal && c == names.Length - 1 ? 14f : 0f), y, cols[c] - pad - 6f, TableRowH);
                x += cols[c];
            }

            var b = _tableShape.Begin(UiRoot.Scale);
            _tableShape.Place(0f, 0f, W, H);
            _tableEmpty.Active = rows.Count == 0;
            if (rows.Count == 0)
            {
                _tableEmpty.Text = Strings.EmptyHint(_view);
                _tableEmpty.Place(RightX + 12f, y + TableRowH, RightW - 12f, TableRowH);
            }

            TableRow selRow = null;
            for (var i = 0; i < MaxTableRows; i++)
            {
                var r = _table[i];
                if (i >= maxRows || i >= rows.Count)
                {
                    r.Root.Active = false;
                    continue;
                }
                var s = rows[i];
                var ry = y + (i + 1) * TableRowH;
                r.Root.Active = true;
                r.Root.Place(RightX, ry, RightW, TableRowH);
                r.Hit.Rect = r.Root.InWindow;
                r.SourceId = s.InstanceId;
                if (s == sel) selRow = r;

                var color = Theme.SourceColor(s);
                var share = total > 0d ? s.Total / total : 0d;
                b.Rect(RightX + 3f, ry + 4f, 3f, TableRowH - 8f, color);
                b.Rect(RightX + 12f, ry + TableRowH - 3f, (RightW - 16f) * (float)share, 2f, Theme.With(color, 0.75f));

                var cells = heal
                    ? new[] { Strings.SourceName(s), Fmt.Short(s.Total), Fmt.Pct(share), Fmt.Short(s.Dps), Fmt.Count(s.Hits),
                              s.TopHealKind >= 0 ? Healing.KindName(s.TopHealKind) : "—" }
                    : new[] { Strings.SourceName(s), Fmt.Short(s.Total), Fmt.Pct(share), Fmt.Short(s.Dps),
                              s.Hits > 0 ? Fmt.Pct(s.CritRate) : "—", Fmt.Count(s.Hits), Fmt.Short(s.MaxHit) };
                var cx = 0f;
                for (var c = 0; c < r.Cells.Length; c++)
                {
                    var cell = r.Cells[c];
                    cell.Active = c < cells.Length;
                    if (c >= cells.Length) continue;
                    cell.Text = cells[c];
                    var left = c == 0 || (heal && c == cells.Length - 1);
                    cell.Align = left ? TextAnchor.MiddleLeft : TextAnchor.MiddleRight;
                    cell.Color = c == 0 ? Theme.Text : c == 1 ? Theme.Text : Theme.TextDim;
                    var pad = c == 0 ? 12f : 0f;
                    cell.Place(cx + pad + (heal && c == cells.Length - 1 ? 14f : 0f), 0f, cols[c] - pad - 6f, TableRowH);
                    cx += cols[c];
                }
            }
            _tableShape.Commit();

            _tableSel.Active = selRow != null;
            if (selRow != null) _tableSel.Place(RightX, selRow.Root.Y, RightW, TableRowH);
            PlaceTableHover();

            _tableMore.Active = rows.Count > maxRows;
            if (rows.Count > maxRows)
            {
                _tableMore.Text = Strings.MoreItems(rows.Count - maxRows);
                _tableMore.Place(RightX + 12f, y + (maxRows + 1) * TableRowH - 3f, RightW - 12f, 14f);
            }
        }

        private void PlaceTableHover()
        {
            var r = _tableHoverSlot >= 0 && _tableHoverSlot < _table.Length ? _table[_tableHoverSlot] : null;
            _tableHover.Active = r != null && r.Root.Active;
            if (_tableHover.Active) _tableHover.Place(RightX, r.Root.Y, RightW, TableRowH);
        }

        private void Chart(Encounter enc, List<SourceStats> rows, SourceStats sel)
        {
            var shown = rows.Take(6).ToList();
            if (sel != null && !shown.Contains(sel)) shown.Add(sel);

            var n = 2;
            foreach (var s in shown) n = Math.Max(n, s.PerSecond.Count);
            var series = new List<LineChart.Series>(shown.Count);
            var peak = 0f;
            foreach (var s in shown)
            {
                var sm = Smooth(s.PerSecond, n, 5);
                foreach (var v in sm) if (v > peak) peak = v;
                series.Add(new LineChart.Series { Values = sm, Color = Theme.SourceColor(s), Bold = s == sel, Name = Strings.SourceName(s) });
            }
            _chart.Set(series, peak * 1.12f, Strings.Peak(Fmt.Short(peak)), enc.DurationSeconds, UiRoot.Scale);
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

        /// <summary>把这个视图里所有人的拆分加起来：没选中任何人时，拆分区看全队的技能 / 类型 / 元素构成。</summary>
        private static SourceStats Everyone(List<SourceStats> rows)
        {
            var a = new SourceStats { Name = Strings.AllSources, InstanceId = int.MinValue };
            foreach (var s in rows)
            {
                a.Total += s.Total;
                a.Hits += s.Hits;
                a.Crits += s.Crits;
                if (s.MaxHit > a.MaxHit) a.MaxHit = s.MaxHit;
                for (var i = 0; i < a.ByAttribute.Length; i++) a.ByAttribute[i] += s.ByAttribute[i];
                for (var i = 0; i < a.ByType.Length; i++) a.ByType[i] += s.ByType[i];
                for (var i = 0; i < a.ByHealKind.Length; i++) a.ByHealKind[i] += s.ByHealKind[i];
                Merge(a.BySkill, s.BySkill);
                Merge(a.ByTarget, s.ByTarget);
                if (s.FirstHitTime >= 0d && (a.FirstHitTime < 0d || s.FirstHitTime < a.FirstHitTime)) a.FirstHitTime = s.FirstHitTime;
                if (s.LastHitTime > a.LastHitTime) a.LastHitTime = s.LastHitTime;
            }
            return a;
        }

        private static void Merge(Dictionary<string, SkillStats> into, Dictionary<string, SkillStats> from)
        {
            foreach (var kv in from)
            {
                into.TryGetValue(kv.Key, out var st);
                st.Total += kv.Value.Total;
                st.Hits += kv.Value.Hits;
                st.Crits += kv.Value.Crits;
                if (kv.Value.Max > st.Max) st.Max = kv.Value.Max;
                into[kv.Key] = st;
            }
        }

        /// <param name="encKey">这一段是谁（会话 + 段）：换了段拆分表回到顶上。</param>
        private void Breakdown(float y, SourceStats sel, List<SourceStats> everyone, string encKey)
        {
            var src = sel ?? (everyone.Count > 0 ? Everyone(everyone) : null);
            var heal = _view == TrackerView.Healing;
            Dimension dim;
            float tabsW;
            _dims.Active = !heal;
            _healDims.Active = heal;
            if (heal)
            {
                dim = _healDimSel;
                _healDims.SetLabels(Strings.TabHealSources, Strings.TabTargets(_view));
                _healDims.Select(dim == Dimension.Target ? 1 : 0);
                tabsW = _healDims.Place(RightX, y, 72f, 26f);
            }
            else
            {
                if (_dim == Dimension.HealKind) _dim = Dimension.Skill;
                dim = _dim;
                _dims.SetLabels(Strings.TabSkills, Strings.TabTypes, Strings.TabElements, Strings.TabTargets(_view));
                _dims.Select(System.Array.IndexOf(DamageDims, _dim));
                tabsW = _dims.Place(RightX, y, 54f, 26f);
            }
            _breakWho.Active = src != null;
            if (src != null) _breakWho.Text = $"{(sel != null ? Strings.SourceName(sel) : Strings.AllSources)} — {Strings.ViewLabel(_view)}";
            _breakWho.Place(RightX + tabsW + 10f, y, RightW - DonutSize - 16f - tabsW - 10f, 26f);
            y += 30f;

            var tw = RightW - DonutSize - 16f;
            var detailed = dim == Dimension.Skill || dim == Dimension.Target;
            var cols = detailed ? new[] { 138f, 66f, 52f, 48f, 46f, tw - 350f } : new[] { tw - 150f, 90f, 60f };
            var names = detailed
                ? new[] { Strings.ColItem, Strings.ColTotal, Strings.ColShare, Strings.ColHits, Strings.ColCrit, Strings.ColMax }
                : new[] { Strings.ColItem, Strings.ColTotal, Strings.ColShare };

            var x = RightX;
            for (var c = 0; c < _breakHeads.Length; c++)
            {
                _breakHeads[c].Active = src != null && c < names.Length;
                if (c >= names.Length) continue;
                _breakHeads[c].Text = names[c];
                var pad = c == 0 ? 14f : 0f;
                _breakHeads[c].Place(x + pad, y, cols[c] - pad - 4f, BreakRowH);
                x += cols[c];
            }

            _breakEmpty.Active = src == null;
            _donut.Active = src != null;
            var rows = src != null ? TbhCombatTracker.Breakdown.Of(src, dim, _view) : new List<BreakdownRow>();
            if (src == null)
            {
                _breakEmpty.Text = Strings.NoBreakdown;
                _breakEmpty.Place(RightX, y, RightW, BreakRowH);
                _breakHoverRow = _breakHighlight = -1;
            }
            else
            {
                // 拆分行和环上的一段互相高亮：指着哪边都行
                _breakHoverRow = HoveredBreakRow();
                var highlight = _breakHoverRow >= 0 && _breakHoverRow < rows.Count ? _breakHoverRow : _donut.Hovered;
                // 先定环：哪些行单独成段、哪些并进「其他」，表里的色点跟着它
                _donut.Place(RightX + RightW - DonutSize, y - 4f, DonutSize);
                _donut.Set(rows, Fmt.Short(src.Total), Fmt.Short(src.Dps) + "/s", UiRoot.Scale, highlight);
                _breakFolded = _donut.Folded;
                _breakHighlight = highlight;
                if (rows.Count == 0)
                {
                    _breakEmpty.Active = true;
                    _breakEmpty.Text = Strings.NoBreakdown;
                    _breakEmpty.Place(RightX + 14f, y + BreakRowH, tw, BreakRowH);
                }
            }

            double sum = 0;
            foreach (var r in rows) sum += r.Value;
            _breakData = rows;
            _breakSum = sum;
            _breakCols = cols;
            _breakW = tw;
            _breakDetailed = detailed;
            _breakHeal = heal;

            // 一屏 BreakRows 行，多的用滚轮翻（右边的空隙放滚动条）；换了段 / 视图 / 维度 / 选中的人就回到顶上。
            // 视口只和行一样高：行下面的空白照样是「点空白处取消选中」
            var key = $"{encKey}|{(int)_view}|{(int)dim}|{sel?.InstanceId}";
            _breakList.Place(RightX - 2f, y + BreakRowH, tw + 14f, Mathf.Min(rows.Count, BreakRows) * BreakRowH);
            _breakList.Count = rows.Count;
            if (key != _breakKey)
            {
                _breakKey = key;
                _breakList.ScrollToTop();
            }
            _breakRange.Active = rows.Count > BreakRows;
            if (_breakRange.Active) _breakRange.Place(RightX + 14f, y + (BreakRows + 1) * BreakRowH, tw - 14f, BreakRowH);
            BindBreaks();
        }

        private int HoveredBreakRow()
            => _breakHoverSlot >= 0 && _breakHoverSlot < _breaks.Length && _breaks[_breakHoverSlot].Root.Active
                ? _breaks[_breakHoverSlot].Item
                : -1;

        /// <summary>把拆分行摆到当前滚动位置、填上内容。滚动动画每帧都会调它。</summary>
        private void BindBreaks()
        {
            var rows = _breakData;
            var vr = _breakList.Viewport.InWindow;
            var otherHot = _breakHighlight == DonutChart.OtherSlice;
            for (var slot = 0; slot < _breaks.Length; slot++)
            {
                var r = _breaks[slot];
                var i = _breakList.ItemAt(slot, out var y);
                if (slot >= _breakList.PoolSize || i >= rows.Count)
                {
                    r.Root.Active = false;
                    r.Item = -1;
                    continue;
                }
                var row = rows[i];
                r.Item = i;
                r.Root.Active = true;
                r.Root.Place(2f, y, _breakW, BreakRowH);
                // 被视口裁掉的那部分不该还能指
                var rr = r.Root.InWindow;
                var top = Mathf.Max(rr.y, vr.y);
                var bottom = Mathf.Min(rr.yMax, vr.yMax);
                r.Hit.Rect = new Rect(rr.x, top, rr.width, Mathf.Max(0f, bottom - top));
                r.Hover.Active = i == _breakHighlight || (otherHot && i >= _breakFolded);
                r.Hover.Place(-2f, 0f, _breakW + 4f, BreakRowH);
                r.Dot.Place(2f, BreakRowH * 0.5f - 3.5f, 7f, 7f);
                r.Dot.Color = i < _breakFolded ? Theme.PaletteAt(i) : Theme.OtherSlice;
                var share = Fmt.Pct(_breakSum > 0 ? row.Value / _breakSum : 0d);
                var cells = _breakDetailed
                    ? new[] { row.Label, Fmt.Short(row.Value), share, Fmt.Count(row.Hits),
                              row.Hits > 0 && !_breakHeal ? Fmt.Pct((double)row.Crits / row.Hits) : "—", Fmt.Short(row.Max) }
                    : new[] { row.Label, Fmt.Short(row.Value), share };
                var cx = 0f;
                for (var c = 0; c < r.Cells.Length; c++)
                {
                    var cell = r.Cells[c];
                    cell.Active = c < cells.Length && c < _breakCols.Length;
                    if (!cell.Active) continue;
                    cell.Text = cells[c];
                    cell.Color = c <= 1 ? Theme.Text : Theme.TextDim;
                    var pad = c == 0 ? 14f : 0f;
                    cell.Place(cx + pad, 0f, _breakCols[c] - pad - 4f, BreakRowH);
                    cx += _breakCols[c];
                }
            }
            _breakList.PaintThumb();
            if (_breakRange.Active)
            {
                _breakList.VisibleRange(out var from, out var to);
                _breakRange.Text = $"{from}–{to} / {rows.Count}";
            }
            // 滚动时光标下换了一行：重排一次，环上跟着突出它
            if (HoveredBreakRow() != _breakHoverRow) Dirty = true;
        }

        // ================================================================== 右侧：载入中 / 失败

        private void LoadingPane(Item item, bool loading)
        {
            var rec = item.Rec;
            var failed = rec != null && EncounterLoader.Failed(rec);
            _loadingText.Text = rec == null ? Strings.NoEncounters
                              : failed ? LoadError()
                              : Strings.Loading(Title(rec));
            _loadingText.Place(RightX, BodyY + 150f, RightW, 24f);
            _retry.Active = failed && EncounterLoader.Error != "noLog";
            _retry.SetText(Strings.BtnRetry);
            var bw = _retry.Measure(12f);
            _retry.Place(RightX + (RightW - bw) * 0.5f, BodyY + 184f, bw, 26f);
        }

        private static string LoadError()
        {
            var e = EncounterLoader.Error;
            return e == "notFound" ? Strings.LoadNotFound : e == "noLog" ? Strings.LoadNoLog : Strings.LoadFailed(e);
        }

        private void Retry()
        {
            var it = Current();
            if (it.Rec == null) return;
            EncounterLoader.ClearError();
            Dirty = true;
        }

        // ================================================================== 导入

        private void TogglePicker()
        {
            _picker = !_picker;
            if (_picker) RefreshFiles();
            Dirty = true;
        }

        private void Picker()
        {
            var x = RightX;
            var y = BodyY;
            var h = H - BodyY - Pad;
            _pickBg.Place(x, y, RightW, h);
            _pickTitle.Text = Strings.PickLog;
            _pickTitle.Place(x + 12f, y + 6f, RightW - 110f, 24f);
            _pickCancel.SetText(Strings.BtnCancel);
            var bw = Mathf.Max(64f, _pickCancel.Measure(12f));
            _pickCancel.Place(x + RightW - bw - 8f, y + 6f, bw, 24f);

            _pickList.Place(x + 4f, y + 38f, RightW - 8f, h - 42f);
            _pickList.Count = _files.Count;
            _pickEmpty.Active = _files.Count == 0;
            if (_files.Count == 0)
            {
                _pickEmpty.Text = Strings.NoLogs;
                _pickEmpty.Place(x, y + 60f, RightW, 24f);
            }
            BindPicker();
        }

        private void BindPicker()
        {
            var vw = _pickList.Viewport.W;
            var vr = _pickList.Viewport.InWindow;
            for (var slot = 0; slot < _pickRows.Length; slot++)
            {
                var r = _pickRows[slot];
                var index = _pickList.ItemAt(slot, out var y);
                if (slot >= _pickList.PoolSize || index >= _files.Count)
                {
                    r.Root.Active = false;
                    r.Item = -1;
                    continue;
                }
                var f = _files[index];
                r.Item = index;
                r.Root.Active = true;
                r.Root.Place(0f, y, vw, _pickList.RowH);
                var rr = r.Root.InWindow;
                var top = Mathf.Max(rr.y, vr.y);
                var bottom = Mathf.Min(rr.yMax, vr.yMax);
                r.Hit.Rect = new Rect(rr.x, top, rr.width - 9f, Mathf.Max(0f, bottom - top));

                var current = string.Equals(f.FullName, EventLogWriter.CurrentPath, StringComparison.OrdinalIgnoreCase);
                r.Name.Text = f.Name + (current ? Strings.CurrentFileTag : "");
                r.Name.Place(10f, 0f, vw - 220f, _pickList.RowH);
                r.Size.Text = Size(f);
                r.Size.Place(vw - 206f, 0f, 70f, _pickList.RowH);
                r.Date.Text = f.LastWriteTime.ToString("MM-dd HH:mm", CultureInfo.InvariantCulture);
                r.Date.Place(vw - 126f, 0f, 112f, _pickList.RowH);
            }
            PlacePickHover();
            _pickList.PaintThumb();
        }

        private void PlacePickHover()
        {
            var r = _pickHoverSlot >= 0 && _pickHoverSlot < _pickRows.Length ? _pickRows[_pickHoverSlot] : null;
            _pickHover.Active = r != null && r.Root.Active;
            if (_pickHover.Active) _pickHover.Place(0f, r.Root.Y + 1f, _pickList.Viewport.W - 6f, _pickList.RowH - 2f);
        }

        private void RefreshFiles()
        {
            _pickList.ScrollToTop();
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

        /// <summary>后台线程读文件、重新解析；主线程在 Tick 里取结果。解析器是纯 C#，不碰任何 Unity 对象。</summary>
        private void StartImport(FileInfo f)
        {
            if (_importing) return;
            _importing = true;
            _importingName = f.Name;
            _picker = false;
            Dirty = true;

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

        private void PollImport()
        {
            var result = _importResult;
            if (result != null)
            {
                _importResult = null;
                _imported = result;
                _sel = result.Records.Count > 0 ? result.Records[result.Records.Count - 1].Index : -1;
                _list.ScrollToTop();
                var current = string.Equals(result.SourcePath, EventLogWriter.CurrentPath, StringComparison.OrdinalIgnoreCase);
                SetStatus(Strings.ImportDone(result.Records.Count, result.EventCount) +
                          (current ? Strings.ImportCurrentFile : result.Truncated ? Strings.ImportTruncated : ""));
                Mod.Log.Msg($"已导入战斗日志 {result.SourcePath}：{result.Records.Count} 段，{result.EventCount} 条事件" +
                            $"（不认识 {result.UnknownLines}，坏行 {result.BadLines}{(result.Truncated ? "，末尾不完整" : "")}）");
                Dirty = true;
            }

            var error = _importError;
            if (error != null)
            {
                _importError = null;
                SetStatus(Strings.ImportFailed(error));
                Mod.Log.Warning($"导入战斗日志失败：{error}");
                Dirty = true;
            }
        }

        private void BackToLive()
        {
            _imported = null;
            _sel = -1;
            _list.ScrollToTop();
            Dirty = true;
        }

        // ================================================================== 其它按钮

        private void Export()
        {
            if (_shown == null) return;
            var path = DamageTracker.ExportCsv(_shown, _imported == null ? "live" : "import");
            SetStatus(path != null ? Strings.Exported(Path.GetFileName(path)) : Strings.ExportFailed);
        }

        public void OpenLogFolder()
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

        private void SetStatus(string s)
        {
            _status = s;
            _statusUntil = Time.unscaledTime + 8f;
            Dirty = true;
        }

        // ================================================================== 小工具

        private static string Title(EncounterRecord r) => Strings.EncounterTitle(r.StageName, r.Run, r.StageNo, r.Index);

        private static string Clock(Session s, double start)
            => s.StartedAt.HasValue
                ? s.StartedAt.Value.AddSeconds(start).ToLocalTime().ToString("HH:mm", CultureInfo.InvariantCulture)
                : "+" + Fmt.Dur(start);

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
    }
}
