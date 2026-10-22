using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using System.Text.Json;
using System.Runtime.InteropServices;
using Microsoft.UI.Windowing;
using Windows.Storage.Streams;

namespace NcmX;

public sealed class MainWindow : Window
{
    private readonly EngineClient engine = new();
    private readonly ColorPickerOverlay pickerOverlay = new();
    private readonly Grid shell = new() { Background = new SolidColorBrush(Colors.Transparent) };
    private readonly Grid titleBarArea = new() { Height = 32, Background = new SolidColorBrush(Colors.Transparent) };
    private readonly Grid root = new() { Padding = new Thickness(20), RowSpacing = 16 };
    private readonly ContentControl settingsHost = new() { HorizontalContentAlignment = HorizontalAlignment.Stretch };
    private readonly NavigationView navigation = new() {
        PaneDisplayMode = NavigationViewPaneDisplayMode.LeftMinimal,
        IsPaneOpen = false, IsPaneToggleButtonVisible = true, IsSettingsVisible = false,
        IsBackButtonVisible = NavigationViewBackButtonVisible.Collapsed,
        AlwaysShowHeader = true, PaneTitle = "NCM X", OpenPaneLength = 220
    };
    private readonly List<UIElement> pages = new();
    private readonly Dictionary<string, ToggleSwitch> toggles = new();
    private readonly Dictionary<string, Button> picks = new();
    private readonly StackPanel colors = new() { Orientation = Orientation.Horizontal, Spacing = 6 };
    private readonly TextBlock status = Text("엔진 연결 중…", 22, true);
    private readonly TextBlock count = Text("작업 횟수: 0회", 14);
    private readonly TextBlock regionInfo = Text("영역을 지정하면 미리보기가 표시됩니다.", 13);
    private Grid? previewLayout;
    private readonly Image preview = new() { Height = 150, Stretch = Stretch.Uniform };
    private readonly TextBox log = new() { IsReadOnly = true, AcceptsReturn = true,
        TextWrapping = TextWrapping.Wrap, FontFamily = new FontFamily("Consolas"), FontSize = 12,
        MinHeight = 0, VerticalAlignment = VerticalAlignment.Stretch, BorderThickness = new Thickness(0) };
    private readonly Slider delay = new() { Minimum = .1, Maximum = 3, StepFrequency = .1, Value = .2 };
    private readonly TextBlock delayLabel = Text("새로고침 대기 · 0.2초", 13);
    private readonly ComboBox wait = new() { HorizontalAlignment = HorizontalAlignment.Stretch };
    private readonly ComboBox siteMode = new() { HorizontalAlignment = HorizontalAlignment.Stretch };
    private readonly TextBlock modeDescription = Text("좌석 색 변화를 확인하는 기존 재클릭 방식을 사용합니다.", 12);
    private readonly StackPanel presetPanel = new() { Spacing = 6, Visibility = Visibility.Collapsed };
    private readonly StackPanel presetButtons = new() { Orientation = Orientation.Horizontal, Spacing = 6 };
    private readonly TextBlock colorSource = Text("직접 지정한 색상 사용", 12);
    private readonly Dictionary<string, ToggleButton> presetChoices = new();
    private readonly ComboBox method = new() { HorizontalAlignment = HorizontalAlignment.Stretch };
    private readonly Button start = new() { Content = "매크로 시작", HorizontalAlignment = HorizontalAlignment.Stretch, IsEnabled = false };
    private readonly Button stop = new() { Content = "중지 · ESC", HorizontalAlignment = HorizontalAlignment.Stretch };
    private readonly Queue<(string Title, string Text)> warnings = new();
    private ContentDialog? currentDialog;
    private bool successDialog;
    private bool dialogBusy;
    private bool updating;
    private bool ready;
    private bool busy;
    private readonly Queue<string> lines = new();

