// InphicBatteryTray.cs  —  英菲克鼠标电量托盘显示器
// 纯只读：不修改设备、不修改 INPHIC HUB，仅接收设备主动上报的电量报告。
//
// 图标：由 鼠标.svg 渲染的剪影，按电量从下往上填充，颜色随电量变化。
//
// 编译：
//   C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe /target:winexe /platform:x64 ^
//     /resource:MouseIcon.png,MouseIcon.png ^
//     /out:InphicBatteryTray.exe /reference:System.dll /reference:System.Windows.Forms.dll ^
//     /reference:System.Drawing.dll InphicBatteryTray.cs

using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;
using Microsoft.Win32;

namespace InphicBatteryTray
{
    static class Native
    {
        public const int DIGCF_PRESENT = 0x02;
        public const int DIGCF_DEVICEINTERFACE = 0x10;
        public const uint GENERIC_READ = 0x80000000;
        public const uint GENERIC_WRITE = 0x40000000;
        public const int FILE_SHARE_READ = 1;
        public const int FILE_SHARE_WRITE = 2;
        public const uint OPEN_EXISTING = 3;
        public const uint FILE_FLAG_OVERLAPPED = 0x40000000;
        public static readonly IntPtr INVALID_HANDLE_VALUE = new IntPtr(-1);
        public const int ERROR_IO_PENDING = 997;
        public const uint WAIT_OBJECT_0 = 0;

        [StructLayout(LayoutKind.Sequential)]
        public struct GUID { public uint Data1; public ushort Data2; public ushort Data3; public byte b0, b1, b2, b3, b4, b5, b6, b7; }

