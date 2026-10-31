// MKW Ultrawide Patcher - .exe version (Windows, .NET Framework 4.x, no Python needed)
// Build: mcs -sdk:4.5 -target:winexe -optimize+ -r:System.Windows.Forms.dll -r:System.Drawing.dll UltrawidePatcher.cs

using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

[assembly: AssemblyTitle("MKW Ultrawide Patcher")]
[assembly: AssemblyProduct("MKW Ultrawide Patcher")]
[assembly: AssemblyDescription("Reversible ultrawide patch for Mario Kart World on Eglantine")]
[assembly: AssemblyVersion("1.0.0.0")]
[assembly: AssemblyFileVersion("1.0.0.0")]

namespace UltrawidePatcher
{
    // =====================================================================
    //  ENGINE
    // =====================================================================

    class Segment
    {
        public string Name;
        public uint MemOffset;
        public List<byte> Data;
    }

    class Nso
    {
        public byte[] Header;
        public uint Flags;
        public byte[] Pre;
        public Segment[] Segs = new Segment[3];
        public string BuildId;
        static readonly string[] Names = { "text", "ro", "data" };

        public Nso(byte[] raw)
        {
            if (raw.Length < 0x100 || raw[0] != 'N' || raw[1] != 'S' || raw[2] != 'O' || raw[3] != '0')
                throw new Exception("This is not an NSO file (missing NSO0 signature).");
            Header = new byte[0x100];
            Array.Copy(raw, Header, 0x100);
            Flags = U32(raw, 0x0C);
            uint first = uint.MaxValue;
            for (int i = 0; i < 3; i++)
            {
                int h = 0x10 + i * 0x10;
                uint foff = U32(raw, h), moff = U32(raw, h + 4), size = U32(raw, h + 8);
                uint csize = U32(raw, 0x60 + i * 4);
                bool comp = (Flags & (1u << i)) != 0;
                first = Math.Min(first, foff);
                byte[] blob = new byte[comp ? csize : size];
                Array.Copy(raw, (int)foff, blob, 0, blob.Length);
                byte[] data = comp ? Lz4.Decompress(blob, (int)size) : blob;
                Segs[i] = new Segment { Name = Names[i], MemOffset = moff, Data = new List<byte>(data) };
            }
            Pre = new byte[first - 0x100];
            Array.Copy(raw, 0x100, Pre, 0, Pre.Length);
            BuildId = BuildIdOf(raw);
        }

        public static string BuildIdOf(byte[] head)
        {
            var sb = new StringBuilder();
            for (int i = 0x40; i < 0x60; i++) sb.Append(head[i].ToString("X2"));
            string s = sb.ToString().TrimEnd('0');
            return s.Length == 0 ? "0" : s;
        }

        public byte[] Build()
        {
            byte[] h = (byte[])Header.Clone();
            W32(h, 0x0C, Flags & ~0x3Fu);
            var outp = new List<byte>(h);
            outp.AddRange(Pre);
            for (int i = 0; i < 3; i++)
            {
                int ho = 0x10 + i * 0x10;
                W32(h, ho, (uint)outp.Count);
                W32(h, ho + 4, Segs[i].MemOffset);
                W32(h, ho + 8, (uint)Segs[i].Data.Count);
                W32(h, 0x60 + i * 4, (uint)Segs[i].Data.Count);
                outp.AddRange(Segs[i].Data);
            }
            byte[] res = outp.ToArray();
            Array.Copy(h, res, 0x100);
            return res;
        }

        public static uint U32(byte[] b, int o) { return BitConverter.ToUInt32(b, o); }
        public static void W32(byte[] b, int o, uint v) { BitConverter.GetBytes(v).CopyTo(b, o); }
    }

    static class Lz4
    {
        public static byte[] Decompress(byte[] src, int outSize)
        {
            byte[] dst = new byte[outSize];
            int i = 0, o = 0, n = src.Length;
            while (i < n)
            {
                int token = src[i++];
                int lit = token >> 4;
                if (lit == 15) { int b; do { b = src[i++]; lit += b; } while (b == 255); }
                Buffer.BlockCopy(src, i, dst, o, lit);
                i += lit; o += lit;
                if (i >= n) break;
                int off = src[i] | (src[i + 1] << 8);
                i += 2;
                int ml = token & 15;
                if (ml == 15) { int b; do { b = src[i++]; ml += b; } while (b == 255); }
                ml += 4;
                int s = o - off;
                if (off >= ml) { Buffer.BlockCopy(dst, s, dst, o, ml); o += ml; }
                else { for (int k = 0; k < ml; k++) dst[o++] = dst[s + k]; }
            }
            if (o != outSize) throw new Exception("LZ4: unexpected size (" + o + " != " + outSize + ")");
            return dst;
        }
    }

