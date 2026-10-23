using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media.Imaging;

namespace AllBenRecoil
{
    // =========================================================================
    //  Vision: ระบบ AI ตรวจจับปืน ของแต่ง และลำกล้อง จากหน้ากระเป๋า (Tab) ใน PUBG
    // =========================================================================

    public static class GdiHelper
    {
        [DllImport("gdi32.dll")] public static extern bool DeleteObject(IntPtr hObject);

        public static BitmapSource ToBitmapSource(Bitmap bmp)
        {
            if (bmp == null) return null;
            IntPtr hBmp = bmp.GetHbitmap();
            try
            {
                return System.Windows.Interop.Imaging.CreateBitmapSourceFromHBitmap(
                    hBmp, IntPtr.Zero, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
            }
            finally
            {
                DeleteObject(hBmp);
            }
        }
    }

    public class ColorSig
    {
        public double R { get; set; }
        public double G { get; set; }
        public double B { get; set; }
        public double Brightness { get; set; }
        public double Variance { get; set; }
        public bool IsEmpty { get; set; }
    }

    public static class ImageFeatures
    {
        // คำนวณ 64-bit dHash (Difference Hash) บนภาพ grayscale 9x8
        public static ulong ComputeDHash(Bitmap src)
        {
            if (src == null) return 0;
            using var small = new Bitmap(9, 8, PixelFormat.Format32bppArgb);
            using (var g = Graphics.FromImage(small))
            {
                g.InterpolationMode = InterpolationMode.HighQualityBilinear;
                g.DrawImage(src, 0, 0, 9, 8);
            }

            // อ่าน grayscale
            byte[] gray = new byte[72];
            for (int y = 0; y < 8; y++)
            {
                for (int x = 0; x < 9; x++)
                {
                    var c = small.GetPixel(x, y);
                    gray[y * 9 + x] = (byte)(0.299 * c.R + 0.587 * c.G + 0.114 * c.B);
                }
            }

            ulong hash = 0;
            int bit = 0;
            for (int y = 0; y < 8; y++)
            {
                for (int x = 0; x < 8; x++)
                {
                    int left = gray[y * 9 + x];
                    int right = gray[y * 9 + x + 1];
                    if (left > right) hash |= (1UL << bit);
                    bit++;
                }
            }
            return hash;
        }

        // วิเคราะห์สี ความสว่าง และความแปรปรวน (เช็คว่าช่องว่างเปล่าหรือไม่)
        public static ColorSig AnalyzeColor(Bitmap src)
        {
            if (src == null) return new ColorSig { IsEmpty = true };
            double sumR = 0, sumG = 0, sumB = 0, sumBri = 0;
            int total = 0;

            // sampling กึ่งกลางภาพ 60% ป้องกันขอบกรอบ
            int startX = (int)(src.Width * 0.2);
            int endX = (int)(src.Width * 0.8);
            int startY = (int)(src.Height * 0.2);
            int endY = (int)(src.Height * 0.8);

            var brightnesses = new List<double>();
            for (int y = startY; y < endY; y += 2)
            {
                for (int x = startX; x < endX; x += 2)
                {
                    var c = src.GetPixel(x, y);
                    sumR += c.R;
                    sumG += c.G;
                    sumB += c.B;
                    double bri = 0.299 * c.R + 0.587 * c.G + 0.114 * c.B;
                    sumBri += bri;
                    brightnesses.Add(bri);
                    total++;
                }
            }

            if (total == 0) return new ColorSig { IsEmpty = true };
            double avgR = sumR / total;
            double avgG = sumG / total;
            double avgB = sumB / total;
            double avgBri = sumBri / total;

            // ความแปรปรวนของแสง (ช่องว่างมักมืดสม่ำเสมอ หรือความแปรปรวนต่ำมาก)
            double variance = brightnesses.Average(b => Math.Pow(b - avgBri, 2));

            // ช่องว่างใน PUBG มักมีค่าความสว่างเฉลี่ย < 28 และความแปรปรวน < 45
            bool isEmpty = avgBri < 26.0 && variance < 50.0;

            return new ColorSig
            {
                R = avgR,
                G = avgG,
                B = avgB,
                Brightness = avgBri,
                Variance = variance,
                IsEmpty = isEmpty
            };
        }

        public static int HammingDistance(ulong a, ulong b) =>
            BitOperations.PopCount(a ^ b);
    }

    // =========================================================================
    //  ฐานข้อมูลปืน ของแต่ง และลำกล้อง พร้อมตัวคูณแรงดีด
    // =========================================================================