    public MainWindow()
    {
        Title = "NCM X · WinUI 3";
        double scale = Math.Max(1, GetDpiForWindow(WinRT.Interop.WindowNative.GetWindowHandle(this)) / 96.0);
        var area = DisplayArea.GetFromWindowId(AppWindow.Id, DisplayAreaFallback.Primary).WorkArea;
        int width = Math.Min((int)(1040 * scale + 16), area.Width - 32);
        int height = Math.Min((int)(710 * scale + 40), area.Height - 32);
        AppWindow.MoveAndResize(new Windows.Graphics.RectInt32(area.X + (area.Width - width) / 2,
            area.Y + (area.Height - height) / 2, width, height));
        if (AppWindow.Presenter is OverlappedPresenter presenter) {
            presenter.PreferredMinimumWidth = Math.Min((int)(820 * scale), area.Width - 16);
            presenter.PreferredMinimumHeight = Math.Min((int)(720 * scale), area.Height - 16);
        }
        try { SystemBackdrop = new MicaBackdrop(); }
        catch (Exception ex) { StartupDiagnostics.Write("Mica unavailable: " + ex); }
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        StartupDiagnostics.Write("Building header");
        BuildHeader();
        StartupDiagnostics.Write("Building settings and preview");
        BuildBody();
        StartupDiagnostics.Write("Building action buttons");
        BuildFooter();
        navigation.Content = root;
        BuildWindowChrome();
        Closed += (_, _) => { pickerOverlay.Dispose(); engine.Dispose(); };
        engine.Message += message => DispatcherQueue.TryEnqueue(() => OnMessage(message));
        engine.Error += text => { StartupDiagnostics.Write("Engine stderr: " + text); DispatcherQueue.TryEnqueue(() => AddLog(text)); };
        engine.Exited += () => DispatcherQueue.TryEnqueue(() => {
            pickerOverlay.Hide(); ready = false; busy = false; start.IsEnabled = false; settingsHost.IsEnabled = false;
            status.Text = "엔진이 종료되었습니다";
            ShowWarning("엔진 종료", "로그를 확인한 후 앱을 다시 실행하세요.");
        });
        root.Loaded += async (_, _) => {
            try {
                if (Environment.GetCommandLineArgs().Contains("--test-color-cursor")) {
                    pickerOverlay.Show();
                    bool correct = pickerOverlay.VerifyCrosshair();
                    pickerOverlay.Hide();
                    StartupDiagnostics.Write("Color cursor self-test: " + (correct ? "PASS" : "FAIL"));
                    if (!correct) throw new InvalidOperationException("십자 커서 검사 실패");
                }
                StartupDiagnostics.Write("Starting Python engine"); engine.Start();
                if (Environment.GetCommandLineArgs().Contains("--test-native-layout")) await CheckNativeResize();
                if (Environment.GetCommandLineArgs().Contains("--test-titlebar")) {
                    var bar = AppWindow.TitleBar;
                    bool correct = ExtendsContentIntoTitleBar && bar.ButtonBackgroundColor?.A == 0
                        && bar.ButtonInactiveBackgroundColor?.A == 0 && titleBarArea.ActualHeight > 0;
                    StartupDiagnostics.Write("Unified title bar self-test: " + (correct ? "PASS" : "FAIL"));
                    if (!correct) throw new InvalidOperationException("제목 표시줄 검사 실패");
                }
            }
            catch (Exception ex) { status.Text = "엔진 연결 실패"; ShowWarning("실행 오류", ex.Message); }
        };
    }

    private async Task CheckNolUI()
    {
        var originalSize = AppWindow.Size;
        try {
            await Task.Delay(700); // Let the independent native-resize smoke check finish first.
            if (presetChoices.Count != 4) throw new InvalidOperationException("프리셋 개수 오류");
            siteMode.SelectedIndex = 1;
            presetChoices["purple"].IsChecked = presetChoices["green"].IsChecked = true;
            UpdateModeUI(); await SendSettings();
            await Task.Delay(150);
            if (presetPanel.Visibility != Visibility.Visible || presetChoices.Count(p => p.Value.IsChecked == true) != 2)
                throw new InvalidOperationException("NOL 복수 선택 오류");
            double dpi = GetDpiForWindow(WinRT.Interop.WindowNative.GetWindowHandle(this)) / 96.0;
            AppWindow.Resize(new Windows.Graphics.SizeInt32((int)(840 * dpi), (int)(720 * dpi)));
            navigation.SelectedItem = navigation.MenuItems[1];
            toggles["is_yes24_mode"].IsOn = true;
            toggles["use_multi_refresh"].IsOn = true;
            await Task.Delay(200); root.UpdateLayout();
            double waitBottom = wait.TransformToVisual(settingsHost).TransformPoint(new Windows.Foundation.Point(0, wait.ActualHeight)).Y;
            double pickBottom = picks["refresh2"].TransformToVisual(settingsHost).TransformPoint(new Windows.Foundation.Point(0, picks["refresh2"].ActualHeight)).Y;
            if (waitBottom > settingsHost.ActualHeight + 1 || pickBottom > settingsHost.ActualHeight + 1)
                throw new InvalidOperationException($"설정 영역 넘침: {waitBottom}/{pickBottom}/{settingsHost.ActualHeight}");
            preview.Source = new WriteableBitmap(320, 180); // Simulate an actual captured region, without screen capture.
            navigation.SelectedItem = navigation.MenuItems[0];
            await Task.Delay(200); root.UpdateLayout();
            double previewHeight = previewLayout!.RowDefinitions[1].ActualHeight;
            if (previewHeight < 60 || presetButtons.ActualWidth > presetPanel.ActualWidth + 1)
                throw new InvalidOperationException($"NOL 홈 영역 넘침: preview={previewHeight:F1}; presets={presetButtons.ActualWidth:F1}/{presetPanel.ActualWidth:F1}");
            siteMode.SelectedIndex = 0; UpdateModeUI();
            if (presetPanel.Visibility != Visibility.Collapsed) throw new InvalidOperationException("일반 모드 전환 오류");
            siteMode.SelectedIndex = 1;
            if (presetChoices.Count(p => p.Value.IsChecked == true) != 2) throw new InvalidOperationException("프리셋 선택 보존 오류");
            foreach (var pair in presetChoices) pair.Value.IsChecked = false;
            siteMode.SelectedIndex = 0;
            toggles["is_yes24_mode"].IsOn = toggles["use_multi_refresh"].IsOn = false;
            UpdateModeUI(); await SendSettings();
            StartupDiagnostics.Write($"NOL mode and multi-preset UI self-test: PASS; settings={waitBottom:F1}/{pickBottom:F1}; available={settingsHost.ActualHeight:F1}; preview={previewHeight:F1}");
        } catch (Exception ex) { StartupDiagnostics.Write("NOL UI self-test: FAIL " + ex); }
        finally { preview.Source = null; AppWindow.Resize(originalSize); }
    }

