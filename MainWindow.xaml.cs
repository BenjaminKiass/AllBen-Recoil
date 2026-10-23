using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

namespace AllBenRecoil
{
    public partial class MainWindow : Window
    {
        readonly Settings cfg;
        readonly Overlay osd;
        readonly KeyHook hook;
        System.Windows.Forms.NotifyIcon tray;
        VisionWindow visionWin;
        bool capturingHotkey;

        static readonly SolidColorBrush RedBrush = new(Color.FromRgb(0xEF, 0x44, 0x44));
        static readonly SolidColorBrush CyanBrush = new(Color.FromRgb(0x38, 0xBD, 0xF8));
        static readonly SolidColorBrush TextWhite = new(Color.FromRgb(0xF0, 0xF6, 0xFC));
        static readonly SolidColorBrush MutedText = new(Color.FromRgb(0x80, 0x94, 0xBC));
        static readonly SolidColorBrush CardActiveBg = new(Color.FromRgb(0x11, 0x1A, 0x2E));
        static readonly SolidColorBrush CardInactiveBg = new(Color.FromRgb(0x0F, 0x17, 0x2A));
        static readonly SolidColorBrush CardActiveBorder = new(Color.FromRgb(0x1D, 0x6B, 0xFF));
        static readonly SolidColorBrush CardInactiveBorder = new(Color.FromRgb(0x1E, 0x29, 0x3B));

        public MainWindow()
        {
            InitializeComponent();

            cfg = Settings.Load();
            osd = new Overlay();

            // นำเข้าค่าลง RecoilEngine
            RecoilEngine.Slots[0].Name = cfg.Names[0];
            RecoilEngine.Slots[0].Base = cfg.Gun1Base;
            RecoilEngine.Slots[0].X = cfg.Gun1X;

            RecoilEngine.Slots[1].Name = cfg.Names[1];
            RecoilEngine.Slots[1].Base = cfg.Gun2Base;
            RecoilEngine.Slots[1].X = cfg.Gun2X;

            RecoilEngine.Slots[2].Name = cfg.Names[2];
            RecoilEngine.Slots[2].Base = cfg.Gun3Base;
            RecoilEngine.Slots[2].X = cfg.Gun3X;

            RecoilEngine.RequireADS = cfg.RequireADS;
            RecoilEngine.HumanizeJitter = cfg.Humanize;
            RecoilEngine.ActiveSlot = Math.Clamp(cfg.Gun, 1, 3);

            // เริ่มเอนจิน Standalone 1ms
            RecoilEngine.Start();
            RecoilEngine.StateChanged += OnEngineStateChanged;
            RecoilEngine.SlotChanged += OnEngineSlotChanged;

            // คีย์บอร์ด Hook
            hook = new KeyHook();
            hook.KeyDown += vk => Dispatcher.BeginInvoke(new Action(() => OnKeyDownHook(vk)));
            hook.KeyUp += vk => Dispatcher.BeginInvoke(new Action(() => OnKeyUpHook(vk)));

            // ตั้งค่าตำแหน่งหน้าต่าง
            if (cfg.Left.HasValue && cfg.Top.HasValue && cfg.Left.Value >= -100 && cfg.Top.Value >= -100)
            {
                WindowStartupLocation = WindowStartupLocation.Manual;
                Left = cfg.Left.Value;
                Top = cfg.Top.Value;
            }

            SetupBranding();
            SetupTray();
            UpdateAllUI();
            StartGameWatcher();

            Loaded += (s, e) =>
            {
                osd.Configure(cfg.OsdMode, cfg.OsdCorner);
                Sfx.Play(Sfx.Ready, cfg.Sound);
            };
        }

        void SetupBranding()
        {
            var logo = Branding.LogoImage();
            if (logo != null) LogoImg.Source = logo;
        }

        void SetupTray()
        {
            tray = new System.Windows.Forms.NotifyIcon
            {
                Visible = true,
                Text = "all@Ben-Recoil (Standalone)"
            };
            tray.DoubleClick += (s, e) => ShowFromTray();

            var menu = new System.Windows.Forms.ContextMenuStrip();
            menu.Items.Add("เปิดหน้าต่าง", null, (s, e) => ShowFromTray());
            menu.Items.Add("เปิด/ปิด มาโคร", null, (s, e) => RecoilEngine.Toggle());
            menu.Items.Add("-");
            menu.Items.Add("ออก", null, (s, e) => Close());
            tray.ContextMenuStrip = menu;
            UpdateTray();
        }