    public class WeaponDef
    {
        public string Id { get; set; }
        public string Name { get; set; }
        public double BaseRecoil { get; set; }
        public double DefaultX { get; set; }
        public bool HasMuzzle { get; set; } = true;
        public bool HasGrip { get; set; } = true;
        public bool HasStock { get; set; }
        public bool HasScope { get; set; } = true;
    }

    public class AttachmentDef
    {
        public string Id { get; set; }
        public string Name { get; set; }
        public double MultY { get; set; }
        public double MultX { get; set; }
    }

    public static class PUBGData
    {
        public static readonly Dictionary<string, WeaponDef> Weapons = new(StringComparer.OrdinalIgnoreCase)
        {
            ["M416"] = new WeaponDef { Id = "M416", Name = "M416", BaseRecoil = 7.0, DefaultX = 0, HasStock = true },
            ["Beryl"] = new WeaponDef { Id = "Beryl", Name = "Beryl M762", BaseRecoil = 11.5, DefaultX = 0, HasStock = false },
            ["AUG"] = new WeaponDef { Id = "AUG", Name = "AUG", BaseRecoil = 7.5, DefaultX = 0, HasStock = false },
            ["ACE32"] = new WeaponDef { Id = "ACE32", Name = "ACE32", BaseRecoil = 8.5, DefaultX = 0, HasStock = true },
            ["SCAR-L"] = new WeaponDef { Id = "SCAR-L", Name = "SCAR-L", BaseRecoil = 6.5, DefaultX = 0, HasStock = false },
            ["AKM"] = new WeaponDef { Id = "AKM", Name = "AKM", BaseRecoil = 9.5, DefaultX = 0, HasGrip = false, HasStock = false },
            ["MP5K"] = new WeaponDef { Id = "MP5K", Name = "MP5K", BaseRecoil = 5.5, DefaultX = 0, HasStock = true },
            ["Vector"] = new WeaponDef { Id = "Vector", Name = "Vector", BaseRecoil = 6.0, DefaultX = 0, HasStock = true },
            ["UMP45"] = new WeaponDef { Id = "UMP45", Name = "UMP45", BaseRecoil = 4.5, DefaultX = 0, HasStock = false },
            ["Mini14"] = new WeaponDef { Id = "Mini14", Name = "Mini14", BaseRecoil = 4.0, DefaultX = 0, HasGrip = false, HasStock = false },
            ["SKS"] = new WeaponDef { Id = "SKS", Name = "SKS", BaseRecoil = 7.0, DefaultX = 0, HasStock = true },
            ["SLR"] = new WeaponDef { Id = "SLR", Name = "SLR", BaseRecoil = 9.0, DefaultX = 0, HasGrip = false, HasStock = true },
            ["Mk12"] = new WeaponDef { Id = "Mk12", Name = "Mk12", BaseRecoil = 4.5, DefaultX = 0, HasStock = false }
        };

        public static readonly Dictionary<string, AttachmentDef> Muzzles = new(StringComparer.OrdinalIgnoreCase)
        {
            ["None"] = new AttachmentDef { Id = "None", Name = "ไม่มี", MultY = 1.0, MultX = 1.0 },
            ["Compensator"] = new AttachmentDef { Id = "Compensator", Name = "Compensator (คอมเพน)", MultY = 0.85, MultX = 0.90 },
            ["MuzzleBrake"] = new AttachmentDef { Id = "MuzzleBrake", Name = "Muzzle Brake (เบรก)", MultY = 0.90, MultX = 0.90 },
            ["FlashHider"] = new AttachmentDef { Id = "FlashHider", Name = "Flash Hider (ลดแสง)", MultY = 0.90, MultX = 0.90 },
            ["Suppressor"] = new AttachmentDef { Id = "Suppressor", Name = "Suppressor (เก็บเสียง)", MultY = 1.00, MultX = 1.00 }
        };

