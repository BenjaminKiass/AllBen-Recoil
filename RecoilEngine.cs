using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;

namespace AllBenRecoil
{
    // =========================================================================
    //  RecoilEngine: Standalone Native Precision Mouse Recoil Engine
    //  ไม่ต้องพึ่ง Logitech G Hub, ทำงานผ่าน High-Precision Multimedia Timer (1ms)
    //  คำนวณเคิร์ฟเลเซอร์ v8 + ตัวคูณ AI Vision + Sub-pixel Accumulator
    // =========================================================================
    public static class RecoilEngine
    {
        [DllImport("user32.dll")]
        static extern void mouse_event(uint dwFlags, uint dx, uint dy, uint dwData, UIntPtr dwExtraInfo);

        [DllImport("user32.dll")]
        public static extern short GetAsyncKeyState(int vKey);

        [DllImport("winmm.dll", EntryPoint = "timeBeginPeriod")]
        static extern uint TimeBeginPeriod(uint uMilliseconds);

        [DllImport("winmm.dll", EntryPoint = "timeEndPeriod")]
        static extern uint TimeEndPeriod(uint uMilliseconds);

        const uint MOUSEEVENTF_MOVE = 0x0001;
        const int VK_LBUTTON = 0x01;
        const int VK_RBUTTON = 0x02;

        public class GunProfile
        {
            public string Name { get; set; } = "M416";
            public double Base { get; set; } = 5.0;
            public double X { get; set; } = 0.0;
            public double Multiplier { get; set; } = 1.0;
            public double[] Steps { get; set; } = DefaultM416Curve();
        }

        public static bool IsEnabled { get; set; } = false;
        public static bool RequireADS { get; set; } = true;         // ดึงเฉพาะตอนเล็งคลิกขวา (ปลอดภัยบน Desktop 100%)
        public static bool HumanizeJitter { get; set; } = true;     // สุ่มไมโครฟลักชูเอชันเลียนแบบมือโปร (ลดการตรวจจับ)
        public static int ActiveSlot { get; set; } = 1;            // 1, 2, 3

        public static readonly GunProfile[] Slots = new GunProfile[3]
        {
            new GunProfile { Name = "M416 เลเซอร์", Base = 5.0, X = 0.0, Steps = DefaultM416Curve() },
            new GunProfile { Name = "Beryl M762", Base = 15.5, X = 0.0, Steps = DefaultBerylCurve() },
            new GunProfile { Name = "AUG / ACE32", Base = 6.0, X = 0.0, Steps = DefaultAugCurve() }
        };

        public static event Action<bool> StateChanged;
        public static event Action<int> SlotChanged;

        static Thread worker;
        static volatile bool running = false;
        static readonly Random rnd = new Random();

        public static void Start()
        {
            if (running) return;
            running = true;
            TimeBeginPeriod(1);

            worker = new Thread(WorkerLoop)
            {
                IsBackground = true,
                Priority = ThreadPriority.Highest,
                Name = "AllBenRecoil_Engine"
            };
            worker.Start();
        }

        public static void Stop()
        {
            running = false;
            TimeBeginPeriod(1);
        }

        public static void Toggle()
        {
            IsEnabled = !IsEnabled;
            StateChanged?.Invoke(IsEnabled);
        }

        public static void SetSlot(int slot)
        {
            ActiveSlot = Math.Clamp(slot, 1, 3);
            SlotChanged?.Invoke(ActiveSlot);
        }

        public static GunProfile CurrentGun => Slots[Math.Clamp(ActiveSlot - 1, 0, 2)];

