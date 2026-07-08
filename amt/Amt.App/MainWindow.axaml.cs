using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Animation;
using Avalonia.Animation.Easings;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Media.Transformation;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Amt.Core;

namespace Amt.App;

/// One row in the installed table, ready for the UI to bind to. Name/File/Installed are fixed at scan time, but
/// Latest + Status (and its colour) arrive LATER — filled by the unified CF+Modrinth update scan — so those raise
/// PropertyChanged and the bound cells refresh in place without rebuilding the row. Category starts as the keyword
/// guess and is upgraded to the mod's real CurseForge tag once the category list is fetched.
public sealed class ModRow : INotifyPropertyChanged
{
    public string Name { get; init; } = "";
    public string Installed { get; init; } = "";
    public int Category { get; set; }
    public ModStatus? Detail { get; set; }     // the full CF+Modrinth result — the Update-all installer reads this

    // AMT-06 row upgrade: manifest-fed author + icon URL, file-system size/date, and the mod's environment.
    public string Author { get; init; } = "";
    public string IconUrl { get; init; } = "";
    public string SizeText { get; init; } = "";
    public string AddedText { get; init; } = "";
    public string Initial => Name.Length > 0 ? Name[..1].ToUpperInvariant() : "?";   // the icon's letter fallback

    // notifying: the enable toggle RENAMES the jar (.jar ⇄ .jar.disabled), so the file name follows it live
    private string _file = "";
    public string File { get => _file; set => Set(ref _file, value); }

    private bool _enabled = true;
    public bool Enabled { get => _enabled; set => Set(ref _enabled, value); }

    private string _env = "";
    public string Env { get => _env; set => Set(ref _env, value); }

    private Bitmap? _icon;                     // streamed in by IconCache; null = the monogram fallback shows
    public Bitmap? Icon { get => _icon; set => Set(ref _icon, value); }

    // notifying so the per-row update BUTTON's hit-testing (IsHitTestVisible binding) tracks it live after a scan
    private bool _hasUpdate;
    public bool HasUpdate { get => _hasUpdate; set => Set(ref _hasUpdate, value); }

    private string _latest = "—";
    public string Latest { get => _latest; set => Set(ref _latest, value); }

    private string _status = "—";
    public string Status { get => _status; set => Set(ref _status, value); }

    private IBrush? _statusBrush;              // colours the STATUS cell: amber=update, green=current, grey=local/off
    public IBrush? StatusBrush { get => _statusBrush; set => Set(ref _statusBrush, value); }

    public event PropertyChangedEventHandler? PropertyChanged;
    private void Set<T>(ref T field, T value, [CallerMemberName] string? prop = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return;
        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(prop));
    }
}

/// One Forks-page row: one upstream of one fork, after ForkWatcher checked it. Rows are built from completed
/// results (no live mutation), so plain init-only properties are enough.
public sealed class ForkRow
{
    public string Name { get; init; } = "";
    public string Detail { get; init; } = "";   // kind:id · note
    public string Base { get; init; } = "";
    public string Latest { get; init; } = "";
    public string Verdict { get; init; } = "";
    public IBrush? VerdictBrush { get; init; }
    public string Url { get; init; } = "";
    public bool HasUrl => Url.Length > 0;

    // AMT-21 cohesion: a monogram for the 34px row icon, + a friendly chip label for the verdict
    public string Initial => Name.Length > 0 ? Name[..1].ToUpperInvariant() : "?";
    public string Label => Verdict switch
    {
        "UPDATE" => "Behind", "current" => "Up to date", "no-upstream" => "No upstream",
        "set-base" => "Set base", "ahead" => "Ahead", "review" => "Review", _ => Verdict,
    };
}

/// One Browse-page result: a catalogue hit from either (or both) sites. Only the streamed icon mutates.
public sealed class BrowseRow : INotifyPropertyChanged
{
    public string Title { get; init; } = "";
    public string Author { get; init; } = "";
    public string Desc { get; init; } = "";
    public string Downloads { get; init; } = "";
    public string IconUrl { get; init; } = "";
    public string MrUrl { get; init; } = "";
    public string CfUrl { get; init; } = "";
    public bool OnMr => MrUrl.Length > 0;
    public bool OnCf => CfUrl.Length > 0;
    public string Initial => Title.Length > 0 ? Title[..1].ToUpperInvariant() : "?";

    private Bitmap? _icon;
    public Bitmap? Icon { get => _icon; set { if (_icon == value) return; _icon = value; PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Icon))); } }
    public event PropertyChangedEventHandler? PropertyChanged;
}

/// One Cross-version matrix row: an MC version and what each site's newest build for it is.
public sealed class CrossRow
{
    public string Mc { get; init; } = "";
    public string Cf { get; init; } = "";
    public string Mr { get; init; } = "";
    public IBrush? McBrush { get; init; }   // the pack's own MC version highlights
}

/// One Data/Resource-packs row (read-only scan result).
public sealed class PackRow
{
    public string Name { get; init; } = "";
    public string Desc { get; init; } = "";
    public string Source { get; init; } = "";
    public string Format { get; init; } = "";
    public string Size { get; init; } = "";
    public string Path { get; init; } = "";
    public string Badge { get; init; } = "";
    public IBrush? BadgeBg { get; init; }
    public IBrush? BadgeFg { get; init; }
    public bool HasBadge => Badge.Length > 0;
}

public partial class MainWindow : Window
{
    // Everything instance-specific (mods folder, MC version, loader, the user's CF key) lives in the persisted
    // settings — nothing is hardcoded anymore. On a dev machine the first run auto-points at the live dev pack
    // below (only if it actually exists) so the app opens working; a public first run starts empty and the
    // Settings tab is where the user points it at their instance.
    private static readonly string DevInstance =
        @"C:\Users\linde\curseforge\minecraft\Instances\Ultimate vibes distant horizons version";
    private AppSettings _settings = new();

    private readonly List<ModRow> _all = new();
    private readonly ObservableCollection<ModRow> _rows = new();
    private int _cat;            // 0 = All
    private int _tab;            // 0 = Installed
    private string _query = "";
    private bool _updOnly;
    private int _sortCol;        // 0=MOD 1=INSTALLED 2=LATEST 3=STATUS (the header Tags)
    private bool _sortDesc;      // clicking the active header flips direction
    private int _updates;        // how many installed mods have a newer build on EITHER site (CF or Modrinth)
    private int _scanGen;        // bumps on every LoadMods so a stale in-flight scan can't stamp a rebuilt table
    private bool _installing;    // Update-all re-entry guard
    private IReadOnlyDictionary<int, string>? _cfCatNames;   // CF's id→name category list, fetched once per run

    // palette (mirrors the App.axaml brushes each hex is commented against there, for the bits we build in code)
    private static readonly IBrush Ink = SolidColorBrush.Parse("#ffffff");
    private static readonly IBrush Mut = SolidColorBrush.Parse("#d4d4d4");
    private static readonly IBrush Dim = SolidColorBrush.Parse("#9b9b9b");
    private static readonly IBrush Strong = SolidColorBrush.Parse("#ffffff");
    private static readonly IBrush Primary = SolidColorBrush.Parse("#5a2e7c");
    private static readonly IBrush OnPrimary = SolidColorBrush.Parse("#ffffff");
    private static readonly IBrush Raised = SolidColorBrush.Parse("#383838");
    private static readonly IBrush Ok = SolidColorBrush.Parse("#6ccb8f");
    private static readonly IBrush Up = SolidColorBrush.Parse("#e6b450");
    private static readonly IBrush Err = SolidColorBrush.Parse("#d9534f");   // API-down / bad-key state

    public MainWindow()
    {
        InitializeComponent();

        // Settings first — everything below reads them. First run on the dev machine: point at the live dev
        // pack so the app opens working (a public machine won't have that path, so it stays unconfigured and
        // the Settings tab is the entry point).
        _settings = SettingsStore.Load();
        if (_settings.InstancePath.Length == 0 && Directory.Exists(DevInstance))
        {
            _settings.InstancePath = DevInstance;
            SettingsStore.Save(_settings);
        }

        // the status-bar version chips come from the assemblies — bump each csproj <Version> per patch, they follow
        UiVer.Text = VerOf(typeof(MainWindow).Assembly);
        CoreVer.Text = VerOf(typeof(UpdateChecker).Assembly);

        RowsList.ItemsSource = _rows;
        ApplyColumnPrefs();   // the persisted column picks (AMT-06)
        ApplyRowDensity();    // the persisted row density (AMT-12)
        BuildGlobalSearch();  // the one-bar global search popup (AMT-16)
        LoadTabOrder();   // the drag-reordered strip order (validated permutation, AMT-04)
        BuildTabs();
        ShowInstance();
        FillSettingsForm();
        // settings AUTO-SAVE when a field loses focus — the Save button is just the manual backstop
        foreach (var c in new Control[] { SetInstance, SetMcVersion, SetLoader, SetCfKey, SetDownloadDir, SetLaunchCmd })
            c.LostFocus += OnSettingsAutoSave;
        // instant display from the non-secret profile cache; the real session restores silently below (Opened)
        if (AccountStore.LoadProfile() is { Username.Length: > 0 } p)
            _account = new Account { Username = p.Username, Uuid = p.Uuid };
        SizeChanged += (_, _) => { LayoutTabs(); DockOnResize(); };   // tabs fold + panels auto-hide/clamp with the window
        Opened += (_, _) =>
        {
            RoundCorners(); LayoutTabs(); RestoreSidebarFromSettings(); LoadMods();
            CheckAppUpdateAsync();   // quiet self-update check (AMT-13) — shows a chip only if there's a newer release
            RestoreAccountSilentAsync();   // silent MSAL refresh (AMT-14) — no browser if the cached token is live
            // dev/test hooks: AMT_START_TAB=<idx> opens on that tab; AMT_BROWSE_QUERY=<q> runs a Browse search
            // on launch — both exist so tab flows can be screenshot-verified without a mouse
            if (int.TryParse(Environment.GetEnvironmentVariable("AMT_START_TAB"), out var t)
                && t >= 0 && t < TabDefs.Length) SelectTab(t);
            if (Environment.GetEnvironmentVariable("AMT_BROWSE_QUERY") is { Length: > 0 } q)
            { BrowseBox.Text = q; RunBrowseSearch(); }
            // AMT_SEARCH=<q>: drive the global-search bar on launch (verify the popup without a keyboard)
            if (Environment.GetEnvironmentVariable("AMT_SEARCH") is { Length: > 0 } gs)
                DispatcherTimer.RunOnce(() => { SearchBox.Text = gs; }, TimeSpan.FromMilliseconds(400));
            // AMT_CONTENT=res|data: flip the Installed content-type switch on launch (mouse-free verify)
            var ct = Environment.GetEnvironmentVariable("AMT_CONTENT");
            if (ct is "res" or "data")
                DispatcherTimer.RunOnce(() =>
                {
                    _contentType = ct;
                    CtMods.Classes.Remove("ctactive");
                    (ct == "res" ? CtRes : CtData).Classes.Add("ctactive");
                    CtxCategories.IsVisible = false; RailPanel.IsVisible = false;
                    SelectTab(0);
                }, TimeSpan.FromMilliseconds(500));
            // AMT_CROSS_MOD=<filter>: after the scan has had time to land, open Cross-version filtered to it
            // and pick the first match — proves the matrix without a mouse
            if (Environment.GetEnvironmentVariable("AMT_CROSS_MOD") is { Length: > 0 } cm)
                DispatcherTimer.RunOnce(() =>
                {
                    SelectTab(4);
                    CrossFilterBox.Text = cm;
                    // select on the NEXT tick so the filter's ItemsSource swap has landed first
                    Dispatcher.UIThread.Post(() => { if (CrossModsList.ItemCount > 0) CrossModsList.SelectedIndex = 0; });
                }, TimeSpan.FromSeconds(12));
        };
    }

    // ---- data ----

    private void LoadMods()
    {
        _all.Clear();
        _updates = 0;   // the old count belongs to rows that no longer exist; the fresh scan refills it

        // the manifest rides along for the row EXTRAS (AMT-06): the CF avatar thumbnail + primary author are
        // right there in minecraftinstance.json — icons and an AUTHOR column with no key and no network
        var byDisk = new Dictionary<string, InstalledAddon>(StringComparer.OrdinalIgnoreCase);
        foreach (var a in InstanceReader.Read(_settings.InstanceJson))
            if (a.OnDisk.Length > 0) byDisk[a.OnDisk] = a;

        foreach (var m in ModScanner.Scan(_settings.ModsDir))
        {
            byDisk.TryGetValue(m.FileName, out var addon);
            // the manifest keys by the ENABLED name — a disabled jar still matches once .disabled is stripped
            if (addon is null && m.FileName.EndsWith(".disabled", StringComparison.OrdinalIgnoreCase))
                byDisk.TryGetValue(m.FileName[..^".disabled".Length], out addon);

            string size = "", added = "";
            try
            {
                var fi = new FileInfo(Path.Combine(_settings.ModsDir, m.FileName));
                if (fi.Exists) { size = FmtSize(fi.Length); added = fi.LastWriteTime.ToString("yyyy-MM-dd"); }
            }
            catch { /* size/date are cosmetic — a racing delete just leaves them blank */ }

            var row = new ModRow
            {
                Name = m.DisplayName,
                File = m.FileName,
                Installed = string.IsNullOrEmpty(m.Version) ? "—" : m.Version,
                Category = Categories.Classify(m.ModId, m.DisplayName),
                StatusBrush = Dim,
                Author = addon?.PrimaryAuthor ?? "",
                IconUrl = addon?.ThumbnailUrl ?? "",
                SizeText = size,
                AddedText = added,
                Enabled = m.Enabled,
            };
            // a .jar.disabled mod is shown but marked — it's excluded from the update scan (not live in the pack)
            if (!m.Enabled) row.Status = "disabled";
            _all.Add(row);
        }

        // last-known state first (author call): the cached scan fills the table instantly, the live scan
        // then overwrites it — the app never opens looking blank when it knew better yesterday
        var cache = ScanCache.Load();
        if (cache.Count > 0)
        {
            foreach (var row in _all)
            {
                if (row.Status == "disabled" || !cache.TryGetValue(row.File, out var c)) continue;
                if (c.Latest.Length > 0) row.Latest = c.Latest;
                row.Status = c.Status.Length > 0 ? c.Status : row.Status;
                row.HasUpdate = c.HasUpdate;
                row.Env = c.Env;
                row.StatusBrush = c.HasUpdate ? Up : c.Status == "up to date" ? Ok : Dim;
            }
            _updates = _all.Count(r => r.HasUpdate);
        }

        BuildCats();
        ApplyFilter();
        StreamIconsAsync();    // fire-and-forget: icons trickle in (disk cache first run onward)
        CheckUpdatesAsync();   // fire-and-forget: fills Latest + Status live once the sites answer
    }