        void UpdateTray()
        {
            if (tray == null) return;
            var old = tray.Icon;
            tray.Icon = TrayArt.Make(RecoilEngine.IsEnabled, RecoilEngine.ActiveSlot, out var winIcon);
            try
            {
                Icon = System.Windows.Interop.Imaging.CreateBitmapSourceFromHIcon(
                    winIcon.Handle, Int32Rect.Empty, System.Windows.Media.Imaging.BitmapSizeOptions.FromEmptyOptions());
            }
            catch { }
            old?.Dispose();
        }

        public void StartInTray()
        {
            WindowState = WindowState.Minimized;
            Hide();
        }

        public void ShowFromTray()
        {
            Show();
            WindowState = WindowState.Normal;
            Activate();
        }

        void OnEngineStateChanged(bool on)
        {
            Dispatcher.BeginInvoke(new Action(() =>
            {
                UpdatePowerUI();
                UpdateTray();
                Sfx.Play(on ? Sfx.On : Sfx.Off, cfg.Sound);
                PopOsd(on ? $"มาโคร: เปิด ({RecoilEngine.CurrentGun.Name})" : "มาโคร: ปิด");
            }));
        }

        void OnEngineSlotChanged(int slot)
        {
            Dispatcher.BeginInvoke(new Action(() =>
            {
                cfg.Gun = slot;
                cfg.Save();
                UpdateGunCardsUI();
                UpdateSlidersUI();
                UpdateTray();
                Sfx.Play(Sfx.Pick, cfg.Sound);
                PopOsd($"เลือก: {RecoilEngine.CurrentGun.Name} (Base {RecoilEngine.CurrentGun.Base:F1})");
            }));
        }

        void UpdateAllUI()
        {
            UpdatePowerUI();
            UpdateGunCardsUI();
            UpdateSlidersUI();
            RequireAdsToggle.IsChecked = cfg.RequireADS;
            HumanizeToggle.IsChecked = cfg.Humanize;
            SoundToggle.IsChecked = cfg.Sound;
            AutoStartToggle.IsChecked = AutoStart.IsEnabled();
            HotkeyBtn.Content = KeyName(cfg.HotKey);
            PresetMenuBtn.Content = $"โปรไฟล์: {cfg.Preset}";
            ResBtn.Content = cfg.AutoRes ? $"{cfg.GameResW}×{cfg.GameResH} ในเกม" : "ปิด";
        }

        void UpdatePowerUI()
        {
            bool on = RecoilEngine.IsEnabled;
            PowerRing.Stroke = on ? CyanBrush : RedBrush;
            PowerGlow.Fill = on ? CyanBrush : RedBrush;
            PowerIcon.Foreground = on ? CyanBrush : RedBrush;
            PowerStatusText.Text = on ? "ON" : "OFF";
            PowerStatusText.Foreground = on ? CyanBrush : RedBrush;
            PowerSubText.Text = on ? "มาโครเปิดอยู่ (เลเซอร์พร้อมยิง)" : "มาโครปิดอยู่";
            SafetyStatusText.Text = cfg.RequireADS ? "✓ ดึงเฉพาะตอนเล็งขวา (ADS)" : "⚡ ดึงทุกการคลิกซ้าย (Hipfire)";
            SafetyStatusText.Foreground = cfg.RequireADS ? CyanBrush : RedBrush;
        }

        void UpdateGunCardsUI()
        {
            int slot = RecoilEngine.ActiveSlot;
            Card1.Background = slot == 1 ? CardActiveBg : CardInactiveBg;
            Card1.BorderBrush = slot == 1 ? CardActiveBorder : CardInactiveBorder;
            Gun1NameText.Foreground = slot == 1 ? CyanBrush : TextWhite;
            Gun1NameText.Text = RecoilEngine.Slots[0].Name;
            Gun1BaseText.Text = $"base {RecoilEngine.Slots[0].Base:F1}";

            Card2.Background = slot == 2 ? CardActiveBg : CardInactiveBg;
            Card2.BorderBrush = slot == 2 ? CardActiveBorder : CardInactiveBorder;
            Gun2NameText.Foreground = slot == 2 ? CyanBrush : TextWhite;
            Gun2NameText.Text = RecoilEngine.Slots[1].Name;
            Gun2BaseText.Text = $"base {RecoilEngine.Slots[1].Base:F1}";

            Card3.Background = slot == 3 ? CardActiveBg : CardInactiveBg;
            Card3.BorderBrush = slot == 3 ? CardActiveBorder : CardInactiveBorder;
            Gun3NameText.Foreground = slot == 3 ? CyanBrush : TextWhite;
            Gun3NameText.Text = RecoilEngine.Slots[2].Name;
            Gun3BaseText.Text = $"base {RecoilEngine.Slots[2].Base:F1}";

            TuningGunTitle.Text = $"ปรับค่า · ช่อง {slot} ({RecoilEngine.CurrentGun.Name})";
        }