    private void BuildWindowChrome()
    {
        var transparent = new SolidColorBrush(Colors.Transparent);
        navigation.Background = transparent;
        navigation.Resources["NavigationViewContentBackground"] = transparent;
        shell.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        shell.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        var title = Text("NCM X", 12, true);
        title.VerticalAlignment = VerticalAlignment.Center;
        title.Margin = new Thickness(16, 0, 0, 0);
        titleBarArea.Children.Add(title);
        shell.Children.Add(titleBarArea);
        Grid.SetRow(navigation, 1); shell.Children.Add(navigation);
        Content = shell;
        ExtendsContentIntoTitleBar = true;
        AppWindow.TitleBar.PreferredHeightOption = TitleBarHeightOption.Standard;
        SetTitleBar(titleBarArea);
        void UpdateInsets() {
            double dpi = Math.Max(1, GetDpiForWindow(WinRT.Interop.WindowNative.GetWindowHandle(this)) / 96.0);
            titleBarArea.Padding = new Thickness(AppWindow.TitleBar.LeftInset / dpi, 0,
                AppWindow.TitleBar.RightInset / dpi, 0);
        }
        AppWindow.Changed += (_, _) => UpdateInsets();
        titleBarArea.SizeChanged += (_, _) => UpdateInsets();
        shell.ActualThemeChanged += (_, _) => ApplyTitleBarColors();
        Activated += (_, args) => title.Opacity = args.WindowActivationState == WindowActivationState.Deactivated ? .55 : 1;
        UpdateInsets(); ApplyTitleBarColors();
    }

    private void ApplyTitleBarColors()
    {
        bool dark = shell.ActualTheme == ElementTheme.Dark;
        var foreground = dark ? Colors.White : Colors.Black;
        var bar = AppWindow.TitleBar;
        bar.BackgroundColor = Colors.Transparent;
        bar.InactiveBackgroundColor = Colors.Transparent;
        bar.ButtonBackgroundColor = Colors.Transparent;
        bar.ButtonInactiveBackgroundColor = Colors.Transparent;
        bar.ForegroundColor = foreground;
        bar.ButtonForegroundColor = foreground;
        bar.InactiveForegroundColor = dark ? Windows.UI.Color.FromArgb(255, 160, 160, 160) : Windows.UI.Color.FromArgb(255, 100, 100, 100);
        bar.ButtonInactiveForegroundColor = bar.InactiveForegroundColor;
        bar.ButtonHoverForegroundColor = foreground;
        bar.ButtonPressedForegroundColor = foreground;
        bar.ButtonHoverBackgroundColor = dark ? Windows.UI.Color.FromArgb(24, 255, 255, 255) : Windows.UI.Color.FromArgb(16, 0, 0, 0);
        bar.ButtonPressedBackgroundColor = dark ? Windows.UI.Color.FromArgb(40, 255, 255, 255) : Windows.UI.Color.FromArgb(28, 0, 0, 0);
    }