    private static string FmtSize(long bytes) => bytes switch
    {
        >= 1 << 20 => $"{bytes / (double)(1 << 20):0.#} MB",
        >= 1 << 10 => $"{bytes / (double)(1 << 10):0} KB",
        _ => $"{bytes} B",
    };

    // Stream the row icons in the background: IconCache serves from disk after the first run, so this is a
    // one-time trickle per new mod. Generation-guarded so a mid-sweep rescan stops the stale sweep cold.
    private int _iconGen;
    private async void StreamIconsAsync()
    {
        var gen = ++_iconGen;
        var rows = _all.Where(r => r.IconUrl.Length > 0 && r.Icon is null).ToList();
        foreach (var row in rows)
        {
            if (gen != _iconGen) return;
            var bmp = await Task.Run(() => IconCache.GetAsync(row.IconUrl));
            if (gen != _iconGen) return;
            if (bmp is not null) row.Icon = bmp;   // back on the UI thread (await resumed here)
        }
    }

    // The unified update check (UpdateChecker = CurseForge + Modrinth merged per jar) — OFF the UI thread — then
    // fill in the Latest + Status cells + the Updates count. Modrinth is keyless; the CF side runs live with the
    // user's key, else falls back to the manifest's cached-latest, so most rows fill either way. A network failure
    // just leaves the columns as the scan left them — this never throws into the UI. `async void` is the right
    // shape here: a fire-and-forget UI-triggered task whose only "result" is mutating the bound rows.
    private async void CheckUpdatesAsync()
    {
        // Snapshot the settings + generation: a Refresh/Save mid-scan bumps _scanGen, and this scan's late results
        // are then dropped instead of stamping the rebuilt table (or mixing two instances' data).
        var gen = ++_scanGen;
        var settings = _settings;
        CoreStatus.Text = "checking updates…";
        CoreStatus.Foreground = Up;                    // busy = amber, idle = muted (colour-coded state)
        SegUpdText.Text = "checking…";                 // the Updates segment says what's happening
        Refresh.Classes.Add("spinning");               // the ⟳ glyph spins for the scan's duration

        IReadOnlyList<ModStatus> statuses;
        try { statuses = await Task.Run(() => UpdateChecker.ScanAsync(settings)); }
        catch { if (gen == _scanGen) ScanUiDone(); return; }
        if (gen != _scanGen) return;   // a newer scan owns the table now

        // back on the UI thread (the await captured it) — safe to touch the bound rows
        var byFile = new Dictionary<string, ModStatus>(StringComparer.OrdinalIgnoreCase);
        foreach (var st in statuses) byFile[st.OnDisk] = st;

        foreach (var row in _all)
        {
            if (!byFile.TryGetValue(row.File, out var st)) continue;
            row.Detail = st;
            // LATEST shows the side that actually HAS the newer build (that's the actionable one); with no update,
            // prefer Modrinth's clean version string over CF's full file name.
            var latest = st.MrHasUpdate ? st.MrLatestVersion
                       : st.CfHasUpdate ? st.CfLatestName
                       : st.MrLatestVersion.Length > 0 ? st.MrLatestVersion
                       : st.CfLatestName;
            if (latest.Length > 0) row.Latest = latest;
            row.HasUpdate = st.HasUpdate;
            if (st.Env.Length > 0) row.Env = st.Env;
            row.Status = st.Source == "local" ? "local" : st.HasUpdate ? "update" : "up to date";
            row.StatusBrush = st.HasUpdate ? Up : st.Source == "local" ? Dim : Ok;
        }
        _updates = _all.Count(r => r.HasUpdate);
        ScanUiDone();

        // "up to date" quiets down after ~10s (author call) — the state returns on the next scan; only the
        // actionable amber "update" stays. Guarded by generation so a newer scan's rows aren't wiped.
        DispatcherTimer.RunOnce(() =>
        {
            if (gen != _scanGen) return;
            foreach (var r in _all)
                if (r.Status == "up to date") r.Status = "";
        }, TimeSpan.FromSeconds(10));

        // persist this scan as the next launch's instant last-known state (best-effort, off-thread)
        var snapshot = _all
            .Where(r => r.Status != "disabled")
            .Select(r => new CachedMod { File = r.File, Latest = r.Latest == "—" ? "" : r.Latest, Status = r.Status, HasUpdate = r.HasUpdate, Env = r.Env })
            .ToList();
        _ = Task.Run(() => ScanCache.Save(snapshot));

        // per-API chips, from what the scan ACTUALLY got (never assumed): Modrinth is keyless so any hit = ok;
        // CF is live only when the keyed API answered (a cached-manifest fallback is honestly labelled).
        var mrOk = statuses.Any(s => s.Source.Contains("Modrinth"));
        SetChip(MrDot, MrState, mrOk ? Ok : Err, mrOk ? "live" : "offline?");   // same wording as the CF chip
        var cfLive = statuses.Any(s => s.Source.Contains("CF") && !s.CfLatestCached);
        var cfCached = statuses.Any(s => s.CfLatestCached);
        if (settings.CurseForgeApiKey.Length == 0)
            SetChip(CfDot, CfState, Up, cfCached ? "no key — cached" : "no key");
        else
            SetChip(CfDot, CfState, cfLive ? Ok : Err, cfLive ? "live" : "no answer — key ok?");

        await ApplyCfCategoriesAsync(gen);   // upgrade keyword guesses to real CF tags (no-op without a key)
        ApplyFilter();   // refresh the counts + (if the Updates filter is on) the visible list
    }

    // restore the scan-progress UI (spinner, segment label, core state colour) to rest
    private void ScanUiDone()
    {
        CoreStatus.Text = "idle";
        CoreStatus.Foreground = Mut;
        SegUpdText.Text = "Updates";
        Refresh.Classes.Remove("spinning");
    }

    // Swap the keyword-guessed buckets for the mods' REAL CurseForge tags: fetch CF's official id→name category
    // list once per run, then map each row's manifest categoryIds (primary first) onto a sidebar bucket. Rows CF
    // can't map keep the keyword guess — the heuristic is the fallback, never fought.
    private async Task ApplyCfCategoriesAsync(int gen)
    {
        if (_cfCatNames is null && _settings.CurseForgeApiKey.Length > 0)
        {
            var key = _settings.CurseForgeApiKey;
            _cfCatNames = await Task.Run(() => CurseForgeClient.CategoriesAsync(key));
            if (gen != _scanGen) return;
        }
        if (_cfCatNames is not { Count: > 0 }) return;

        foreach (var row in _all)
        {
            if (row.Detail is not { CfCategoryIds.Count: > 0 } d) continue;
            var names = d.CfCategoryIds
                .Select(id => _cfCatNames.TryGetValue(id, out var n) ? n : "")
                .Where(n => n.Length > 0);
            var bucket = Categories.FromCf(names);
            if (bucket >= 0) row.Category = bucket;
        }
        BuildCats();   // sidebar counts moved between buckets
    }

    // Sidebar header + the status-bar API chips reflect the configured instance. The search band's placeholder
    // is the full mods path (Explorer address-bar style — the empty bar tells you WHERE you are, typing searches).
    private void ShowInstance()
    {
        var p = _settings.InstancePath.TrimEnd('\\', '/');
        InstanceName.Text = p.Length > 0 ? Path.GetFileName(p) : "no instance";
        InstanceMeta.Text = $"{_settings.McVersion} · {_settings.Loader}";
        SearchBox.PlaceholderText = _settings.ModsDir.Length > 0 ? _settings.ModsDir : "search installed mods…";
        // pre-scan chip baseline: a grey dot alone = "not checked yet" (no stray dash); the scan fills in
        // live/cached/offline. Only the missing-key case carries text, since that's actionable.
        SetChip(MrDot, MrState, Dim, "");
        SetChip(CfDot, CfState, _settings.CurseForgeApiKey.Length == 0 ? Up : Dim,
                _settings.CurseForgeApiKey.Length == 0 ? "no key" : "");
    }

    private static void SetChip(Avalonia.Controls.Shapes.Ellipse dot, TextBlock text, IBrush colour, string state)
    {
        dot.Fill = colour;
        text.Text = state;
    }

    private readonly HashSet<int> _hiddenCats = new();   // right-click-hidden categories (persistence → AMT-12)

    // rebuild the sidebar category list (with live counts + the active highlight). Zero-count categories hide
    // (author call — the toggle for this behaviour joins the Settings inventory), as do right-click-hidden ones;
    // the active category always shows so the filter can't strand invisibly.
    private void BuildCats()
    {
        CatsPanel.Children.Clear();
        RailPanel.Children.Clear();   // the icon rail mirrors the same list as monogram chips
        RailInstanceText.Text = DockRules.Monogram(InstanceName.Text ?? "");
        ToolTip.SetTip(RailInstance, $"{InstanceName.Text} — instance settings");
        for (var i = 0; i < Categories.All.Length; i++)
        {
            var count = i == 0 ? _all.Count : _all.Count(m => m.Category == i);
            var active = i == _cat;
            if (!active && i != 0 && (count == 0 || _hiddenCats.Contains(i))) continue;

            var name = new TextBlock { Text = Categories.All[i], FontSize = 12.5, Foreground = active ? Strong : Mut };
            var cnt = new TextBlock
            {
                // count stays a readable grey — never the purple accent (unreadable on the dark row)
                Text = count.ToString(), FontSize = 12.5,
                Foreground = active ? Mut : Dim, HorizontalAlignment = HorizontalAlignment.Right,
            };
            var grid = new Grid();
            grid.Children.Add(name);
            grid.Children.Add(cnt);

            var b = new Border { Child = grid, Tag = i.ToString(), Cursor = new Cursor(StandardCursorType.Hand) };
            b.Classes.Add("cat");
            if (active) b.Classes.Add("active");
            b.PointerPressed += OnCatClick;
            CatsPanel.Children.Add(b);

            // the same category as an icon-rail chip: a monogram square, tooltip = the full name + count.
            // Reuses the "cat" hover/active styling; the local Padding beats the style's row padding.
            var chip = new Border
            {
                Height = 32, Padding = new Thickness(0), Tag = i.ToString(),
                Cursor = new Cursor(StandardCursorType.Hand),
                Child = new TextBlock
                {
                    Text = DockRules.Monogram(Categories.All[i]), FontSize = 11, FontWeight = FontWeight.SemiBold,
                    Foreground = active ? Strong : Mut,
                    HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
                },
            };
            ToolTip.SetTip(chip, $"{Categories.All[i]}  ({count})");
            chip.Classes.Add("cat");
            if (active) chip.Classes.Add("active");
            chip.PointerPressed += OnCatClick;
            RailPanel.Children.Add(chip);
        }
    }

    // rebuild the visible table from the current tab/category/search/updates filters
    private void ApplyFilter()
    {
        var q = _query.ToLowerInvariant();
        var filtered = new List<ModRow>();
        foreach (var m in _all)
        {
            if (_cat != 0 && m.Category != _cat) continue;
            if (_updOnly && !m.HasUpdate) continue;   // "Updates" segment → mods with a newer build on either site
            if (q.Length > 0 &&
                !m.Name.ToLowerInvariant().Contains(q) &&
                !m.File.ToLowerInvariant().Contains(q)) continue;
            filtered.Add(m);
        }

        // sort by the active header column; STATUS sorts by actionability (updates first when ascending)
        IEnumerable<ModRow> sorted = _sortCol switch
        {
            1 => filtered.OrderBy(r => r.Installed, StringComparer.OrdinalIgnoreCase),
            2 => filtered.OrderBy(r => r.Latest, StringComparer.OrdinalIgnoreCase),
            3 => filtered.OrderBy(StatusRank).ThenBy(r => r.Name, StringComparer.OrdinalIgnoreCase),
            _ => filtered.OrderBy(r => r.Name, StringComparer.OrdinalIgnoreCase),
        };
        if (_sortDesc) sorted = sorted.Reverse();

        _rows.Clear();
        foreach (var m in sorted) _rows.Add(m);
        ModsCount.Text = $"{_rows.Count} mods";
        UpdatesCount.Text = $"{_updates} updates";
        UpdatesCount.Foreground = _updates > 0 ? Up : Mut;   // slight highlight while something is pending
        CatLabel.Text = $"category: {Categories.All[_cat]}";

        // the Update-all button exists only while there is something to update (and isn't mid-run)
        UpdateAllBtn.IsVisible = _updates > 0;
        if (!_installing) UpdateAllText.Text = $"Update all ({_updates})";
    }

    // ---- interactions ----

    private void OnTabClick(object? sender, PointerPressedEventArgs e)
    {
        if (sender is Control b && b.Tag is string s && int.TryParse(s, out var idx)) { SelectTab(idx); e.Handled = true; }
    }

