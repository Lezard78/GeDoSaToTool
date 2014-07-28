﻿using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Data;
using System.Drawing;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Forms;
using System.IO;
using System.Security.Principal;

namespace GeDoSaToTool
{
    public partial class MainForm : Form
    {
        const string REG_PATH = @"HKEY_LOCAL_MACHINE\SOFTWARE\Durante\GeDoSaTo";
        const string INJECTION_REG_PATH = @"HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\Windows NT\CurrentVersion\Windows";
        const string AUTOSTART_REG_PATH = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Run";

        static Native native = new Native();
        bool autoActivate = true;
        bool startMinimized = false;

        List<string> settings = new List<string>();
        string appinitString = "";

        public MainForm(bool autoAct, bool startMin)
        {
            autoActivate = autoAct;
            startMinimized = startMin;
            InitializeComponent();
        }
        private bool ContainsUnicodeCharacter(string input)
        {
            const int MaxAnsiCode = 255;

            return input.Any(c => c > MaxAnsiCode);
        }
        private bool IsAdministrator()
        {
            WindowsIdentity identity = WindowsIdentity.GetCurrent();
            WindowsPrincipal principal = new WindowsPrincipal(identity);
            return principal.IsInRole(WindowsBuiltInRole.Administrator);
        }

        private void MainForm_Load(object sender, EventArgs e)
        {
            // startup checks
            string dir = Directory.GetCurrentDirectory() + "\\";
            if (ContainsUnicodeCharacter(dir))
            {
                MessageBox.Show("The installation path (" + dir + ") contains special characters. Please install GeDoSaTo to a path with only ANSI characters.",
                    "Installation Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
                Close(); 
            }
            if (!IsAdministrator())
            {
                MessageBox.Show("Please run GeDoSaToTool as Administrator (to enable dll injection).",
                    "Privilege Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
                Close(); 
            }

            try
            {
                Microsoft.Win32.Registry.SetValue(REG_PATH, "InstallPath", dir);
                installLabel.Text = "Installation path: " + dir;
            }
            catch (Exception ex)
            {
                installLabel.Text = "Error setting Installation path: \n" + ex.Message;
            }

            // load whitelist state
            try
            {
                bool useBlacklist = (string)Microsoft.Win32.Registry.GetValue(REG_PATH, "UseBlacklist", false) == "True";
                if (useBlacklist)
                {
                    blacklistRadioButton.Checked = true;
                    whitelistRadioButton.Checked = false;
                }
            }
            catch (Exception) { }
            whitelistRadioButton_CheckedChanged(null, null);
            blacklistRadioButton_CheckedChanged(null, null);

            // load native dll
            try
            {
                native.init();
            }
            catch (Exception ex)
            {
                dllVersionlabel.Text = "Error loading dll:\n " + ex.Message;
            }
            dllVersionlabel.Text = native.getVersionText();

            // setup global hotkey
            Hotkey hk = new Hotkey();
            hk.KeyCode = Keys.G;
            hk.Windows = true;
            hk.Pressed += delegate {
                if (deactivateButton.Enabled) deactivateButton_Click(null, null);
                else activateButton_Click(null, null);
            };
            if (!hk.GetCanRegister(this))
            {
                globalHotkeyLabel.Text = "Error: Could not register global hotkey.";
            }
            else
            {
                hk.Register(this);
                globalHotkeyLabel.Text = "Global hotkey to toggle: " + hk.ToString();
            }

            // check autostart state
            Microsoft.Win32.RegistryKey key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(AUTOSTART_REG_PATH, true);
            if (key.GetValue("GeDoSaToTool") != null) startupCheckBox.Checked = true;

            // activate automatically if set to do so
            if (autoActivate) activateButton_Click(null, null);

            // disable dangling alternative injection (from improper shutdown)
            string dllfn = Directory.GetCurrentDirectory() + "\\" + "GeDoSaTo.dll";
            string initval = (string)Microsoft.Win32.Registry.GetValue(INJECTION_REG_PATH, "AppInit_DLLs", "");
            string val = initval.Replace(dllfn + ",", "");
            val = val.Replace(dllfn, "");
            if (val != initval)
            {
                Microsoft.Win32.Registry.SetValue(INJECTION_REG_PATH, "AppInit_DLLs", val);
                MessageBox.Show("Old registry entries for alternative injection found and cleared. Please make sure to close GeDoSaToTool properly.",
                    "Warning", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }

            settings.AddRange(native.getSettingsString().Split(','));

            // minimize if requested
            if (startMinimized)
            {
                WindowState = FormWindowState.Minimized;
                MainForm_Resize(null, null);
            }
        }

        private void MainForm_FormClosed(object sender, FormClosedEventArgs e)
        {
            deactivateButton_Click(sender, e);
        }

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == Native.WM_SHOWME)
            {
                ShowMe();
            }
            base.WndProc(ref m);
        }

        public void ShowMe()
        {
            notifyIcon_MouseDoubleClick(null, null);
            TopMost = true;
            TopMost = false;
        }

        private void MainForm_Resize(object sender, EventArgs e)
        {
            if (this.WindowState == FormWindowState.Minimized)
            {
                notifyIcon.Visible = true;
                notifyIcon.ShowBalloonTip(3000, "GeDoSaTo Minimized", "Double-click to restore", ToolTipIcon.Info);
                this.ShowInTaskbar = false;
                new MessageWindow(this);
            }
        }

        private void notifyIcon_MouseDoubleClick(object sender, MouseEventArgs e)
        {
            this.WindowState = FormWindowState.Normal;
            this.ShowInTaskbar = true;
            notifyIcon.Visible = false;
        }

        private void activateButton_Click(object sender, EventArgs e)
        {
            try
            {
                // hook injection
                native.activateHook();
                // reg injection
                string dllfn = Directory.GetCurrentDirectory() + "\\" + "GeDoSaTo.dll";
                appinitString = (string)Microsoft.Win32.Registry.GetValue(INJECTION_REG_PATH, "AppInit_DLLs", "");
                string newAppinit = appinitString.Length > 0 ? dllfn + "," + appinitString : dllfn;
                Microsoft.Win32.Registry.SetValue(INJECTION_REG_PATH, "AppInit_DLLs", newAppinit);
                Microsoft.Win32.Registry.SetValue(INJECTION_REG_PATH, "LoadAppInit_DLLs", 1);
                // report success
                reportLabel.Text = "Activated";
                deactivateButton.Enabled = true;
                activateButton.Enabled = false;
            }
            catch (Win32Exception ex)
            {
                reportLabel.Text = "Activation failed: \n" + ex.Message;
            }
        }

        private void deactivateButton_Click(object sender, EventArgs e)
        {
            deactivateButton.Enabled = false;
            activateButton.Enabled = true;

            Microsoft.Win32.Registry.SetValue(INJECTION_REG_PATH, "AppInit_DLLs", appinitString);
            bool ret = native.deactivateHook();
            reportLabel.Text = "Deactivated (unhooked " + (ret ? "successfully" : "unsuccessfully") + ")";
        }

        private void whitelistRadioButton_CheckedChanged(object sender, EventArgs e)
        {
            if (!whitelistRadioButton.Checked) return;
            blacklistRadioButton.Checked = false;
            try
            {
                Microsoft.Win32.Registry.SetValue(REG_PATH, "UseBlacklist", false);
            }
            catch (Exception) { }
        }

        private void blacklistRadioButton_CheckedChanged(object sender, EventArgs e)
        {
            if (!blacklistRadioButton.Checked) return;
            whitelistRadioButton.Checked = false;
            try
            {
                Microsoft.Win32.Registry.SetValue(REG_PATH, "UseBlacklist", true);
            }
            catch (Exception) { }
        }

        private void linkLabel_LinkClicked(object sender, LinkLabelLinkClickedEventArgs e)
        {
            System.Diagnostics.Process.Start("https://www.paypal.com/cgi-bin/webscr?cmd=_donations&business=peter%40metaclassofnil.com&lc=US&item_name=Durante&item_number=GeDoSaTo&no_shipping=2&currency_code=EUR");
        }

        private void linkLabelReadme_LinkClicked(object sender, LinkLabelLinkClickedEventArgs e)
        {
            new TextForm("README.txt", false).Show();
        }

        private void linkLabelVersions_LinkClicked(object sender, LinkLabelLinkClickedEventArgs e)
        {
            new TextForm("VERSIONS.txt", false).Show();
        }

        private void settingsButton_Click(object sender, EventArgs e)
        {
            new TextForm("config\\GeDoSaTo.ini", true, settings).Show();
        }

        private void keybindingsButton_Click(object sender, EventArgs e)
        {
            string[] keys = { "VK_LBUTTON", "VK_RBUTTON", "VK_CANCEL", "VK_MBUTTON", "VK_XBUTTON1", "VK_XBUTTON2", "VK_BACK", "VK_TAB", "VK_CLEAR", "VK_RETURN", "VK_SHIFT", "VK_CONTROL", "VK_MENU", "VK_PAUSE", "VK_CAPITAL", "VK_KANA", "VK_HANGUL", "VK_JUNJA", "VK_FINAL", "VK_HANJA", "VK_KANJI", "VK_ESCAPE", "VK_CONVERT", "VK_NONCONVERT", "VK_ACCEPT", "VK_MODECHANGE", "VK_SPACE", "VK_PRIOR", "VK_NEXT", "VK_END", "VK_HOME", "VK_LEFT", "VK_UP", "VK_RIGHT", "VK_DOWN", "VK_SELECT", "VK_PRINT", "VK_EXECUTE", "VK_SNAPSHOT", "VK_INSERT", "VK_DELETE", "VK_HELP", "VK_0", "VK_1", "VK_2", "VK_3", "VK_4", "VK_5", "VK_6", "VK_7", "VK_8", "VK_9", "VK_A", "VK_B", "VK_C", "VK_D", "VK_E", "VK_F", "VK_G", "VK_H", "VK_I", "VK_J", "VK_K", "VK_L", "VK_M", "VK_N", "VK_O", "VK_P", "VK_Q", "VK_R", "VK_S", "VK_T", "VK_U", "VK_V", "VK_W", "VK_X", "VK_Y", "VK_Z", "VK_LWIN", "VK_RWIN", "VK_APPS", "VK_SLEEP", "VK_NUMPAD0", "VK_NUMPAD1", "VK_NUMPAD2", "VK_NUMPAD3", "VK_NUMPAD4", "VK_NUMPAD5", "VK_NUMPAD6", "VK_NUMPAD7", "VK_NUMPAD8", "VK_NUMPAD9", "VK_MULTIPLY", "VK_ADD", "VK_SEPARATOR", "VK_SUBTRACT", "VK_DECIMAL", "VK_DIVIDE", "VK_F1", "VK_F2", "VK_F3", "VK_F4", "VK_F5", "VK_F6", "VK_F7", "VK_F8", "VK_F9", "VK_F10", "VK_F11", "VK_F12", "VK_F13", "VK_F14", "VK_F15", "VK_F16", "VK_F17", "VK_F18", "VK_F19", "VK_F20", "VK_F21", "VK_F22", "VK_F23", "VK_F24", "VK_NUMLOCK", "VK_SCROLL", "VK_LSHIFT", "VK_RSHIFT", "VK_LCONTROL", "VK_RCONTROL", "VK_LMENU", "VK_RMENU" };
            var keyList = new List<string>(keys);
            new TextForm("config\\GeDoSaToKeys.ini", true, keyList).Show();
        }

        private void whitelistButton_Click(object sender, EventArgs e)
        {
            new TextForm("config\\whitelist.txt", true).Show();
        }

        private void blacklistButton_Click(object sender, EventArgs e)
        {
            new TextForm("config\\blacklist.txt", true).Show();
        }

        private void postButton_Click(object sender, EventArgs e)
        {
            new TextForm("assets\\post.fx", true).Show();
        }

        private void altPostButton_Click(object sender, EventArgs e)
        {
            new TextForm("assets\\post_asmodean.fx", true).Show();
        }

        private void startupCheckBox_CheckedChanged(object sender, EventArgs e)
        {
            Microsoft.Win32.RegistryKey key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(AUTOSTART_REG_PATH, true);
            if (startupCheckBox.Checked)
            {
                key.SetValue("GeDoSaToTool", Application.ExecutablePath.ToString() + " -m");
            }
            else
            {
                key.DeleteValue("GeDoSaToTool", false);
            }
        }
    }
}

class Native
{
    // Winapi