    class GameInfo
    {
        public string Name;
        public uint[] Sites;
        public Dictionary<string, uint[]> Groups;
    }

    static class Engine
    {
        public const float OrigRatio = 16f / 9f;
        public const string TestedBuild = "D00E4F399E411D0613AA8D26B1C02FEF";
        public const string TestedName = "Mario Kart World 1.8.0 (update 917504) on Eglantine";

        public static readonly Dictionary<string, GameInfo> Known = new Dictionary<string, GameInfo>
        {
            { TestedBuild, new GameInfo {
                Name = "Mario Kart World 1.8.0",
                Sites = new uint[] { 0x0008A6D4, 0x0008A784, 0x00DEFAFC, 0x00DEFDD4, 0x00DEFEC0 },
                Groups = new Dictionary<string, uint[]> {
                    { "A", new uint[] { 0x0008A6D4, 0x0008A784 } },
                    { "B", new uint[] { 0x00DEFAFC, 0x00DEFDD4, 0x00DEFEC0 } } } } },
        };

        public static Action<string> Log = s => { };

        public static string VariantName(string v)
        {
            return v == "B" ? "game cameras only" : v == "A" ? "layer system only" : "everything";
        }

        public class Paths
        {
            public string Main, Orig, State;
            public Paths(string folder)
            {
                Main = Path.Combine(folder, "main");
                Orig = Path.Combine(folder, "main.original");
                State = Path.Combine(folder, "ultrawide_estado.json");
            }
        }

        static bool IsFdiv(uint w) { return (w & 0xFFE0FC00) == 0x1E201800; }
        static bool IsFsub(uint w) { return (w & 0xFFE0FC00) == 0x1E203800; }

        static int LdrFirstOff(uint w)
        {
            if ((w & 0xFFC00000) == 0xBD400000) return (int)((w >> 10) & 0xFFF) * 4;
            if ((w & 0xFFC00000) == 0x2D400000) return (int)((w >> 15) & 0x7F) * 4;
            return 0;
        }

        static bool StoresTo(uint w, uint rt, int off)
        {
            return (w & 0xFFC00000) == 0xBD000000 && (w & 31) == rt && ((w >> 10) & 0xFFF) * 4 == off;
        }

        static uint Word(List<byte> d, int a)
        {
            return (uint)(d[a] | (d[a + 1] << 8) | (d[a + 2] << 16) | (d[a + 3] << 24));
        }

        static void Put(List<byte> d, int a, uint v)
        {
            d[a] = (byte)v; d[a + 1] = (byte)(v >> 8); d[a + 2] = (byte)(v >> 16); d[a + 3] = (byte)(v >> 24);
        }

        public static string FindSites(Nso nso, out List<uint> sites)
        {
            var text = nso.Segs[0].Data;
            GameInfo gi;
            if (Known.TryGetValue(nso.BuildId, out gi) && gi.Sites.All(a => IsFdiv(Word(text, (int)a))))
            {
                sites = gi.Sites.ToList();
                return gi.Name;
            }
            int n = text.Count / 4;
            uint[] w = new uint[n];
            byte[] arr = text.ToArray();
            Buffer.BlockCopy(arr, 0, w, 0, n * 4);
            int[] viewport = { 0x7B0, 0x7B4, 0x7B8, 0x7BC };
            sites = new List<uint>();
            for (int i = 12; i < n - 8; i++)
            {
                uint x = w[i];
                if (!IsFdiv(x)) continue;
                uint rd = x & 31, rn = (x >> 5) & 31, rm = (x >> 16) & 31;
                var subs = new HashSet<uint>();
                bool vp = false;
                for (int j = i - 12; j < i; j++)
                {
                    if (IsFsub(w[j])) subs.Add(w[j] & 31);
                    if (viewport.Contains(LdrFirstOff(w[j]))) vp = true;
                }
                if (!subs.Contains(rn) || !subs.Contains(rm)) continue;
                bool st = false;
                for (int j = i + 1; j < i + 6; j++) if (StoresTo(w[j], rd, 0xB0)) st = true;
                if (vp || st) sites.Add((uint)(i * 4));
            }
            return sites.Count > 0 ? "Unknown (auto-detection)" : null;
        }

        static uint B(long src, long dst) { return 0x14000000u | (uint)(((dst - src) >> 2) & 0x3FFFFFF); }