        static void WorkerLoop()
        {
            double accumY = 0.0;
            double accumX = 0.0;

            while (running)
            {
                if (!IsEnabled)
                {
                    Thread.Sleep(10);
                    continue;
                }

                bool leftDown = (GetAsyncKeyState(VK_LBUTTON) & 0x8000) != 0;
                bool rightDown = (GetAsyncKeyState(VK_RBUTTON) & 0x8000) != 0;

                bool shouldSpray = leftDown && (!RequireADS || rightDown);

                if (shouldSpray)
                {
                    var gun = CurrentGun;
                    var steps = gun.Steps ?? DefaultM416Curve();
                    long freq = Stopwatch.Frequency;
                    long startTick = Stopwatch.GetTimestamp();
                    long lastTick = startTick;
                    accumY = 0.0;
                    accumX = 0.0;

                    while (running && IsEnabled)
                    {
                        leftDown = (GetAsyncKeyState(VK_LBUTTON) & 0x8000) != 0;
                        rightDown = (GetAsyncKeyState(VK_RBUTTON) & 0x8000) != 0;
                        if (!leftDown || (RequireADS && !rightDown)) break;

                        long now = Stopwatch.GetTimestamp();
                        double elapsedMs = (now - startTick) * 1000.0 / freq;
                        double dtMs = (now - lastTick) * 1000.0 / freq;
                        lastTick = now;

                        // ขั้นดึงในสเต็ป 10ms
                        int stepIdx = (int)(elapsedMs / 10.0);
                        double curveVal = (stepIdx < steps.Length) ? steps[stepIdx] : steps[^1];

                        // คำนวณแรงดึงตามเวลาจริง dt
                        double baseRate = gun.Base * 0.1; // ต่อนิวตัน
                        double rateY = (baseRate + curveVal) * gun.Multiplier;
                        double rateX = (gun.X * 0.1) * gun.Multiplier;

                        // ไมโครจิสเตอร์แบบโปร (ความนิ่งไม่สะบัด แต่ไม่เป็นหุ่นยนต์ตรงเป๊ะ)
                        if (HumanizeJitter)
                        {
                            rateY += (rnd.NextDouble() - 0.5) * 0.08;
                            rateX += (rnd.NextDouble() - 0.5) * 0.08;
                        }

                        accumY += rateY * (dtMs / 10.0);
                        accumX += rateX * (dtMs / 10.0);

                        int moveY = (int)Math.Round(accumY);
                        int moveX = (int)Math.Round(accumX);

                        if (moveY != 0 || moveX != 0)
                        {
                            accumY -= moveY;
                            accumX -= moveX;
                            mouse_event(MOUSEEVENTF_MOVE, (uint)moveX, (uint)moveY, 0, UIntPtr.Zero);
                        }

                        // วนลูปละเอียด 2ms เพื่อความลื่นสมูท
                        Thread.Sleep(2);
                    }
                }
                else
                {
                    Thread.Sleep(2);
                }
            }

            TimeEndPeriod(1);
        }

        // =========================================================================
        //  เคิร์ฟเลเซอร์ Monotonic (สมูท ไม่มีหยักสะบัด)
        // =========================================================================
        public static double[] DefaultM416Curve()
        {
            // 300 steps (3 วินาที)
            var curve = new double[300];
            for (int i = 0; i < 300; i++)
            {
                if (i < 10) curve[i] = 1.2;
                else if (i < 25) curve[i] = 1.6;
                else if (i < 50) curve[i] = 2.0;
                else if (i < 80) curve[i] = 2.4;
                else if (i < 120) curve[i] = 2.7;
                else if (i < 160) curve[i] = 2.9;
                else curve[i] = 3.0;
            }
            return curve;
        }

        public static double[] DefaultBerylCurve()
        {
            var curve = new double[300];
            for (int i = 0; i < 300; i++)
            {
                if (i < 10) curve[i] = 1.5;
                else if (i < 25) curve[i] = 2.0;
                else if (i < 50) curve[i] = 2.6;
                else if (i < 80) curve[i] = 3.2;
                else if (i < 120) curve[i] = 3.6;
                else if (i < 160) curve[i] = 3.9;
                else curve[i] = 4.0;
            }
            return curve;
        }

        public static double[] DefaultAugCurve()
        {
            var curve = new double[300];
            for (int i = 0; i < 300; i++)
            {
                if (i < 10) curve[i] = 1.0;
                else if (i < 25) curve[i] = 1.4;
                else if (i < 50) curve[i] = 1.8;
                else if (i < 80) curve[i] = 2.2;
                else if (i < 120) curve[i] = 2.5;
                else if (i < 160) curve[i] = 2.8;
                else curve[i] = 2.9;
            }
            return curve;
        }
    }
}