    private async Task CheckNativeResize()
    {
        var original = AppWindow.Size;
        double dpi = GetDpiForWindow(WinRT.Interop.WindowNative.GetWindowHandle(this)) / 96.0;
        try {
            AppWindow.Resize(new Windows.Graphics.SizeInt32((int)(1080 * dpi), original.Height));
            await Task.Delay(200); root.UpdateLayout();
            double wideFont = picks["region"].FontSize, wideButtonHeight = picks["region"].ActualHeight;
            double wideLog = log.ActualWidth, wideRoot = root.ActualWidth;
            AppWindow.Resize(new Windows.Graphics.SizeInt32((int)(840 * dpi), original.Height));
            await Task.Delay(200); root.UpdateLayout();
            double narrowFont = picks["region"].FontSize, narrowButtonHeight = picks["region"].ActualHeight;
            double narrowLog = log.ActualWidth, narrowRoot = root.ActualWidth;
            bool pass = wideRoot > narrowRoot && wideLog > narrowLog && narrowLog > 100
                && Math.Abs(wideFont - narrowFont) < .01
                && Math.Abs(wideButtonHeight - narrowButtonHeight) < .01;
            StartupDiagnostics.Write($"Native resize self-test: {(pass ? "PASS" : "FAIL")}; " +
                $"root={wideRoot:F1}->{narrowRoot:F1}; log={wideLog:F1}->{narrowLog:F1}; " +
                $"font={wideFont:F1}->{narrowFont:F1}; buttonHeight={wideButtonHeight:F1}->{narrowButtonHeight:F1}");
            if (!pass) throw new InvalidOperationException("창 크기 변경 검사 실패");
        } finally { AppWindow.Resize(original); }
    }

    [DllImport("user32.dll")] private static extern uint GetDpiForWindow(nint window);

    private static TextBlock Text(string value, double size, bool bold = false) => new() {
        Text = value, FontSize = size, TextWrapping = TextWrapping.Wrap,
        FontWeight = bold ? Microsoft.UI.Text.FontWeights.SemiBold : Microsoft.UI.Text.FontWeights.Normal
    };
    private static StackPanel Stack(params UIElement[] children)
    {
        var stack = new StackPanel { Spacing = 12 };
        foreach (var child in children) stack.Children.Add(child);
        return stack;
    }
    private static T? Resource<T>(string key) where T : class
    {
        try {
            return Application.Current.Resources.TryGetValue(key, out var value) ? value as T : null;
        } catch (Exception ex) { StartupDiagnostics.Write($"Resource fallback {key}: {ex.Message}"); return null; }
    }
    private static Border Card(string heading, params UIElement[] children)
    {
        var stack = Stack(Text(heading, 17, true));
        foreach (var child in children) stack.Children.Add(child);
        return new Border {
            Padding = new Thickness(20), CornerRadius = new CornerRadius(12),
            Background = Resource<Brush>("CardBackgroundFillColorDefaultBrush") ?? new SolidColorBrush(Windows.UI.Color.FromArgb(255, 248, 248, 248)),
            BorderBrush = Resource<Brush>("CardStrokeColorDefaultBrush") ?? new SolidColorBrush(Windows.UI.Color.FromArgb(255, 215, 215, 215)),
            BorderThickness = new Thickness(1), Child = stack
        };
    }
    private void BuildHeader()
    {
        navigation.Header = new TextBlock { Text = "NCM X", FontSize = 24,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold };
        AutomationProperties.SetName(navigation, "홈 및 설정 메뉴");
    }

    private Button Pick(string key, string title)
    {
        var button = new Button { Content = title, HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Left };
        AutomationProperties.SetName(button, title);
        button.Click += async (_, _) => {
            busy = true; UpdateEnabled();
            await engine.SendAsync(new { action = "select", kind = key });
        };
        picks[key] = button;
        return button;
    }
    private ToggleSwitch Toggle(string key, string label, bool value = false)
    {
        var toggle = new ToggleSwitch { Header = label, IsOn = value, OnContent = "켜짐", OffContent = "꺼짐" };
        AutomationProperties.SetName(toggle, label);
        toggle.Toggled += async (_, _) => {
            if (updating) return;
            updating = true;
            if (key == "use_custom_refresh" && toggle.IsOn) toggles["use_multi_refresh"].IsOn = false;
            if (key == "use_multi_refresh" && toggle.IsOn) toggles["use_custom_refresh"].IsOn = false;
            updating = false;
            UpdateModeUI();
            await SendSettings();
        };
        toggles[key] = toggle;
        return toggle;
    }