        public static readonly Dictionary<string, AttachmentDef> Grips = new(StringComparer.OrdinalIgnoreCase)
        {
            ["None"] = new AttachmentDef { Id = "None", Name = "ไม่มี", MultY = 1.0, MultX = 1.0 },
            ["Vertical"] = new AttachmentDef { Id = "Vertical", Name = "Vertical Grip (กริปตรง)", MultY = 0.85, MultX = 1.00 },
            ["Half"] = new AttachmentDef { Id = "Half", Name = "Half Grip (กริปส้ม)", MultY = 0.92, MultX = 0.92 },
            ["Angled"] = new AttachmentDef { Id = "Angled", Name = "Angled Grip (กริปสามเหลี่ยม)", MultY = 1.00, MultX = 0.85 },
            ["Thumb"] = new AttachmentDef { Id = "Thumb", Name = "Thumb Grip (กริปนิ้วโป้ง)", MultY = 0.95, MultX = 1.00 },
            ["Light"] = new AttachmentDef { Id = "Light", Name = "Light Grip (กริปแดง)", MultY = 0.98, MultX = 1.00 },
            ["Laser"] = new AttachmentDef { Id = "Laser", Name = "Laser Sight (เลเซอร์)", MultY = 1.00, MultX = 1.00 }
        };

        public static readonly Dictionary<string, AttachmentDef> Stocks = new(StringComparer.OrdinalIgnoreCase)
        {
            ["None"] = new AttachmentDef { Id = "None", Name = "ไม่มี", MultY = 1.0, MultX = 1.0 },
            ["Tactical"] = new AttachmentDef { Id = "Tactical", Name = "Tactical Stock (พานท้ายแทค)", MultY = 0.95, MultX = 0.95 },
            ["Heavy"] = new AttachmentDef { Id = "Heavy", Name = "Heavy Stock (พานท้ายหนา)", MultY = 0.95, MultX = 0.95 },
            ["CheekPad"] = new AttachmentDef { Id = "CheekPad", Name = "Cheek Pad (รองแก้ม)", MultY = 0.95, MultX = 0.95 }
        };

        public static readonly Dictionary<string, AttachmentDef> Scopes = new(StringComparer.OrdinalIgnoreCase)
        {
            ["None"] = new AttachmentDef { Id = "None", Name = "ศูนย์เปิด / ไม่ติด", MultY = 1.0, MultX = 1.0 },
            ["RedDot"] = new AttachmentDef { Id = "RedDot", Name = "Red Dot", MultY = 1.0, MultX = 1.0 },
            ["Holo"] = new AttachmentDef { Id = "Holo", Name = "Holographic", MultY = 1.0, MultX = 1.0 },
            ["2x"] = new AttachmentDef { Id = "2x", Name = "สโคป 2x", MultY = 1.20, MultX = 1.0 },
            ["3x"] = new AttachmentDef { Id = "3x", Name = "สโคป 3x", MultY = 1.40, MultX = 1.0 },
            ["4x"] = new AttachmentDef { Id = "4x", Name = "สโคป 4x", MultY = 1.65, MultX = 1.0 },
            ["6x"] = new AttachmentDef { Id = "6x", Name = "สโคป 6x", MultY = 2.05, MultX = 1.0 }
        };

        // คำนวณแรงดีดสุทธิ (ปัดทีละ 0.5 ตามที่ LiveLink รับได้)
        public static (double Base, double X) CalculateRecoil(
            double initialBase, double initialX,
            string muzzle, string grip, string stock, string scope)
        {
            double mY = Muzzles.TryGetValue(muzzle ?? "None", out var m) ? m.MultY : 1.0;
            double mX = m != null ? m.MultX : 1.0;

            double gY = Grips.TryGetValue(grip ?? "None", out var g) ? g.MultY : 1.0;
            double gX = g != null ? g.MultX : 1.0;

            double sY = Stocks.TryGetValue(stock ?? "None", out var s) ? s.MultY : 1.0;
            double sX = s != null ? s.MultX : 1.0;

            double scY = Scopes.TryGetValue(scope ?? "None", out var sc) ? sc.MultY : 1.0;
            double scX = sc != null ? sc.MultX : 1.0;

            double finalBase = initialBase * mY * gY * sY * scY;
            double finalX = initialX * mX * gX * sX;

            double snappedBase = Math.Max(0, Math.Round(finalBase * 2.0) / 2.0);
            double snappedX = Math.Round(finalX * 2.0) / 2.0;

            return (snappedBase, snappedX);
        }
    }

    // =========================================================================
    //  พิกัดช่องอุปกรณ์บนจอ (รองรับ 1728x1080 และ 1920x1080 + ปรับ Offset ±px)
    // =========================================================================

    public class SlotBox
    {
        public System.Drawing.Rectangle Weapon { get; set; }
        public System.Drawing.Rectangle Scope { get; set; }
        public System.Drawing.Rectangle Muzzle { get; set; }
        public System.Drawing.Rectangle Grip { get; set; }
        public System.Drawing.Rectangle Mag { get; set; }
        public System.Drawing.Rectangle Stock { get; set; }
    }