        public static float ApplyCamera(Nso nso, double ratio, List<uint> sites)
        {
            float factor = (float)(ratio / (16.0 / 9.0));
            var text = nso.Segs[0].Data;
            long limit = nso.Segs[1].MemOffset;
            int cave = (text.Count + 15) & ~15;
            int need = 16 + sites.Count * 24;
            if (cave + need > limit) throw new Exception("Not enough free space at the end of .text for the patch.");
            while (text.Count < cave + need) text.Add(0);
            int lit = cave;
            Put(text, lit, BitConverter.ToUInt32(BitConverter.GetBytes(factor), 0));
            int stub = cave + 16;
            foreach (uint site in sites)
            {
                uint orig = Word(text, (int)site);
                uint rd = orig & 31;
                uint[] code = {
                    orig,                                                                  // fdiv Sd, Sn, Sm
                    0xFC1F0FFF,                                                            // str  d31, [sp, #-16]!
                    0x1C000000u | ((uint)(((lit - (stub + 8)) >> 2) & 0x7FFFF) << 5) | 31, // ldr  s31, =factor
                    0x1E200800u | (31u << 16) | (rd << 5) | rd,                            // fmul Sd, Sd, s31
                    0xFC4107FF,                                                            // ldr  d31, [sp], #16
                    B(stub + 20, site + 4),                                                // b    back
                };
                for (int k = 0; k < code.Length; k++) Put(text, stub + 4 * k, code[k]);
                Put(text, (int)site, B(site, stub));
                stub += 24;
            }
            return factor;
        }

        static string Sha1(string path)
        {
            using (var s = File.OpenRead(path))
            using (var h = SHA1.Create())
                return BitConverter.ToString(h.ComputeHash(s)).Replace("-", "").ToLowerInvariant();
        }

        static string ReadState(Paths p)
        {
            return File.Exists(p.State) ? File.ReadAllText(p.State, Encoding.UTF8) : null;
        }

        static string JStr(string json, string key)
        {
            var m = Regex.Match(json, "\"" + key + "\"\\s*:\\s*\"([^\"]*)\"");
            return m.Success ? m.Groups[1].Value : null;
        }

        static double? JNum(string json, string key)
        {
            var m = Regex.Match(json, "\"" + key + "\"\\s*:\\s*([-0-9.eE+]+)");
            double v;
            if (m.Success && double.TryParse(m.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out v)) return v;
            return null;
        }

        public static string BuildIdOfFile(string path)
        {
            byte[] head = new byte[0x60];
            using (var f = File.OpenRead(path))
                if (f.Read(head, 0, 0x60) < 0x60) return null;
            if (head[0] != 'N' || head[1] != 'S' || head[2] != 'O' || head[3] != '0') return null;
            return Nso.BuildIdOf(head);
        }

        public static string Status(Paths p, out bool patched)
        {
            string st = ReadState(p);
            patched = st != null;
            if (st == null) return "ORIGINAL";
            double ratio = JNum(st, "ratio") ?? 0;
            string modo = JStr(st, "modo");
            if (modo == "camara")
                return string.Format(CultureInfo.InvariantCulture, "PATCHED - camera, variant: {0} (ratio {1:0.0000})",
                    VariantName(JStr(st, "variante") ?? "todo"), ratio);
            return string.Format(CultureInfo.InvariantCulture, "PATCHED - 16:9 values (ratio {0:0.0000})", ratio);
        }

        static Nso LoadOriginal(Paths p)
        {
            if (!File.Exists(p.Main) && !File.Exists(p.Orig))
                throw new Exception("Can't find 'main' in " + Path.GetDirectoryName(p.Main));
            string src;
            if (File.Exists(p.Orig))
            {
                string st = ReadState(p);
                if (st != null && File.Exists(p.Main))
                {
                    string sh = JStr(st, "sha1_parchado");
                    if (!string.IsNullOrEmpty(sh) && sh != Sha1(p.Main))
                    {
                        Log("\r\n[!] 'main' changed since the last patch (did you update the game?).\r\n");
                        Log("    Using the current 'main' as the new original.\r\n");
                        File.Copy(p.Main, p.Orig, true);
                    }
                }
                src = p.Orig;
            }
            else src = p.Main;
            Log("Reading and decompressing " + Path.GetFileName(src) + " ...\r\n");
            return new Nso(File.ReadAllBytes(src));
        }