        bool suppressSliderChange = false;
        void UpdateSlidersUI()
        {
            suppressSliderChange = true;
            var gun = RecoilEngine.CurrentGun;
            BaseSlider.Value = gun.Base;
            BaseValText.Text = $"{gun.Base:F1}";
            XSlider.Value = gun.X;
            XValText.Text = $"{gun.X:F1}";
            MultiplierText.Text = $"{gun.Multiplier:F2}x {(gun.Multiplier == 1.0 ? "(ค่าเดิม)" : "(คูณ AI)")}";
            suppressSliderChange = false;
        }

        void PopOsd(string text)
        {
            var gun = RecoilEngine.CurrentGun;
            osd?.SetState(RecoilEngine.IsEnabled, RecoilEngine.ActiveSlot, gun.Name, $"base {gun.Base:F1} · x {gun.X:F1}");
            osd?.Pop(text);
        }

        // ---------------------------------------------------------------- การคลิกปรับค่า
        void Power_Click(object sender, MouseButtonEventArgs e) => RecoilEngine.Toggle();

        void SelectGun1(object sender, MouseButtonEventArgs e) => RecoilEngine.SetSlot(1);
        void SelectGun2(object sender, MouseButtonEventArgs e) => RecoilEngine.SetSlot(2);
        void SelectGun3(object sender, MouseButtonEventArgs e) => RecoilEngine.SetSlot(3);

        void BaseSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (suppressSliderChange) return;
            double v = Math.Round(e.NewValue * 2) / 2.0;
            RecoilEngine.CurrentGun.Base = v;
            BaseValText.Text = $"{v:F1}";
            SaveGunToConfig();
            UpdateGunCardsUI();
        }