    // switch the active tab from anywhere — a tab click OR the overflow ▾ menu
    private void SelectTab(int idx)
    {
        CloseGlobalSearch();   // navigating dismisses the global-search overlay (AMT-16)
        // a popped-out tab's page lives in ITS window — selecting it focuses that window instead
        if (_poppedTabs.Contains(idx) && _popouts.TryGetValue(idx, out var pw)) { pw.Activate(); return; }
        _tab = idx;
        _closedTabs.Remove(idx);   // picking a tab (incl. from the ▾ list) reopens it
        for (var i = 0; i < _tabPanels.Length; i++)
        {
            var p = _tabPanels[i];
            if (p is null) continue;
            var isThis = i == idx;
            p.Classes.Remove("active");
            if (isThis) p.Classes.Add("active");
            p.ZIndex = isThis ? 2 : 0;   // the active tab renders ON TOP so overlapping feet stay clean
        }
        // wired pages show; the rest get the placeholder (Ported + Tools remain).
        // On the Installed tab the visible panel follows the content-type switch (AMT-20): Mods -> InstalledPanel,
        // Resource/Data packs -> the pack panels shown INLINE under Installed.
        var onInstalled = idx == 0;
        InstalledPanel.IsVisible = onInstalled && _contentType == "mods";
        ForksPanel.IsVisible = idx == 1;
        BrowsePanel.IsVisible = idx == 2;
        CrossPanel.IsVisible = idx == 4;
        DataPacksPanel.IsVisible = idx == 5 || (onInstalled && _contentType == "data");
        ResPacksPanel.IsVisible = idx == 6 || (onInstalled && _contentType == "res");
        ToolsPanel.IsVisible = idx == 7;
        SettingsPanel.IsVisible = idx == SettingsTabIdx;
        SoonPanel.IsVisible = idx is not (0 or 1 or 2 or 4 or 5 or 6 or 7) && idx != SettingsTabIdx;
        if (idx == 4) CrossModsList.ItemsSource = _all.OrderBy(r => r.Name, StringComparer.OrdinalIgnoreCase).ToList();
        if (idx == 5 || (onInstalled && _contentType == "data")) LoadDataPacks();
        if (idx == 6 || (onInstalled && _contentType == "res")) LoadResPacks();
        if (idx == 7 && ToolsFolders.Children.Count == 0) BuildTools();
        SoonLabel.Text = idx >= 0 && idx < TabDefs.Length ? $"{TabDefs[idx].Label} — coming soon" : "coming soon";
        if (idx == 1 && !_forksLoaded) LoadForksAsync();   // first visit checks the upstreams (lazy)
        UpdateSidebarContext();   // the sidebar is CONTEXTUAL (AMT-05) — it follows the active tab
        LayoutTabs();   // re-run so a tab picked from the overflow menu is pulled back into the visible strip
    }

    // The contextual sidebar (AMT-05): each tab brings its own panel. Installed = instance + categories,
    // Settings = the section nav, everything else = a placeholder until its page lands. The icon rail's
    // category chips are Installed-specific, so they hide with it (the instance chip stays useful everywhere).
    private void UpdateSidebarContext()
    {
        CtxInstalled.IsVisible = _tab == 0;
        CtxSettings.IsVisible = _tab == SettingsTabIdx;
        CtxSoon.IsVisible = _tab != 0 && _tab != SettingsTabIdx;
        if (CtxSoon.IsVisible && _tab >= 0 && _tab < TabDefs.Length) CtxSoonTitle.Text = TabDefs[_tab].Label;
        RailPanel.IsVisible = _tab == 0;
    }

    // a Settings-context nav row → scroll the page to that section (works even if Settings is popped out —
    // BringIntoView scrolls whichever ScrollViewer currently hosts the section)
    private void OnSettingsNav(object? sender, PointerPressedEventArgs e)
    {
        if (sender is not Border b || b.Tag is not string tag) return;
        Control? sec = tag switch
        {
            "instance" => SecInstance, "sources" => SecSources,
            "downloads" => SecDownloads, "appearance" => SecAppearance,
            "feedback" => SecFeedback, _ => null,
        };
        sec?.BringIntoView();
    }

    // The top-bar tabs: label + an optional little count badge. Installed (0) + Settings are wired so far.
    // Badges stay null until something real computes them (Forks will get the ForkWatcher's UPDATE count when
    // that tab is wired) — a shipped count must never be a mockup number.
    private static readonly (string Label, string? Badge)[] TabDefs =
    {
        ("Installed", null), ("Forks", null), ("Browse", null), ("Ported", null),
        ("Cross-version", null), ("Data packs", null), ("Resource packs", null),
        ("Tools", null), ("Settings", null),
    };
    private const int SettingsTabIdx = 8;    // "Settings" in TabDefs — keep in sync if tabs are reordered
                                             // (Feedback is now a section INSIDE Settings, not its own tab)

    // ---- tabs: built in code so they can progressively fold into an overflow ▾ menu as the window narrows ----

    private int _hoverTab = -1;         // the tab under the pointer — separators adjacent to it hide (Explorer)
    private readonly HashSet<int> _closedTabs = new();   // tabs the user X-closed — they live in the ▾ list until reopened
    // Explorer model (author, final): EVERY tab is a FIXED standard width. The window resizing NEVER changes a
    // tab's width — it only changes how many are VISIBLE; the rest fold into the ▾ list. Long labels wrap to a
    // second line inside that fixed width. Tabs sit flush; each tab draws its own 1px seam on its right edge.
    private const double TabW = 140;    // wide enough that "Resource packs" sits on one line (author)
    private const double TabGap = 0;    // tabs are flush — the seam is drawn INSIDE each tab's right edge
    private const double OverflowChipW = 34; // width of the ▾ overflow chip when it's shown

    private readonly Panel[] _tabPanels = new Panel[TabDefs.Length];      // each tab, so LayoutTabs can show/hide it
    private readonly Border[] _tabSeams = new Border[TabDefs.Length];     // the 1px seam on tab i's RIGHT edge
    private Border? _overflowChip;      // the ▾ chip that holds the tabs that don't fit
    private MenuFlyout? _overflowMenu;  // its menu, rebuilt with the hidden tabs on each layout

    // ---- AMT-04: drag-reorder + pop-out ----
    private int[] _tabOrder = [];                        // presentation order (a permutation of tab indices; persisted)
    private readonly HashSet<int> _poppedTabs = new();   // tabs living in their own pop-out window right now
    private readonly Dictionary<int, Window> _popouts = new();
    private int _dragTab = -1;          // tab index a drag might be starting on (-1 = none)
    private Point _dragStart;           // press point in TabsPanel space
    private bool _reordering;           // the horizontal drag threshold was crossed — we're live-reordering

    // the persisted order is trusted only if it's a REAL permutation — a stale/edited file can't lose tabs
    private void LoadTabOrder()
    {
        _tabOrder = Enumerable.Range(0, TabDefs.Length).ToArray();
        if (_settings.TabOrder is { Length: > 0 } saved
            && saved.Length == TabDefs.Length
            && saved.OrderBy(x => x).SequenceEqual(_tabOrder))
            _tabOrder = saved.ToArray();
    }

    // (Re)fill the strip in presentation order: tabs, then the ▾ chip. Called on build and after each reorder.
    private void RebuildStrip()
    {
        TabsPanel.Children.Clear();
        foreach (var i in _tabOrder)
            if (_tabPanels[i] is { } p) TabsPanel.Children.Add(p);
        if (_overflowChip is not null) TabsPanel.Children.Add(_overflowChip);
    }

    // Give a tab (or the chip / a separator) its own Width+Opacity transitions so folding into / out of the
    // overflow animates smoothly. A Transitions collection can't be shared, so this returns a fresh one each call.
    private static Transitions FoldTransitions() => new()
    {
        // A SHORT width transition damps the per-frame jitter of a live resize — tabs GLIDE with the border instead
        // of stepping on every raw size event — while staying short enough not to feel like the old lag; ease-OUT
        // responds immediately. The fold (a visibility change) rides the same width transition to collapse/expand,
        // plus a softer opacity fade. (This replaces the round-12 per-frame transition toggle, which itself jittered.)
        new DoubleTransition { Property = Layoutable.WidthProperty, Duration = TimeSpan.FromMilliseconds(55), Easing = new CubicEaseOut() },
        new DoubleTransition { Property = Visual.OpacityProperty, Duration = TimeSpan.FromMilliseconds(130), Easing = new CubicEaseOut() },
    };

    // Build the tab strip: each tab = a TabShape silhouette behind a CENTRED, wrapping label, with a thin seam
    // between two idle tabs. LayoutTabs() sizes them all identically and folds the overflow into the ▾ chip.
    private void BuildTabs()
    {
        TabsPanel.Children.Clear();
        TabsPanel.Spacing = 0;   // tabs sit flush; the 1px seam between idle tabs is the only gap

        for (var i = 0; i < TabDefs.Length; i++)
        {
            var (label, badge) = TabDefs[i];

            // LEFT-aligned + wrapping (author): labels start at the tab's left edge like Explorer; an
            // over-long label drops to a second line rather than widening its tab — every tab stays TabW.
            var text = new TextBlock
            {
                Text = label, FontSize = 13, TextWrapping = TextWrapping.Wrap, TextAlignment = TextAlignment.Left,
                LineHeight = 15, VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Left,
            };
            text.Classes.Add("tablabel");

            Control content;
            if (badge is null)
                content = text;
            else
            {
                var row = new StackPanel
                {
                    Orientation = Orientation.Horizontal, Spacing = 5,
                    VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Left,
                };
                row.Children.Add(text);
                row.Children.Add(new TextBlock { Text = badge, Foreground = Dim, FontSize = 10, VerticalAlignment = VerticalAlignment.Center });
                content = row;
            }
            // label clears the X on the right (26px); 10px left inset = the Explorer-style left alignment line
            var pad = new Border { Child = content, Padding = new Thickness(10, 0, 26, 0), ClipToBounds = true };

            var idx = i;
            // the × close button: RIGHT edge, vertically centred (browser/Explorer placement). Clicking it folds
            // the tab into the ▾ list rather than destroying it; it reopens from that menu. e.Handled so the
            // click doesn't select the tab.
            var close = new Border
            {
                Width = 15, Height = 15, CornerRadius = new CornerRadius(3), Background = Brushes.Transparent,
                HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 10, 0), Cursor = new Cursor(StandardCursorType.Hand),
                Child = new TextBlock { Text = "✕", Foreground = Dim, FontSize = 9, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center },
            };
            close.Classes.Add("tabclose");
            close.PointerPressed += (_, ev) => { ev.Handled = true; CloseTab(idx); };

            var tab = new Panel
            {
                Width = TabW, Tag = i.ToString(), ZIndex = i == _tab ? 2 : 0,
                ClipToBounds = true, Transitions = FoldTransitions(),
            };
            tab.Classes.Add("tab");
            if (i == _tab) tab.Classes.Add("active");
            tab.Children.Add(new TabShape());
            tab.Children.Add(pad);
            tab.Children.Add(close);

            // the seam rides INSIDE the tab's right edge (so reordering moves it with its tab); shown only
            // when this tab and its next VISIBLE neighbour are both idle (UpdateSeparators decides)
            var seam = new Border
            {
                Width = 1, Height = 16, Background = Raised, Opacity = 0,
                HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Bottom,
                Margin = new Thickness(0, 0, 0, 8),
            };
            _tabSeams[i] = seam;
            tab.Children.Add(seam);

            // press selects; a horizontal drag live-reorders; a vertical drag tears the tab out into its own
            // window (Explorer). The press point is recorded here, the thresholds live in OnTabDragMove.
            tab.PointerPressed += (s2, ev) =>
            {
                OnTabClick(s2, ev);
                _dragTab = idx; _dragStart = ev.GetPosition(TabsPanel); _reordering = false;
            };
            tab.PointerMoved += (_, ev) => OnTabDragMove(idx, tab, ev);
            tab.PointerReleased += (_, _) => OnTabDragEnd();
            // hover hides the seams either side of this tab (Explorer) — a light separator refresh, no relayout
            tab.PointerEntered += (_, _) => { _hoverTab = idx; UpdateSeparators(); };
            tab.PointerExited += (_, _) => { if (_hoverTab == idx) { _hoverTab = -1; UpdateSeparators(); } };
            _tabPanels[i] = tab;
        }