    public static class InventoryLayout
    {
        public static (SlotBox Slot1, SlotBox Slot2) GetLayout(string res, int offX, int offY)
        {
            // พิกัดฐานอิงตามความละเอียดแนวตั้ง 1080p ในหน้ากระเป๋า (Tab) ของ PUBG
            // ใน 1728x1080 (จอยืด): ช่องอาวุธ 1 และ 2 จะอยู่ขวาของจอ
            int base1X = res == "1920x1080" ? 1290 : 1180;
            int base1Y = 200;

            int base2X = base1X;
            int base2Y = 475;

            // นำ offset มารวม
            base1X += offX;
            base1Y += offY;
            base2X += offX;
            base2Y += offY;

            SlotBox BuildSlot(int bx, int by) => new SlotBox
            {
                // ตัวปืน (ไอคอน/ชื่อ)
                Weapon = new System.Drawing.Rectangle(bx, by, 220, 60),
                // ลำกล้อง (Scope) ด้านบนขวาของช่องปืน
                Scope  = new System.Drawing.Rectangle(bx + 225, by + 10, 48, 48),
                // ปลอก/คอมเพน (Muzzle) ซ้ายสุดใต้ลำกล้อง
                Muzzle = new System.Drawing.Rectangle(bx + 10, by + 68, 46, 46),
                // ด้ามจับ (Grip) ถัดจากปลอก
                Grip   = new System.Drawing.Rectangle(bx + 65, by + 68, 46, 46),
                // แม็กกาซีน (Mag)
                Mag    = new System.Drawing.Rectangle(bx + 120, by + 68, 46, 46),
                // พานท้าย (Stock) ขวาสุด
                Stock  = new System.Drawing.Rectangle(bx + 225, by + 68, 46, 46),
            };

            return (BuildSlot(base1X, base1Y), BuildSlot(base2X, base2Y));
        }
    }

    // =========================================================================
    //  ระบบจัดการแม่แบบ (Template Manager & Learner)
    // =========================================================================

    public class ItemTemplate
    {
        public string Category { get; set; }   // Weapon, Muzzle, Grip, Stock, Scope
        public string ItemId { get; set; }     // M416, Compensator, Vertical, etc.
        public ulong DHash { get; set; }
        public double AvgR { get; set; }
        public double AvgG { get; set; }
        public double AvgB { get; set; }
        public double Brightness { get; set; }
        public string ImageFile { get; set; }
    }

    public static class TemplateManager
    {
        static readonly string TemplateDir = Path.Combine(Settings.KitDir, "vision", "templates");
        static readonly string JsonFile = Path.Combine(TemplateDir, "templates.json");
        static readonly string ImageDir = Path.Combine(TemplateDir, "images");

        static List<ItemTemplate> customTemplates = new();
        static readonly object gate = new();

        static TemplateManager()
        {
            LoadCustom();
        }

        public static void LoadCustom()
        {
            lock (gate)
            {
                try
                {
                    if (File.Exists(JsonFile))
                    {
                        string json = File.ReadAllText(JsonFile);
                        customTemplates = JsonSerializer.Deserialize<List<ItemTemplate>>(json) ?? new();
                    }
                }
                catch (Exception ex)
                {
                    Log.Error("load templates", ex);
                }
            }
        }