        public static void PatchCamera(Paths p, double ratio, string variant)
        {
            Nso nso = LoadOriginal(p);
            Log("Build ID: " + nso.BuildId + "\r\n");
            List<uint> sites;
            string name = FindSites(nso, out sites);
            if (name == null || sites.Count == 0)
                throw new Exception("Could not find the camera aspect calculation in this executable.");
            GameInfo gi;
            if (variant != "todo")
            {
                if (Known.TryGetValue(nso.BuildId, out gi) && gi.Groups != null)
                    sites = sites.Where(s => gi.Groups[variant].Contains(s)).ToList();
                else
                {
                    Log("Variants A/B are only mapped for MKW 1.8.0; patching everything.\r\n");
                    variant = "todo";
                }
            }
            Log("Game: " + name + "   |   Variant: " + VariantName(variant) + "\r\n");
            Log("Aspect calculations found: " + sites.Count + "\r\n");
            foreach (var s in sites) Log("   main+0x" + s.ToString("X8") + "\r\n");

            if (!File.Exists(p.Orig))
            {
                File.Copy(p.Main, p.Orig);
                Log("Backup created: main.original\r\n");
            }
            float factor = ApplyCamera(nso, ratio, sites);
            File.WriteAllBytes(p.Main, nso.Build());

            var inv = CultureInfo.InvariantCulture;
            var json = new StringBuilder();
            json.Append("{\n");
            json.Append("  \"modo\": \"camara\",\n");
            json.Append("  \"variante\": \"" + variant + "\",\n");
            json.Append("  \"ratio\": " + ratio.ToString("R", inv) + ",\n");
            json.Append("  \"factor\": " + ((double)factor).ToString("R", inv) + ",\n");
            json.Append("  \"sitios\": [" + string.Join(", ", sites.Select(s => "\"0x" + s.ToString("X") + "\"")) + "],\n");
            json.Append("  \"build_id\": \"" + nso.BuildId + "\",\n");
            json.Append("  \"sha1_parchado\": \"" + Sha1(p.Main) + "\"\n");
            json.Append("}");
            File.WriteAllText(p.State, json.ToString(), new UTF8Encoding(false));
            Log(string.Format(inv, "\r\nDone: camera set to {0:0.0000} (factor x{1:0.0000}).\r\n", ratio, factor));
        }

        public static void Revert(Paths p)
        {
            if (!File.Exists(p.Orig)) { Log("No main.original found: nothing to restore.\r\n"); return; }
            File.Copy(p.Orig, p.Main, true);
            File.Delete(p.Orig);
            if (File.Exists(p.State)) File.Delete(p.State);
            Log("Restored: 'main' is back to the original. Backup removed.\r\n");
        }
    }

    // =====================================================================
    //  USER INTERFACE
    // =====================================================================

    static class C
    {
        public static Color Bg = Color.FromArgb(0x16, 0x18, 0x1d);
        public static Color Panel = Color.FromArgb(0x1f, 0x22, 0x29);
        public static Color Fg = Color.FromArgb(0xe6, 0xe8, 0xee);
        public static Color Muted = Color.FromArgb(0x9a, 0xa1, 0xad);
        public static Color Accent = Color.FromArgb(0xe2, 0x31, 0x2d);
        public static Color AccentHover = Color.FromArgb(0xf0, 0x44, 0x40);
        public static Color Ok = Color.FromArgb(0x3f, 0xb9, 0x50);
        public static Color Warn = Color.FromArgb(0xd2, 0x99, 0x22);
        public static Color Entry = Color.FromArgb(0x2a, 0x2e, 0x37);
        public static Color EntryHover = Color.FromArgb(0x35, 0x3a, 0x45);
    }

    class MainForm : Form
    {
        static readonly string[] PresetNames = {
            "21:9  -  2560 x 1080", "21:9  -  3440 x 1440", "21:9  -  exact (2.333)",
            "32:9  -  5120 x 1440 / 3840 x 1080", "16:10", "Custom..." };
        static readonly double[] PresetRatios = { 2560.0 / 1080, 3440.0 / 1440, 21.0 / 9, 32.0 / 9, 16.0 / 10, 0 };

        static readonly string[][] Variants = {
            new[] { "todo", "Everything (recommended)", "Cameras + UI with correct proportions.\nIn menus the scenery shows on the sides." },
            new[] { "B", "Game cameras only", "The track looks right. Menus fill the screen,\nbut the HUD stays stretched." },
            new[] { "A", "Layer system only", "Experimental. For testing only." },
        };

        string folder;
        Label lblFolder, lblGame, lblState, lblWarn, lblRatio;
        ComboBox cbPreset;
        TextBox tbW, tbH, tbLog;
        Panel customPanel, pagePatch, pageHelp;
        RadioButton[] radios = new RadioButton[3];
        Button btnApply, btnRevert, tabPatch, tabHelp;
        ProgressBar progress;
        bool busy;

        public MainForm()
        {
            folder = AppDomain.CurrentDomain.BaseDirectory.TrimEnd('\\', '/');
            Text = "MKW Ultrawide Patcher";
            BackColor = C.Bg;
            ForeColor = C.Fg;
            Font = new Font("Segoe UI", 9.5f);
            AutoScaleMode = AutoScaleMode.None;
            FormBorderStyle = FormBorderStyle.FixedSingle;
            MaximizeBox = false;
            ClientSize = new Size(820, 660);
            StartPosition = FormStartPosition.CenterScreen;
            try { Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); } catch { }
            BuildUi();
            ApplyDpiScale();
            Engine.Log = s => AppendLog(s);
            RefreshStatus();
        }