        BuildOverflowChip();
        RebuildStrip();
    }

    // A seam is drawn on a tab's right edge only when that tab AND the next VISIBLE tab in presentation order
    // are both IDLE (not active, not hovered) — so the active/hovered tab reads as a clean card with no line
    // touching it (Explorer reference). Folded (width-0) tabs are skipped when finding the neighbour.
    private void UpdateSeparators()
    {
        var visible = _tabOrder.Where(t => _tabPanels[t]?.IsHitTestVisible ?? false).ToList();
        foreach (var t in _tabOrder)
        {
            var seam = _tabSeams[t];
            if (seam is null) continue;
            var pos = visible.IndexOf(t);
            var show = false;
            if (pos >= 0 && pos + 1 < visible.Count)
            {
                var next = visible[pos + 1];
                show = t != _tab && next != _tab && t != _hoverTab && next != _hoverTab;
            }
            seam.Opacity = show ? 1 : 0;
        }
    }

    // The ▾ overflow chip: a small tab-styled button at the end of the strip. It's hidden (Width 0) until some
    // tabs don't fit, then it fades in and its menu lists the hidden tabs so they're still reachable.
    private void BuildOverflowChip()
    {
        _overflowMenu = new MenuFlyout { Placement = PlacementMode.BottomEdgeAlignedLeft };
        _overflowChip = new Border
        {
            // ▾ centred in a tab-height chip (author: bigger + properly aligned — the old ⌄ floated high)
            Child = new TextBlock
            {
                Text = "▾", Foreground = Mut, FontSize = 18,
                HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 0, 2),
            },
            Width = 0, Opacity = 0, IsHitTestVisible = false,   // hidden until something overflows
            Height = 31, CornerRadius = new CornerRadius(7, 7, 0, 0),
            VerticalAlignment = VerticalAlignment.Bottom, Cursor = new Cursor(StandardCursorType.Hand),
            ClipToBounds = true, Transitions = FoldTransitions(),
        };
        _overflowChip.Classes.Add("overflowchip");
        _overflowChip.PointerPressed += (_, _) => _overflowMenu?.ShowAt(_overflowChip);
        // RebuildStrip() appends the chip after the ordered tabs
    }

    // Close a tab → it folds into the ▾ list (not destroyed); it reopens by picking it from that menu.
    private void CloseTab(int idx)
    {
        if (idx < 0 || idx >= TabDefs.Length) return;
        _closedTabs.Add(idx);
        if (_tab == idx)   // don't leave a closed tab active — jump to the first still-open tab IN ORDER
        {
            foreach (var i in _tabOrder)
                if (!_closedTabs.Contains(i) && !_poppedTabs.Contains(i)) { SelectTab(i); return; }
        }
        LayoutTabs();
    }

    // ---- AMT-04: tab drag mechanics — horizontal = live reorder, vertical = tear out into a window ----

    private void OnTabDragMove(int idx, Panel tab, PointerEventArgs e)
    {
        if (_dragTab != idx) return;
        var p = e.GetPosition(TabsPanel);

        // a decisive VERTICAL pull tears the tab out into its own window (Explorer). Only before reordering
        // starts — once tabs are shuffling horizontally, vertical wiggle shouldn't surprise-detach.
        if (!_reordering && Math.Abs(p.Y - _dragStart.Y) > 46)
        {
            _dragTab = -1;
            PopOutTab(idx);
            return;
        }

        if (!_reordering && Math.Abs(p.X - _dragStart.X) < 8) return;   // click jitter tolerance
        _reordering = true;

        // all visible tabs share TabW, so the slot under the pointer is plain arithmetic
        var vis = _tabOrder.Where(t => _tabPanels[t]?.IsHitTestVisible ?? false).ToList();
        if (vis.Count < 2) return;
        var slot = Math.Clamp((int)(p.X / (TabW + TabGap)), 0, vis.Count - 1);
        if (vis[slot] == idx) return;

        // move the dragged tab so it occupies `slot` among the visible tabs, preserving everyone else's order
        var list = _tabOrder.ToList();
        list.Remove(idx);
        var visNoDrag = vis.Where(t => t != idx).ToList();
        var orderPos = slot < visNoDrag.Count ? list.IndexOf(visNoDrag[slot]) : list.IndexOf(visNoDrag[^1]) + 1;
        list.Insert(orderPos, idx);
        _tabOrder = list.ToArray();

        RebuildStrip();
        UpdateSeparators();
        e.Pointer.Capture(tab);   // re-assert capture — the strip rebuild must not orphan the gesture
    }

    private void OnTabDragEnd()
    {
        if (_dragTab < 0 && !_reordering) return;
        _dragTab = -1;
        if (!_reordering) return;
        _reordering = false;
        _settings.TabOrder = _tabOrder.ToArray();   // the new order is the user's layout — persist it
        SettingsStore.Save(_settings);
        LayoutTabs();
    }

    // the tab-index → page-control map: pop-outs and their return path both use it (null = not built yet)
    private Control? PageFor(int idx) => idx switch
    {
        0 => InstalledPanel, 1 => ForksPanel, 2 => BrowsePanel, 4 => CrossPanel,
        5 => DataPacksPanel, 6 => ResPacksPanel, 7 => ToolsPanel,
        SettingsTabIdx => SettingsPanel, _ => null,
    };

    // Tear a tab out into its own floating window (AMT-04). The REAL pages re-parent into the pop-out and
    // return on close; placeholder pages get a fresh placeholder. While popped, the tab leaves the strip and
    // lives in the ▾ list as "(window)".
    private void PopOutTab(int idx)
    {
        if (idx < 0 || idx >= TabDefs.Length || _poppedTabs.Contains(idx)) return;

        // every REAL page re-parents into the pop-out and returns on close; unbuilt tabs get a placeholder
        Control content;
        if (PageFor(idx) is { } page)
        {
            ContentHost.Children.Remove(page);
            page.IsVisible = true;
            content = page;
            if (idx == 1 && !_forksLoaded) LoadForksAsync();
        }
        else content = new TextBlock
        {
            Text = $"{TabDefs[idx].Label} — coming soon", FontSize = 14, Foreground = Dim,
            HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
        };

        _poppedTabs.Add(idx);
        if (_tab == idx)   // the popped tab can't stay active here — jump to the first still-open tab
            foreach (var i in _tabOrder)
                if (!_closedTabs.Contains(i) && !_poppedTabs.Contains(i)) { SelectTab(i); break; }

        var win = MakePanelWindow(TabDefs[idx].Label, content, onClosed: () =>
        {
            if (PageFor(idx) is not null)
            {
                (content.Parent as Panel)?.Children.Remove(content);   // detach from the dead window's chrome
                ContentHost.Children.Insert(0, content);               // return the page to the main window
                content.IsVisible = _tab == idx;
            }
            _poppedTabs.Remove(idx);
            _popouts.Remove(idx);
            LayoutTabs();
        });
        _popouts[idx] = win;
        LayoutTabs();
        win.Show();   // a peer window, not owned — it survives the main window minimising (Explorer behaviour)
    }

    // A floating dock-engine panel window: frameless, all four faces FREE → every corner rounds (the #368
    // corner rule, via DockRules so it stays one law). Mini title bar (drag-move, double-click max, ✕),
    // 6px edge grips for resize. Styled in code because Window.Styles in MainWindow.axaml don't reach here.
    private Window MakePanelWindow(string title, Control content, Action onClosed)
    {
        var win = new Window
        {
            Title = $"{title} — Ant's Modding Tools", Icon = Icon,
            Width = 940, Height = 640, MinWidth = 420, MinHeight = 280,
            WindowDecorations = Avalonia.Controls.WindowDecorations.None, Background = Brushes.Transparent,
            WindowStartupLocation = WindowStartupLocation.Manual,
            Position = Position + new PixelPoint(90, 90),
        };

        var r = DockRules.Corners(leftDocked: false, topDocked: false, rightDocked: false, bottomDocked: false, radius: 8);

        var closeBtn = new Border
        {
            Width = 40, Height = 30, Background = Brushes.Transparent,
            Child = new TextBlock { Text = "✕", Foreground = Mut, FontSize = 11, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center },
            Cursor = new Cursor(StandardCursorType.Hand),
        };
        closeBtn.PointerEntered += (_, _) => closeBtn.Background = new SolidColorBrush(Color.Parse("#e81123"));
        closeBtn.PointerExited += (_, _) => closeBtn.Background = Brushes.Transparent;
        closeBtn.PointerPressed += (_, ev) => { ev.Handled = true; win.Close(); };

        var titleBar = new DockPanel { Background = SolidColorBrush.Parse("#202020"), Height = 34 };   // the Panel token
        DockPanel.SetDock(closeBtn, Dock.Right);
        titleBar.Children.Add(closeBtn);
        titleBar.Children.Add(new TextBlock
        {
            Text = title, Foreground = Ink, FontSize = 12.5, FontWeight = FontWeight.SemiBold,
            VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(14, 0, 0, 0),
        });
        titleBar.PointerPressed += (_, ev) => { if (ev.GetCurrentPoint(win).Properties.IsLeftButtonPressed) win.BeginMoveDrag(ev); };
        titleBar.DoubleTapped += (_, _) => win.WindowState = win.WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;

        var body = new DockPanel();
        DockPanel.SetDock(titleBar, Dock.Top);
        body.Children.Add(titleBar);
        body.Children.Add(content);

        var frame = new Border
        {
            Background = SolidColorBrush.Parse("#191919"),    // the Bg token
            BorderBrush = SolidColorBrush.Parse("#3a3a3a"),   // the Line2 token
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(r.TL, r.TR, r.BR, r.BL), ClipToBounds = true, Child = body,
        };

        var root = new Panel();
        root.Children.Add(frame);
        root.Children.Add(ResizeGrips(win));
        win.Content = root;

        win.Opened += (_, _) => RoundCornersFor(win);
        win.Closed += (_, _) => onClosed();
        return win;
    }

    // the main window's 6px edge-grip pattern, generated for any frameless window
    private static Grid ResizeGrips(Window w)
    {
        var g = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("6,*,6"),
            RowDefinitions = new RowDefinitions("6,*,6"),
        };
        void Grip(int row, int col, WindowEdge edge, StandardCursorType cur)
        {
            var b = new Border { Background = Brushes.Transparent, Cursor = new Cursor(cur) };
            Grid.SetRow(b, row); Grid.SetColumn(b, col);
            b.PointerPressed += (_, e) => { if (e.GetCurrentPoint(w).Properties.IsLeftButtonPressed) w.BeginResizeDrag(edge, e); };
            g.Children.Add(b);
        }
        Grip(0, 0, WindowEdge.NorthWest, StandardCursorType.TopLeftCorner);
        Grip(0, 1, WindowEdge.North, StandardCursorType.SizeNorthSouth);
        Grip(0, 2, WindowEdge.NorthEast, StandardCursorType.TopRightCorner);
        Grip(1, 0, WindowEdge.West, StandardCursorType.SizeWestEast);
        Grip(1, 2, WindowEdge.East, StandardCursorType.SizeWestEast);
        Grip(2, 0, WindowEdge.SouthWest, StandardCursorType.BottomLeftCorner);
        Grip(2, 1, WindowEdge.South, StandardCursorType.SizeNorthSouth);
        Grip(2, 2, WindowEdge.SouthEast, StandardCursorType.BottomRightCorner);
        return g;
    }

    // Every tab is a FIXED standard width (TabW). Resizing the window NEVER changes a tab's width — it only
    // changes HOW MANY are visible; the rest (plus any X-closed tabs) fold into the ▾ list. The active tab is
    // always kept visible.
    private void LayoutTabs()
    {
        if (TabsPanel is null || _overflowChip is null || _tabPanels.Length == 0) return;
        var total = Bounds.Width;
        if (total <= 0) return;

        var n = TabDefs.Length;
        const double reserved = 210;                 // WNL badge column + window controls + margins
        var avail = Math.Max(0, total - reserved);

        // eligibility follows PRESENTATION ORDER (drag-reordered); closed + popped-out tabs live in the ▾ list
        var eligible = new List<int>();
        foreach (var i in _tabOrder) if (!_closedTabs.Contains(i) && !_poppedTabs.Contains(i)) eligible.Add(i);

        // the ▾ chip is shown when anything is hidden — closed, popped, or eligible tabs that don't fit
        var fitNoChip = (int)Math.Floor((avail + TabGap) / (TabW + TabGap));
        var overflow = _closedTabs.Count > 0 || _poppedTabs.Count > 0 || fitNoChip < eligible.Count;
        var room = overflow ? avail - OverflowChipW - TabGap : avail;
        var maxShow = Math.Min(eligible.Count, Math.Max(1, (int)Math.Floor((room + TabGap) / (TabW + TabGap))));

        var visible = new bool[n];
        for (var k = 0; k < maxShow; k++) visible[eligible[k]] = true;
        // keep the active tab visible (swap it into the last shown slot if it folded)
        if (_tab >= 0 && _tab < n && !_closedTabs.Contains(_tab) && !visible[_tab] && maxShow > 0)
        {
            visible[eligible[maxShow - 1]] = false;
            visible[_tab] = true;
        }

        for (var i = 0; i < n; i++)
        {
            var p = _tabPanels[i];
            if (p is null) continue;
            p.Width = visible[i] ? TabW : 0;   // FIXED width — only visibility changes on resize
            p.Opacity = visible[i] ? 1 : 0;
            p.IsHitTestVisible = visible[i];
        }
        UpdateSeparators();   // seams only between two idle, both-visible tabs

        _overflowChip.Width = overflow ? OverflowChipW : 0;
        _overflowChip.Opacity = overflow ? 1 : 0;
        _overflowChip.IsHitTestVisible = overflow;
        if (overflow && _overflowMenu is not null)
        {
            _overflowMenu.Items.Clear();
            foreach (var i in _tabOrder)
                if (!visible[i])   // folded, X-closed, or living in a pop-out window
                {
                    var idx = i;
                    var suffix = _poppedTabs.Contains(i) ? "  (window)" : _closedTabs.Contains(i) ? "  (closed)" : "";
                    var item = new MenuItem { Header = TabDefs[i].Label + suffix };
                    item.Click += (_, _) => SelectTab(idx);
                    _overflowMenu.Items.Add(item);
                }
        }
    }

    private void OnCatClick(object? sender, PointerPressedEventArgs e)
    {
        if (sender is not Border b || b.Tag is not string s || !int.TryParse(s, out var idx)) return;
        if (e.GetCurrentPoint(b).Properties.IsRightButtonPressed)
        {
            // right-click hides a category (author: "dont know why youd want to... but the feature should be
            // available"); right-click the Categories header to bring them all back
            if (idx != 0) { _hiddenCats.Add(idx); if (_cat == idx) _cat = 0; BuildCats(); ApplyFilter(); }
            return;
        }
        _cat = idx;
        BuildCats();
        ApplyFilter();
    }

    // "Categories" header: left-click collapses/expands the list; right-click unhides every hidden category
    private void OnToggleCats(object? sender, PointerPressedEventArgs e)
    {
        if (e.GetCurrentPoint(sender as Control).Properties.IsRightButtonPressed)
        {
            _hiddenCats.Clear();
            BuildCats();
            return;
        }
        CatsPanel.IsVisible = !CatsPanel.IsVisible;
    }

    // ---- AMT-07: the Forks page — ForkWatcher wired to a user-editable forks.json ----

    private static string ForksFile =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "AntsModdingTools", "forks.json");
    private readonly ObservableCollection<ForkRow> _forkRows = new();
    private bool _forksLoaded;      // the check runs once on first visit; Recheck re-runs it
    private bool _forksChecking;    // re-entry guard

    // Read the registry; a first run seeds it — from the dev pack's real registry when present (this machine),
    // else an empty template the "Edit registry" button opens for the user to fill.
    private string LoadForksRegistry()
    {
        try
        {
            if (!System.IO.File.Exists(ForksFile))
            {
                var devSeed = Path.Combine(DevInstance, ".uvrun", "wnl-updater", "forks.json");
                Directory.CreateDirectory(Path.GetDirectoryName(ForksFile)!);
                System.IO.File.WriteAllText(ForksFile, System.IO.File.Exists(devSeed)
                    ? System.IO.File.ReadAllText(devSeed)
                    : "{\n  \"forks\": [\n  ]\n}\n");
            }
            return System.IO.File.ReadAllText(ForksFile);
        }
        catch { return "{\"forks\":[]}"; }
    }

    private async void LoadForksAsync()
    {
        if (_forksChecking) return;
        _forksChecking = true;
        _forksLoaded = true;
        ForksList.ItemsSource = _forkRows;
        _forkRows.Clear();
        ForksStatus.Text = "checking upstreams…";
        ForksEmpty.IsVisible = false;
        ForksLoad.IsVisible = true; ForksLoad.IsIndeterminate = true;   // AMT-21 non-blocking loading bar

        var registry = LoadForksRegistry();
        var settings = _settings;
        IReadOnlyList<ForkResult> results;
        try { results = await Task.Run(() => ForkWatcher.CheckAllAsync(registry, settings)); }
        catch { results = []; }

        _forkRows.Clear();
        var updates = 0;
        foreach (var fork in results)
            foreach (var u in fork.Upstreams)
            {
                if (u.Verdict == "UPDATE") updates++;
                var detail = $"{u.Kind}: {u.Id}";
                if (u.Note.Length > 0) detail += $"  ·  {u.Note}";
                if (u.Error is { Length: > 0 } err) detail += $"  ·  {err}";
                _forkRows.Add(new ForkRow
                {
                    Name = fork.Name,
                    Detail = detail,
                    Base = u.Base.Length > 0 ? u.Base : "—",
                    Latest = u.Latest ?? "—",
                    Verdict = u.Verdict,
                    VerdictBrush = u.Verdict switch
                    {
                        "UPDATE" => Up, "current" => Ok, "no-upstream" => Err, _ => Dim,
                    },
                    Url = u.Url ?? "",
                });
            }

        ForksLoad.IsIndeterminate = false; ForksLoad.IsVisible = false;   // stop the bar (guard against idle CPU)
        ForksEmpty.IsVisible = _forkRows.Count == 0;
        ForksStatus.Text = results.Count == 0
            ? "no forks tracked yet"
            : $"{results.Count} forks · {_forkRows.Count} upstreams · {updates} behind upstream";
        _forksChecking = false;
    }

    private void OnRecheckForks(object? sender, PointerPressedEventArgs e) => LoadForksAsync();

    private void OnEditForks(object? sender, PointerPressedEventArgs e)
    {
        LoadForksRegistry();   // ensures the file exists before opening it
        OpenUrl(ForksFile);
    }

    private void OnForkOpenUrl(object? sender, PointerPressedEventArgs e)
    {
        if (sender is Control { DataContext: ForkRow { HasUrl: true } row }) OpenUrl(row.Url);
    }

    // ---- AMT-08: the Browse page — CatalogueSearch (Modrinth drives, CF merges in) ----

    private readonly ObservableCollection<BrowseRow> _browseRows = new();
    private bool _browseSearching;
    private int _browseGen;   // stops a stale icon sweep when a new search replaces the results

    private void OnBrowseKey(object? sender, KeyEventArgs e) { if (e.Key == Key.Enter) RunBrowseSearch(); }
    private void OnBrowseGo(object? sender, PointerPressedEventArgs e) => RunBrowseSearch();

    private async void RunBrowseSearch()
    {
        var q = (BrowseBox.Text ?? "").Trim();
        if (q.Length == 0 || _browseSearching) return;
        _browseSearching = true;
        BrowseList.ItemsSource = _browseRows;
        BrowseStatus.Text = $"searching “{q}”…";

        var settings = _settings;
        IReadOnlyList<CatalogueHit> hits;
        try { hits = await Task.Run(() => CatalogueSearch.SearchAsync(q, settings)); }
        catch { hits = []; }

        var gen = ++_browseGen;
        _browseRows.Clear();
        foreach (var h in hits)
            _browseRows.Add(new BrowseRow
            {
                Title = h.Title,
                Author = h.Author.Length > 0 ? $"by {h.Author}" : "",
                Desc = h.Description,
                Downloads = FmtCount(h.Downloads),
                IconUrl = h.IconUrl,
                MrUrl = h.ModrinthUrl,
                CfUrl = h.CfUrl,
            });

        BrowseStatus.Text = hits.Count == 0
            ? $"no results for “{q}”" + (settings.CurseForgeApiKey.Length == 0 ? "  ·  (CF search needs your API key — Settings)" : "")
            : $"{hits.Count} results  ·  {hits.Count(h => h.OnModrinth && h.OnCurseForge)} on both sites";
        _browseSearching = false;

        // stream the result icons (same disk-backed cache as the installed table)
        foreach (var row in _browseRows.ToList())
        {
            if (gen != _browseGen) return;
            if (row.IconUrl.Length == 0) continue;
            var bmp = await Task.Run(() => IconCache.GetAsync(row.IconUrl));
            if (gen != _browseGen) return;
            if (bmp is not null) row.Icon = bmp;
        }
    }

    private static string FmtCount(long n) => n switch
    {
        >= 1_000_000 => $"{n / 1_000_000.0:0.#}M",
        >= 1_000 => $"{n / 1_000.0:0.#}k",
        _ => n.ToString(),
    };

    private void OnBrowseOpenMr(object? sender, PointerPressedEventArgs e)
    { if (sender is Control { DataContext: BrowseRow { OnMr: true } r }) { e.Handled = true; OpenUrl(r.MrUrl); } }
    private void OnBrowseOpenCf(object? sender, PointerPressedEventArgs e)
    { if (sender is Control { DataContext: BrowseRow { OnCf: true } r }) { e.Handled = true; OpenUrl(r.CfUrl); } }

    // double-click a result → open its page (Modrinth first, else CF)
    private void OnBrowseActivated(object? sender, TappedEventArgs e)
    {
        if (BrowseList.SelectedItem is not BrowseRow r) return;
        var url = r.OnMr ? r.MrUrl : r.CfUrl;
        if (url.Length > 0) OpenUrl(url);
    }

    // ---- AMT-09: the Cross-version page — per-mod MC-version matrix across both sites ----

    private int _crossGen;   // a new pick cancels the previous Modrinth fetch's right to fill the matrix

    private void OnCrossFilter(object? sender, TextChangedEventArgs e)
    {
        var q = (CrossFilterBox.Text ?? "").Trim().ToLowerInvariant();
        CrossModsList.ItemsSource = _all
            .Where(r => q.Length == 0 || r.Name.ToLowerInvariant().Contains(q) || r.File.ToLowerInvariant().Contains(q))
            .OrderBy(r => r.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private async void OnCrossModSelected(object? sender, SelectionChangedEventArgs e)
    {
        if (CrossModsList.SelectedItem is not ModRow row) return;
        var gen = ++_crossGen;
        CrossTitle.Text = row.Name;

        // CF matrix rides the last scan for free; Modrinth needs its per-project version list
        var cf = new Dictionary<string, string>();
        if (row.Detail is { } d)
            foreach (var c in d.CfCrossVersions)
                cf.TryAdd(c.Mc, c.FileName);

        var mr = new Dictionary<string, string>();
        if (row.Detail is { MrProjectId.Length: > 0 } det)
        {
            CrossStatus.Text = "fetching Modrinth versions…";
            var pid = det.MrProjectId;
            var loader = _settings.Loader;
            var list = await Task.Run(() => ModrinthClient.ProjectVersionsAsync(pid, loader));
            if (gen != _crossGen) return;   // the user picked another mod meanwhile
            foreach (var (mc, ver) in list) mr.TryAdd(mc, ver);
        }

        var rows = cf.Keys.Union(mr.Keys)
            .OrderByDescending(McSortKey)
            .Select(mc => new CrossRow
            {
                Mc = mc,
                Cf = cf.GetValueOrDefault(mc, "—"),
                Mr = mr.GetValueOrDefault(mc, "—"),
                McBrush = mc == _settings.McVersion ? Up : Strong,   // OUR version pops out of the list
            })
            .ToList();
        CrossMatrixList.ItemsSource = rows;
        CrossStatus.Text = rows.Count == 0
            ? (row.Detail is null ? "no scan data yet — run a scan first (⟳)" : "neither site lists other MC versions for this mod")
            : $"{rows.Count} MC versions  ·  CF {cf.Count}  ·  Modrinth {mr.Count}";
    }

    private static (int, int, int, int) McSortKey(string s)
    {
        var p = s.Split('.');
        int G(int i) => i < p.Length && int.TryParse(p[i], out var n) ? n : 0;
        return (G(0), G(1), G(2), G(3));
    }

    // ---- AMT-14: launcher — boot the game via the configured command; account sign-in lands after the
    //      security-design research (best-in-class token storage) so the auth backend is built once, correctly ----

    private Account? _account;

    // reflect the signed-in account into the Play button tooltip + the Settings account row
    private void RefreshAccountUi()
    {
        var signedIn = _account is { } a && !a.Expired;
        ToolTip.SetTip(PlayBtn, signedIn ? $"Boot the game as {_account!.Username}" : "Boot the game (offline / no account)");
        if (AccountBtnText is null) return;   // settings not built yet
        AccountBtnText.Text = signedIn ? "Sign out" : "Sign in";
        AccountStatus.Text = signedIn ? $"Signed in as {_account!.Username}" : "Not signed in";
    }

    private void OnPlay(object? sender, PointerPressedEventArgs e)
    {
        if (!GameLauncher.CanLaunch(_settings))
        {
            SelectTab(SettingsTabIdx);
            SecLaunch?.BringIntoView();
            return;
        }
        var (ok, msg) = GameLauncher.Launch(_settings, _account);
        PlayText.Text = ok ? "Launched" : "Play";
        CoreStatus.Text = msg;
        CoreStatus.Foreground = ok ? Ok : Err;
        // the button label reverts after a moment so it's ready for the next boot
        DispatcherTimer.RunOnce(() => PlayText.Text = "Play", TimeSpan.FromSeconds(4));
    }

    private bool _authBusy;

    // silent restore on launch: if MSAL's OS-encrypted cache holds a live account, refresh the session with no
    // browser. Best-effort and quiet — a miss just leaves the cached display name (or "not signed in").
    private async void RestoreAccountSilentAsync()
    {
        var settings = _settings;
        if (MsAccount.ResolveClientId(settings).Length == 0) return;
        try
        {
            var acc = await MsAccount.TrySilentAsync(settings);
            if (acc is not null) { _account = acc; AccountStore.SaveProfile(acc); RefreshAccountUi(); }
        }
        catch { /* silent restore is best-effort */ }
    }

    // The account button: signed in → full-purge sign-out; signed out → interactive MSAL sign-in (system browser,
    // PKCE). The MC token never touches disk — MSAL's OS-encrypted cache holds the only persisted secret.
    private async void OnAccountButton(object? sender, PointerPressedEventArgs e)
    {
        if (_authBusy) return;

        if (_account is { AccessToken.Length: > 0 } || (_account is not null && MsAccount.ResolveClientId(_settings).Length > 0 && await MsAccount.HasCachedAccountAsync(_settings)))
        {
            // signed in → sign out: purge MSAL's cache + the profile + memory, then point at MS consent revoke
            _authBusy = true;
            AccountStatus.Text = "signing out…";
            await MsAccount.SignOutAsync(_settings);
            AccountStore.Clear();
            _account = null;
            _authBusy = false;
            RefreshAccountUi();
            AccountStatus.Text = "signed out — to revoke access fully, visit account.live.com/consent/Manage";
            return;
        }

        if (MsAccount.ResolveClientId(_settings).Length == 0)
        {
            AccountStatus.Text = "sign-in needs an Azure app client id — see the launcher setup guide (LAUNCHER-SETUP.md)";
            return;
        }

        _authBusy = true;
        AccountBtnText.Text = "Signing in…";
        AccountStatus.Text = "a browser window will open — sign in on Microsoft's site (your password never touches this app)";
        try
        {
            var acc = await MsAccount.SignInAsync(_settings);
            if (acc is not null)
            {
                _account = acc;
                AccountStore.SaveProfile(acc);
                AccountStatus.Text = $"signed in as {acc.Username}";
            }
            else
            {
                AccountStatus.Text = "sign-in didn't complete (cancelled, or the client id isn't approved for Minecraft yet)";
            }
        }
        catch { AccountStatus.Text = "sign-in failed — check your connection and the client id"; }
        finally { _authBusy = false; RefreshAccountUi(); }
    }

    // ---- AMT-13: app self-updater — a quiet GitHub-release check, a chip only when there's something newer ----

    private SelfUpdateInfo? _appUpdate;

    private async void CheckAppUpdateAsync()
    {
        var current = VerOf(typeof(MainWindow).Assembly).TrimStart('v');
        // dev hook: AMT_FAKE_UPDATE=1 forces the chip/dialog (no published release is newer than the dev build,
        // so this is the only way to exercise the UI)
        if (Environment.GetEnvironmentVariable("AMT_FAKE_UPDATE") == "1")
        {
            _appUpdate = new SelfUpdateInfo
            {
                HasUpdate = true, CurrentVersion = current, LatestVersion = "9.9.9",
                ReleaseUrl = "https://github.com/DegradingAnt/Ants-modding-tools/releases",
                Notes = "• Example release notes\n• Wired the self-updater\n• Nightly channel coming (AMT-24)",
            };
            AppUpdateText.Text = "v9.9.9 available";
            AppUpdateChip.IsVisible = true;
            return;
        }
        SelfUpdateInfo info;
        try { info = await Task.Run(() => SelfUpdate.CheckAsync(current)); }
        catch { return; }   // a failed check is silent — the app just doesn't nag
        if (!info.HasUpdate) return;

        _appUpdate = info;
        AppUpdateText.Text = $"v{info.LatestVersion} available";
        AppUpdateChip.IsVisible = true;
    }

    // clicking the chip opens a small dialog: what's new + Download (the release asset) / release page / later
    private void OnAppUpdateClick(object? sender, PointerPressedEventArgs e)
    {
        if (_appUpdate is not { } info) return;

        var body = new StackPanel { Spacing = 12, Margin = new Thickness(20) };
        body.Children.Add(new TextBlock
        {
            Text = $"Ant's Modding Tools v{info.LatestVersion}", Foreground = Ink,
            FontSize = 16, FontWeight = FontWeight.SemiBold,
        });
        body.Children.Add(new TextBlock
        {
            Text = $"You're on v{info.CurrentVersion}. A newer release is available.",
            Foreground = Dim, FontSize = 12.5,
        });
        if (info.Notes.Length > 0)
        {
            var notes = info.Notes.Length > 600 ? info.Notes[..600] + "…" : info.Notes;
            body.Children.Add(new Border
            {
                Background = SolidColorBrush.Parse("#202020"), CornerRadius = new CornerRadius(6),
                Padding = new Thickness(12), MaxHeight = 240,
                Child = new ScrollViewer
                {
                    Content = new TextBlock { Text = notes, Foreground = Mut, FontSize = 12, TextWrapping = TextWrapping.Wrap },
                },
            });
        }

        var dlg = new Window
        {
            Title = "Update available", Width = 460, SizeToContent = SizeToContent.Height,
            CanResize = false, WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Background = SolidColorBrush.Parse("#191919"),
        };

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10, HorizontalAlignment = HorizontalAlignment.Right };
        Border DialogButton(string label, IBrush bg, IBrush fg, Action act)
        {
            var b = new Border
            {
                Background = bg, CornerRadius = new CornerRadius(4), Padding = new Thickness(16, 8),
                Cursor = new Cursor(StandardCursorType.Hand),
                Child = new TextBlock { Text = label, Foreground = fg, FontSize = 12.5, FontWeight = FontWeight.SemiBold },
            };
            b.PointerPressed += (_, _) => act();
            return b;
        }
        // Download grabs the release asset if the release published one, else opens the release page
        var primaryLabel = info.AssetUrl.Length > 0 ? "Download" : "Open release";
        buttons.Children.Add(DialogButton("Later", SolidColorBrush.Parse("#383838"), Mut, () => dlg.Close()));
        buttons.Children.Add(DialogButton(primaryLabel, Primary, OnPrimary, () =>
        {
            OpenUrl(info.AssetUrl.Length > 0 ? info.AssetUrl : info.ReleaseUrl);
            dlg.Close();
        }));
        body.Children.Add(buttons);

        dlg.Content = body;
        dlg.ShowDialog(this);
    }

    // ---- AMT-11: Tools page — a card grid of quick actions the app already has the data for ----

    // one action card: icon glyph + label + sublabel, whole card clickable
    private Border ToolCard(string glyph, string label, string sub, Action onClick)
    {
        var card = new Border
        {
            Width = 260, Margin = new Thickness(0, 0, 12, 12), Padding = new Thickness(14, 12),
            Background = SolidColorBrush.Parse("#202020"), BorderBrush = SolidColorBrush.Parse("#2d2d2d"),
            BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(6),
            Cursor = new Cursor(StandardCursorType.Hand),
            BoxShadow = BoxShadows.Parse("0 1 4 0 #40000000"),
        };
        card.Classes.Add("toolcard");   // AMT-15: smooth bg + transform transition (defined in the styles)
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12 };
        row.Children.Add(new TextBlock { Text = glyph, FontSize = 20, VerticalAlignment = VerticalAlignment.Center });
        var text = new StackPanel { Spacing = 2, VerticalAlignment = VerticalAlignment.Center };
        text.Children.Add(new TextBlock { Text = label, Foreground = Ink, FontWeight = FontWeight.SemiBold, FontSize = 13.5 });
        text.Children.Add(new TextBlock { Text = sub, Foreground = Dim, FontSize = 11.5, TextWrapping = TextWrapping.Wrap, MaxWidth = 180 });
        row.Children.Add(text);
        card.Child = row;
        card.PointerPressed += (_, _) => onClick();
        // hover: brighten + lift a hair (the transition on the toolcard class makes both glide + a deeper shadow)
        card.PointerEntered += (_, _) =>
        {
            card.Background = SolidColorBrush.Parse("#262626");
            card.RenderTransform = TransformOperations.Parse("translateY(-2px)");
            card.BoxShadow = BoxShadows.Parse("0 4 12 0 #55000000");
        };
        card.PointerExited += (_, _) =>
        {
            card.Background = SolidColorBrush.Parse("#202020");
            card.RenderTransform = TransformOperations.Parse("translateY(0px)");
            card.BoxShadow = BoxShadows.Parse("0 1 4 0 #40000000");
        };
        return card;
    }

    private void OpenIfExists(string path) { if (path.Length > 0 && (Directory.Exists(path) || System.IO.File.Exists(path))) OpenUrl(path); }

    private void BuildTools()
    {
        var inst = _settings.InstancePath;
        ToolsFolders.Children.Clear();
        ToolsFolders.Children.Add(ToolCard("📁", "Instance folder", "the pack root", () => OpenIfExists(inst)));
        ToolsFolders.Children.Add(ToolCard("🧩", "Mods folder", "installed .jar files", () => OpenIfExists(_settings.ModsDir)));
        ToolsFolders.Children.Add(ToolCard("🎨", "Resource packs", "resourcepacks/", () => OpenIfExists(Path.Combine(inst, "resourcepacks"))));
        ToolsFolders.Children.Add(ToolCard("📦", "Data packs", "config/paxi/datapacks", () => OpenIfExists(Path.Combine(inst, "config", "paxi", "datapacks"))));
        ToolsFolders.Children.Add(ToolCard("⬇", "Downloads", "update staging + jar backups", () => OpenIfExists(_settings.DownloadDir)));
        ToolsFolders.Children.Add(ToolCard("⚙", "AMT config", "settings, cache, icons", () =>
            OpenIfExists(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "AntsModdingTools"))));

        ToolsFiles.Children.Clear();
        ToolsFiles.Children.Add(ToolCard("🧾", "options.txt", "MC options + active packs", () => OpenIfExists(Path.Combine(inst, "options.txt"))));
        ToolsFiles.Children.Add(ToolCard("📋", "Instance manifest", "minecraftinstance.json", () => OpenIfExists(_settings.InstanceJson)));
        ToolsFiles.Children.Add(ToolCard("🔱", "Fork registry", "forks.json (fork watch)", () => { LoadForksRegistry(); OpenIfExists(ForksFile); }));
        ToolsFiles.Children.Add(ToolCard("💬", "Feedback log", "your saved notes", () =>
            OpenIfExists(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "AntsModdingTools", "feedback.log"))));

        ToolsActions.Children.Clear();
        ToolsActions.Children.Add(ToolCard("⟳", "Rescan mods", "re-check both sites", () => { SelectTab(0); LoadMods(); }));
        ToolsActions.Children.Add(ToolCard("🔎", "Check forks", "re-run fork watch", () => { SelectTab(1); LoadForksAsync(); }));
        ToolsActions.Children.Add(ToolCard("🌐", "Project on GitHub", "Ant's Modding Tools", () => OpenUrl("https://github.com/DegradingAnt/Ants-modding-tools")));
        ToolsActions.Children.Add(ToolCard("🔑", "Get a CF API key", "console.curseforge.com", () => OpenUrl("https://console.curseforge.com/#/api-keys")));
    }

    // ---- AMT-20: content-type switch — the Installed tab shows Mods OR the installed resource/data packs ----

    private string _contentType = "mods";   // "mods" | "res" | "data"

    private void OnContentType(object? sender, PointerPressedEventArgs e)
    {
        if (sender is not Border b || b.Tag is not string t || t == _contentType) return;
        _contentType = t;
        // highlight the active chip
        CtMods.Classes.Remove("ctactive"); CtRes.Classes.Remove("ctactive"); CtData.Classes.Remove("ctactive");
        (t == "res" ? CtRes : t == "data" ? CtData : CtMods).Classes.Add("ctactive");
        // Categories only make sense for Mods; the search band's placeholder follows the content
        CtxCategories.IsVisible = t == "mods";
        RailPanel.IsVisible = t == "mods";
        SearchBox.PlaceholderText = t switch
        {
            "res" => "search resource packs…", "data" => "search data packs…",
            _ => _settings.ModsDir.Length > 0 ? _settings.ModsDir : "search installed mods…",
        };
        if (_tab == 0) SelectTab(0);   // re-apply visibility for the new content type
    }

    // ---- AMT-10: Data packs + Resource packs pages (read-only scans; each visit rescans — it's cheap) ----

    private void LoadDataPacks()
    {
        var packs = PackScanner.ScanDataPacks(_settings.InstancePath);
        DataPacksList.ItemsSource = packs.Select(p => new PackRow
        {
            Name = p.Name,
            Desc = p.Description,
            Source = p.Source,
            Format = p.PackFormat > 0 ? $"fmt {p.PackFormat}" : "",
            Size = p.SizeBytes > 0 ? FmtSize(p.SizeBytes) : "",
            Path = p.Path,
        }).ToList();
        DataPacksStatus.Text = packs.Count == 0
            ? "no data packs found (config/paxi/datapacks + each world's datapacks/ are scanned)"
            : $"{packs.Count} packs  ·  {packs.Count(p => p.Source == "paxi")} via Paxi  ·  double-click opens the pack";
    }

    private void LoadResPacks()
    {
        var packs = PackScanner.ScanResourcePacks(_settings.InstancePath);
        ResPacksList.ItemsSource = packs.Select(p => new PackRow
        {
            Name = p.Name,
            Desc = p.Description,
            Source = p.Source,
            Format = p.PackFormat > 0 ? $"fmt {p.PackFormat}" : "",
            Size = p.SizeBytes > 0 ? FmtSize(p.SizeBytes) : "",
            Path = p.Path,
            Badge = p.Active ? "ACTIVE" : "",
            BadgeBg = Primary,
            BadgeFg = OnPrimary,
        }).ToList();
        ResPacksStatus.Text = packs.Count == 0
            ? "no resource packs found in resourcepacks/"
            : $"{packs.Count} packs  ·  {packs.Count(p => p.Active)} active (order from options.txt — active first, priority order)";
    }

    private void OnOpenDataPacksFolder(object? sender, PointerPressedEventArgs e)
    {
        var p = Path.Combine(_settings.InstancePath, "config", "paxi", "datapacks");
        if (Directory.Exists(p)) OpenUrl(p);
    }

    private void OnOpenResPacksFolder(object? sender, PointerPressedEventArgs e)
    {
        var p = Path.Combine(_settings.InstancePath, "resourcepacks");
        if (Directory.Exists(p)) OpenUrl(p);
    }

    // double-click a pack row → reveal it (folders open; zips open their folder)
    private void OnPackActivated(object? sender, TappedEventArgs e)
    {
        var row = (sender as ListBox)?.SelectedItem as PackRow;
        if (row is null || row.Path.Length == 0) return;
        OpenUrl(Directory.Exists(row.Path) ? row.Path : Path.GetDirectoryName(row.Path) ?? row.Path);
    }

    // ---- AMT-06: column picker + the per-row enable toggle ----

    // Apply the persisted column choices: a hidden column = a no-* class on the list (styles collapse every
    // row's matching cells) + the header hides directly (it's a single element outside the list).
    private void ApplyColumnPrefs()
    {
        void Cls(string cls, bool show)
        {
            if (show) RowsList.Classes.Remove(cls);
            else if (!RowsList.Classes.Contains(cls)) RowsList.Classes.Add(cls);
        }
        Cls("no-author", _settings.ColAuthor);
        Cls("no-env", _settings.ColEnv);
        Cls("no-size", _settings.ColSize);
        Cls("no-added", _settings.ColAdded);
        HdrAuthor.IsVisible = _settings.ColAuthor;
        HdrEnv.IsVisible = _settings.ColEnv;
        HdrSize.IsVisible = _settings.ColSize;
        HdrAdded.IsVisible = _settings.ColAdded;
    }

    // the ▥ header button: check/uncheck the optional columns; each flip applies + persists immediately
    private void OnColumnsPicker(object? sender, PointerPressedEventArgs e)
    {
        var menu = new MenuFlyout { Placement = PlacementMode.BottomEdgeAlignedRight };
        void Add(string label, Func<bool> get, Action<bool> set)
        {
            var item = new MenuItem { Header = label, ToggleType = MenuItemToggleType.CheckBox, IsChecked = get(), StaysOpenOnClick = true };
            item.Click += (_, _) =>
            {
                set(!get());
                item.IsChecked = get();
                SettingsStore.Save(_settings);
                ApplyColumnPrefs();
                SyncColumnCheckboxes();   // keep the Settings-page mirror in step
            };
            menu.Items.Add(item);
        }
        Add("Author", () => _settings.ColAuthor, v => _settings.ColAuthor = v);
        Add("Environment", () => _settings.ColEnv, v => _settings.ColEnv = v);
        Add("Size", () => _settings.ColSize, v => _settings.ColSize = v);
        Add("Date added", () => _settings.ColAdded, v => _settings.ColAdded = v);
        menu.ShowAt(ColsPicker);
    }

    // push the current flags into the Settings-page checkboxes without re-triggering their handlers
    private bool _syncingCols;
    private void SyncColumnCheckboxes()
    {
        _syncingCols = true;
        SetColAuthor.IsChecked = _settings.ColAuthor;
        SetColEnv.IsChecked = _settings.ColEnv;
        SetColSize.IsChecked = _settings.ColSize;
        SetColAdded.IsChecked = _settings.ColAdded;
        _syncingCols = false;
    }

    // The enable toggle = a REAL jar rename (.jar ⇄ .jar.disabled — the pack convention ModScanner reads).
    // The guard against phantom fires: container recycling re-syncs IsChecked from the binding, so only act
    // when the switch DISAGREES with the file's actual on-disk state. A locked jar (game running) reverts.
    private void OnRowEnableToggled(object? sender, RoutedEventArgs e)
    {
        if (sender is not ToggleSwitch ts || ts.DataContext is not ModRow row) return;
        var want = ts.IsChecked == true;
        var isOn = !row.File.EndsWith(".disabled", StringComparison.OrdinalIgnoreCase);
        if (want == isOn || _settings.ModsDir.Length == 0) return;

        var from = Path.Combine(_settings.ModsDir, row.File);
        var toName = want ? row.File[..^".disabled".Length] : row.File + ".disabled";
        try
        {
            if (System.IO.File.Exists(Path.Combine(_settings.ModsDir, toName)))
                throw new IOException("target name already exists");
            System.IO.File.Move(from, Path.Combine(_settings.ModsDir, toName));
            row.File = toName;
            row.Enabled = want;
            if (!want) row.HasUpdate = false;   // a disabled mod isn't actionable
            row.Status = want ? "" : "disabled";
            row.StatusBrush = Dim;
        }
        catch
        {
            row.Enabled = !want;   // revert — the binding pushes the switch back
            row.Status = "locked — game running?";
            row.StatusBrush = Err;
        }
    }

    // The per-row status cell: on an update row it's the UPDATE BUTTON — press = update THAT mod (same
    // installer path as Update all, single item; old jar backed up, modified jars skipped by the installer).
    private async void OnRowStatusPressed(object? sender, PointerPressedEventArgs e)
    {
        if ((sender as Control)?.DataContext is not ModRow row) return;
        if (!row.HasUpdate || row.Detail is null || _installing) return;
        e.Handled = true;

        row.Status = "updating…";
        var settings = _settings;
        var detail = row.Detail;
        var results = await Task.Run(() => UpdateInstaller.UpdateAllAsync(new[] { detail }, settings,
            msg => Dispatcher.UIThread.Post(() => CoreStatus.Text = msg)));
        var r = results.FirstOrDefault();
        if (r is { Ok: true })
        {
            row.Status = "updated";
            row.StatusBrush = Ok;
            row.HasUpdate = false;
            _updates = Math.Max(0, _updates - 1);
        }
        else
        {
            row.Status = "failed";
            row.StatusBrush = Err;
            CoreStatus.Text = r?.Message ?? "update failed";
        }
        ApplyFilter();
    }

    private void OnSearchChanged(object? sender, TextChangedEventArgs e)
    {
        var t = SearchBox.Text ?? "";
        // The double-click-inserted PATH is display/edit material, not a search — filtering by it would match
        // nothing and blank the table. It only becomes a query once the user actually edits it.
        _query = t.Equals(_settings.ModsDir, StringComparison.OrdinalIgnoreCase) ? "" : t;
        ApplyFilter();
        UpdateGlobalSearch(_query);   // AMT-16: the one bar also searches settings + offers a catalogue search
    }

    // ---- AMT-16: global search — the top bar searches mods + settings + the catalogue from one field ----
    // Rendered as an in-window overlay (GlobalSearchOverlay) rather than an OS-window Popup, so it inherits the
    // theme and doesn't light-dismiss when focus moves. Toggled + filled here as the query changes.

    // the searchable Settings entries: a label + keywords + the section to scroll to when picked
    private (string Label, string[] Keys, Func<Control?> Section)[] SettingsTargets => new (string, string[], Func<Control?>)[]
    {
        ("Instance folder", new[] { "instance", "folder", "path", "pack", "mods dir" }, () => SecInstance),
        ("Game target (MC version, loader)", new[] { "version", "loader", "game", "target", "neoforge", "fabric" }, () => SecInstance),
        ("CurseForge API key", new[] { "curseforge", "cf", "api", "key", "token" }, () => SecSources),
        ("Download folder", new[] { "download", "backup", "staging" }, () => SecDownloads),
        ("Launch command", new[] { "launch", "play", "command", "boot", "run", "game" }, () => SecLaunch),
        ("Minecraft account", new[] { "account", "sign in", "login", "microsoft", "msa", "auth" }, () => SecLaunch),
        ("Row density", new[] { "density", "row", "spacing", "compact", "cozy" }, () => SecAppearance),
        ("Table columns", new[] { "column", "author", "environment", "size", "date" }, () => SecAppearance),
        ("Feedback", new[] { "feedback", "bug", "idea", "report" }, () => SecFeedback),
    };

    private void BuildGlobalSearch() { /* overlay is declared in XAML; nothing to construct */ }

    private void CloseGlobalSearch() { if (GlobalSearchOverlay is not null) GlobalSearchOverlay.IsVisible = false; }

    private void UpdateGlobalSearch(string query)
    {
        if (GlobalSearchOverlay is null || GlobalSearchResults is null) return;
        var q = query.Trim();
        // don't pop for the inserted path or a too-short query
        if (q.Length < 2 || q.Equals(_settings.ModsDir, StringComparison.OrdinalIgnoreCase))
        { GlobalSearchOverlay.IsVisible = false; return; }
        var lq = q.ToLowerInvariant();
        GlobalSearchResults.Children.Clear();

        // group 1: matching installed mods (top 6)
        var mods = _all.Where(m => m.Name.ToLowerInvariant().Contains(lq) || m.File.ToLowerInvariant().Contains(lq))
                       .Take(6).ToList();
        if (mods.Count > 0)
        {
            GlobalSearchResults.Children.Add(GroupHeader("MODS"));
            foreach (var m in mods)
            {
                var row = m;   // capture
                GlobalSearchResults.Children.Add(ResultRow("🧩", m.Name, m.File, () =>
                {
                    SelectTab(0);
                    _cat = 0; _updOnly = false; SearchBox.Text = row.Name; _query = row.Name;
                    ApplyFilter();
                    CloseGlobalSearch();
                }));
            }
        }

        // group 2: matching settings
        var settings = SettingsTargets.Where(t => t.Label.ToLowerInvariant().Contains(lq)
                                              || t.Keys.Any(k => k.Contains(lq) || lq.Contains(k))).Take(5).ToList();
        if (settings.Count > 0)
        {
            GlobalSearchResults.Children.Add(GroupHeader("SETTINGS"));
            foreach (var t in settings)
            {
                var target = t;
                GlobalSearchResults.Children.Add(ResultRow("⚙", target.Label, "Settings", () =>
                {
                    SelectTab(SettingsTabIdx);
                    target.Section()?.BringIntoView();
                    CloseGlobalSearch();
                }));
            }
        }

        // group 3: always offer a catalogue search
        GlobalSearchResults.Children.Add(GroupHeader("BROWSE"));
        GlobalSearchResults.Children.Add(ResultRow("🔎", $"Search the catalogue for “{q}”", "Modrinth + CurseForge", () =>
        {
            SelectTab(2);
            BrowseBox.Text = q;
            RunBrowseSearch();
            CloseGlobalSearch();
        }));

        GlobalSearchOverlay.IsVisible = true;
    }

    private static TextBlock GroupHeader(string text) => new()
    {
        Text = text, Foreground = SolidColorBrush.Parse("#9b9b9b"), FontSize = 10, FontWeight = FontWeight.Bold,
        Margin = new Thickness(10, 10, 0, 4),
    };

    private Border ResultRow(string glyph, string title, string sub, Action onClick)
    {
        var b = new Border
        {
            Padding = new Thickness(10, 7), CornerRadius = new CornerRadius(4),
            Background = Brushes.Transparent, Cursor = new Cursor(StandardCursorType.Hand),
        };
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10 };
        row.Children.Add(new TextBlock { Text = glyph, FontSize = 14, VerticalAlignment = VerticalAlignment.Center });
        var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        text.Children.Add(new TextBlock { Text = title, Foreground = Ink, FontSize = 13, TextTrimming = TextTrimming.CharacterEllipsis, MaxWidth = 380 });
        text.Children.Add(new TextBlock { Text = sub, Foreground = Dim, FontSize = 11 });
        row.Children.Add(text);
        b.Child = row;
        b.PointerEntered += (_, _) => b.Background = SolidColorBrush.Parse("#2f2f2f");
        b.PointerExited += (_, _) => b.Background = Brushes.Transparent;
        b.PointerPressed += (_, _) => onClick();
        return b;
    }

    // Double-click on the empty search bar drops the shown path in as SELECTED text — instantly editable,
    // deletable, copyable (author: "editable and removeable"). With text present it just selects everything.
    private void OnSearchDoubleTapped(object? sender, TappedEventArgs e)
    {
        if (string.IsNullOrEmpty(SearchBox.Text) && _settings.ModsDir.Length > 0)
            SearchBox.Text = _settings.ModsDir;
        SearchBox.SelectAll();
    }

    // ---- clickable status-bar items: each jumps to what it describes ----

    private void OnStatusMods(object? sender, PointerPressedEventArgs e) { SelectTab(0); SetUpdOnly(false); }
    private void OnStatusUpdates(object? sender, PointerPressedEventArgs e) { SelectTab(0); SetUpdOnly(true); }
    private void OnStatusApi(object? sender, PointerPressedEventArgs e) => SelectTab(SettingsTabIdx);
    private void OnStatusCategory(object? sender, PointerPressedEventArgs e)
    {
        // click resets the category filter back to All (and shows the Installed list it applies to)
        SelectTab(0);
        _cat = 0;
        BuildCats();
        ApplyFilter();
    }

    // ---- sortable headers ----

    // Click a column header to sort by it; click the active one again to flip direction.
    private void OnSortHeader(object? sender, PointerPressedEventArgs e)
    {
        if (sender is not TextBlock tb || tb.Tag is not string s || !int.TryParse(s, out var col)) return;
        if (col == _sortCol) _sortDesc = !_sortDesc;
        else { _sortCol = col; _sortDesc = false; }
        UpdateSortHeaders();
        ApplyFilter();
    }

    // the active header carries the direction arrow; the others show their plain label
    private void UpdateSortHeaders()
    {
        var labels = new[] { "MOD", "INSTALLED", "LATEST", "STATUS" };
        var headers = new[] { HdrMod, HdrInstalled, HdrLatest, HdrStatus };
        for (var i = 0; i < headers.Length; i++)
            headers[i].Text = i == _sortCol ? $"{labels[i]} {(_sortDesc ? "▼" : "▲")}" : labels[i];
    }

    // STATUS column sort key: actionability first — updates, then current, then local/disabled/unknown
    private static int StatusRank(ModRow r) => r.Status switch
    {
        "update" => 0,
        "up to date" => 1,
        "local" => 2,
        "disabled" => 3,
        _ => 4,
    };

    // ---- per-row changelog flyout ----

    // ≣ on a row: the LATEST build's changelog. Modrinth's rides the scan response (instant); CurseForge's is
    // fetched on demand with the user's key. Plain-text flyout — good enough to read what changed before updating.
    private async void OnChangelog(object? sender, PointerPressedEventArgs e)
    {
        if (sender is not Border b || b.DataContext is not ModRow row) return;
        e.Handled = true;   // don't let the click select/activate the row underneath

        var text = row.Detail?.MrChangelog ?? "";
        var body = new TextBlock
        {
            Text = text.Length > 0 ? text : "loading…",
            TextWrapping = TextWrapping.Wrap, Foreground = Mut, FontSize = 12.5, LineHeight = 19,
        };
        // a fixed panel WIDTH (not just a max on the text) is what stops lines running past the flyout edge;
        // a title header makes it read as a page rather than a floating blob
        var panel = new StackPanel { Width = 520, Spacing = 10 };
        panel.Children.Add(new TextBlock
        {
            Text = $"{row.Name} — latest changelog",
            Foreground = Strong, FontWeight = FontWeight.SemiBold, FontSize = 13.5,
        });
        panel.Children.Add(new Border { Height = 1, Background = SolidColorBrush.Parse("#3a3a3a") });
        panel.Children.Add(body);
        var fly = new Flyout
        {
            Content = new ScrollViewer
            {
                Content = panel, MaxHeight = 420,
                HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled,
            },
            Placement = PlacementMode.BottomEdgeAlignedRight,
        };
        fly.ShowAt(b);

        if (text.Length > 0 || row.Detail is null) { if (row.Detail is null) body.Text = "no update data yet — scan still running?"; return; }
        if (row.Detail.CfProjectId != 0 && row.Detail.CfLatestFileId != 0 && _settings.CurseForgeApiKey.Length > 0)
        {
            var cl = await CurseForgeClient.ChangelogAsync(row.Detail.CfProjectId, row.Detail.CfLatestFileId, _settings.CurseForgeApiKey);
            body.Text = cl.Length > 0 ? cl : "no changelog published for this file";
        }
        else
        {
            body.Text = row.Detail.CfProjectId != 0
                ? "no changelog available — CurseForge changelogs need the API key (Settings)"
                : "no changelog available for this mod";
        }
    }

    private void OnSegAll(object? sender, PointerPressedEventArgs e) => SetUpdOnly(false);
    private void OnSegUpd(object? sender, PointerPressedEventArgs e) => SetUpdOnly(true);

    private void SetUpdOnly(bool on)
    {
        _updOnly = on;
        SegAll.Background = on ? Raised : Primary;
        SegAllText.Foreground = on ? Mut : OnPrimary;
        SegUpd.Background = on ? Primary : Raised;
        SegUpdText.Foreground = on ? OnPrimary : Mut;
        ApplyFilter();
    }

    private void OnRefresh(object? sender, PointerPressedEventArgs e) => LoadMods();

    // Double-click a mod → its page on the site it was scraped from (CF page from the manifest, else the
    // Modrinth project). When the in-app Browse exists this becomes the in-app view instead (author call).
    private void OnRowActivated(object? sender, TappedEventArgs e)
    {
        if (RowsList.SelectedItem is not ModRow row) return;
        var url = row.Detail?.CfWebUrl is { Length: > 0 } cf ? cf
                : row.Detail?.MrProjectId is { Length: > 0 } mr ? $"https://modrinth.com/mod/{mr}"
                : "";
        if (url.Length > 0) OpenUrl(url);
        else OpenUrl(_settings.ModsDir);   // unknown/local mod — the folder is the most useful fallback
    }

    // ---- Settings page ----

    // push the loaded settings into the form fields (startup + after an external change), and fill the
    // dropdowns with their known defaults — both stay free-typed for anything the lists miss
    private void FillSettingsForm()
    {
        SetInstance.Text = _settings.InstancePath;
        SetLoader.ItemsSource = new[] { "neoforge", "forge", "fabric", "quilt" };
        SetMcVersion.Text = _settings.McVersion;
        SetLoader.Text = _settings.Loader;
        SetCfKey.Text = _settings.CurseForgeApiKey;
        SetDownloadDir.Text = _settings.DownloadDir;
        // appearance (AMT-12) — reflect the persisted density + column picks into the form
        SetDensity.SelectedIndex = _settings.RowDensity switch { "compact" => 0, "comfortable" => 2, _ => 1 };
        SetColAuthor.IsChecked = _settings.ColAuthor;
        SetColEnv.IsChecked = _settings.ColEnv;
        SetColSize.IsChecked = _settings.ColSize;
        SetColAdded.IsChecked = _settings.ColAdded;
        SetLaunchCmd.Text = _settings.LaunchCommand;
        RefreshAccountUi();
        FillMcVersionsAsync();
    }

    // ---- AMT-12: appearance settings (row density + a column-inventory mirror of the ▥ picker) ----

    // the density level → row vertical padding; applied as a live DynamicResource so all ~800 rows retune at once
    private static Thickness RowMarginFor(string density) => density switch
    {
        "compact" => new Thickness(16, 7),
        "comfortable" => new Thickness(16, 18),
        _ => new Thickness(16, 12),   // cozy (default)
    };

    private void ApplyRowDensity()
    {
        if (Application.Current is { } app) app.Resources["RowMargin"] = RowMarginFor(_settings.RowDensity);
    }

    private void OnDensityChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (!IsLoaded && _settings.RowDensity == DensityFromIndex(SetDensity.SelectedIndex)) return;
        _settings.RowDensity = DensityFromIndex(SetDensity.SelectedIndex);
        SettingsStore.Save(_settings);
        ApplyRowDensity();
    }

    private static string DensityFromIndex(int i) => i switch { 0 => "compact", 2 => "comfortable", _ => "cozy" };

    // the settings-side column checkboxes mirror the ▥ picker — same persisted flags, same ApplyColumnPrefs
    private void OnColCheck(object? sender, RoutedEventArgs e)
    {
        if (_syncingCols) return;   // this change came from SyncColumnCheckboxes, not the user — don't loop
        if (sender is not CheckBox cb || cb.Tag is not string tag) return;
        var on = cb.IsChecked == true;
        switch (tag)
        {
            case "author": _settings.ColAuthor = on; break;
            case "env": _settings.ColEnv = on; break;
            case "size": _settings.ColSize = on; break;
            case "added": _settings.ColAdded = on; break;
        }
        SettingsStore.Save(_settings);
        ApplyColumnPrefs();
    }

    // MC version dropdown = Mojang's live release list (keyless), static fallback offline. Fire-and-forget;
    // the field is usable as plain text the whole time.
    private async void FillMcVersionsAsync()
    {
        var keep = SetMcVersion.Text;
        IReadOnlyList<string> releases;
        try { releases = await Task.Run(() => MojangVersions.ReleasesAsync()); }
        catch { return; }
        SetMcVersion.ItemsSource = releases;
        SetMcVersion.Text = keep;   // setting ItemsSource must not clobber the user's value
    }

    // 📁 browse buttons — the system folder picker fills the adjacent path box (still hand-editable)
    private async void OnBrowseInstance(object? sender, PointerPressedEventArgs e) =>
        SetInstance.Text = await PickFolderAsync(SetInstance.Text) ?? SetInstance.Text;

    private async void OnBrowseDownload(object? sender, PointerPressedEventArgs e) =>
        SetDownloadDir.Text = await PickFolderAsync(SetDownloadDir.Text) ?? SetDownloadDir.Text;

    private async Task<string?> PickFolderAsync(string? current)
    {
        IStorageFolder? start = null;
        if (!string.IsNullOrWhiteSpace(current))
            try { start = await StorageProvider.TryGetFolderFromPathAsync(current); } catch { /* bad path — no start hint */ }
        var picked = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            AllowMultiple = false,
            SuggestedStartLocation = start,
        });
        return picked.Count > 0 ? picked[0].TryGetLocalPath() : null;
    }

    // Persist the form → disk. Settings AUTO-SAVE whenever a field loses focus (author call); the Save button
    // is the belt-and-braces manual trigger. Emptied fields self-heal back to their defaults, so a wiped box can
    // never brick a lookup. A FRESH AppSettings instance is built rather than mutating the current one: a
    // background scan may still be reading the old object on a worker thread, and swapping the reference gives
    // it a consistent snapshot instead of a torn half-old/half-new read. The key is persisted, never logged.
    private void SaveSettingsFromForm(string doneMsg)
    {
        var oldKey = _settings.CurseForgeApiKey;
        var mc = (SetMcVersion.Text ?? "").Trim();
        var loader = (SetLoader.Text ?? "").Trim().ToLowerInvariant();
        var dl = (SetDownloadDir.Text ?? "").Trim();
        var launch = (SetLaunchCmd.Text ?? "").Trim();

        // defaults auto-reinsert when a field was emptied (author call)
        if (mc.Length == 0) mc = "1.21.1";
        if (loader.Length == 0) loader = "neoforge";
        if (dl.Length == 0)
            dl = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "AntsModdingTools", "downloads");

        _settings = new AppSettings
        {
            InstancePath = (SetInstance.Text ?? "").Trim().TrimEnd('\\', '/'),
            McVersion = mc,
            Loader = loader,
            CurseForgeApiKey = (SetCfKey.Text ?? "").Trim(),
            DownloadDir = dl,
            // NOT form fields — carry the dock/tab layout over or a settings edit would silently reset it
            SidebarWidth = _settings.SidebarWidth,
            SidebarState = _settings.SidebarState,
            TabOrder = _settings.TabOrder,
            ColAuthor = _settings.ColAuthor, ColEnv = _settings.ColEnv,
            ColSize = _settings.ColSize, ColAdded = _settings.ColAdded,
            RowDensity = _settings.RowDensity,
            LaunchCommand = launch, MsaClientId = _settings.MsaClientId,
        };
        SettingsStore.Save(_settings);
        if (_settings.CurseForgeApiKey != oldKey) _cfCatNames = null;   // category list follows the key

        // reflect the healed defaults back into the form
        if (SetMcVersion.Text != mc) SetMcVersion.Text = mc;
        if (SetLoader.Text != loader) SetLoader.Text = loader;
        if (SetDownloadDir.Text != dl) SetDownloadDir.Text = dl;

        SettingsMsg.Text = _settings.InstancePath.Length > 0 && !Directory.Exists(_settings.InstancePath)
            ? "saved — that folder doesn't exist"
            : "saved";
        ShowInstance();
        // NO auto-rescan (author call): saving saves; the toolbar ⟳ is the one rescan trigger.
    }

    private void OnSettingsAutoSave(object? sender, RoutedEventArgs e) => SaveSettingsFromForm("saved");

    // ↗ open-in-Explorer buttons on the folder cards
    private void OnOpenInstanceFolder(object? sender, PointerPressedEventArgs e)
    { if (_settings.InstancePath.Length > 0) OpenUrl(_settings.InstancePath); }
    private void OnOpenDownloadFolder(object? sender, PointerPressedEventArgs e)
    { if (_settings.DownloadDir.Length > 0) OpenUrl(_settings.DownloadDir); }

    // 👁 hold-style toggle: click flips between masked and readable — readable only while the user wants it
    private void OnToggleKeyReveal(object? sender, PointerPressedEventArgs e) =>
        SetCfKey.PasswordChar = SetCfKey.PasswordChar == '•' ? '\0' : '•';

    // the note's link → the exact CF console page the key lives on
    private void OnOpenKeyPage(object? sender, PointerPressedEventArgs e) =>
        OpenUrl("https://console.curseforge.com/#/api-keys");

    // the WNL badge → the project's GitHub
    private void OnOpenRepo(object? sender, PointerPressedEventArgs e) =>
        OpenUrl("https://github.com/DegradingAnt/Ants-modding-tools");

    private static void OpenUrl(string url)
    {
        try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); }
        catch { /* browser launch is best-effort */ }
    }

    // Feedback (a Settings section): append the note to a local feedback log, timestamp-free (Date.Now is fine
    // in the app, unlike the workflow sandbox). Nothing leaves the machine.
    private void OnSendFeedback(object? sender, PointerPressedEventArgs e)
    {
        var text = (FeedbackBox.Text ?? "").Trim();
        if (text.Length == 0) { FeedbackMsg.Text = "type something first"; return; }
        try
        {
            var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "AntsModdingTools");
            Directory.CreateDirectory(dir);
            File.AppendAllText(Path.Combine(dir, "feedback.log"),
                $"--- {DateTime.Now:yyyy-MM-dd HH:mm} (v{VerOf(typeof(MainWindow).Assembly)}) ---\n{text}\n\n");
            FeedbackBox.Text = "";
            FeedbackMsg.Text = "saved — thank you";
        }
        catch { FeedbackMsg.Text = "couldn't write the feedback file"; }
    }

    // ---- dock engine (AMT-03): the sidebar is the engine's first panel ----
    // Three states (DockPanelState), all living in BodyGrid column 0: Expanded (user-dragged width),
    // IconRail (44px monogram chips), Hidden (24px ⟩ handle). DockRules (Amt.Core) owns every number and the
    // faces-independent corner rule; this section owns the controls and the drag mechanics. User choices
    // persist to settings; the <900px auto-hide does NOT (it's the window's doing, not the user's).

    private DockPanelState _sideState = DockPanelState.Expanded;
    private bool _sideAutoHidden;   // hidden by the narrow-window rule — restores itself when the window re-widens
    private bool _splitterDrag;     // a live splitter drag is in progress (keeps the splitter alive across snaps)

    // One function owns the column width + which face is visible, so the three states can never half-apply.
    private void ApplySidebar()
    {
        var w = _sideState switch
        {
            DockPanelState.Expanded => Bounds.Width > 0 ? DockRules.ClampWidth(_settings.SidebarWidth, Bounds.Width)
                                                        : _settings.SidebarWidth,
            DockPanelState.IconRail => DockRules.RailWidth,
            _ => DockRules.HandleWidth,
        };
        BodyGrid.ColumnDefinitions[0].Width = new GridLength(w);
        SidebarFull.IsVisible = _sideState == DockPanelState.Expanded;
        SidebarIconRail.IsVisible = _sideState == DockPanelState.IconRail;
        SidebarRail.IsVisible = _sideState == DockPanelState.Hidden;
        // the splitter lives on the Expanded seam; during a drag it stays alive so a snap to the rail
        // doesn't kill the pointer capture mid-gesture (the user can drag straight back out)
        SidebarSplitter.IsVisible = _sideState == DockPanelState.Expanded || _splitterDrag;

        // faces-independent corner rule (author #368): every sidebar face is docked — left/top/bottom against
        // the window chrome, right against the content panel — so every corner is a square seam. The rule is
        // applied through DockRules so floating panels (AMT-04 pop-outs) inherit the same law.
        var c = DockRules.Corners(leftDocked: true, topDocked: true, rightDocked: true, bottomDocked: true, radius: 6);
        SidebarBorder.CornerRadius = new CornerRadius(c.TL, c.TR, c.BR, c.BL);
    }

    // A DELIBERATE state change (footer ⟨, handle ⟩, splitter release) — applies and persists.
    private void SetSidebarState(DockPanelState st)
    {
        _sideState = st;
        _sideAutoHidden = false;   // an explicit choice overrides any pending auto-hide restore
        ApplySidebar();
        _settings.SidebarState = st switch
        {
            DockPanelState.IconRail => "rail",
            DockPanelState.Hidden => "hidden",
            _ => "expanded",
        };
        SettingsStore.Save(_settings);
    }

    // startup: restore the persisted layout (called once the window has real bounds)
    private void RestoreSidebarFromSettings()
    {
        _sideState = _settings.SidebarState switch
        {
            "rail" => DockPanelState.IconRail,
            "hidden" => DockPanelState.Hidden,
            _ => DockPanelState.Expanded,
        };
        ApplySidebar();
        DockOnResize();   // a small first window can immediately auto-hide
    }

    // window resize: auto-hide under the threshold, restore over it, and re-clamp the expanded width so the
    // panel never exceeds half the window (the clamp is display-only — the user's chosen width isn't mutated,
    // so re-widening the window gives their width back)
    private void DockOnResize()
    {
        if (Bounds.Width <= 0) return;
        if (Bounds.Width < DockRules.AutoHideBelow)
        {
            if (_sideState == DockPanelState.Expanded)
            {
                _sideAutoHidden = true;
                _sideState = DockPanelState.Hidden;   // not persisted — the window did this, not the user
            }
        }
        else if (_sideAutoHidden)
        {
            _sideAutoHidden = false;
            _sideState = DockPanelState.Expanded;
        }
        ApplySidebar();
    }

    private void OnCollapseSidebar(object? sender, PointerPressedEventArgs e) => SetSidebarState(DockPanelState.Hidden);
    private void OnRestoreSidebar(object? sender, PointerPressedEventArgs e) => SetSidebarState(DockPanelState.Expanded);

    // ---- the splitter drag: live resize, snap to the icon rail when dragged very narrow ----
    private void OnSplitterPressed(object? sender, PointerPressedEventArgs e)
    {
        _splitterDrag = true;
        e.Pointer.Capture(SidebarSplitter);
        e.Handled = true;
    }

    private void OnSplitterMoved(object? sender, PointerEventArgs e)
    {
        if (!_splitterDrag) return;
        var x = e.GetPosition(BodyGrid).X;   // the pointer's x IS the requested sidebar width
        if (DockRules.StateForDrag(x) == DockPanelState.IconRail)
        {
            _sideState = DockPanelState.IconRail;   // snap: very-narrow drags mean "give me the rail"
        }
        else
        {
            _settings.SidebarWidth = DockRules.ClampWidth(x, Bounds.Width);
            _sideState = DockPanelState.Expanded;
        }
        ApplySidebar();
    }

    private void OnSplitterReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (!_splitterDrag) return;
        _splitterDrag = false;
        e.Pointer.Capture(null);
        SetSidebarState(_sideState);   // commit + persist the final width/state once, on release
    }

    // ---- Update all (download + install) ----

    // The author-chosen CurseForge-app model: one click downloads every newer build and swaps it into mods/,
    // with the old jars moved to a timestamped backup (see UpdateInstaller's boundaries — modified jars are
    // skipped, failures leave the installed jar untouched). Runs off-thread; progress lands in the status bar.
    private async void OnUpdateAll(object? sender, PointerPressedEventArgs e)
    {
        if (_installing || _updates == 0) return;
        _installing = true;
        UpdateAllBtn.Opacity = 0.55;
        try
        {
            var todo = _all.Where(r => r.HasUpdate && r.Detail is not null).Select(r => r.Detail!).ToList();
            var settings = _settings;
            var results = await Task.Run(() => UpdateInstaller.UpdateAllAsync(todo, settings,
                msg => Dispatcher.UIThread.Post(() => CoreStatus.Text = msg)));

            var ok = results.Count(r => r.Ok);
            var failed = results.Where(r => !r.Ok).ToList();
            CoreStatus.Text = failed.Count == 0
                ? $"updated {ok} mods"
                : $"updated {ok}, {failed.Count} failed/skipped — first: {failed[0].Name}: {failed[0].Message}";
        }
        finally
        {
            _installing = false;
            UpdateAllBtn.Opacity = 1;
        }
        LoadMods();   // rescan the folder: new jars become rows, the update check re-runs
    }

    // ---- window chrome (frameless) ----

    private void OnTitleBarPressed(object? sender, PointerPressedEventArgs e)
    {
        if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
            BeginMoveDrag(e);
    }

    private void OnTitleBarDoubleTapped(object? sender, TappedEventArgs e) => ToggleMaximise();

    private void OnResizePressed(object? sender, PointerPressedEventArgs e)
    {
        if (sender is Control c && c.Tag is string tag
            && Enum.TryParse<WindowEdge>(tag, out var edge)
            && e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
            BeginResizeDrag(edge, e);
    }

    private void OnMinimize(object? sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;
    private void OnMaxRestore(object? sender, RoutedEventArgs e) => ToggleMaximise();
    private void OnClose(object? sender, RoutedEventArgs e) => Close();

    private void ToggleMaximise() =>
        WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;

    // "v0.2.0" from an assembly's version (the csproj <Version> lands here at build time)
    private static string VerOf(Assembly a) =>
        a.GetName().Version is { } v ? $"v{v.Major}.{v.Minor}.{v.Build}" : "v?";

    // Win11 rounded window corners on a frameless window (DWM does the rounding for us).
    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int val, int size);

    private void RoundCorners() => RoundCornersFor(this);

    // shared with pop-out panel windows (AMT-04) — every frameless window gets the same DWM rounding
    private static void RoundCornersFor(Window w)
    {
        if (!OperatingSystem.IsWindows()) return;
        var h = w.TryGetPlatformHandle()?.Handle ?? IntPtr.Zero;
        if (h == IntPtr.Zero) return;
        var round = 2; // DWMWCP_ROUND
        DwmSetWindowAttribute(h, 33, ref round, sizeof(int)); // 33 = DWMWA_WINDOW_CORNER_PREFERENCE
    }
}