    enum HookType : int
    {
        WH_JOURNALRECORD = 0,
        WH_JOURNALPLAYBACK = 1,
        WH_KEYBOARD = 2,
        WH_GETMESSAGE = 3,
        WH_CALLWNDPROC = 4,
        WH_CBT = 5,
        WH_SYSMSGFILTER = 6,
        WH_MOUSE = 7,
        WH_HARDWARE = 8,
        WH_DEBUG = 9,
        WH_SHELL = 10,
        WH_FOREGROUNDIDLE = 11,
        WH_CALLWNDPROCRET = 12,
        WH_KEYBOARD_LL = 13,
        WH_MOUSE_LL = 14
    }

    [DllImport("user32.dll", CharSet = CharSet.Auto, SetLastError = true)]
    static extern IntPtr SetWindowsHookEx(HookType hookType, IntPtr lpfn, IntPtr hMod, uint dwThreadId);

    [DllImport("user32.dll", CharSet = CharSet.Auto, SetLastError = true)]
    static extern bool UnhookWindowsHookEx(IntPtr hhk);

    [DllImport("kernel32.dll", CharSet = CharSet.Auto, SetLastError = true)]
    static extern IntPtr GetModuleHandle(string lpModuleName);

    [DllImport("kernel32.dll", CharSet = CharSet.Auto, SetLastError = true)]
    static extern IntPtr LoadLibrary(string lpLibName);