        // The layout is written for 96 DPI. On Windows with display scaling (125%, 150%, 200%...)
        // the fonts grow automatically, so every position and size is scaled by the same factor.
        void ApplyDpiScale()
        {
            float k = 1f;
            bool scaleFonts = false;
            string test = Environment.GetEnvironmentVariable("UWP_TEST_SCALE");
            float tk;
            if (!string.IsNullOrEmpty(test) && float.TryParse(test, NumberStyles.Float, CultureInfo.InvariantCulture, out tk))
            { k = tk; scaleFonts = true; }
            else
                using (var g = CreateGraphics()) k = g.DpiX / 96f;
            if (Math.Abs(k - 1f) < 0.01f) return;

            var all = new List<KeyValuePair<Control, Rectangle>>();
            Collect(this, all);
            var fonts = all.Select(kv => kv.Key.Font).ToList();
            if (scaleFonts) Font = new Font(Font.FontFamily, Font.Size * k, Font.Style);
            ClientSize = new Size((int)(820 * k), (int)(660 * k));
            for (int i = 0; i < all.Count; i++)
            {
                var kv = all[i];
                Control c = kv.Key;
                Rectangle r = kv.Value;
                if (scaleFonts) c.Font = new Font(fonts[i].FontFamily, fonts[i].Size * k, fonts[i].Style);
                c.Location = new Point((int)(r.X * k), (int)(r.Y * k));
                var tb = c as TextBoxBase;
                if (tb != null && !tb.Multiline) c.Width = (int)(r.Width * k);           // height follows the font
                else if (!c.AutoSize || tb != null) c.Size = new Size((int)(r.Width * k), (int)(r.Height * k));
                var cb = c as ComboBox;
                if (cb != null) cb.ItemHeight = (int)(cb.ItemHeight * k);
            }
        }

        static void Collect(Control parent, List<KeyValuePair<Control, Rectangle>> list)
        {
            foreach (Control c in parent.Controls)
            {
                list.Add(new KeyValuePair<Control, Rectangle>(c, c.Bounds));
                Collect(c, list);
            }
        }

        Label MkLabel(string text, Control parent, int x, int y, Color color, float size = 9.5f, bool bold = false, int w = 0)
        {
            var l = new Label {
                Text = text, Left = x, Top = y, ForeColor = color, BackColor = Color.Transparent,
                Font = new Font(bold ? "Segoe UI Semibold" : "Segoe UI", size), AutoSize = w == 0
            };
            if (w > 0) { l.Width = w; l.Height = (int)(size * 2.2); }
            parent.Controls.Add(l);
            return l;
        }

        Button MkButton(string text, Control parent, int x, int y, int w, bool accent)
        {
            var b = new Button {
                Text = text, Left = x, Top = y, Width = w, Height = 34, FlatStyle = FlatStyle.Flat,
                BackColor = accent ? C.Accent : C.Entry, ForeColor = accent ? Color.White : C.Fg,
                Font = new Font(accent ? "Segoe UI Semibold" : "Segoe UI", 9.5f), Cursor = Cursors.Hand
            };
            b.FlatAppearance.BorderSize = 0;
            b.FlatAppearance.MouseOverBackColor = accent ? C.AccentHover : C.EntryHover;
            b.FlatAppearance.MouseDownBackColor = accent ? C.AccentHover : C.EntryHover;
            parent.Controls.Add(b);
            return b;
        }