        void XSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (suppressSliderChange) return;
            double v = Math.Round(e.NewValue * 2) / 2.0;
            RecoilEngine.CurrentGun.X = v;
            XValText.Text = $"{v:F1}";
            SaveGunToConfig();
        }

        void BaseMinus_Click(object sender, RoutedEventArgs e) => BaseSlider.Value = Math.Max(0, BaseSlider.Value - 0.5);
        void BasePlus_Click(object sender, RoutedEventArgs e) => BaseSlider.Value = Math.Min(40, BaseSlider.Value + 0.5);
        void XMinus_Click(object sender, RoutedEventArgs e) => XSlider.Value = Math.Max(-15, XSlider.Value - 0.5);
        void XPlus_Click(object sender, RoutedEventArgs e) => XSlider.Value = Math.Min(15, XSlider.Value + 0.5);

        void SaveGunToConfig()
        {
            cfg.Gun1Base = RecoilEngine.Slots[0].Base;
            cfg.Gun1X = RecoilEngine.Slots[0].X;
            cfg.Gun2Base = RecoilEngine.Slots[1].Base;
            cfg.Gun2X = RecoilEngine.Slots[1].X;
            cfg.Gun3Base = RecoilEngine.Slots[2].Base;
            cfg.Gun3X = RecoilEngine.Slots[2].X;
            cfg.Save();
        }

        // ---------------------------------------------------------------- AI Vision
        void Vision_Click(object sender, RoutedEventArgs e)
        {
            if (visionWin != null) { visionWin.Activate(); return; }
            visionWin = new VisionWindow(cfg, ApplyVisionResult) { Owner = this };
            visionWin.Closed += (s, a) => visionWin = null;
            visionWin.Show();
        }

        void ApplyVisionResult(int slotIdx, double b, double x, string summary)
        {
            int s = Math.Clamp(slotIdx, 1, 3);
            RecoilEngine.Slots[s - 1].Base = b;
            RecoilEngine.Slots[s - 1].X = x;
            SaveGunToConfig();

            UpdateGunCardsUI();
            UpdateSlidersUI();
            PopOsd($"AI: {summary}");
            Sfx.Play(Sfx.Pick, cfg.Sound);
        }

        async void TriggerVisionScan(bool manual)
        {
            if (!manual) await Task.Delay(250); // รอให้หน้ากระเป๋าเรนเดอร์จบ

            var rep = await VisionEngine.ScanAsync(cfg);
            if (visionWin != null && visionWin.IsVisible) visionWin.RenderReport(rep);

            if (rep != null && rep.Success && cfg.VisionAutoApply)
            {
                int target = (RecoilEngine.ActiveSlot == 2) ? 2 : 1;
                var res = target == 1 ? rep.Slot1 : rep.Slot2;
                if (res != null) ApplyVisionResult(target, res.FinalBase, res.FinalX, res.Summary);
            }
            else if (manual && rep != null && rep.Success)
            {
                PopOsd("AI: สแกนจอสำเร็จ");
                Sfx.Play(Sfx.Pick, cfg.Sound);
            }
        }

        // ---------------------------------------------------------------- คีย์ลัด
        void OnKeyDownHook(int vk)
        {
            if (capturingHotkey)
            {
                capturingHotkey = false;
                cfg.HotKey = vk;
                cfg.Save();
                HotkeyBtn.Content = KeyName(vk);
                return;
            }

            if (vk == cfg.HotKey) { RecoilEngine.Toggle(); return; }
            if (vk == cfg.PresetKey) { CyclePreset(); return; }
            if (vk == cfg.VisionKey) { TriggerVisionScan(true); return; }

            // สลับปืนตามปุ่มเลข 1, 2, 3
            if (cfg.SlotSwitch > 0)
            {
                if (vk == 0x31) RecoilEngine.SetSlot(1);
                else if (vk == 0x32) RecoilEngine.SetSlot(2);
                else if (vk == 0x33) RecoilEngine.SetSlot(3);
            }
        }

        void OnKeyUpHook(int vk)
        {
            // ปล่อยปุ่ม Tab (0x09) เพื่อสแกนกระเป๋าออโต้
            if (vk == 0x09 && cfg.VisionAuto && IsGameRunning())
            {
                TriggerVisionScan(false);
            }
        }

        void Hotkey_Click(object sender, RoutedEventArgs e)
        {
            capturingHotkey = true;
            HotkeyBtn.Content = "...";
        }

        string KeyName(int vk) => vk switch
        {
            192 => "`",
            0x76 => "F7",
            0x77 => "F8",
            0x78 => "F9",
            0x70 => "F1",
            _ => ((Key)KeyInterop.KeyFromVirtualKey(vk)).ToString()
        };

        // ---------------------------------------------------------------- การตั้งค่าอื่นๆ
        void RequireAds_Click(object sender, RoutedEventArgs e)
        {
            cfg.RequireADS = RequireAdsToggle.IsChecked == true;
            RecoilEngine.RequireADS = cfg.RequireADS;
            cfg.Save();
            UpdatePowerUI();
        }

        void Humanize_Click(object sender, RoutedEventArgs e)
        {
            cfg.Humanize = HumanizeToggle.IsChecked == true;
            RecoilEngine.HumanizeJitter = cfg.Humanize;
            cfg.Save();
        }

        void Sound_Click(object sender, RoutedEventArgs e)
        {
            cfg.Sound = SoundToggle.IsChecked == true;
            cfg.Save();
        }

        void AutoStart_Click(object sender, RoutedEventArgs e)
        {
            bool en = AutoStartToggle.IsChecked == true;
            AutoStart.Set(en);
        }

        void SlotSwitch_Click(object sender, RoutedEventArgs e)
        {
            cfg.SlotSwitch = (cfg.SlotSwitch + 1) % 3;
            cfg.Save();
            SlotSwitchBtn.Content = cfg.SlotSwitch switch
            {
                1 => "ปุ่ม 1 · 2",
                2 => "ปุ่ม 1 · 2 + ลูกกลิ้ง",
                _ => "ปิด"
            };
        }

        void Osd_Click(object sender, RoutedEventArgs e)
        {
            cfg.OsdMode = (cfg.OsdMode + 1) % 3;
            cfg.Save();
            osd.Configure(cfg.OsdMode, cfg.OsdCorner);
            OsdBtn.Content = cfg.OsdMode switch
            {
                1 => "ตอนเปลี่ยน",
                2 => "ตลอด",
                _ => "ปิด"
            };
        }

        void Res_Click(object sender, RoutedEventArgs e)
        {
            cfg.AutoRes = !cfg.AutoRes;
            cfg.Save();
            ResBtn.Content = cfg.AutoRes ? "1728×1080 ในเกม" : "ปิด";
        }

        // ---------------------------------------------------------------- โปรไฟล์ Presets
        void CyclePreset()
        {
            var list = Preset.ListAll();
            if (list.Count == 0) return;
            int idx = list.IndexOf(cfg.Preset);
            string next = list[(idx + 1) % list.Count];
            LoadPresetByName(next);
        }

        void LoadPresetByName(string name)
        {
            var p = Preset.Load(name);
            if (p == null || p.Guns.Count == 0) return;

            cfg.Preset = name;
            for (int i = 0; i < Math.Min(p.Guns.Count, 3); i++)
            {
                RecoilEngine.Slots[i].Name = p.Guns[i].Name;
                RecoilEngine.Slots[i].Base = p.Guns[i].Base;
                RecoilEngine.Slots[i].X = p.Guns[i].X;
                RecoilEngine.Slots[i].Multiplier = p.Guns[i].Multiplier > 0 ? p.Guns[i].Multiplier : 1.0;
                var st = p.Guns[i].StepArray();
                if (st != null) RecoilEngine.Slots[i].Steps = st;
            }
            SaveGunToConfig();
            UpdateAllUI();
            PopOsd($"โหลดโปรไฟล์: {name}");
            Sfx.Play(Sfx.Pick, cfg.Sound);
        }

        void PresetMenu_Click(object sender, RoutedEventArgs e)
        {
            var list = Preset.ListAll();
            var cm = new ContextMenu();
            foreach (var name in list)
            {
                var mi = new MenuItem { Header = name };
                string n = name;
                mi.Click += (s, a) => LoadPresetByName(n);
                cm.Items.Add(mi);
            }
            cm.IsOpen = true;
        }

        void SavePreset_Click(object sender, RoutedEventArgs e)
        {
            Preset.Save(cfg.Preset, RecoilEngine.Slots);
            PopOsd($"บันทึกโปรไฟล์: {cfg.Preset} สำเร็จ");
            Sfx.Play(Sfx.Pick, cfg.Sound);
        }

        void ResetPreset_Click(object sender, RoutedEventArgs e)
        {
            RecoilEngine.Slots[0].Base = 5.0;
            RecoilEngine.Slots[0].X = 0.0;
            RecoilEngine.Slots[0].Multiplier = 1.0;
            RecoilEngine.Slots[0].Steps = RecoilEngine.DefaultM416Curve();

            RecoilEngine.Slots[1].Base = 15.5;
            RecoilEngine.Slots[1].X = 0.0;
            RecoilEngine.Slots[1].Multiplier = 1.0;
            RecoilEngine.Slots[1].Steps = RecoilEngine.DefaultBerylCurve();

            RecoilEngine.Slots[2].Base = 6.0;
            RecoilEngine.Slots[2].X = 0.0;
            RecoilEngine.Slots[2].Multiplier = 1.0;
            RecoilEngine.Slots[2].Steps = RecoilEngine.DefaultAugCurve();

            SaveGunToConfig();
            UpdateAllUI();
            PopOsd("รีเซ็ตเป็นค่าเริ่มต้นแล้ว");
            Sfx.Play(Sfx.Pick, cfg.Sound);
        }

        // ---------------------------------------------------------------- ตรวจจับเกม PUBG
        DispatcherTimer gameTimer;
        bool gameRunningBefore = false;

        static bool IsGameRunning()
        {
            var p = Process.GetProcessesByName("TslGame");
            bool run = p.Length > 0;
            foreach (var item in p) item.Dispose();
            return run;
        }

        void StartGameWatcher()
        {
            gameTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
            gameTimer.Tick += (s, e) =>
            {
                bool running = IsGameRunning();
                if (running != gameRunningBefore)
                {
                    gameRunningBefore = running;
                    if (running && cfg.AutoRes) Display.Switch(cfg.GameResW, cfg.GameResH);
                    else if (!running && cfg.AutoRes) Display.Restore();
                }
            };
            gameTimer.Start();
        }

        // ---------------------------------------------------------------- หน้าต่าง
        void Title_Drag(object sender, MouseButtonEventArgs e)
        {
            if (e.ButtonState == MouseButtonState.Pressed) DragMove();
        }

        void Min_Click(object sender, RoutedEventArgs e) => Hide();
        void Close_Click(object sender, RoutedEventArgs e) => Close();

        protected override void OnClosing(CancelEventArgs e)
        {
            RecoilEngine.Stop();
            hook?.Dispose();
            osd?.Close();
            visionWin?.Close();
            if (tray != null) { tray.Visible = false; tray.Dispose(); }
            if (cfg.AutoRes && gameRunningBefore) Display.Restore();

            if (!double.IsNaN(Left) && !double.IsNaN(Top))
            {
                cfg.Left = Left;
                cfg.Top = Top;
                cfg.Save();
            }
            base.OnClosing(e);
        }
    }
}