        public static void SaveTemplate(string category, string itemId, Bitmap iconBmp)
        {
            lock (gate)
            {
                try
                {
                    Directory.CreateDirectory(TemplateDir);
                    Directory.CreateDirectory(ImageDir);

                    ulong hash = ImageFeatures.ComputeDHash(iconBmp);
                    var color = ImageFeatures.AnalyzeColor(iconBmp);

                    string fileName = $"{category}_{itemId}.png";
                    string filePath = Path.Combine(ImageDir, fileName);
                    iconBmp.Save(filePath, ImageFormat.Png);

                    customTemplates.RemoveAll(t => t.Category.Equals(category, StringComparison.OrdinalIgnoreCase) &&
                                                   t.ItemId.Equals(itemId, StringComparison.OrdinalIgnoreCase));

                    customTemplates.Add(new ItemTemplate
                    {
                        Category = category,
                        ItemId = itemId,
                        DHash = hash,
                        AvgR = color.R,
                        AvgG = color.G,
                        AvgB = color.B,
                        Brightness = color.Brightness,
                        ImageFile = fileName
                    });

                    var opt = new JsonSerializerOptions { WriteIndented = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
                    File.WriteAllText(JsonFile, JsonSerializer.Serialize(customTemplates, opt));
                    Log.Info($"saved custom vision template: {category} - {itemId}");
                }
                catch (Exception ex)
                {
                    Log.Error("save vision template", ex);
                }
            }
        }

        // จับคู่ภาพไอคอนกับแม่แบบที่บันทึกไว้ หรือใช้ Fallback Heuristic
        public static (string ItemId, double Confidence) Match(string category, Bitmap iconBmp)
        {
            if (iconBmp == null) return ("None", 1.0);
            var color = ImageFeatures.AnalyzeColor(iconBmp);
            if (color.IsEmpty) return ("None", 0.95);

            ulong hash = ImageFeatures.ComputeDHash(iconBmp);

            lock (gate)
            {
                // 1) ตรวจสอบกับแม่แบบที่ผู้ใช้เซฟไว้ก่อน (แม่นที่สุด)
                ItemTemplate best = null;
                int minDistance = int.MaxValue;

                foreach (var t in customTemplates.Where(x => x.Category.Equals(category, StringComparison.OrdinalIgnoreCase)))
                {
                    int dist = ImageFeatures.HammingDistance(hash, t.DHash);
                    if (dist < minDistance)
                    {
                        minDistance = dist;
                        best = t;
                    }
                }

                // ถ้า dHash ต่างกันไม่เกิน 14 บิต (จาก 64) = แม่แบบตรงกัน
                if (best != null && minDistance <= 14)
                {
                    double conf = Math.Max(0.5, 1.0 - (minDistance / 64.0));
                    return (best.ItemId, conf);
                }
            }

            // 2) Fallback Color/Feature Heuristic ถ้ายังไม่มีแม่แบบ
            return MatchFallback(category, hash, color);
        }

        static (string ItemId, double Confidence) MatchFallback(string category, ulong hash, ColorSig color)
        {
            switch (category.ToLowerInvariant())
            {
                case "muzzle":
                    // Compensator vs Suppressor vs Muzzle Brake
                    if (color.Brightness > 75) return ("Compensator", 0.70);
                    if (color.Brightness > 50) return ("MuzzleBrake", 0.65);
                    return ("FlashHider", 0.60);

                case "grip":
                    // Half Grip (กริปส้ม - R สูงกว่า B มาก)
                    if (color.R > color.B + 30 && color.R > 65) return ("Half", 0.85);
                    // Light Grip (แดง/เหลืองสว่าง)
                    if (color.R > color.B + 45) return ("Light", 0.80);
                    // Vertical Grip (ยาว มืด)
                    if (color.Brightness < 55) return ("Vertical", 0.75);
                    // Thumb / Angled
                    return ("Thumb", 0.65);

                case "stock":
                    if (color.Brightness > 45) return ("Tactical", 0.75);
                    return ("Heavy", 0.65);

                case "scope":
                    // Red Dot / Holo / 2x / 3x / 4x / 6x
                    if (color.Brightness < 35) return ("RedDot", 0.70);
                    if (color.Brightness > 65) return ("3x", 0.65);
                    return ("2x", 0.60);

                case "weapon":
                    // ปืนยอดนิยม
                    if (color.R > color.B + 15) return ("Beryl", 0.70);
                    return ("M416", 0.75);

                default:
                    return ("None", 0.50);
            }
        }
    }

    // =========================================================================
    //  ผลลัพธ์การสแกน (Detection Report)
    // =========================================================================

    public class SlotResult
    {
        public int SlotIndex { get; set; }     // 1 หรือ 2
        public string WeaponName { get; set; } = "M416";
        public string Muzzle { get; set; } = "None";
        public string Grip { get; set; } = "None";
        public string Stock { get; set; } = "None";
        public string Scope { get; set; } = "None";

        public double InitialBase { get; set; } = 7.0;
        public double InitialX { get; set; } = 0.0;
        public double FinalBase { get; set; } = 7.0;
        public double FinalX { get; set; } = 0.0;

        public Bitmap WeaponBmp { get; set; }
        public Bitmap ScopeBmp { get; set; }
        public Bitmap MuzzleBmp { get; set; }
        public Bitmap GripBmp { get; set; }
        public Bitmap StockBmp { get; set; }