        void BuildUi()
        {
            MkLabel("MKW Ultrawide Patcher", this, 16, 12, C.Fg, 16f, true);
            MkLabel("Reversible ultrawide patch for Eglantine  ·  tested on " + Engine.TestedName, this, 18, 46, C.Muted, 8.5f);

            tabPatch = MkButton("Patch", this, 16, 74, 110, false);
            tabHelp = MkButton("Instructions", this, 130, 74, 130, false);
            tabPatch.Height = tabHelp.Height = 30;
            tabPatch.Click += (s, e) => ShowPage(true);
            tabHelp.Click += (s, e) => ShowPage(false);

            pagePatch = new Panel { Left = 16, Top = 106, Width = 788, Height = 538, BackColor = C.Panel };
            pageHelp = new Panel { Left = 16, Top = 106, Width = 788, Height = 538, BackColor = C.Panel, Visible = false };
            Controls.Add(pagePatch);
            Controls.Add(pageHelp);

            var P = pagePatch;
            MkLabel("exefs folder", P, 16, 14, C.Fg, 11f, true);
            var bFolder = MkButton("Change...", P, 660, 10, 112, false);
            bFolder.Click += (s, e) => PickFolder();
            lblFolder = MkLabel("", P, 16, 42, C.Muted, 8.5f, false, 640);
            lblFolder.AutoEllipsis = true;

            lblGame = MkLabel("", P, 16, 72, C.Fg, 9.5f, false, 756);
            lblState = MkLabel("", P, 16, 94, C.Muted, 9.5f, false, 756);
            lblWarn = MkLabel("", P, 16, 116, C.Muted, 8.5f, false, 756);
            lblWarn.Height = 34;

            // monitor
            MkLabel("Your monitor", P, 16, 156, C.Fg, 11f, true);
            cbPreset = new ComboBox {
                Left = 16, Top = 186, Width = 330, DropDownStyle = ComboBoxStyle.DropDownList,
                FlatStyle = FlatStyle.Flat, BackColor = C.Entry, ForeColor = C.Fg,
                DrawMode = DrawMode.OwnerDrawFixed, ItemHeight = 22
            };
            cbPreset.Items.AddRange(PresetNames);
            cbPreset.SelectedIndex = 1;
            cbPreset.DrawItem += (s, e) => {
                if (e.Index < 0) return;
                bool sel = (e.State & DrawItemState.Selected) != 0 && (e.State & DrawItemState.ComboBoxEdit) == 0;
                using (var bg = new SolidBrush(sel ? C.Accent : C.Entry)) e.Graphics.FillRectangle(bg, e.Bounds);
                TextRenderer.DrawText(e.Graphics, PresetNames[e.Index], cbPreset.Font,
                    new Rectangle(e.Bounds.X + 6, e.Bounds.Y, e.Bounds.Width - 6, e.Bounds.Height),
                    sel ? Color.White : C.Fg, TextFormatFlags.VerticalCenter | TextFormatFlags.Left);
            };
            cbPreset.SelectedIndexChanged += (s, e) => { ToggleCustom(); UpdateRatio(); };
            P.Controls.Add(cbPreset);

            customPanel = new Panel { Left = 16, Top = 222, Width = 330, Height = 30, BackColor = C.Panel, Visible = false };
            tbW = new TextBox { Left = 0, Top = 2, Width = 70, Text = "3440", BackColor = C.Entry, ForeColor = C.Fg, BorderStyle = BorderStyle.FixedSingle };
            var x = new Label { Text = "x", Left = 76, Top = 5, AutoSize = true, ForeColor = C.Fg };
            tbH = new TextBox { Left = 94, Top = 2, Width = 70, Text = "1440", BackColor = C.Entry, ForeColor = C.Fg, BorderStyle = BorderStyle.FixedSingle };
            tbW.TextChanged += (s, e) => UpdateRatio();
            tbH.TextChanged += (s, e) => UpdateRatio();
            customPanel.Controls.AddRange(new Control[] { tbW, x, tbH });
            P.Controls.Add(customPanel);
            lblRatio = MkLabel("", P, 16, 258, C.Muted, 8.5f, false, 340);

            // variant
            MkLabel("What to patch", P, 400, 156, C.Fg, 11f, true);
            int y = 184;
            for (int i = 0; i < 3; i++)
            {
                radios[i] = new RadioButton {
                    Text = Variants[i][1], Left = 400, Top = y, AutoSize = true, ForeColor = C.Fg,
                    BackColor = C.Panel, Checked = i == 0, Tag = Variants[i][0], FlatStyle = FlatStyle.Flat
                };
                P.Controls.Add(radios[i]);
                var d = MkLabel(Variants[i][2], P, 420, y + 24, C.Muted, 8.5f, false, 360);
                d.Height = Variants[i][2].Contains("\n") ? 36 : 20;
                y += Variants[i][2].Contains("\n") ? 66 : 50;
            }

            // buttons
            btnApply = MkButton("Apply patch", P, 16, 360, 150, true);
            btnApply.Click += (s, e) => Apply();
            btnRevert = MkButton("Restore original", P, 176, 360, 170, false);
            btnRevert.Click += (s, e) => DoRevert();
            progress = new ProgressBar { Left = 602, Top = 368, Width = 170, Height = 16, Style = ProgressBarStyle.Marquee,
                MarqueeAnimationSpeed = 0, Visible = false };
            P.Controls.Add(progress);

            tbLog = new TextBox {
                Left = 16, Top = 408, Width = 756, Height = 114, Multiline = true, ReadOnly = true,
                ScrollBars = ScrollBars.Vertical, BackColor = C.Bg, ForeColor = C.Muted,
                BorderStyle = BorderStyle.None, Font = new Font("Consolas", 9f)
            };
            P.Controls.Add(tbLog);

            // instructions
            var help = new TextBox {
                Left = 16, Top = 14, Width = 756, Height = 510, Multiline = true, ReadOnly = true,
                ScrollBars = ScrollBars.Vertical, BackColor = C.Panel, ForeColor = C.Fg,
                BorderStyle = BorderStyle.None, Font = new Font("Segoe UI", 10f), Text = Instructions()
            };
            pageHelp.Controls.Add(help);

            ShowPage(true);
            UpdateRatio();
        }