    [DllImport("kernel32", CharSet = CharSet.Ansi, ExactSpelling = true, SetLastError = true)]
    static extern IntPtr GetProcAddress(IntPtr hModule, string procName);

    // Single instance stuff

    public const int HWND_BROADCAST = 0xffff;
    public static readonly int WM_SHOWME = RegisterWindowMessage("WM_SHOWME");
    [DllImport("user32")]
    public static extern bool PostMessage(IntPtr hwnd, int msg, IntPtr wparam, IntPtr lparam);
    [DllImport("user32")]
    public static extern int RegisterWindowMessage(string message);

    // GeDoSaTo

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    delegate IntPtr GeDoSaToVersion();
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    delegate IntPtr GeDoSaToSettings();
    
    IntPtr dllHandle = IntPtr.Zero;
    IntPtr hookFun = IntPtr.Zero;
    IntPtr hookHandle = IntPtr.Zero;
    GeDoSaToVersion geDoSaToVersion;
    GeDoSaToSettings geDoSaToSettings;

    bool inited = false;

    // Interface

    public Native()
    {
    }

    ~Native()
    {
        if (hookHandle != IntPtr.Zero) deactivateHook();
    }

    public void init()
    {
        if (!inited)
        {
            inited = true;
            dllHandle = LoadLibrary("GeDoSaTo.dll");
            if (dllHandle == IntPtr.Zero) throw new Win32Exception();
            hookFun = GetProcAddress(dllHandle, "GeDoSaToHook");
            if (hookFun == IntPtr.Zero) throw new Win32Exception();

            IntPtr pGeDoSaToVersion = GetProcAddress(dllHandle, "GeDoSaToVersion");
            IntPtr pGeDoSaToSettings = GetProcAddress(dllHandle, "GeDoSaToSettings");
            geDoSaToVersion = (GeDoSaToVersion)Marshal.GetDelegateForFunctionPointer(pGeDoSaToVersion, typeof(GeDoSaToVersion));
            geDoSaToSettings = (GeDoSaToSettings)Marshal.GetDelegateForFunctionPointer(pGeDoSaToSettings, typeof(GeDoSaToSettings));
        }
    }

    public string getVersionText()
    {
        IntPtr ptr;
        try
        {
            ptr = geDoSaToVersion();
        }
        catch (Exception e)
        {
            return "Error loading GeDoSaTo.dll: \n" + e.Message;
        }
        return "GeDoSaTo.dll loaded, " + System.Runtime.InteropServices.Marshal.PtrToStringAnsi(ptr);
    }

    public string getSettingsString()
    {
        IntPtr ptr;
        try
        {
            ptr = geDoSaToSettings();
        }
        catch (Exception)
        {
            return "";
        }
        return System.Runtime.InteropServices.Marshal.PtrToStringAnsi(ptr);
    }

    public void activateHook()
    {
        hookHandle = SetWindowsHookEx(HookType.WH_CBT, hookFun, dllHandle, 0);
        if (hookHandle == IntPtr.Zero) throw new Win32Exception();
    }

    public bool deactivateHook()
    {
        return UnhookWindowsHookEx(hookHandle);
    }
}