        public BitmapSource WeaponImg => GdiHelper.ToBitmapSource(WeaponBmp);
        public BitmapSource ScopeImg => GdiHelper.ToBitmapSource(ScopeBmp);
        public BitmapSource MuzzleImg => GdiHelper.ToBitmapSource(MuzzleBmp);
        public BitmapSource GripImg => GdiHelper.ToBitmapSource(GripBmp);
        public BitmapSource StockImg => GdiHelper.ToBitmapSource(StockBmp);

        public string Summary
        {
            get
            {
                var parts = new List<string>();
                if (Muzzle != "None") parts.Add(Muzzle);
                if (Grip != "None") parts.Add(Grip);
                if (Stock != "None") parts.Add(Stock);
                if (Scope != "None") parts.Add(Scope);
                string att = parts.Count > 0 ? string.Join(" · ", parts) : "ปืนเปล่า";
                return $"[{WeaponName}] {att} → Base {FinalBase:0.0}";
            }
        }
    }

    public class DetectionReport
    {
        public DateTime Timestamp { get; set; } = DateTime.Now;
        public SlotResult Slot1 { get; set; }
        public SlotResult Slot2 { get; set; }
        public bool Success { get; set; }
        public string Error { get; set; }
    }

    // =========================================================================
    //  เอนจินสแกนภาพสด (Screen Capture & Scanner)
    // =========================================================================

    public static class VisionEngine
    {
        public static event Action<DetectionReport> Detected;
        public static DetectionReport LastReport { get; private set; }

        static bool scanning;

        public static Bitmap CaptureRect(System.Drawing.Rectangle rect)
        {
            if (rect.Width <= 0 || rect.Height <= 0) return null;
            try
            {
                var bmp = new Bitmap(rect.Width, rect.Height, PixelFormat.Format32bppArgb);
                using (var g = Graphics.FromImage(bmp))
                {
                    g.CopyFromScreen(rect.X, rect.Y, 0, 0, rect.Size, CopyPixelOperation.SourceCopy);
                }
                return bmp;
            }
            catch (Exception ex)
            {
                Log.Error($"capture rect {rect}", ex);
                return null;
            }
        }

        public static async Task<DetectionReport> ScanAsync(Settings cfg)
        {
            if (scanning) return LastReport;
            scanning = true;

            return await Task.Run(() =>
            {
                try
                {
                    var (box1, box2) = InventoryLayout.GetLayout(cfg.VisionRes, cfg.VisionOffsetX, cfg.VisionOffsetY);

                    var res1 = ScanSlot(1, box1);
                    var res2 = ScanSlot(2, box2);

                    var report = new DetectionReport
                    {
                        Slot1 = res1,
                        Slot2 = res2,
                        Success = true
                    };

                    LastReport = report;
                    Detected?.Invoke(report);
                    return report;
                }
                catch (Exception ex)
                {
                    Log.Error("vision scan", ex);
                    var err = new DetectionReport { Success = false, Error = ex.Message };
                    LastReport = err;
                    return err;
                }
                finally
                {
                    scanning = false;
                }
            });
        }

        static SlotResult ScanSlot(int slotIndex, SlotBox box)
        {
            var res = new SlotResult { SlotIndex = slotIndex };

            res.WeaponBmp = CaptureRect(box.Weapon);
            res.ScopeBmp  = CaptureRect(box.Scope);
            res.MuzzleBmp = CaptureRect(box.Muzzle);
            res.GripBmp   = CaptureRect(box.Grip);
            res.StockBmp  = CaptureRect(box.Stock);

            // แมตช์แต่ละช่อง
            var (wId, _) = TemplateManager.Match("Weapon", res.WeaponBmp);
            var (scId, _) = TemplateManager.Match("Scope", res.ScopeBmp);
            var (mId, _)  = TemplateManager.Match("Muzzle", res.MuzzleBmp);
            var (gId, _)  = TemplateManager.Match("Grip", res.GripBmp);
            var (sId, _)  = TemplateManager.Match("Stock", res.StockBmp);

            res.WeaponName = wId;
            res.Scope = scId;
            res.Muzzle = mId;
            res.Grip = gId;
            res.Stock = sId;

            // คำนวณแรงดีด
            double baseRecoil = PUBGData.Weapons.TryGetValue(wId, out var wDef) ? wDef.BaseRecoil : 7.0;
            double defX = wDef != null ? wDef.DefaultX : 0.0;
            res.InitialBase = baseRecoil;
            res.InitialX = defX;

            var (fBase, fX) = PUBGData.CalculateRecoil(baseRecoil, defX, mId, gId, sId, scId);
            res.FinalBase = fBase;
            res.FinalX = fX;

            return res;
        }
    }
}