        static string Instructions()
        {
            return string.Join("\r\n", new[] {
                "HOW TO USE",
                "",
                "1.  Copy UltrawidePatcher.exe into the game's \"exefs\" folder,",
                "     next to the \"main\" file, and open it.",
                "     (Or open it from anywhere and use \"Change...\" to pick exefs.)",
                "",
                "2.  Choose your monitor resolution.",
                "",
                "3.  Choose what to patch. \"Everything\" is recommended.",
                "",
                "4.  Click \"Apply patch\". It takes a few seconds.",
                "",
                "5.  In Eglantine: Aspect Ratio -> \"Stretch to Window\" (or 21:9).",
                "",
                "6.  Launch the game.",
                "",
                "TO REMOVE IT",
                "     Click \"Restore original\". \"main\" goes back to being identical,",
                "     byte for byte, to what you had.",
                "",
                "DETAILS",
                "  •  The first time, \"main.original\" is created as a backup.",
                "      Don't delete it while the patch is applied.",
                "  •  You can apply another resolution or variant without restoring first:",
                "      it always patches from the original.",
                "  •  The patched \"main\" is stored decompressed (bigger file). That's normal.",
                "  •  If you update the game, the program detects it and uses the new main.",
                "",
                "WHAT IT DOES",
                "  Every frame the game computes:  aspect = screen width / height.",
                "  The patch multiplies that result by (your ratio / 16:9), so the",
                "  3D camera sees more to the sides instead of being stretched.",
                "",
                "LIMITATIONS",
                "  •  FPS stays at 60 (the cap comes from the game and the emulator).",
                "  •  In menus, the 3D scenery is visible on both sides of the panel.",
                "",
                "VERSIONS",
                "  Only tested with:",
                "     " + Engine.TestedName,
                "     Build ID " + Engine.TestedBuild,
                "",
                "  With other versions the program tries to find the code by itself",
                "  (auto-detection). It may or may not work: if the game crashes or",
                "  looks wrong, use \"Restore original\".",
            });
        }

        void ShowPage(bool patch)
        {
            pagePatch.Visible = patch;
            pageHelp.Visible = !patch;
            tabPatch.BackColor = patch ? C.Panel : C.Bg;
            tabHelp.BackColor = patch ? C.Bg : C.Panel;
            tabPatch.ForeColor = patch ? C.Fg : C.Muted;
            tabHelp.ForeColor = patch ? C.Muted : C.Fg;
        }

        void ToggleCustom()
        {
            customPanel.Visible = cbPreset.SelectedIndex == PresetNames.Length - 1;
        }

        bool TryRatio(out double r)
        {
            r = 0;
            int i = cbPreset.SelectedIndex;
            if (i >= 0 && PresetRatios[i] > 0) { r = PresetRatios[i]; return true; }
            double w, h;
            if (double.TryParse(tbW.Text.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out w) &&
                double.TryParse(tbH.Text.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out h) && w > 0 && h > 0)
            { r = w / h; return true; }
            return false;
        }

        void UpdateRatio()
        {
            double r;
            if (TryRatio(out r))
                lblRatio.Text = string.Format(CultureInfo.InvariantCulture, "Ratio {0:0.0000}   ·   camera factor x{1:0.0000}",
                    r, r / (16.0 / 9.0));
            else lblRatio.Text = "Enter a valid width and height";
        }

        void AppendLog(string s)
        {
            if (InvokeRequired) { BeginInvoke(new Action<string>(AppendLog), s); return; }
            tbLog.AppendText(s.Replace("\r\n", "\n").Replace("\n", "\r\n"));
        }

        Engine.Paths Paths() { return new Engine.Paths(folder); }

        void PickFolder()
        {
            using (var d = new FolderBrowserDialog { Description = "Choose the exefs folder (where 'main' is)", SelectedPath = folder })
                if (d.ShowDialog(this) == DialogResult.OK) { folder = d.SelectedPath; RefreshStatus(); }
        }