        [StructLayout(LayoutKind.Sequential)]
        public struct SP_DEVICE_INTERFACE_DATA
        {
            public int cbSize;
            public GUID InterfaceClassGuid;
            public int Flags;
            public IntPtr Reserved;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct HIDD_ATTRIBUTES
        {
            public int Size;
            public ushort VendorID;
            public ushort ProductID;
            public ushort VersionNumber;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct OVERLAPPED
        {
            public IntPtr Internal;
            public IntPtr InternalHigh;
            public int Offset;
            public int OffsetHigh;
            public IntPtr hEvent;
        }

        [DllImport("setupapi.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        public static extern IntPtr SetupDiGetClassDevsW(ref GUID ClassGuid, IntPtr Enumerator, IntPtr hwndParent, int Flags);

        [DllImport("setupapi.dll", SetLastError = true)]
        public static extern bool SetupDiEnumDeviceInterfaces(IntPtr hDevInfo, IntPtr devInfo, ref GUID interfaceClassGuid, int memberIndex, ref SP_DEVICE_INTERFACE_DATA deviceInterfaceData);

        [DllImport("setupapi.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        public static extern bool SetupDiGetDeviceInterfaceDetailW(IntPtr hDevInfo, ref SP_DEVICE_INTERFACE_DATA deviceInterfaceData, IntPtr deviceInterfaceDetailData, int deviceInterfaceDetailDataSize, out int requiredSize, IntPtr deviceInfoData);

        [DllImport("setupapi.dll", SetLastError = true)]
        public static extern bool SetupDiDestroyDeviceInfoList(IntPtr DeviceInfoSet);

        [DllImport("hid.dll")]
        public static extern void HidD_GetHidGuid(out GUID guid);

        [DllImport("hid.dll", SetLastError = true)]
        public static extern bool HidD_GetAttributes(IntPtr device, ref HIDD_ATTRIBUTES attributes);

        [DllImport("hid.dll", CharSet = CharSet.Unicode)]
        public static extern bool HidD_GetProductString(IntPtr device, IntPtr buffer, int bufferLength);

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        public static extern IntPtr CreateFileW(string fileName, uint desiredAccess, int shareMode, IntPtr securityAttributes, uint creationDisposition, uint flagsAndAttributes, IntPtr templateFile);

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern bool CloseHandle(IntPtr hObject);

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern bool ReadFile(IntPtr hFile, byte[] buffer, int numberOfBytesToRead, out int numberOfBytesRead, IntPtr lpOverlapped);

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern bool WriteFile(IntPtr hFile, byte[] buffer, int numberOfBytesToWrite, out int numberOfBytesWritten, IntPtr lpOverlapped);

        [DllImport("hid.dll", SetLastError = true)]
        public static extern bool HidD_GetPreparsedData(IntPtr hidDeviceObject, out IntPtr preparsedData);

        [DllImport("hid.dll")]
        public static extern int HidP_GetCaps(IntPtr preparsedData, byte[] capabilities);

        [DllImport("hid.dll")]
        public static extern bool HidD_FreePreparsedData(IntPtr preparsedData);

        [DllImport("hid.dll", SetLastError = true)]
        public static extern bool HidD_SetFeature(IntPtr hidDeviceObject, byte[] buffer, int bufferLength);

        [DllImport("hid.dll", SetLastError = true)]
        public static extern bool HidD_GetFeature(IntPtr hidDeviceObject, byte[] buffer, int bufferLength);

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern IntPtr CreateEventW(IntPtr lpEventAttributes, bool bManualReset, bool bInitialState, IntPtr lpName);

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern uint WaitForSingleObject(IntPtr hHandle, uint dwMilliseconds);

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern bool GetOverlappedResult(IntPtr hFile, IntPtr lpOverlapped, out int lpNumberOfBytesTransferred, bool bWait);

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern bool ResetEvent(IntPtr hEvent);

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern bool CancelIoEx(IntPtr hFile, IntPtr lpOverlapped);

        [DllImport("user32.dll", SetLastError = true)]
        public static extern bool DestroyIcon(IntPtr hIcon);
    }

    class HidInterface
    {
        public string Path;
        public ushort Vid;
        public ushort Pid;
        public string Product;
        public string Transport;   // USB / 蓝牙
        public bool IsIn9;         // IN9 系列：需要主动查询
        // [PATCH] 采集 Usage Page / Usage，仅用于日志观测，暂不参与筛选
        public ushort UsagePage;
        public ushort Usage;
        public int InLen;          // 输入报告长度
        public int OutLen;         // 输出报告长度
        public int FeatLen;        // Feature 报告长度
    }

    class DeviceState
    {
        public string Label;
        public int Battery = -1;
        public double LastIntervalMs;
        public DateTime LastSeen = DateTime.MinValue;
        public int ChargeState = -1;   // 1 正常 / 2 已满 / 3 充电
        public string Protocol;        // "IN10 push" / "IN9 query"
        public string Product;
        public ushort Vid;
        public ushort Pid;
        public string Transport;
        public int TypeCode = -1;
    }

    class Reader
    {
        // 从 HID 接口路径推断连接方式（蓝牙设备走 BTHENUM / bth 路径）
        public static string DetectTransport(string path)
        {
            if (string.IsNullOrEmpty(path)) return "未知";
            string p = path.ToLowerInvariant();
            if (p.Contains("bth") || p.Contains("bluetooth")) return "蓝牙 (BLE)";
            return "USB";
        }

        public static List<HidInterface> Enumerate()
        {
            var list = new List<HidInterface>();
            Native.GUID guid;
            Native.HidD_GetHidGuid(out guid);
            IntPtr set = Native.SetupDiGetClassDevsW(ref guid, IntPtr.Zero, IntPtr.Zero,
                Native.DIGCF_PRESENT | Native.DIGCF_DEVICEINTERFACE);
            if (set == Native.INVALID_HANDLE_VALUE) return list;

            try
            {
                int idx = 0;
                while (true)
                {
                    var did = new Native.SP_DEVICE_INTERFACE_DATA();
                    did.cbSize = Marshal.SizeOf(typeof(Native.SP_DEVICE_INTERFACE_DATA));
                    if (!Native.SetupDiEnumDeviceInterfaces(set, IntPtr.Zero, ref guid, idx, ref did))
                        break;
                    idx++;

                    int need;
                    Native.SetupDiGetDeviceInterfaceDetailW(set, ref did, IntPtr.Zero, 0, out need, IntPtr.Zero);
                    if (need <= 0) continue;

                    IntPtr buf = Marshal.AllocHGlobal(need);
                    try
                    {
                        Marshal.WriteInt32(buf, 0, IntPtr.Size == 8 ? 8 : 6);
                        if (!Native.SetupDiGetDeviceInterfaceDetailW(set, ref did, buf, need, out need, IntPtr.Zero))
                            continue;
                        string path = Marshal.PtrToStringUni(new IntPtr(buf.ToInt64() + 4));
                        if (string.IsNullOrEmpty(path)) continue;

                        var dev = new HidInterface();
                        dev.Path = path;
                        IntPtr h = Native.CreateFileW(path, 0, Native.FILE_SHARE_READ | Native.FILE_SHARE_WRITE,
                            IntPtr.Zero, Native.OPEN_EXISTING, 0, IntPtr.Zero);
                        if (h == Native.INVALID_HANDLE_VALUE) continue;
                        try
                        {
                            var attr = new Native.HIDD_ATTRIBUTES();
                            attr.Size = Marshal.SizeOf(typeof(Native.HIDD_ATTRIBUTES));
                            if (Native.HidD_GetAttributes(h, ref attr))
                            {
                                dev.Vid = attr.VendorID;
                                dev.Pid = attr.ProductID;
                            }
                            IntPtr sbuf = Marshal.AllocHGlobal(512);
                            try
                            {
                                if (Native.HidD_GetProductString(h, sbuf, 512))
                                    dev.Product = Marshal.PtrToStringUni(sbuf);
                            }
                            finally { Marshal.FreeHGlobal(sbuf); }

                            // 报告长度：WriteFile 必须严格匹配，否则会 ERROR_INVALID_PARAMETER
                            IntPtr pp;
                            if (Native.HidD_GetPreparsedData(h, out pp) && pp != IntPtr.Zero)
                            {
                                try
                                {
                                    byte[] caps = new byte[256];
                                    if (Native.HidP_GetCaps(pp, caps) == 0x00110000)
                                    {
                                        // [PATCH] HIDP_CAPS 布局：Usage 在偏移 0，UsagePage 在偏移 2
                                        dev.Usage = BitConverter.ToUInt16(caps, 0);
                                        dev.UsagePage = BitConverter.ToUInt16(caps, 2);
                                        dev.InLen = BitConverter.ToUInt16(caps, 4);
                                        dev.OutLen = BitConverter.ToUInt16(caps, 6);
                                        dev.FeatLen = BitConverter.ToUInt16(caps, 8);
                                    }
                                }
                                finally { Native.HidD_FreePreparsedData(pp); }
                            }
                        }
                        finally { Native.CloseHandle(h); }

                        if (dev.Vid != 0 || dev.Pid != 0)
                        {
                            dev.Transport = DetectTransport(path);
                            list.Add(dev);
                        }
                    }
                    finally { Marshal.FreeHGlobal(buf); }
                }
            }
            finally { Native.SetupDiDestroyDeviceInfoList(set); }

            return list;
        }
    }

    static class Program
    {
        // 已从 INPHIC.exe 逆向出的候选 VID/PID
        static readonly ushort[][] Candidates = new ushort[][]
        {
            new ushort[]{0x1D57,0xFA60}, new ushort[]{0x1D57,0xFA65},
            // [PATCH] FA93 = 同一只鼠标的「有线」模式（插线充电时枚举出来的另一个 PID）。
            // 不认它的话，插线后 2.4G 那台停推、超时变灰，而有线这台根本没被打开。
            new ushort[]{0x1D57,0xFA93},
            new ushort[]{0x045E,0x0095}, new ushort[]{0x045E,0x0090},
            new ushort[]{0x045E,0x0093}, new ushort[]{0x045E,0x0099},
            new ushort[]{0x045E,0x0098}, new ushort[]{0x045E,0x0273},
            new ushort[]{0x045E,0x0271},
        };

        static readonly HashSet<int> TypeCodes = new HashSet<int> { 0x95, 0x90, 0x93, 0x99, 0x98, 0x73 };

        // [PATCH] IN10 的电量只在 Usage Page 0x000A 的接口上推送。
        // 实测 KP 8K（1D57:FA65）四个接口：
        //   0x000C/0x0001 in=3   消费者控制
        //   0x0001/0x0080 in=2   系统控制
        //   0x000A/0x0000 in=5   <- 电量
        //   0xFF00/0x0001 in=64  厂商通道
        // 加上这个条件后只打开电量接口，不再触碰键盘/厂商通道。
        static bool IsIn10(HidInterface dev)
        {
            return dev.UsagePage == 0x000A && dev.InLen >= 5;
        }

        // [PATCH] 诊断：已知厂商 VID 的接口，每个路径首次出现时记一行，
        // 用来确认"某只设备到底暴露了哪些接口"，插拔后新增的会再记。
        static readonly HashSet<string> probedPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        static bool IsKnownVid(ushort vid)
        {
            if (IsIn9Vid(vid)) return true;
            foreach (ushort[] c in Candidates)
                if (c[0] == vid) return true;
            return false;
        }

        // [PATCH] 有线模式：同一只鼠标插线后枚举成 1D57:FA93。
        // 它一出现就说明鼠标在走线缆，此时 2.4G 那条推送会静默，
        // 但不能当成"已关机"灰显——实际是在充电。
        static bool IsWiredSibling(HidInterface dev)
        {
            return dev.Vid == 0x1D57 && dev.Pid == 0xFA93;
        }

        // [PATCH] 同一只鼠标在 2.4G 与有线两种连接方式下会同时枚举成两台设备：
        // 实测插线时 1D57:FA65 与 1D57:FA93 会同时在线，且都上报同一电量与充电状态。
        // 它们不是两台设备，详情浮层要按"物理设备"折叠，否则插线后必然重复两条。
        // 这里把有线身份 FA93 归并到 2.4G 身份 FA65 的同一组。
        static string DeviceGroupKey(DeviceState d)
        {
            ushort pid = d.Pid;
            if (d.Vid == 0x1D57 && pid == 0xFA93) pid = 0xFA65;
            return string.Format("{0:X4}:{1:X4}", d.Vid, pid);
        }

        // [PATCH] 同组内挑一条展示。权重：在线 +2、有线模式 +1，同分再比最后上报时间。
        // 有线优先是为了防抖：插线后 FA65 与 FA93 都在以 2s 周期上报，
        // 若只按"谁更新"选，名字会在两个型号之间来回跳。
        static int DeviceRank(DeviceState d, DateTime now)
        {
            int score = 0;
            if ((now - d.LastSeen).TotalMilliseconds <= OfflineAfterMs) score += 2;
            if (d.Vid == 0x1D57 && d.Pid == 0xFA93) score += 1;
            return score;
        }

        const string RUN_KEY = @"Software\Microsoft\Windows\CurrentVersion\Run";
        const string RUN_NAME = "InphicBatteryTray";

        static NotifyIcon tray;
        static Icon currentIcon;
        static string logPath = null;
        static Bitmap srcIcon = null;
        static MenuItem autoItem;
        static int lastDrawnLevel = -999;
        static bool lastDrawnOffline = true;

        // 超过该时长没收到电量报告即视为离线（设备上报周期约 2s）
        const int OfflineAfterMs = 8000;
        static System.Windows.Forms.Timer statusTimer;

        static readonly object sync = new object();
        // [PATCH] 图标重绘互斥：TickStatus(UI线程) 与 UpdateTrayUI(读取线程) 都会进来
        static readonly object trayLock = new object();
        // [PATCH] 日志写入互斥：多个读取线程并发 AppendAllText 会互相抢占文件
        static readonly object logLock = new object();
        // [PATCH] 单实例互斥体
        static Mutex instanceMutex = null;
        static readonly Dictionary<string, DeviceState> states = new Dictionary<string, DeviceState>();
        static volatile bool running = true;
        // [PATCH] 是否检测到「有线模式」的同款设备在线（插线充电时会出现）。
        // 由 Supervise 每轮枚举更新，用于区分"真离线"和"插线充电导致 2.4G 静默"。
        static volatile bool wiredCharging;
        static Thread supervisor;

        static int logWrites = 0;

        static void Log(string s)
        {
            if (logPath == null) return;
            try
            {
                lock (logLock)
                {
                    // [PATCH] 运行期也做轮转。原版只在启动时检查一次，
                    // 而电量报文每 2 秒写一行，长期开机可涨到几十 MB。
                    if (++logWrites % 500 == 0)
                    {
                        try
                        {
                            if (File.Exists(logPath) && new FileInfo(logPath).Length > 512 * 1024)
                                File.Delete(logPath);
                        }
                        catch { }
                    }
                    File.AppendAllText(logPath, DateTime.Now.ToString("HH:mm:ss.fff") + "  " + s + "\r\n");
                }
            }
            catch { }
        }

        [STAThread]
        static void Main(string[] args)
        {
            // [PATCH] 单实例。原版没有 Mutex，重复启动会出现多个托盘图标，
            // 并且多个进程抢同一个 tray.log 互相干扰。
            // 用 Local\ 前缀：同一用户会话内唯一，不同用户各自可以跑一份。
            bool createdNew = true;
            try
            {
                instanceMutex = new Mutex(true, @"Local\InphicBatteryTray.SingleInstance", out createdNew);
            }
            catch (Exception)
            {
                createdNew = true;
            }
            if (!createdNew)
            {
                if (instanceMutex != null) { instanceMutex.Dispose(); instanceMutex = null; }
                return;
            }

            logPath = Environment.GetEnvironmentVariable("INPHIC_TRAY_LOG");
            foreach (string a in args)
            {
                if (a.StartsWith("--log=")) logPath = a.Substring(6);
            }
            if (logPath == null)
            {
                // 默认写日志到程序目录，方便排查（尤其 IN9 支持）
                try
                {
                    string dir = Path.GetDirectoryName(Application.ExecutablePath);
                    logPath = Path.Combine(dir, "tray.log");
                    if (File.Exists(logPath) && new FileInfo(logPath).Length > 512 * 1024)
                        File.Delete(logPath);          // 超过 512KB 就换新的
                }
                catch { logPath = null; }
            }
            Log("=== start ===");

            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);

            LoadIconSource();

            tray = new NotifyIcon();
            tray.Text = "英菲克 电量：搜索设备中…";
            tray.Icon = DrawIcon(-1, true);
            tray.Visible = true;

            var menu = new ContextMenu();

            autoItem = new MenuItem("开机自动启动");
            autoItem.Checked = IsAutoStartEnabled();
            autoItem.Click += delegate { ToggleAutoStart(); };

            menu.MenuItems.Add("显示详情", delegate { ShowDetails(); });
            menu.MenuItems.Add("重新扫描设备", delegate { Rescan(); });
            menu.MenuItems.Add(autoItem);
            menu.MenuItems.Add("-");
            menu.MenuItems.Add("退出", delegate { Quit(); });

            tray.ContextMenu = menu;
            tray.DoubleClick += delegate { ShowDetails(); };

            supervisor = new Thread(Supervise);
            supervisor.IsBackground = true;
            supervisor.Start();

            // 每秒检查一次：设备是否已经长时间没有上报（关机/断连/休眠）
            statusTimer = new System.Windows.Forms.Timer();
            statusTimer.Interval = 1000;
            statusTimer.Tick += delegate { TickStatus(); };
            statusTimer.Start();

            // 自测：设置 INPHIC_TEST_POPUP=1 时启动 2.5 秒后自动弹一次浮层
            if (Environment.GetEnvironmentVariable("INPHIC_TEST_POPUP") == "1")
            {
                var testTimer = new System.Windows.Forms.Timer();
                testTimer.Interval = 2500;
                testTimer.Tick += delegate
                {
                    testTimer.Stop();
                    try { ShowDetails(); Log("test popup shown"); }
                    catch (Exception ex) { Log("test popup FAILED: " + ex); }
                };
                testTimer.Start();
            }

            // [PATCH] 退出时统一释放托盘图标、GDI 对象和定时器
            try { Application.Run(); }
            finally
            {
                running = false;
                if (supervisor != null) { try { supervisor.Interrupt(); } catch { } }
                if (statusTimer != null) statusTimer.Dispose();
                if (popup != null) { try { popup.Dispose(); } catch { } }
                if (tray != null) { try { tray.Visible = false; tray.Dispose(); } catch { } }
                if (currentIcon != null) { try { currentIcon.Dispose(); } catch { } }
                if (srcIcon != null) { try { srcIcon.Dispose(); } catch { } }
                if (instanceMutex != null) { try { instanceMutex.Dispose(); } catch { } }
            }
        }

        // ---------------------------------------------------------- 开机自启

        static bool IsAutoStartEnabled()
        {
            try
            {
                using (RegistryKey k = Registry.CurrentUser.OpenSubKey(RUN_KEY, false))
                {
                    if (k == null) return false;
                    object v = k.GetValue(RUN_NAME);
                    if (v == null) return false;
                    string cur = v.ToString().Trim('"');
                    return string.Equals(cur, Application.ExecutablePath,
                        StringComparison.OrdinalIgnoreCase);
                }
            }
            catch { return false; }
        }

        static void ToggleAutoStart()
        {
            try
            {
                bool want = !IsAutoStartEnabled();
                using (RegistryKey k = Registry.CurrentUser.OpenSubKey(RUN_KEY, true))
                {
                    if (k == null)
                    {
                        MessageBox.Show("无法打开启动项注册表键。", "英菲克 电量",
                            MessageBoxButtons.OK, MessageBoxIcon.Warning);
                        return;
                    }
                    if (want)
                        k.SetValue(RUN_NAME, "\"" + Application.ExecutablePath + "\"");
                    else
                        k.DeleteValue(RUN_NAME, false);
                }
                if (autoItem != null) autoItem.Checked = IsAutoStartEnabled();
                Log("autostart -> " + want);
            }
            catch (Exception ex)
            {
                MessageBox.Show("设置开机自启动失败：" + ex.Message, "英菲克 电量",
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        }

        // ---------------------------------------------------------- 图标

        static void LoadIconSource()
        {
            try
            {
                Assembly asm = Assembly.GetExecutingAssembly();
                using (Stream s = asm.GetManifestResourceStream("MouseIcon.png"))
                {
                    if (s != null) srcIcon = new Bitmap(s);
                }
                Log("icon source: " + (srcIcon == null ? "MISSING" : srcIcon.Width + "x" + srcIcon.Height));
            }
            catch (Exception ex) { Log("icon load error: " + ex.Message); }
        }

        static Color StatusColor(int lv)
        {
            if (lv <= 15) return Color.FromArgb(255, 70, 70);
            if (lv <= 40) return Color.FromArgb(255, 190, 60);
            return Color.FromArgb(0x19, 0xFA, 0xC6);   // #19fac6
        }

        // 原图主体是 #1afa29 高饱和绿；缩放混色后仍保持 g 明显优势
        static bool IsGreenBody(Color c)
        {
            return c.G > 110 && (c.G - c.R) > 45 && (c.G - c.B) > 45;
        }

        static Color OutlineColor { get { return Color.FromArgb(38, 32, 62); } }

        // 最外一圈：alpha 有效且四邻域里有背景（保证 16px 下轮廓实心不虚）
        static bool[,] BuildRing(int[,] alpha, int w, int h, int thr)
        {
            bool[,] ring = new bool[h, w];
            for (int y = 0; y < h; y++)
            {
                for (int x = 0; x < w; x++)
                {
                    if (alpha[y, x] < thr) continue;
                    if (x == 0 || y == 0 || x == w - 1 || y == h - 1)
                    {
                        ring[y, x] = true;
                        continue;
                    }
                    if (alpha[y, x + 1] < thr || alpha[y, x - 1] < thr ||
                        alpha[y + 1, x] < thr || alpha[y - 1, x] < thr)
                        ring[y, x] = true;
                }
            }
            return ring;
        }

        // 按电量从下往上填充鼠标主体；外圈实心描边，滚轮/顶部保持原色
        // offline=true 时整体灰显（设备关机 / 断连 / 长时间未上报）
        static Icon DrawIcon(int level, bool offline)
        {
            Size sz = SystemInformation.SmallIconSize;
            if (sz.Width < 16) sz = new Size(16, 16);
            int w = sz.Width, h = sz.Height;

            using (Bitmap scaled = new Bitmap(w, h, PixelFormat.Format32bppArgb))
            {
                using (Graphics g = Graphics.FromImage(scaled))
                {
                    g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                    g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                    g.SmoothingMode = SmoothingMode.HighQuality;
                    g.Clear(Color.Transparent);
                    if (srcIcon != null)
                        g.DrawImage(srcIcon, new Rectangle(0, 0, w, h));
                }

                using (Bitmap outp = new Bitmap(w, h, PixelFormat.Format32bppArgb))
                {
                    double fillTop = h * (1.0 - Math.Max(0, Math.Min(100, level)) / 100.0);
                    Color st = StatusColor(level);
                    Color empty = Color.FromArgb(100, 100, 110);
                    Color outline = OutlineColor;
                    Color offOutline = Color.FromArgb(52, 52, 62);
                    bool grey = offline || level < 0;

                    int[,] alpha = new int[h, w];
                    for (int y = 0; y < h; y++)
                        for (int x = 0; x < w; x++)
                            alpha[y, x] = scaled.GetPixel(x, y).A;

                    bool[,] ring = BuildRing(alpha, w, h, 40);

                    for (int y = 0; y < h; y++)
                    {
                        for (int x = 0; x < w; x++)
                        {
                            Color c = scaled.GetPixel(x, y);
                            if (c.A < 40) continue;   // 丢弃极淡边缘，避免 16px 下发虚

                            Color o;
                            if (grey)
                            {
                                if (ring[y, x])
                                {
                                    o = offOutline;
                                }
                                else
                                {
                                    // 灰度化，但保留内部明暗（滚轮仍可辨）
                                    int L = (int)(0.299 * c.R + 0.587 * c.G + 0.114 * c.B);
                                    int v = 74 + L * 108 / 255;
                                    if (v > 255) v = 255;
                                    o = Color.FromArgb(v, v, Math.Min(255, v + 8));
                                }
                            }
                            else if (ring[y, x])
                            {
                                o = outline;
                            }
                            else if (IsGreenBody(c))
                            {
                                o = (y >= fillTop) ? st : empty;
                            }
                            else
                            {
                                o = c;   // 滚轮 / 顶部 / 高光 原样保留
                            }

                            outp.SetPixel(x, y, Color.FromArgb(c.A, o));
                        }
                    }

                    IntPtr hIcon = outp.GetHicon();
                    try
                    {
                        // [PATCH] 移除每次重绘都往磁盘写 tray.log.icon.png 的调试代码
                        using (Icon tmp = Icon.FromHandle(hIcon))
                            return (Icon)tmp.Clone();
                    }
                    finally
                    {
                        Native.DestroyIcon(hIcon);
                    }
                }
            }
        }

        // ---------------------------------------------------------- 菜单动作

        static void Quit()
        {
            // [PATCH] 资源统一交给 Main 的 finally 释放，这里不再重复 Dispose
            Application.Exit();
        }

        static void Rescan()
        {
            // [PATCH] 原版会清空 activePaths 并对读取线程 Interrupt。
            // 但 Interrupt 对阻塞在 P/Invoke 里的线程无效，旧线程要等到下一次
            // 电量报文才会退出；而 activePaths 已被清空，Supervise 会立刻给同一路径
            // 再起一条线程，旧线程退出时又把这个路径删掉 —— 每点一次就叠加一组线程和句柄。
            // 正确做法：不杀线程，只唤醒 supervisor 让它立即重新枚举，新设备由它自己补上。
            try { if (supervisor != null) supervisor.Interrupt(); } catch { }
        }

        // 已建立读取连接的接口路径，防止每轮枚举重复打开
        static readonly HashSet<string> activePaths =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // [PATCH] 原版这里还有 readerThreads 列表和 readersSnapshot()，只服务于
        // Rescan() 里"杀线程"的逻辑。改成唤醒 supervisor 之后两者都成了死代码，
        // 而该列表只增不减，故一并移除。

        static string ChargeText(int c)
        {
            switch (c)
            {
                case 1: return "正常（未充电）";
                case 2: return "已充满";
                case 3: return "充电中";
                default: return "未知";
            }
        }

        static void ShowDetails()
        {
            string msg;
            lock (sync)
            {
                var sb = new System.Text.StringBuilder();

                // [PATCH] 先按物理设备折叠。同一只鼠标的 2.4G 记录与有线记录会同时存在
                // （插线时两台都在线，拔线后有线那台消失），只应显示一条。
                // 同组内按 DeviceRank 取舍：在线 > 有线 > 最后上报更晚。
                var groupOrder = new List<string>();
                var groupBest = new Dictionary<string, DeviceState>();
                DateTime now = DateTime.Now;
                foreach (var kv in states)
                {
                    DeviceState d = kv.Value;
                    string key = DeviceGroupKey(d);
                    DeviceState cur;
                    if (!groupBest.TryGetValue(key, out cur))
                    {
                        groupOrder.Add(key);
                        groupBest[key] = d;
                        continue;
                    }
                    int dRank = DeviceRank(d, now);
                    int cRank = DeviceRank(cur, now);
                    if (dRank > cRank || (dRank == cRank && d.LastSeen > cur.LastSeen))
                        groupBest[key] = d;
                }

                foreach (string key in groupOrder)
                {
                    var d = groupBest[key];
                    bool off = (now - d.LastSeen).TotalMilliseconds > OfflineAfterMs;

                    sb.AppendLine(string.IsNullOrEmpty(d.Product) ? d.Label : d.Product);
                    sb.AppendLine("  连接方式  : " + (string.IsNullOrEmpty(d.Transport) ? "未知" : d.Transport));
                    sb.AppendLine(string.Format("  硬件 ID   : {0:X4}:{1:X4}", d.Vid, d.Pid));
                    if (d.TypeCode >= 0)
                        sb.AppendLine(string.Format("  协议族    : type 0x{0:X2}", d.TypeCode));
                    sb.AppendLine("  协议      : " + (string.IsNullOrEmpty(d.Protocol) ? "未知" : d.Protocol));
                    sb.AppendLine("  电量      : " + d.Battery + "%");
                    sb.AppendLine("  充电状态  : " + ChargeText(d.ChargeState));
                    sb.AppendLine(string.Format("  刷新间隔  : {0:F0} ms", d.LastIntervalMs));
                    sb.AppendLine(string.Format("  最后上报  : {0:HH:mm:ss}", d.LastSeen));
                    sb.AppendLine("  当前状态  : " + (off ? "已断开 / 已关机" : "在线"));
                    sb.AppendLine();
                }

                msg = sb.Length == 0
                    ? "尚未收到任何电量上报。\n\n请确认：\n· 设备已连接并唤醒\n· 原程序不是独占状态（本工具为只读共享）"
                    : sb.ToString();
            }
            ShowInfoPopup(msg);
        }

        static InfoPopup popup;

        // 非模态信息浮层：不抢焦点、不阻塞、鼠标移开自动消失
        static void ShowInfoPopup(string text)
        {
            if (popup != null)
            {
                try { popup.Close(); popup.Dispose(); } catch { }
                popup = null;
            }
            popup = new InfoPopup(text);
            popup.FormClosed += delegate { popup = null; };
            popup.ShowNear(Cursor.Position);
        }

        // ---------------------------------------------------------- 设备读取

        static void Supervise()
        {
            while (running)
            {
                try
                {
                    List<HidInterface> all = Reader.Enumerate();
                    // [PATCH] 诊断：已知厂商 VID 的接口，每个路径只记一次
                    bool wiredNow = false;
                    foreach (HidInterface d in all)
                    {
                        if (IsWiredSibling(d)) wiredNow = true;
                        if (!IsKnownVid(d.Vid)) continue;
                        bool fresh;
                        lock (probedPaths) fresh = probedPaths.Add(d.Path);
                        if (!fresh) continue;
                        Log(string.Format("iface: {0:X4}:{1:X4} usagePage=0x{2:X4} usage=0x{3:X4} in={4} out={5} feat={6}  {7}",
                            d.Vid, d.Pid, d.UsagePage, d.Usage, d.InLen, d.OutLen, d.FeatLen,
                            string.IsNullOrEmpty(d.Product) ? "(无产品名)" : d.Product));
                    }
                    if (wiredNow != wiredCharging)
                    {
                        wiredCharging = wiredNow;
                        Log("wired charging mode: " + (wiredNow ? "ON（检测到有线设备）" : "OFF"));
                    }
                    var targets = new List<HidInterface>();
                    int skippedByUsage = 0;
                    foreach (HidInterface d in all)
                    {
                        bool matched = false;
                        foreach (ushort[] c in Candidates)
                        {
                            if (d.Vid == c[0] && d.Pid == c[1]) { matched = true; break; }
                        }
                        if (matched)
                        {
                            // [PATCH] 只保留电量接口。原版只按 VID/PID 匹配，会把同一只鼠标的
                            // 消费者控制(0x000C)、系统控制(0x0001)、厂商通道(0xFF00) 一并打开读取。
                            // 实测 KP 8K：电量在 UsagePage=0x000A / usage=0x0000 / in=5。
                            if (IsIn10(d)) { d.IsIn9 = false; targets.Add(d); }
                            else skippedByUsage++;
                            continue;
                        }

                        if (IsIn9Vid(d.Vid))
                        {
                            bool featOk = d.FeatLen >= 33;
                            Log(string.Format("IN9 iface: in={0} out={1} feat={2} {3}{4}",
                                d.InLen, d.OutLen, d.FeatLen, d.Product,
                                featOk ? "  <- candidate" : ""));
                            // IN9 走 Feature Report（报告 ID 0x0C，33 字节）
                            if (featOk) { d.IsIn9 = true; targets.Add(d); }
                        }
                    }
                    Log(string.Format("enumerate: {0} hid interfaces, {1} candidates ({2} IN9), {3} skipped by usage page",
                        all.Count, targets.Count, CountIn9(targets), skippedByUsage));

                    // [PATCH] 清理已经不存在的接口状态。
                    // 同一只鼠标插线时会多枚举出一台 FA93（有线模式），拔线后这台设备整个
                    // 从系统里消失，但 states 里的记录不会自己走，于是详情浮层里永远多出
                    // 一条"已断开 / 已关机"。这里按本轮枚举结果把消失的路径删掉。
                    // 注意：只删"系统里已经不存在"的路径。接收器还插着、只是鼠标关机导致
                    // 链路静默的情况，路径仍在枚举结果里，记录会保留并正常显示"已断开"。
                    var presentPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    foreach (HidInterface d in all) presentPaths.Add(d.Path);
                    var gone = new List<string>();
                    lock (sync)
                    {
                        foreach (var kv in states)
                        {
                            if (!presentPaths.Contains(kv.Key)) gone.Add(kv.Key);
                        }
                        foreach (string k in gone) states.Remove(k);
                    }
                    foreach (string k in gone)
                    {
                        Log("state pruned: " + k);
                    }

                    foreach (HidInterface d in targets)
                    {
                        HidInterface dd = d;
                        lock (activePaths)
                        {
                            if (activePaths.Contains(dd.Path)) continue;
                            activePaths.Add(dd.Path);
                        }
                        var t = new Thread(delegate ()
                        {
                            try
                            {
                                if (dd.IsIn9) In9QueryLoop(dd);
                                else ReadLoop(dd);
                            }
                            finally
                            {
                                lock (activePaths) activePaths.Remove(dd.Path);
                            }
                        });
                        t.IsBackground = true;
                        t.Start();
                    }

                    Thread.Sleep(10000);
                }
                catch (ThreadInterruptedException)
                {
                    // [PATCH] Rescan() 的唤醒信号：立刻重新枚举，不要再等
                }
                catch (Exception)
                {
                    Thread.Sleep(5000);
                }
            }
        }

        // ---------------------------------------------------------- IN9（主动查询）

        // IN9 系列：USB=VID_248A，2.4G=VID_249A（PID 不固定）
        static readonly ushort[] In9Vids = new ushort[] { 0x248A, 0x249A };

        static bool IsIn9Vid(ushort vid)
        {
            for (int i = 0; i < In9Vids.Length; i++)
                if (In9Vids[i] == vid) return true;
            return false;
        }

        static int CountIn9(List<HidInterface> list)
        {
            int n = 0;
            foreach (HidInterface d in list) if (d.IsIn9) n++;
            return n;
        }

        static string Hex(byte[] b, int n)
        {
            var sb = new System.Text.StringBuilder();
            for (int i = 0; i < n && i < b.Length; i++)
                sb.Append(b[i].ToString("X2")).Append(' ');
            return sb.ToString().Trim();
        }

        // IN9 命令帧（33 字节 Feature Report，实测自 IN9 升级版）
        //   0C 01 <cmd> 00 <seq> <payload...>
        //   握手 cmd=0xF0 payload=01 01 FF
        //   查电量 cmd=0x20 payload=00 00
        static byte[] BuildIn9Frame(byte cmd, byte seq, byte[] payload)
        {
            byte[] f = new byte[33];
            f[0] = 0x0C;      // 报告 ID
            f[1] = 0x01;
            f[2] = cmd;
            f[3] = 0x00;
            f[4] = seq;
            if (payload != null)
                Array.Copy(payload, 0, f, 5, Math.Min(payload.Length, 28));
            return f;
        }

        static bool HidWrite(IntPtr h, byte[] frame, int outLen)
        {
            if (outLen < frame.Length) outLen = frame.Length;
            byte[] b = new byte[outLen];
            Array.Copy(frame, b, frame.Length);

            IntPtr ev = Native.CreateEventW(IntPtr.Zero, true, false, IntPtr.Zero);
            if (ev == IntPtr.Zero) return false;
            IntPtr ovPtr = Marshal.AllocHGlobal(Marshal.SizeOf(typeof(Native.OVERLAPPED)));
            try
            {
                Native.OVERLAPPED ov = new Native.OVERLAPPED();
                ov.hEvent = ev;
                Marshal.StructureToPtr(ov, ovPtr, false);

                int written = 0;
                bool ok = Native.WriteFile(h, b, outLen, out written, ovPtr);
                if (!ok && Marshal.GetLastWin32Error() != Native.ERROR_IO_PENDING) return false;

                if (Native.WaitForSingleObject(ev, 1000) != Native.WAIT_OBJECT_0)
                {
                    Native.CancelIoEx(h, ovPtr);
                    return false;
                }
                int got = 0;
                return Native.GetOverlappedResult(h, ovPtr, out got, false);
            }
            finally
            {
                Marshal.FreeHGlobal(ovPtr);
                Native.CloseHandle(ev);
            }
        }

        static int HidReadTimeout(IntPtr h, byte[] buf, int ms)
        {
            IntPtr ev = Native.CreateEventW(IntPtr.Zero, true, false, IntPtr.Zero);
            if (ev == IntPtr.Zero) return -1;
            IntPtr ovPtr = Marshal.AllocHGlobal(Marshal.SizeOf(typeof(Native.OVERLAPPED)));
            try
            {
                Native.OVERLAPPED ov = new Native.OVERLAPPED();
                ov.hEvent = ev;
                Marshal.StructureToPtr(ov, ovPtr, false);

                int read = 0;
                bool ok = Native.ReadFile(h, buf, buf.Length, out read, ovPtr);
                if (!ok && Marshal.GetLastWin32Error() != Native.ERROR_IO_PENDING) return -1;

                if (Native.WaitForSingleObject(ev, (uint)ms) != Native.WAIT_OBJECT_0)
                {
                    Native.CancelIoEx(h, ovPtr);
                    return 0;
                }
                int got = 0;
                if (!Native.GetOverlappedResult(h, ovPtr, out got, false)) return -1;
                return got;
            }
            finally
            {
                Marshal.FreeHGlobal(ovPtr);
                Native.CloseHandle(ev);
            }
        }

        static void In9QueryLoop(HidInterface dev)
        {
            string label = string.Format("{0:X4}:{1:X4}", dev.Vid, dev.Pid);
            Log("IN9 try open: " + dev.Path);
            IntPtr h = Native.CreateFileW(dev.Path,
                Native.GENERIC_READ | Native.GENERIC_WRITE,
                Native.FILE_SHARE_READ | Native.FILE_SHARE_WRITE,
                IntPtr.Zero, Native.OPEN_EXISTING, 0, IntPtr.Zero);
            if (h == Native.INVALID_HANDLE_VALUE)
            {
                Log("IN9 open FAILED err=" + Marshal.GetLastWin32Error());
                return;
            }
            Log("IN9 opened OK: " + dev.Product + "  feat=" + dev.FeatLen);

            byte seq = 0x01;
            byte[] resp = new byte[33];
            int failStreak = 0;
            int polls = 0;

            try
            {
                while (running)
                {
                    // 握手（只发不收）：0C 01 F0 00 <seq> 01 01 FF
                    byte[] hs = BuildIn9Frame(0xF0, seq, new byte[] { 0x01, 0x01, 0xFF });
                    Native.HidD_SetFeature(h, hs, hs.Length);

                    // 查电量：0C 01 20 00 <seq> 00 00
                    byte[] q = BuildIn9Frame(0x20, seq, new byte[] { 0x00, 0x00 });
                    polls++;

                    if (!Native.HidD_SetFeature(h, q, q.Length))
                    {
                        Log("IN9 SetFeature FAILED err=" + Marshal.GetLastWin32Error());
                        if (++failStreak >= 3) { Log("IN9 give up this interface"); break; }
                    }
                    else
                    {
                        failStreak = 0;
                        Thread.Sleep(10);
                        Array.Clear(resp, 0, resp.Length);
                        if (!Native.HidD_GetFeature(h, resp, resp.Length))
                        {
                            Log("IN9 GetFeature FAILED err=" + Marshal.GetLastWin32Error());
                        }
                        else
                        {
                            if (polls <= 5) Log("IN9 rx: " + Hex(resp, 33));
                            AcceptIn9(dev, label, resp, polls <= 5);
                        }
                    }

                    seq++;
                    if (seq == 0) seq = 1;
                    Thread.Sleep(2500);
                }
            }
            catch (ThreadInterruptedException) { }
            catch (Exception ex) { Log("IN9 loop exception: " + ex.Message); }
            finally { Native.CloseHandle(h); }
        }

        static void AcceptIn9(HidInterface dev, string label, byte[] resp, bool dump)
        {
            // 实测布局：(IN9 升级版)
            //   [0]=0x0C [1]=0x01 [2]=0x20(命令回显) [6]!=0 有效
            //   [8..0x0B]="M909" [0x0F]=IC [0x12]=电量 [0x13]==2 充电中 [0x15]=在线
            bool echo = resp[2] == 0x20;
            bool valid = resp[6] != 0;
            int lv = -1;
            if (echo && valid)
            {
                int v = resp[0x12];
                if (v >= 0 && v <= 100) lv = v;
            }

            if (lv < 0)
            {
                Log("IN9 未认定电量 echo=0x" + resp[2].ToString("X2") + " flag=" + resp[6]
                    + "  raw=" + Hex(resp, 33));
                return;
            }

            int charge = (resp[0x13] == 2) ? 3 : 1;   // 3=充电中，1=正常

            DateTime now = DateTime.Now;
            lock (sync)
            {
                DeviceState st;
                // [PATCH] 按接口路径做键。原来用产品名当键，两只同型号接收器会互相覆盖。
                if (!states.TryGetValue(dev.Path, out st))
                {
                    st = new DeviceState();
                    st.Label = label;
                    states[dev.Path] = st;
                }
                if (st.LastSeen != DateTime.MinValue)
                {
                    // [PATCH] 同 ReadLoop：跨过断连空档的间隔不是真实刷新周期
                    double gap = (now - st.LastSeen).TotalMilliseconds;
                    if (gap <= OfflineAfterMs) st.LastIntervalMs = gap;
                }

                bool changed = (st.Battery != lv || st.ChargeState != charge);
                st.Battery = lv;
                st.ChargeState = charge;
                st.Protocol = "IN9 query";
                st.LastSeen = now;
                st.Product = dev.Product;
                st.Vid = dev.Vid;
                st.Pid = dev.Pid;
                st.Transport = dev.Transport;

                if (changed || dump)
                    Log("IN9 battery " + lv + "%  charge=" + charge + "  [" + dev.Product + "]");
            }
            UpdateTrayUI();
        }

        static void ReadLoop(HidInterface dev)
        {
            string label = string.Format("{0:X4}:{1:X4}", dev.Vid, dev.Pid);
            if (!string.IsNullOrEmpty(dev.Product)) label = dev.Product + " [" + label + "]";

            IntPtr h = Native.CreateFileW(dev.Path,
                Native.GENERIC_READ | Native.GENERIC_WRITE,
                Native.FILE_SHARE_READ | Native.FILE_SHARE_WRITE,
                IntPtr.Zero, Native.OPEN_EXISTING, Native.FILE_FLAG_OVERLAPPED, IntPtr.Zero);
            if (h == Native.INVALID_HANDLE_VALUE)
            {
                h = Native.CreateFileW(dev.Path, Native.GENERIC_READ,
                    Native.FILE_SHARE_READ | Native.FILE_SHARE_WRITE,
                    IntPtr.Zero, Native.OPEN_EXISTING, Native.FILE_FLAG_OVERLAPPED, IntPtr.Zero);
            }
            if (h == Native.INVALID_HANDLE_VALUE) return;
            // [PATCH] 把 UsagePage / Usage / 报告长度打进日志，用于确认电量接口的真实归属
            // （上游 PR #1 硬性要求 UsagePage==0x000A 才认 IN10，需先实测确认再决定是否照做）
            Log(string.Format("opened: {0}  usagePage=0x{1:X4} usage=0x{2:X4}  in={3} out={4} feat={5}",
                label, dev.UsagePage, dev.Usage, dev.InLen, dev.OutLen, dev.FeatLen));

            IntPtr ev = Native.CreateEventW(IntPtr.Zero, true, false, IntPtr.Zero);
            var buf = new byte[256];
            // [PATCH] 异步 IO 期间必须固定缓冲区。原版把托管数组直接传给 overlapped ReadFile，
            // 调用返回后数组就解 pin 了，内核仍可能在后台写入；这期间 GC 压缩堆会把数组搬走，
            // 导致内核写到错误地址（堆损坏）。固定到整个读取循环结束为止。
            GCHandle bufPin = GCHandle.Alloc(buf, GCHandleType.Pinned);
            var ov = new Native.OVERLAPPED();
            ov.hEvent = ev;
            IntPtr ovPtr = Marshal.AllocHGlobal(Marshal.SizeOf(typeof(Native.OVERLAPPED)));

            DateTime lastReport = DateTime.MinValue;

            try
            {
                while (running)
                {
                    Marshal.StructureToPtr(ov, ovPtr, false);
                    Native.ResetEvent(ev);
                    int read = 0;
                    bool ok = Native.ReadFile(h, buf, buf.Length, out read, ovPtr);
                    int err = Marshal.GetLastWin32Error();
                    if (!ok && err != Native.ERROR_IO_PENDING) break;

                    uint rc = Native.WaitForSingleObject(ev, 3000);
                    if (rc != Native.WAIT_OBJECT_0)
                    {
                        // [PATCH] 取消是异步的：CancelIoEx 只是发请求，内核可能仍在访问
                        // ovPtr 和 buf。必须阻塞等取消真正完成再进入下一轮，否则
                        // finally 里释放 ovPtr 时就是 use-after-free。
                        Native.CancelIoEx(h, ovPtr);
                        int aborted;
                        Native.GetOverlappedResult(h, ovPtr, out aborted, true);
                        continue;
                    }
                    int got = 0;
                    if (!Native.GetOverlappedResult(h, ovPtr, out got, false)) continue;
                    if (got <= 0) continue;

                    // 电量事件：0x40 / 0x41，第 4 字节是充电状态（不是设备号）
                    if (buf[0] == 0x03 && TypeCodes.Contains(buf[1]) &&
                        (buf[2] == 0x40 || buf[2] == 0x41) && got >= 5)
                    {
                        int charge = buf[3];
                        int level = buf[4];
                        if (level < 0 || level > 100) continue;

                        DateTime now = DateTime.Now;
                        double interval = 0;
                        if (lastReport != DateTime.MinValue)
                        {
                            // [PATCH] 跨过断连空档算出来的不是刷新周期。插拔线切换时会
                            // 出现 20s+ 的假值（实测拔线后首条显示 26654ms），过滤掉。
                            double gap = (now - lastReport).TotalMilliseconds;
                            if (gap <= OfflineAfterMs) interval = gap;
                        }
                        lastReport = now;

                        lock (sync)
                        {
                            DeviceState st;
                            // [PATCH] 同上，按接口路径做键
                            if (!states.TryGetValue(dev.Path, out st))
                            {
                                st = new DeviceState();
                                st.Label = label;
                                states[dev.Path] = st;
                            }
                            st.Battery = level;
                            st.ChargeState = charge;
                            st.Protocol = "IN10 push";
                            st.LastSeen = now;
                            st.Product = dev.Product;
                            st.Vid = dev.Vid;
                            st.Pid = dev.Pid;
                            st.Transport = dev.Transport;
                            st.TypeCode = buf[1];
                            if (interval > 0) st.LastIntervalMs = interval;
                        }
                        UpdateTrayUI();
                        Log(string.Format("battery {0}%  charge={1}  {2:F0}ms  transport={3}  type=0x{4:X2}  [{5}]",
                            level, charge, interval, dev.Transport, buf[1], label));
                    }
                }
            }
            catch (ThreadInterruptedException) { }
            catch (Exception) { }
            finally
            {
                if (bufPin.IsAllocated) bufPin.Free();   // [PATCH] 释放固定
                Marshal.FreeHGlobal(ovPtr);
                Native.CloseHandle(ev);
                Native.CloseHandle(h);
            }
        }

        static void TickStatus()
        {
            int level = -1;
            int charge = -1;
            DateTime newest = DateTime.MinValue;
            double interval = 0;

            lock (sync)
            {
                foreach (var kv in states)
                {
                    if (kv.Value.LastSeen > newest)
                    {
                        newest = kv.Value.LastSeen;
                        level = kv.Value.Battery;
                        charge = kv.Value.ChargeState;
                        interval = kv.Value.LastIntervalMs;
                    }
                }
            }

            bool offline = (newest == DateTime.MinValue) ||
                           ((DateTime.Now - newest).TotalMilliseconds > OfflineAfterMs);

            RefreshTray(level, interval, offline, newest, charge);
        }

        static void RefreshTray(int level, double interval, bool offline, DateTime lastSeen, int charge)
        {
            // [PATCH] 加锁。原版 TickStatus(UI 线程, 1s) 和 UpdateTrayUI(读取线程, 2s)
            // 会并发进入这里，DrawIcon 里对同一个 srcIcon Bitmap 并发 DrawImage，
            // GDI+ 的 Image 不是线程安全的，会偶发 "A generic error occurred in GDI+"。
            lock (trayLock)
            {
                // [PATCH] 插线充电时 2.4G 链路会静默，但设备还在，不能当"已关机"灰显。
                // 此时保持正常配色 + 显示最后电量，靠 tooltip 说明是在充电。
                // 要求 level>=0：只有"先有过上报、随后静默、同时有线设备出现"才算插线充电，
                // 避免纯有线使用（从没收到过 2.4G 数据）被误判成一直在充电。
                bool cable = offline && wiredCharging && level >= 0;
                bool grey = offline && !cable;
                if (level != lastDrawnLevel || grey != lastDrawnOffline)
                {
                    Log(string.Format("tray state: level={0} offline={1} grey={2} cable={3}",
                        level, offline, grey, cable));
                    Icon old = currentIcon;
                    currentIcon = DrawIcon(level, grey);
                    tray.Icon = currentIcon;
                    lastDrawnLevel = level;
                    lastDrawnOffline = grey;
                    if (old != null) { try { old.Dispose(); } catch { } }
                }

                string tip;
                if (cable)
                    tip = string.Format("英菲克：充电中（有线，最后 {0}%）", level);
                else if (level < 0)
                    tip = "英菲克：未检测到设备";
                else if (offline)
                    tip = string.Format("英菲克：已断开 / 已关机（最后 {0}%，{1:HH:mm:ss}）",
                        level, lastSeen);
                else
                {
                    tip = string.Format("英菲克 电量 {0}%  ({1:F0}ms 刷新)", level, interval);
                    // [PATCH] 无线充电时也把充电状态写进 tooltip（原来只有详情浮层里有）
                    if (charge == 3) tip += "  充电中";
                    else if (charge == 2) tip += "  已充满";
                }

                // [PATCH] NotifyIcon.Text 上限是 63 字符（szTip 64 wchar），原版写的 127 会抛异常
                if (tip.Length > 63) tip = tip.Substring(0, 63);
                tray.Text = tip;
            }
        }

        static void UpdateTrayUI()
        {
            int level = -1;
            int charge = -1;
            double interval = 0;
            DateTime newest = DateTime.MinValue;
            lock (sync)
            {
                foreach (var kv in states)
                {
                    if (kv.Value.LastSeen > newest)
                    {
                        newest = kv.Value.LastSeen;
                        level = kv.Value.Battery;
                        charge = kv.Value.ChargeState;
                        interval = kv.Value.LastIntervalMs;
                    }
                }
            }
            if (level < 0) return;

            bool offline = ((DateTime.Now - newest).TotalMilliseconds > OfflineAfterMs);
            RefreshTray(level, interval, offline, newest, charge);
        }
    }

    // 轻量信息浮层：不抢焦点、不阻塞，鼠标移开自动消失
    class InfoPopup : Form
    {
        System.Windows.Forms.Timer lifeTimer;
        // [PATCH] 缓存字体引用，关闭时释放（原版 new Font 后从不 Dispose，泄漏 GDI 句柄）
        System.Drawing.Font lblFont;
        int countdown;

        protected override bool ShowWithoutActivation { get { return true; } }

        protected override CreateParams CreateParams
        {
            get
            {
                CreateParams cp = base.CreateParams;
                cp.ExStyle |= 0x00000080;   // WS_EX_TOOLWINDOW：不进 Alt+Tab
                cp.ExStyle |= 0x08000000;   // WS_EX_NOACTIVATE：不抢焦点
                return cp;
            }
        }

        public InfoPopup(string text)
        {
            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            TopMost = true;
            StartPosition = FormStartPosition.Manual;
            BackColor = Color.FromArgb(28, 28, 32);

            Label lbl = new Label();
            lbl.Dock = DockStyle.Fill;
            lbl.Text = text;
            lbl.ForeColor = Color.FromArgb(236, 236, 242);
            lblFont = new System.Drawing.Font("Consolas", 9F, FontStyle.Regular, GraphicsUnit.Point);
            lbl.Font = lblFont;
            lbl.TextAlign = ContentAlignment.TopLeft;
            lbl.BackColor = Color.Transparent;
            Controls.Add(lbl);

            Padding = new Padding(18, 13, 18, 13);
            Size ts = TextRenderer.MeasureText(text, lbl.Font,
                new Size(560, 2000), TextFormatFlags.Left | TextFormatFlags.Top);
            ClientSize = new Size(ts.Width + 36, ts.Height + 26);

            lifeTimer = new System.Windows.Forms.Timer();
            lifeTimer.Interval = 1000;
            lifeTimer.Tick += delegate
            {
                countdown--;
                if (countdown <= 0) Close();
            };

            EventHandler enter = delegate { countdown = 9999; };
            EventHandler leave = delegate { countdown = 2; };
            MouseEnter += enter;
            MouseLeave += leave;
            lbl.MouseEnter += enter;
            lbl.MouseLeave += leave;
            Click += delegate { Close(); };
            lbl.Click += delegate { Close(); };

            // [PATCH] 关闭时停掉并释放定时器。原版 lifeTimer 从不 Stop/Dispose，
            // 浮层关掉后仍每秒触发一次，每开一次详情就永久泄漏一个 1Hz 定时器。
            FormClosed += delegate
            {
                try { lifeTimer.Stop(); lifeTimer.Dispose(); } catch { }
                try { if (lblFont != null) { lblFont.Dispose(); lblFont = null; } } catch { }
            };
        }

        public void ShowNear(Point anchor)
        {
            Show();
            Rectangle wa = Screen.FromPoint(anchor).WorkingArea;
            int x = anchor.X - Width / 2;
            int y = anchor.Y - Height - 16;
            if (y < wa.Top) y = anchor.Y + 20;      // 上方放不下就翻到下方
            if (x < wa.Left) x = wa.Left + 4;
            if (x + Width > wa.Right) x = wa.Right - Width - 4;
            Location = new Point(x, y);
            countdown = 4;                          // 4 秒后自动消失；鼠标停留则保持
            lifeTimer.Start();
        }
    }
}
