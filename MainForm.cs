﻿using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using System.IO;
using System.Security.Principal;
using System.Threading;
using System.Net;
using System.Text.RegularExpressions;
using System.Diagnostics;
using System.Text;

namespace GeDoSaToTool
{
    public partial class MainForm : Form
    {
        const string REG_PATH = @"HKEY_LOCAL_MACHINE\SOFTWARE\Durante\GeDoSaTo";
        const string INJECTION_REG_PATH = @"HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\Windows NT\CurrentVersion\Windows";
        const string AUTOSTART_REG_PATH = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Run";
        const string SECUREBOOT_REG_PATH = @"HKEY_LOCAL_MACHINE\SYSTEM\CurrentControlSet\Control\SecureBoot\State";

        const string SHIM_DLL_NAME = @"gedoshim.dll";

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
        private bool IsSecureBootEnabled()
        {
            var secureboot = Microsoft.Win32.Registry.GetValue(SECUREBOOT_REG_PATH, "UEFISecureBootEnabled", 0);
            return secureboot!=null && (int)secureboot!=0;
        }

        private string GetShimDllPath()
        {
            string dllfn = Directory.GetCurrentDirectory() + "\\" + SHIM_DLL_NAME;
            string shortname = Native.GetShortPath(dllfn);
            if(shortname.Length == 0) {
                MessageBox.Show("Could not find a short path for the dll install directory " + dllfn + ". If you experience any issues, try a different installation directory.", 
                    "Path Warning", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return dllfn;
            }
            return shortname;
        }

        private void MainForm_Load(object sender, EventArgs e)
        {
            // startup checks
            string dir = Directory.GetCurrentDirectory() + "\\";
            if(ContainsUnicodeCharacter(dir)) {
                MessageBox.Show("The installation path (" + dir + ") contains special characters. Please install GeDoSaTo to a path with only ANSI characters.",
                    "Installation Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
                Close();
            }
            if(!IsAdministrator()) {
                MessageBox.Show("Please run GeDoSaToTool as Administrator (to enable dll injection).",
                    "Privilege Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
                Close();
            }
            if(IsSecureBootEnabled()) {
                MessageBox.Show("It appears that you are using a Windows 8 or 10 system with UEFI secure boot enabled. This prevents GeDoSaTo from working correctly. Please see the Readme for details.",
                    "Secure Boot Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }

            try {
                Microsoft.Win32.Registry.SetValue(REG_PATH, "InstallPath", dir);
                installLabel.Text = "Installation path: " + dir;
            }
            catch(Exception ex) {
                installLabel.Text = "Error setting Installation path: \n" + ex.Message;
            }

            // load native dll
            try {
                native.init();
            }
            catch(Exception ex) {
                dllVersionlabel.Text = "Error loading dll:\n " + ex.Message;
            }
            dllVersionlabel.Text = native.getVersionText();

            // setup global hotkey
            Hotkey hk = new Hotkey();
            hk.KeyCode = Keys.G;
            hk.Control = true;
            hk.Alt = true;
            hk.Pressed += delegate {
                if(deactivateButton.Enabled) deactivateButton_Click(null, null);
                else activateButton_Click(null, null);
            };
            if(!hk.GetCanRegister(this)) {
                globalHotkeyLabel.Text = "Error: Could not register global hotkey.";
            }
            else {
                hk.Register(this);
                globalHotkeyLabel.Text = "Global hotkey to toggle: " + hk.ToString();
            }

            // check autostart state
            Microsoft.Win32.RegistryKey skey = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(AUTOSTART_REG_PATH, true);
            if(skey.GetValue("GeDoSaToTool") != null) startupCheckBox.Checked = true;

            // check balloon state
            var balloon = Microsoft.Win32.Registry.GetValue(REG_PATH, "HideBalloon", "False");
            if(balloon != null) balloonCheckBox.Checked = (string)balloon == "True";

            // disable dangling alternative injection (from improper shutdown)
            string dllfn = GetShimDllPath();
            if(dllfn.Length == 0) {
                MessageBox.Show("Could not determine dll directory. Please reinstall to a different location.",
                    "Installation Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
                Close();
            }
            string initval = (string)Microsoft.Win32.Registry.GetValue(INJECTION_REG_PATH, "AppInit_DLLs", "");
            string val = initval.Replace(dllfn + ",", "");
            val = val.Replace(dllfn, "");
            if(val != initval) {
                Microsoft.Win32.Registry.SetValue(INJECTION_REG_PATH, "AppInit_DLLs", val);
                MessageBox.Show("Old registry entries for injection found and cleared. Please make sure to close GeDoSaToTool properly.",
                    "Warning", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }

            // activate automatically if set to do so
            if(autoActivate) activateButton_Click(null, null);

            settings.AddRange(native.getSettingsString().Split(','));

            // minimize if requested
            if(startMinimized) {
                WindowState = FormWindowState.Minimized;
                MainForm_Resize(null, null);
            }

            // check for updates
            updateButton.Enabled = false;
            updateButton.Text = "Checking for updates...";
            ThreadPool.QueueUserWorkItem(delegate {
                try {
                    var cli = new WebClient();
                    string github_version = cli.DownloadString("https://raw.githubusercontent.com/PeterTh/gedosato/master/source/version.cpp");
                    var buildReg = new Regex(@"const unsigned VER_BUILD = (\d+);");
                    var verReg = new Regex(@"version \d+\.\d+\.(\d+)");
                    int current = Int32.Parse(verReg.Match(native.getVersion()).Groups[1].ToString());
                    int latest = Int32.Parse(buildReg.Match(github_version).Groups[1].ToString());
                    if(current < latest) {
                        Invoke((MethodInvoker)delegate {
                            updateButton.Enabled = true;
                            updateButton.Text = "Update to\nbuild " + latest;
                        });
                    }
                    else {
                        Invoke((MethodInvoker)delegate {
                            updateButton.Text = "Up to date";
                        });
                    }
                }
                catch(Exception) {
                    Invoke((MethodInvoker)delegate {
                        updateButton.Text = "Error checking for updates (offline?)";
                    });
                }
            });
        }

        private void MainForm_FormClosed(object sender, FormClosedEventArgs e)
        {
            deactivateButton_Click(sender, e);
        }

        protected override void WndProc(ref Message m)
        {
            if(m.Msg == Native.WM_SHOWME) {
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
            if(this.WindowState == FormWindowState.Minimized) {
                notifyIcon.Visible = true;
                if(!balloonCheckBox.Checked) {
                    notifyIcon.ShowBalloonTip(3000, "GeDoSaTo Minimized", "Double-click to restore", ToolTipIcon.Info);
                }
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
            try {
                // reg injection
                string dllfn = GetShimDllPath();
                appinitString = (string)Microsoft.Win32.Registry.GetValue(INJECTION_REG_PATH, "AppInit_DLLs", "");
                string newAppinit = appinitString.Length > 0 ? dllfn + "," + appinitString : dllfn;
                Microsoft.Win32.Registry.SetValue(INJECTION_REG_PATH, "AppInit_DLLs", newAppinit);
                Microsoft.Win32.Registry.SetValue(INJECTION_REG_PATH, "LoadAppInit_DLLs", 1);
                if((int)Microsoft.Win32.Registry.GetValue(INJECTION_REG_PATH, "RequireSignedAppInit_DLLs", 0) != 0) {
                    Microsoft.Win32.Registry.SetValue(INJECTION_REG_PATH, "RequireSignedAppInit_DLLs", 0);
                }
                // report success
                reportLabel.Text = "Activated";
                deactivateButton.Enabled = true;
                activateButton.Enabled = false;
            }
            catch(Win32Exception ex) {
                reportLabel.Text = "Activation failed: \n" + ex.Message;
            }
        }

        private void deactivateButton_Click(object sender, EventArgs e)
        {
            deactivateButton.Enabled = false;
            activateButton.Enabled = true;

            // deactivate hooks
            Microsoft.Win32.Registry.SetValue(INJECTION_REG_PATH, "AppInit_DLLs", appinitString);
            reportLabel.Text = "Deactivated successfully";
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
            string[] buttons = { "DPAD_UP", "DPAD_DOWN", "DPAD_LEFT", "DPAD_RIGHT", "START", "BACK", "LEFT_THUMB", "RIGHT_THUMB", "LEFT_SHOULDER", "RIGHT_SHOULDER", "A", "B", "X", "Y" };
            var keyList = new List<string>(keys);
            for(int i = 0; i < 4; ++i) {
                var xbtns = Array.ConvertAll(buttons, btn => "X" + i + "_" + btn).ToArray();
                keyList.AddRange(xbtns);
            }
            new TextForm("config\\GeDoSaToKeys.ini", true, keyList).Show();
        }

        private void whitelistButton_Click(object sender, EventArgs e)
        {
            new TextForm("config\\whitelist.txt", true).Show();
        }

        private void userWhitelistButton_Click(object sender, EventArgs e)
        {
            string fn = "config\\whitelist_user.txt";
            if(!File.Exists(fn)) {
                string defaultText = "# GeDoSaTo User Whitelist\n" +
                                     "# One application name per line (application name = file name of the .exe without the .exe)\n" +
                                     "# Wildcards supported (* = any sequence of characters, ? = any single character)\n" +
                                     "# Unlike the global whitelist, this file will not be overwritten by updates\n\n" +
                                     "# Format:\n" +
                                     "# Exe File Name (or pattern) || Readable Name (optional)\n\n";
                var stream = new StreamWriter(fn);
                stream.Write(defaultText);
                stream.Close();
            }
            new TextForm(fn, true).Show();
        }

        private void postButton_Click(object sender, EventArgs e)
        {
            new TextForm("assets\\dx9\\post.fx", true).Show();
        }

        private void altPostButton_Click(object sender, EventArgs e)
        {
            new TextForm("assets\\dx9\\post_asmodean.fx", true).Show();
        }

        private void startupCheckBox_CheckedChanged(object sender, EventArgs e)
        {
            Microsoft.Win32.RegistryKey key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(AUTOSTART_REG_PATH, true);
            if(startupCheckBox.Checked) {
                key.SetValue("GeDoSaToTool", Application.ExecutablePath.ToString() + " -m");
            }
            else {
                key.DeleteValue("GeDoSaToTool", false);
            }
        }

        private void balloonCheckBox_CheckedChanged(object sender, EventArgs e)
        {
            Microsoft.Win32.Registry.SetValue(REG_PATH, "HideBalloon", balloonCheckBox.Checked);
        }

        private void updateButton_Click(object sender, EventArgs e)
        {
            Process update = new Process();
            update.StartInfo.FileName = @"GeDoSaToUpdater.exe";
            update.Start();
            Close();
        }

        private void logLinkLabel_LinkClicked(object sender, LinkLabelLinkClickedEventArgs e)
        {
            Process.Start(Directory.GetCurrentDirectory() + "\\logs");
        }

        private void screenshotsLinkLabel_LinkClicked(object sender, LinkLabelLinkClickedEventArgs e)
        {
            Process.Start(Directory.GetCurrentDirectory() + "\\screens");
        }

        private void trayMenuItemExit_Click(object sender, EventArgs e)
        {
            Close();
        }
        private void trayMenuItemRestore_Click(object sender, EventArgs e)
        {
            notifyIcon_MouseDoubleClick(sender, null);
        }
    }
}

class Native
{
    // Winapi

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

    // Path

    const int MAX_PATH = 260;

    [DllImport("kernel32.dll", CharSet = CharSet.Auto)]
    static extern int GetShortPathName(
        [MarshalAs(UnmanagedType.LPTStr)]
            string path,
        [MarshalAs(UnmanagedType.LPTStr)]
            StringBuilder shortPath,
        int shortPathLength
        );

    // GeDoSaTo

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    delegate IntPtr GeDoSaToVersion();
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    delegate IntPtr GeDoSaToSettings();

    IntPtr dllHandle = IntPtr.Zero;
    GeDoSaToVersion geDoSaToVersion;
    GeDoSaToSettings geDoSaToSettings;

    bool inited = false;

    // Interface

    public Native()
    {
    }

    ~Native()
    {
    }

    public void init()
    {
        if(!inited) {
            inited = true;
            dllHandle = LoadLibrary("GeDoSaTo.dll");
            if(dllHandle == IntPtr.Zero) throw new Win32Exception();

            IntPtr pGeDoSaToVersion = GetProcAddress(dllHandle, "GeDoSaToVersion");
            if(pGeDoSaToVersion == IntPtr.Zero) throw new Win32Exception("Couldn't find GeDoSaToVersion proc in dll");
            IntPtr pGeDoSaToSettings = GetProcAddress(dllHandle, "GeDoSaToSettings");
            if(pGeDoSaToSettings == IntPtr.Zero) throw new Win32Exception("Couldn't find GeDoSaToSettings proc in dll");
            geDoSaToVersion = (GeDoSaToVersion)Marshal.GetDelegateForFunctionPointer(pGeDoSaToVersion, typeof(GeDoSaToVersion));
            geDoSaToSettings = (GeDoSaToSettings)Marshal.GetDelegateForFunctionPointer(pGeDoSaToSettings, typeof(GeDoSaToSettings));
        }
    }

    public string getVersionText()
    {
        return "GeDoSaTo.dll loaded, " + getVersion();
    }

    public string getVersion()
    {
        IntPtr ptr;
        try {
            ptr = geDoSaToVersion();
        }
        catch(Exception e) {
            return "Error loading GeDoSaTo.dll: \n" + e.Message;
        }
        return System.Runtime.InteropServices.Marshal.PtrToStringAnsi(ptr);
    }

    public string getSettingsString()
    {
        IntPtr ptr;
        try {
            ptr = geDoSaToSettings();
        }
        catch(Exception) {
            return "";
        }
        return System.Runtime.InteropServices.Marshal.PtrToStringAnsi(ptr);
    }

    public static string GetShortPath(string path)
    {
        var shortPath = new StringBuilder(MAX_PATH);
        GetShortPathName(path, shortPath, MAX_PATH);
        return shortPath.ToString();
    }
}
