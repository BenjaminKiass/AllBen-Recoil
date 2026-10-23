using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Media;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Windows.Media.Imaging;
using Microsoft.Win32;

namespace AllBenRecoil
{
    public class Settings
    {
        public bool Sound { get; set; } = true;
        public int HotKey { get; set; } = 192;      // ` (Tilde / Grave)
        public int Gun { get; set; } = 1;
        public bool TopMost { get; set; } = false;
        public int OsdMode { get; set; } = 1;       // 0 ปิด, 1 ตอนเปลี่ยน, 2 ตลอด
        public int OsdCorner { get; set; } = 0;     // 0 ซ้ายบน
        public string Preset { get; set; } = "M416 เลเซอร์";
        public int PresetKey { get; set; } = 0x78;  // F9
        public bool AutoRes { get; set; } = true;   // จอยืด 1728x1080
        public int GameResW { get; set; } = 1728;
        public int GameResH { get; set; } = 1080;
        public bool ResAlways { get; set; } = false;
        public int SlotSwitch { get; set; } = 2;    // 0 ปิด, 1 ปุ่ม 1/2, 2 ปุ่ม 1/2 + ลูกกลิ้ง
        public bool RequireADS { get; set; } = true; // ดึงเฉพาะตอนเล็งคลิกขวา
        public bool Humanize { get; set; } = true;  // ไมโครจิสเตอร์เลียนแบบมือคน
        public bool VisionAuto { get; set; } = true;
        public bool VisionAutoApply { get; set; } = true;
        public int VisionKey { get; set; } = 0x76;  // F7
        public string VisionRes { get; set; } = "1728x1080";
        public int VisionOffsetX { get; set; } = 0;
        public int VisionOffsetY { get; set; } = 0;
        public double? Left { get; set; } = 150.0;
        public double? Top { get; set; } = 100.0;

        public double Gun1Base { get; set; } = 5.0;
        public double Gun1X { get; set; } = 0.0;
        public double Gun2Base { get; set; } = 15.5;
        public double Gun2X { get; set; } = 0.0;
        public double Gun3Base { get; set; } = 6.0;
        public double Gun3X { get; set; } = 0.0;

        public string[] Names { get; set; } = { "M416 เลเซอร์", "Beryl M762", "AUG / ACE32" };

        public static readonly string FilePath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "AllBenRecoil", "settings.json");

        public static readonly string KitDir = Path.Combine(AppContext.BaseDirectory, "saved");
        static readonly string KitPath = Path.Combine(KitDir, "settings.json");

        public static Settings Load()
        {
            foreach (var p in new[] { KitPath, FilePath })
            {
                try
                {
                    if (!File.Exists(p)) continue;
                    var s = JsonSerializer.Deserialize<Settings>(File.ReadAllText(p));
                    if (s.Names == null || s.Names.Length < 3) s.Names = new Settings().Names;
                    return s;
                }
                catch { }
            }
            return new Settings();
        }

