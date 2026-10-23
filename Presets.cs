using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace AllBenRecoil
{
    public class Preset
    {
        public string App { get; set; } = "AllBen-Recoil";
        public int Format { get; set; } = 1;
        public string Name { get; set; } = "";
        public DateTime Saved { get; set; } = DateTime.Now;
        public List<PresetGun> Guns { get; set; } = new();

        public class PresetGun
        {
            public string Name { get; set; } = "";
            public double Base { get; set; }
            public double X { get; set; }
            public double Multiplier { get; set; } = 1.0;
            public List<double[]> Segs { get; set; }

            public double[] StepArray() =>
                Segs == null || Segs.Count == 0 ? null : Expand(Segs.Select(p => (p[0], (int)p[1])));
        }

        public static readonly string Dir = Path.Combine(Settings.KitDir, "presets");

        static readonly JsonSerializerOptions Json = new()
        {
            WriteIndented = true,
            PropertyNameCaseInsensitive = true,
            DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
        };

        public static double[] Expand(IEnumerable<(double v, int n)> pairs) =>
            pairs.SelectMany(p => Enumerable.Repeat(p.v, Math.Max(0, p.n))).ToArray();

        public static List<(double v, int n)> Compress(double[] steps)
        {
            var list = new List<(double v, int n)>();
            if (steps == null) return list;
            foreach (double raw in steps)
            {
                double v = Math.Round(raw, 3);
                if (list.Count > 0 && list[^1].v == v) list[^1] = (v, list[^1].n + 1);
                else list.Add((v, 1));
            }
            return list;
        }

        public static void EnsureDefaults()
        {
            try
            {
                Directory.CreateDirectory(Dir);
                string m4Path = Path.Combine(Dir, "M416 เลเซอร์.json");
                if (!File.Exists(m4Path))
                {
                    var p = new Preset
                    {
                        Name = "M416 เลเซอร์",
                        Guns = new List<PresetGun>
                        {
                            new PresetGun { Name = "M416 เลเซอร์", Base = 5.0, X = 0.0, Segs = Compress(RecoilEngine.DefaultM416Curve()).Select(x => new[] { x.v, (double)x.n }).ToList() },
                            new PresetGun { Name = "Beryl M762", Base = 15.5, X = 0.0, Segs = Compress(RecoilEngine.DefaultBerylCurve()).Select(x => new[] { x.v, (double)x.n }).ToList() },
                            new PresetGun { Name = "AUG / ACE32", Base = 6.0, X = 0.0, Segs = Compress(RecoilEngine.DefaultAugCurve()).Select(x => new[] { x.v, (double)x.n }).ToList() }
                        }
                    };
                    File.WriteAllText(m4Path, JsonSerializer.Serialize(p, Json));
                }
            }
            catch { }
        }

        public static List<string> ListAll()
        {
            EnsureDefaults();
            try
            {
                return Directory.GetFiles(Dir, "*.json")
                    .Select(Path.GetFileNameWithoutExtension)
                    .OrderBy(n => n)
                    .ToList();
            }
            catch { return new List<string>(); }
        }

        public static Preset Load(string name)
        {
            string p = Path.Combine(Dir, name + ".json");
            if (!File.Exists(p)) return null;
            try
            {
                return JsonSerializer.Deserialize<Preset>(File.ReadAllText(p), Json);
            }
            catch { return null; }
        }

        public static void Save(string name, RecoilEngine.GunProfile[] slots)
        {
            EnsureDefaults();
            var p = new Preset
            {
                Name = name,
                Guns = slots.Select(g => new PresetGun
                {
                    Name = g.Name,
                    Base = g.Base,
                    X = g.X,
                    Multiplier = g.Multiplier,
                    Segs = Compress(g.Steps).Select(x => new[] { x.v, (double)x.n }).ToList()
                }).ToList()
            };
            string path = Path.Combine(Dir, name + ".json");
            File.WriteAllText(path, JsonSerializer.Serialize(p, Json));
        }
    }
}