        void RefreshStatus()
        {
            lblFolder.Text = folder;
            var p = Paths();
            string src = File.Exists(p.Orig) ? p.Orig : p.Main;
            string bid = null;
            try { if (File.Exists(src)) bid = Engine.BuildIdOfFile(src); } catch { }
            if (bid == null)
            {
                lblGame.Text = "No valid 'main' in this folder.";
                lblGame.ForeColor = C.Accent;
                lblState.Text = "";
                lblWarn.Text = "Put the program in the game's exefs folder or pick it with \"Change...\".";
                lblWarn.ForeColor = C.Muted;
                btnApply.Enabled = btnRevert.Enabled = false;
                StyleButtons();
                return;
            }
            string shortId = bid.Length > 16 ? bid.Substring(0, 16) + "..." : bid;
            GameInfo gi;
            if (Engine.Known.TryGetValue(bid, out gi))
            {
                lblGame.Text = "Game: " + gi.Name + "   ·   Build ID " + shortId;
                lblWarn.Text = "Tested version.";
                lblWarn.ForeColor = C.Ok;
            }
            else
            {
                lblGame.Text = "Game: unknown version   ·   Build ID " + shortId;
                lblWarn.Text = "This version has NOT been tested. Auto-detection will be used; if anything breaks, use \"Restore original\".";
                lblWarn.ForeColor = C.Warn;
            }
            lblGame.ForeColor = C.Fg;
            bool patched;
            lblState.Text = "Status: " + Engine.Status(p, out patched);
            lblState.ForeColor = patched ? C.Ok : C.Muted;
            if (!busy)
            {
                btnApply.Enabled = true;
                btnRevert.Enabled = File.Exists(p.Orig);
            }
            StyleButtons();
        }

        void StyleButtons()
        {
            btnApply.BackColor = btnApply.Enabled ? C.Accent : Color.FromArgb(0x6b, 0x2a, 0x28);
            btnRevert.ForeColor = btnRevert.Enabled ? C.Fg : C.Muted;
        }

        void Run(Action work, string doneMsg)
        {
            if (busy) return;
            busy = true;
            btnApply.Enabled = btnRevert.Enabled = false;
            StyleButtons();
            progress.Visible = true;
            progress.MarqueeAnimationSpeed = 25;
            AppendLog("\r\n" + new string('-', 60) + "\r\n");
            Task.Factory.StartNew(() => {
                Exception err = null;
                try { work(); } catch (Exception ex) { err = ex; }
                BeginInvoke(new Action(() => {
                    busy = false;
                    progress.MarqueeAnimationSpeed = 0;
                    progress.Visible = false;
                    RefreshStatus();
                    if (err != null)
                    {
                        AppendLog("\r\n[ERROR] " + err.Message + "\r\n");
                        MessageBox.Show(this, "An error occurred:\n\n" + err.Message, Text, MessageBoxButtons.OK, MessageBoxIcon.Error);
                    }
                    else if (doneMsg != null)
                        MessageBox.Show(this, doneMsg, Text, MessageBoxButtons.OK, MessageBoxIcon.Information);
                }));
            });
        }

        void Apply()
        {
            double r;
            if (!TryRatio(out r)) { MessageBox.Show(this, "Enter a valid width and height.", Text); return; }
            string v = (string)radios.First(rb => rb.Checked).Tag;
            var p = Paths();
            Run(() => Engine.PatchCamera(p, r, v),
                "Patch applied.\n\nIn Eglantine set Aspect Ratio to 'Stretch to Window' (or 21:9) and launch the game.");
        }

        void DoRevert()
        {
            if (MessageBox.Show(this, "Restore the original 'main'?", Text, MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes)
                return;
            var p = Paths();
            Run(() => Engine.Revert(p), "Done: the game is back to its original state.");
        }
    }

    static class Program
    {
        [DllImport("user32.dll")] static extern bool SetProcessDPIAware();

        [STAThread]
        static int Main(string[] args)
        {
            // Console mode for testing:  UltrawidePatcher.exe --cli camara <ratio> <todo|A|B> <folder>
            //                             UltrawidePatcher.exe --cli revert <folder>
            if (args.Length > 0 && args[0] == "--cli")
            {
                Engine.Log = s => Console.Write(s);
                try
                {
                    if (args[1] == "camara")
                        Engine.PatchCamera(new Engine.Paths(args[4]), double.Parse(args[2], CultureInfo.InvariantCulture), args[3]);
                    else if (args[1] == "revert")
                        Engine.Revert(new Engine.Paths(args[2]));
                    return 0;
                }
                catch (Exception ex) { Console.WriteLine("[ERROR] " + ex.Message); return 1; }
            }
            try { if (Environment.OSVersion.Platform == PlatformID.Win32NT) SetProcessDPIAware(); } catch { }
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.Run(new MainForm());
            return 0;
        }
    }
}