    private Grid Option(string key, string label, bool value = false)
    {
        var row = new Grid { MinHeight = 42, ColumnSpacing = 8 };
        row.ColumnDefinitions.Add(new ColumnDefinition());
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(48) });
        var text = Text(label, 13); text.VerticalAlignment = VerticalAlignment.Center; row.Children.Add(text);
        var toggle = Toggle(key, label, value);
        toggle.Header = null; toggle.OnContent = ""; toggle.OffContent = "";
        toggle.MinWidth = 0; toggle.Width = 48;
        toggle.HorizontalAlignment = HorizontalAlignment.Right;
        ToolTipService.SetToolTip(toggle, label);
        Grid.SetColumn(toggle, 1); row.Children.Add(toggle);
        return row;
    }
    private void BuildBody()
    {
        var homeItem = new NavigationViewItem { Content = "홈", Icon = new SymbolIcon(Symbol.Home), Tag = 0 };
        var settingsItem = new NavigationViewItem { Content = "설정", Icon = new SymbolIcon(Symbol.Setting), Tag = 1 };
        navigation.MenuItems.Add(homeItem); navigation.MenuItems.Add(settingsItem);

        var home = new Grid { ColumnSpacing = 16 };
        home.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(350) });
        home.ColumnDefinitions.Add(new ColumnDefinition());
        home.SizeChanged += (_, _) => {
            home.ColumnDefinitions[0].Width = new GridLength(Math.Clamp(home.ActualWidth * .35, 280, 350));
        };
        var left = new Grid { RowSpacing = 16 };
        left.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        left.RowDefinitions.Add(new RowDefinition());
        presetPanel.Children.Add(colorSource);
        presetPanel.Children.Add(presetButtons);
        ToolTipService.SetToolTip(presetPanel, "여러 색상을 선택할 수 있습니다. 선택한 프리셋이 직접 지정 색상을 대체하며, 모두 해제하면 직접 지정 색상을 사용합니다.");
        var primaryButtons = Stack(Pick("region", "영역 설정 · A → B"), Pick("color", "좌석 색상 추가 · A"),
            Pick("final", "최종 클릭 위치 · A"), colors, presetPanel,
            Text("A 지정 · B 영역 끝 · ESC 취소", 12));
        primaryButtons.Spacing = 8;
        left.Children.Add(Card("위치 지정", primaryButtons));
        preview.Height = double.NaN;
        preview.VerticalAlignment = VerticalAlignment.Stretch;
        var previewBody = new Grid { RowSpacing = 8 };
        previewLayout = previewBody;
        previewBody.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        previewBody.RowDefinitions.Add(new RowDefinition());
        previewBody.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        previewBody.Children.Add(Text("감지 영역 미리보기", 17, true));
        Grid.SetRow(preview, 1); previewBody.Children.Add(preview);
        Grid.SetRow(regionInfo, 2); previewBody.Children.Add(regionInfo);
        var previewCard = new Border { Padding = new Thickness(20), CornerRadius = new CornerRadius(12),
            Background = Resource<Brush>("CardBackgroundFillColorDefaultBrush"),
            BorderBrush = Resource<Brush>("CardStrokeColorDefaultBrush"), BorderThickness = new Thickness(1), Child = previewBody };
        Grid.SetRow(previewCard, 1); left.Children.Add(previewCard); home.Children.Add(left);

        var console = new Grid { RowSpacing = 10 };
        console.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        console.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        console.RowDefinitions.Add(new RowDefinition());
        console.Children.Add(Text("실행 로그", 17, true));
        var summary = new Grid { ColumnSpacing = 8 };
        summary.ColumnDefinitions.Add(new ColumnDefinition()); summary.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        status.FontSize = 16; count.FontSize = 12; count.VerticalAlignment = VerticalAlignment.Center;
        summary.Children.Add(status); Grid.SetColumn(count, 1); summary.Children.Add(count);
        Grid.SetRow(summary, 1); console.Children.Add(summary);
        Grid.SetRow(log, 2); console.Children.Add(log);
        var consoleCard = new Border { Padding = new Thickness(20), CornerRadius = new CornerRadius(12),
            Background = Resource<Brush>("CardBackgroundFillColorDefaultBrush"),
            BorderBrush = Resource<Brush>("CardStrokeColorDefaultBrush"), BorderThickness = new Thickness(1), Child = console };
        Grid.SetColumn(consoleCard, 1); home.Children.Add(consoleCard);
        pages.Add(home);

        delay.ValueChanged += async (_, _) => {
            delayLabel.Text = $"새로고침 대기 · {delay.Value:F1}초";
            if (!updating) await SendSettings();
        };
        AutomationProperties.SetName(delay, "새로고침 대기 시간(초)");
        foreach (var label in new[] { "즉시 시작", "1초 후 시작", "2초 후 시작", "3초 후 시작" }) wait.Items.Add(label);
        wait.SelectedIndex = 3;
        wait.SelectionChanged += async (_, _) => { if (!updating) await SendSettings(); };
        AutomationProperties.SetName(wait, "시작 대기 시간");
        var speedCard = Card("속도 및 시작", delayLabel, delay,
            Option("is_random_delay", "무작위 딜레이 · 0.2~0.5초"), Text("시작 대기 시간", 13), wait);

        method.Items.Add("신형 · 고속 스캔 / 중앙 클릭");
        method.Items.Add("구형 · 십자 검증 / 모서리 클릭");
        method.SelectedIndex = 0;
        method.SelectionChanged += async (_, _) => { if (!updating) await SendSettings(); };
        AutomationProperties.SetName(method, "감지 엔진");
        siteMode.Items.Add("일반 모드");
        siteMode.Items.Add("NOL 모드");
        siteMode.SelectedIndex = 0;
        AutomationProperties.SetName(siteMode, "사용 모드");
        siteMode.SelectionChanged += async (_, _) => {
            UpdateModeUI();
            if (!updating) await SendSettings();
        };
        if (speedCard.Child is StackPanel speedContent) {
            speedContent.Spacing = 8;
            speedContent.Children.Insert(1, Text("사용 모드", 13));
            speedContent.Children.Insert(2, siteMode);
            speedContent.Children.Insert(3, modeDescription);
        }
        var modeCard = Card("동작 모드", Text("감지 엔진", 13), method,
            Option("is_yes24_mode", "YES24 · 방향키 새로고침"),
            Option("use_custom_refresh", "단일 위치 새로고침"),
            Option("use_multi_refresh", "다중 위치 새로고침"),
            Option("no_refresh_mode", "새로고침 없이 감지만 수행"),
            Pick("focus", "회차창 위치 · A / YES24"),
            Pick("refresh1", "새로고침 위치 1 · A"),
            Pick("refresh2", "새로고침 위치 2 · A"));
        if (modeCard.Child is StackPanel modeContent) modeContent.Spacing = 8;
        var options = new Grid { ColumnSpacing = 12 };
        for (int i = 0; i < 2; i++) options.ColumnDefinitions.Add(new ColumnDefinition());
        options.Children.Add(speedCard);
        Grid.SetColumn(modeCard, 1); options.Children.Add(modeCard);
        pages.Add(options);

        settingsHost.IsEnabled = false;
        settingsHost.VerticalContentAlignment = VerticalAlignment.Stretch;
        settingsHost.Content = pages[0];
        navigation.SelectionChanged += (_, args) => {
            if (args.SelectedItem is NavigationViewItem item && item.Tag is int page)
                settingsHost.Content = pages[page];
        };
        navigation.ItemInvoked += (_, _) => navigation.IsPaneOpen = false;
        navigation.SelectedItem = homeItem;
        root.Children.Add(settingsHost);
        UpdateModeUI();
    }

    private void BuildFooter()
    {
        var footer = new Grid { ColumnSpacing = 12 };
        footer.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        footer.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(220) });
        start.Style = Resource<Style>("AccentButtonStyle");
        start.Height = stop.Height = 48;
        start.Click += async (_, _) => {
            await SendSettings();
            busy = true; UpdateEnabled();
            await engine.SendAsync(new { action = "start" });
        };
        stop.Click += async (_, _) => await engine.SendAsync(new { action = "stop" });
        footer.Children.Add(start); Grid.SetColumn(stop, 1); footer.Children.Add(stop);
        Grid.SetRow(footer, 1); root.Children.Add(footer);
    }

    private Task SendSettings()
    {
        if (!ready || updating || busy) return Task.CompletedTask;
        var values = toggles.ToDictionary(pair => pair.Key, pair => (object)pair.Value.IsOn);
        values["refresh_delay"] = delay.Value;
        values["start_wait_time"] = Math.Max(0, wait.SelectedIndex);
        values["site_mode"] = siteMode.SelectedIndex == 1 ? "nol" : "general";
        values["selected_presets"] = presetChoices.Where(pair => pair.Value.IsChecked == true).Select(pair => pair.Key).ToArray();
        values["scan_method"] = Math.Max(0, method.SelectedIndex) + 1;
        return engine.SendAsync(new { action = "settings", settings = values });
    }
    private void BuildColorPresets(JsonElement presets)
    {
        presetChoices.Clear(); presetButtons.Children.Clear();
        foreach (var preset in presets.EnumerateArray()) {
            string id = preset.GetProperty("id").GetString()!;
            string name = preset.GetProperty("label").GetString()!;
            int[] rgb = preset.GetProperty("rgb").EnumerateArray().Select(c => c.GetInt32()).ToArray();
            var content = new StackPanel { Spacing = 2, HorizontalAlignment = HorizontalAlignment.Center };
            content.Children.Add(new Border { Width = 24, Height = 8, CornerRadius = new CornerRadius(3),
                Background = new SolidColorBrush(Windows.UI.Color.FromArgb(255,(byte)rgb[0],(byte)rgb[1],(byte)rgb[2])) });
            content.Children.Add(Text(name, 11));
            var choice = new ToggleButton { Content = content, Width = 50, Height = 36, Padding = new Thickness(4) };
            AutomationProperties.SetName(choice, "NOL " + name + " 프리셋");
            ToolTipService.SetToolTip(choice, $"{name} · #{rgb[0]:X2}{rgb[1]:X2}{rgb[2]:X2} · 복수 선택 가능");
            choice.Click += async (_, _) => { UpdateModeUI(); if (!updating) await SendSettings(); };
            presetChoices[id] = choice; presetButtons.Children.Add(choice);
        }
    }
    private void UpdateModeUI()
    {
        bool nol = siteMode.SelectedIndex == 1;
        presetPanel.Visibility = nol ? Visibility.Visible : Visibility.Collapsed;
        modeDescription.Text = nol ? "최종 버튼 색 변화로 좌석 클릭 반영을 확인합니다." : "기존 좌석 색 기반 재클릭 방식을 사용합니다.";
        int selected = presetChoices.Count(pair => pair.Value.IsChecked == true);
        colorSource.Text = selected > 0 ? $"NOL 프리셋 · {selected}개 사용 중" : "NOL 프리셋 · 복수 선택";
        if (!toggles.ContainsKey("is_yes24_mode")) return;
        bool multi = toggles["use_multi_refresh"].IsOn;
        picks["focus"].Visibility = toggles["is_yes24_mode"].IsOn ? Visibility.Visible : Visibility.Collapsed;
        picks["refresh1"].Visibility = multi || toggles["use_custom_refresh"].IsOn ? Visibility.Visible : Visibility.Collapsed;
        picks["refresh2"].Visibility = multi ? Visibility.Visible : Visibility.Collapsed;
        delay.IsEnabled = !toggles["is_random_delay"].IsOn;
    }
    private void UpdateEnabled()
    {
        settingsHost.IsEnabled = ready && !busy;
        start.IsEnabled = ready && !busy;
        stop.IsEnabled = ready;
        start.Content = busy ? "작업 진행 중…" : "매크로 시작";
    }
    private static string Position(JsonElement value) => value.ValueKind == JsonValueKind.Array
        ? $"({value[0]}, {value[1]})" : "미지정";

    private void OnMessage(JsonElement message)
    {
        switch (message.GetProperty("type").GetString()) {
            case "ready":
                BuildColorPresets(message.GetProperty("presets"));
                ready = true; StartupDiagnostics.Write("Engine ready handshake received"); status.Text = "대기 중"; UpdateEnabled(); _ = SendSettings();
                if (Environment.GetCommandLineArgs().Contains("--test-nol-ui")) _ = CheckNolUI();
                break;
            case "log": AddLog(message.GetProperty("text").GetString()!); break;
            case "status":
                status.Text = message.GetProperty("text").GetString()!;
                string hex = message.GetProperty("color").GetString()!.TrimStart('#');
                if (hex.Length == 6) status.Foreground = new SolidColorBrush(Windows.UI.Color.FromArgb(255,
                    Convert.ToByte(hex[..2],16), Convert.ToByte(hex[2..4],16), Convert.ToByte(hex[4..6],16)));
                break;
            case "count": count.Text = message.GetProperty("text").GetString(); break;
            case "state":
                bool wasBusy = busy;
                busy = message.GetProperty("busy").GetBoolean();
                if (wasBusy && !busy) {
                    // A completed direct color capture switches back to manually sampled colors.
                    var selected = message.GetProperty("selected_presets").EnumerateArray().Select(value => value.GetString()).ToHashSet();
                    foreach (var pair in presetChoices) pair.Value.IsChecked = selected.Contains(pair.Key);
                    UpdateModeUI();
                }
                if (!busy) pickerOverlay.Hide();
                regionInfo.Text = "감지 영역: " + message.GetProperty("region").ToString();
                SetPick("region", "감지 영역 · A → B", message.GetProperty("region"));
                SetPick("final", "최종 클릭 · A", message.GetProperty("final"));
                SetPick("focus", "YES24 회차창 · A", message.GetProperty("focus"));
                SetPick("refresh1", "새로고침 1 · A", message.GetProperty("refresh1"));
                SetPick("refresh2", "새로고침 2 · A", message.GetProperty("refresh2"));
                picks["color"].Content = message.GetProperty("colors").GetArrayLength() > 0 ? "✓ 좌석 색상 추가 · A" : "좌석 색상 추가 · A";
                colors.Children.Clear();
                foreach (var value in message.GetProperty("colors").EnumerateArray()) {
                    int[] rgb = value.EnumerateArray().Select(c => c.GetInt32()).ToArray();
                    string colorHex = $"#{rgb[0]:X2}{rgb[1]:X2}{rgb[2]:X2}";
                    var button = new Button { Content = "×", Width = 40, Height = 36,
                        Background = new SolidColorBrush(Windows.UI.Color.FromArgb(255,(byte)rgb[0],(byte)rgb[1],(byte)rgb[2])),
                        Foreground = new SolidColorBrush((rgb[0]*299+rgb[1]*587+rgb[2]*114)>128000 ? Colors.Black : Colors.White) };
                    ToolTipService.SetToolTip(button, colorHex+" · 클릭하여 삭제");
                    AutomationProperties.SetName(button, colorHex+" 색상 삭제");
                    button.Click += async (_, _) => await engine.SendAsync(new { action = "remove_color", color = rgb });
                    colors.Children.Add(button);
                }
                UpdateEnabled(); break;
            case "picker":
                if (!message.TryGetProperty("active", out var active) || active.GetBoolean()) pickerOverlay.Show();
                else pickerOverlay.Hide();
                break;
            case "preview": _ = SetPreview(message.GetProperty("png").GetString()!); break;
            case "warning":
                pickerOverlay.Hide(); busy = false; UpdateEnabled();
                ShowWarning(message.GetProperty("title").GetString()!, message.GetProperty("text").GetString()!); break;
            case "success": _ = ShowSuccess(message.GetProperty("text").GetString()!); break;
            case "dismiss_success": if (successDialog) currentDialog?.Hide(); break;
        }
    }
    private void SetPick(string key, string label, JsonElement value)
    {
        bool set = value.ValueKind == JsonValueKind.Array;
        picks[key].Content = set ? $"✓ {label}  {Position(value)}" : label;
    }
    private async Task SetPreview(string png)
    {
        try {
            using var stream = new InMemoryRandomAccessStream();
            using (var writer = new DataWriter(stream.GetOutputStreamAt(0))) {
                writer.WriteBytes(Convert.FromBase64String(png));
                await writer.StoreAsync(); await writer.FlushAsync();
            }
            stream.Seek(0);
            var bitmap = new BitmapImage(); await bitmap.SetSourceAsync(stream); preview.Source = bitmap;
        } catch (Exception ex) { AddLog("미리보기 표시 실패: "+ex.Message); }
    }
    private void AddLog(string text)
    {
        lines.Enqueue(text);
        while (lines.Count > 500) lines.Dequeue();
        log.Text = string.Join(Environment.NewLine, lines);
        log.Select(log.Text.Length, 0);
    }
    private void ShowWarning(string title, string text)
    {
        warnings.Enqueue((title, text));
        _ = DrainWarnings();
    }
    private async Task DrainWarnings()
    {
        if (dialogBusy || root.XamlRoot == null) return;
        dialogBusy = true;
        try {
            while (warnings.TryDequeue(out var warning)) {
                currentDialog = new ContentDialog { XamlRoot = root.XamlRoot, Title = warning.Title,
                    Content = warning.Text, CloseButtonText = "확인" };
                await currentDialog.ShowAsync();
            }
        } finally { currentDialog = null; dialogBusy = false; }
    }
    private async Task ShowSuccess(string text)
    {
        if (dialogBusy) return;
        successDialog = dialogBusy = true;
        try {
            currentDialog = new ContentDialog { XamlRoot = root.XamlRoot, Title = "감지 및 클릭 완료",
                Content = text, PrimaryButtonText = "다시 시작 · A", CloseButtonText = "종료 · ESC" };
            currentDialog.CloseButtonClick += async (_, _) => await engine.SendAsync(new { action = "stop" });
            var result = await currentDialog.ShowAsync();
            // A/ESC is also handled globally by the engine. Only a button click needs a new command.
            if (result == ContentDialogResult.Primary) await engine.SendAsync(new { action = "restart" });
        } finally {
            successDialog = dialogBusy = false; currentDialog = null; _ = DrainWarnings();
        }
    }
}