        public void Save()
        {
            var opt = new JsonSerializerOptions { WriteIndented = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
            string text = JsonSerializer.Serialize(this, opt);
            foreach (var p in new[] { KitPath, FilePath })
            {
                try
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(p));
                    SafeFile.Write(p, text);
                }
                catch (Exception ex) { Log.Error("save settings " + p, ex); }
            }
        }
    }

    public static class SafeFile
    {
        public static void Write(string path, string text, System.Text.Encoding enc = null)
        {
            string tmp = path + ".tmp";
            enc ??= new System.Text.UTF8Encoding(false);
            File.WriteAllText(tmp, text, enc);
            try { File.Move(tmp, path, true); }
            catch (IOException)
            {
                File.WriteAllText(path, text, enc);
                try { File.Delete(tmp); } catch { }
            }
        }
    }

    public static class Log
    {
        static readonly string FilePath = Path.Combine(Settings.KitDir, "log.txt");
        static readonly object gate = new();

        public static void Info(string msg) => Append("INFO", msg);
        public static void Warn(string msg) => Append("WARN", msg);
        public static void Error(string msg, Exception ex = null) =>
            Append("ERR ", ex == null ? msg : $"{msg}: {ex.GetType().Name} - {ex.Message}\n{ex.StackTrace}");

        static void Append(string level, string msg)
        {
            try
            {
                lock (gate)
                {
                    Directory.CreateDirectory(Settings.KitDir);
                    string line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}  [{level}]  {msg}\r\n";
                    File.AppendAllText(FilePath, line);
                }
            }
            catch { }
        }
    }

    public static class AutoStart
    {
        const string Key = @"Software\Microsoft\Windows\CurrentVersion\Run";
        const string Name = "AllBenRecoil";

        public static bool IsEnabled()
        {
            try
            {
                using var k = Registry.CurrentUser.OpenSubKey(Key, false);
                return k?.GetValue(Name) != null;
            }
            catch { return false; }
        }

        public static void Set(bool enable)
        {
            try
            {
                using var k = Registry.CurrentUser.OpenSubKey(Key, true);
                if (k == null) return;
                if (enable)
                {
                    string path = Environment.ProcessPath;
                    k.SetValue(Name, $"\"{path}\" --tray");
                }
                else
                {
                    k.DeleteValue(Name, false);
                }
            }
            catch (Exception ex) { Log.Error("autostart set", ex); }
        }
    }

    public static class Sfx
    {
        public static readonly byte[] On = MakeTone(880, 0.08, 0.25);
        public static readonly byte[] Off = MakeTone(440, 0.08, 0.25);
        public static readonly byte[] Pick = MakeChord(new[] { 660.0, 990.0 }, 0.07, 0.2);
        public static readonly byte[] Ready = MakeChord(new[] { 523.25, 659.25, 783.99 }, 0.12, 0.25);

        public static void Play(byte[] wav, bool soundEnabled)
        {
            if (!soundEnabled || wav == null) return;
            try
            {
                using var ms = new MemoryStream(wav);
                using var sp = new SoundPlayer(ms);
                sp.Play();
            }
            catch { }
        }

        static byte[] MakeTone(double freq, double sec, double vol)
        {
            int rate = 22050;
            int samples = (int)(rate * sec);
            short[] buf = new short[samples];
            for (int i = 0; i < samples; i++)
            {
                double env = Math.Sin(Math.PI * i / samples);
                buf[i] = (short)(Math.Sin(2 * Math.PI * freq * i / rate) * vol * env * 32767);
            }
            return EncodeWav(buf, rate);
        }

        static byte[] MakeChord(double[] freqs, double sec, double vol)
        {
            int rate = 22050;
            int samples = (int)(rate * sec);
            short[] buf = new short[samples];
            for (int i = 0; i < samples; i++)
            {
                double env = Math.Sin(Math.PI * i / samples);
                double sum = 0;
                foreach (var f in freqs) sum += Math.Sin(2 * Math.PI * f * i / rate);
                buf[i] = (short)((sum / freqs.Length) * vol * env * 32767);
            }
            return EncodeWav(buf, rate);
        }

        static byte[] EncodeWav(short[] samples, int rate)
        {
            using var ms = new MemoryStream();
            using var w = new BinaryWriter(ms);
            int dataLen = samples.Length * 2;
            w.Write("RIFF".ToCharArray());
            w.Write(36 + dataLen);
            w.Write("WAVEfmt ".ToCharArray());
            w.Write(16);
            w.Write((short)1);
            w.Write((short)1);
            w.Write(rate);
            w.Write(rate * 2);
            w.Write((short)2);
            w.Write((short)16);
            w.Write("data".ToCharArray());
            w.Write(dataLen);
            foreach (var s in samples) w.Write(s);
            return ms.ToArray();
        }
    }

    public static class TrayArt
    {
        public static Icon Make(bool on, int gun, out Icon winIcon)
        {
            using var bmp = new Bitmap(32, 32);
            using (var g = Graphics.FromImage(bmp))
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.Clear(Color.Transparent);

                Color c1 = on ? Color.FromArgb(0x1D, 0x6B, 0xFF) : Color.FromArgb(0x3B, 0x48, 0x61);
                Color c2 = on ? Color.FromArgb(0x5C, 0xE1, 0xFF) : Color.FromArgb(0x6A, 0x7B, 0x9B);

                using var br = new LinearGradientBrush(new Rectangle(0, 0, 32, 32), c1, c2, 45);
                g.FillEllipse(br, 3, 3, 26, 26);

                using var pen = new Pen(Color.FromArgb(0xFF, 0xD7, 0x00), 2);
                g.DrawEllipse(pen, 5, 5, 22, 22);

                string txt = gun.ToString();
                using var font = new Font("Segoe UI", 12, FontStyle.Bold);
                using var textBrush = new SolidBrush(Color.White);
                var sf = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
                g.DrawString(txt, font, textBrush, new RectangleF(0, 0, 32, 32), sf);
            }

            IntPtr hIcon = bmp.GetHicon();
            Icon icon = Icon.FromHandle(hIcon);
            winIcon = (Icon)icon.Clone();
            return icon;
        }
    }

    public static class Branding
    {
        public static BitmapSource LogoImage()
        {
            string p = Path.Combine(AppContext.BaseDirectory, "logo.png");
            if (File.Exists(p))
            {
                try { return new BitmapImage(new Uri(p)); } catch { }
            }
            try
            {
                using var s = Assembly.GetExecutingAssembly().GetManifestResourceStream("logo.png");
                if (s != null)
                {
                    var bi = new BitmapImage();
                    bi.BeginInit();
                    bi.StreamSource = s;
                    bi.CacheOption = BitmapCacheOption.OnLoad;
                    bi.EndInit();
                    return bi;
                }
            }
            catch { }
            return null;
        }
    }
}
