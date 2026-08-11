using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;
using NAudio.CoreAudioApi;
using NAudio.CoreAudioApi.Interfaces;

namespace AudioDucker
{
    public partial class Form1 : Form
    {
        // Windows 11 Rounded Corners API
        [DllImport("dwmapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int attributeValue, int size);

        private const int DWMWA_WINDOW_CORNER_PREFERENCE = 33;
        private const int DWMWCP_ROUND = 2;

        // Audio Engine Variables (Decoupled background timer & thread-safety lock)
        private MMDeviceEnumerator _deviceEnumerator = null!;
        private MMDevice _defaultDevice = null!;
        private System.Threading.Timer _duckingTimer = null!;
        private readonly Stopwatch _holdStopwatch = new Stopwatch();
        private readonly object _audioLock = new object();

        // State Management
        private bool _isUpdatingChecks = false;
        private bool _isExiting = false;
        private bool _isCurrentlyDucking = false;
        private int _lastPeakPercentage = -1; // For redraw optimization

        // Caching & Persistence
        private readonly List<AudioSessionControl> _activeTriggers = new List<AudioSessionControl>();
        private readonly List<AudioSessionControl> _activeTargets = new List<AudioSessionControl>();
        private readonly Dictionary<uint, float> _originalVolumes = new Dictionary<uint, float>();

        // QoL: Remember selections across refreshes by process name
        private readonly HashSet<string> _savedTriggerNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<string> _savedTargetNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // UI & Tray Controls
        private NotifyIcon _notifyIcon = null!;
        private ContextMenuStrip _trayMenu = null!;
        private CheckedListBox _clbTriggers = null!;
        private CheckedListBox _clbTargets = null!;
        private Button _btnRefresh = null!;
        private Button _btnToggle = null!;
        private Label _lblStatus = null!;
        private ProgressBar _pbPeakMeter = null!;
        private Label _lblMeterVal = null!;
        private TrackBar _tbSensitivity = null!;
        private Label _lblSensitivityVal = null!;
        private TrackBar _tbDuckedVolume = null!;
        private Label _lblDuckedVolumeVal = null!;
        private TrackBar _tbHoldDelay = null!;
        private Label _lblHoldDelayVal = null!;
        private ToolTip _uiToolTips = null!;

        // Modern Theme Palette
        private readonly Color _colorBg = Color.FromArgb(32, 32, 32);
        private readonly Color _colorCardBg = Color.FromArgb(45, 45, 48);
        private readonly Color _colorTextPrimary = Color.FromArgb(240, 240, 240);
        private readonly Color _colorTextSecondary = Color.FromArgb(170, 170, 170);
        private readonly Color _colorAccent = Color.FromArgb(0, 120, 212);
        private readonly Color _colorActiveGreen = Color.FromArgb(108, 203, 95);

        public Form1()
        {
            InitializeComponent();
            ApplyNativeWin11Corners();
            LoadApplicationIcon();
            SetupUILayout();
            InitSystemTray();
            InitAudioDevice();

            // Initialize background timer in a stopped state (due: Infinite, period: Infinite)
            _duckingTimer = new System.Threading.Timer(DuckingTimer_Callback, null, Timeout.Infinite, Timeout.Infinite);
        }

        private void Form1_Load(object? sender, EventArgs e)
        {
            // Intentionally empty
        }

        /// <summary>
        /// Centralized unmanaged resource cleanup
        /// </summary>
        private void CleanupResources()
        {
            StopDucking();
            FreeAudioSessions();

            _duckingTimer?.Dispose();
            _notifyIcon?.Dispose();
            _defaultDevice?.Dispose();
            _deviceEnumerator?.Dispose();
            _trayMenu?.Dispose();
            _uiToolTips?.Dispose();
        }

        private void ApplyNativeWin11Corners()
        {
            if (Environment.OSVersion.Version.Build >= 22000)
            {
                try
                {
                    int preference = DWMWCP_ROUND;
                    DwmSetWindowAttribute(this.Handle, DWMWA_WINDOW_CORNER_PREFERENCE, ref preference, sizeof(int));
                }
                catch (Exception) { /* Fails silently if unsupported */ }
            }
        }

        private void LoadApplicationIcon()
        {
            try
            {
                var assembly = typeof(Form1).Assembly;
                using var stream = assembly.GetManifestResourceStream("AudioDucker.Icon.AudioDucker.ico");
                if (stream != null)
                {
                    this.Icon = new System.Drawing.Icon(stream);
                    return;
                }
            }
            catch (Exception) { }
            this.Icon = SystemIcons.Application;
        }

        private void InitSystemTray()
        {
            _trayMenu = new ContextMenuStrip();
            _trayMenu.Items.Add("Open WASAPI Audio Ducker", null, (s, e) => RestoreFromTray());
            _trayMenu.Items.Add("-");
            _trayMenu.Items.Add("Exit", null, (s, e) => ExitApplication());

            _notifyIcon = new NotifyIcon
            {
                Icon = this.Icon,
                Text = "WASAPI Audio Ducker - Stopped",
                ContextMenuStrip = _trayMenu,
                Visible = true
            };

            _notifyIcon.MouseDoubleClick += (s, e) => RestoreFromTray();
            this.Resize += Form1_Resize;
            this.FormClosing += Form1_FormClosing;
        }

        private void Form1_Resize(object? sender, EventArgs e)
        {
            if (this.WindowState == FormWindowState.Minimized)
            {
                this.Hide();
                _notifyIcon.ShowBalloonTip(1000, "WASAPI Audio Ducker", "Running in background.", ToolTipIcon.Info);
            }
        }

        private void RestoreFromTray()
        {
            this.Show();
            this.WindowState = FormWindowState.Normal;
            this.Activate();
        }

        private void ExitApplication()
        {
            _isExiting = true;
            CleanupResources();
            Application.Exit();
        }

        private void Form1_FormClosing(object? sender, FormClosingEventArgs e)
        {
            if (!_isExiting && e.CloseReason == CloseReason.UserClosing)
            {
                e.Cancel = true;
                this.WindowState = FormWindowState.Minimized;
            }
            else
            {
                CleanupResources();
            }
        }

        private void InitAudioDevice()
        {
            try
            {
                _deviceEnumerator = new MMDeviceEnumerator();
                _defaultDevice = _deviceEnumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
                RefreshAudioSessions();
            }
            catch (Exception ex)
            {
                UpdateStatusUI($"Audio Device Error: {ex.Message}", Color.Red);
            }
        }

        private void RefreshAudioSessions()
        {
            _isUpdatingChecks = true;
            _clbTriggers.Items.Clear();
            _clbTargets.Items.Clear();

            if (_defaultDevice == null) return;

            try
            {
                _defaultDevice.AudioSessionManager.RefreshSessions();
                var sessions = _defaultDevice.AudioSessionManager.Sessions;
                var addedPids = new HashSet<uint>();

                for (int i = 0; i < sessions.Count; i++)
                {
                    var session = sessions[i];
                    uint pid = session.GetProcessID;

                    if (pid == 0 || addedPids.Contains(pid)) continue;
                    if (session.State == AudioSessionState.AudioSessionStateExpired) continue;

                    try
                    {
                        var proc = Process.GetProcessById((int)pid);
                        string baseName = proc.ProcessName;
                        var item = new AudioSessionItem
                        {
                            DisplayName = $"{baseName} (PID: {pid})",
                            ProcessName = baseName,
                            ProcessId = pid
                        };

                        int trigIndex = _clbTriggers.Items.Add(item);
                        if (_savedTriggerNames.Contains(baseName))
                            _clbTriggers.SetItemChecked(trigIndex, true);

                        int targIndex = _clbTargets.Items.Add(item);
                        if (_savedTargetNames.Contains(baseName))
                            _clbTargets.SetItemChecked(targIndex, true);

                        addedPids.Add(pid);
                    }
                    catch (Exception) { /* Process likely exited while enumerating */ }
                }
            }
            catch (Exception ex)
            {
                UpdateStatusUI($"Refresh Error: {ex.Message}", Color.Red);
            }
            finally
            {
                _isUpdatingChecks = false;
            }
        }

        private void ClbTriggers_ItemCheck(object? sender, ItemCheckEventArgs e)
        {
            if (_isUpdatingChecks) return;

            if (_clbTriggers.Items[e.Index] is AudioSessionItem item)
            {
                if (e.NewValue == CheckState.Checked)
                {
                    _savedTriggerNames.Add(item.ProcessName);
                    UpdateMutualExclusion(_clbTargets, item.ProcessId);
                }
                else
                {
                    _savedTriggerNames.Remove(item.ProcessName);
                }
            }
        }

        private void ClbTargets_ItemCheck(object? sender, ItemCheckEventArgs e)
        {
            if (_isUpdatingChecks) return;

            if (_clbTargets.Items[e.Index] is AudioSessionItem item)
            {
                if (e.NewValue == CheckState.Checked)
                {
                    _savedTargetNames.Add(item.ProcessName);
                    UpdateMutualExclusion(_clbTriggers, item.ProcessId);
                }
                else
                {
                    _savedTargetNames.Remove(item.ProcessName);
                }
            }
        }

        private void UpdateMutualExclusion(CheckedListBox targetList, uint processId)
        {
            _isUpdatingChecks = true;
            for (int i = 0; i < targetList.Items.Count; i++)
            {
                if (targetList.Items[i] is AudioSessionItem targetItem && targetItem.ProcessId == processId)
                {
                    targetList.SetItemChecked(i, false);
                    if (targetList == _clbTriggers) _savedTriggerNames.Remove(targetItem.ProcessName);
                    if (targetList == _clbTargets) _savedTargetNames.Remove(targetItem.ProcessName);
                }
            }
            _isUpdatingChecks = false;
        }

        private void FreeAudioSessions()
        {
            lock (_audioLock)
            {
                foreach (var session in _activeTriggers) session?.Dispose();
                foreach (var session in _activeTargets) session?.Dispose();

                _activeTriggers.Clear();
                _activeTargets.Clear();
                _originalVolumes.Clear();
            }
        }

        private void PrepareDuckingSessions()
        {
            FreeAudioSessions();

            var triggerPids = new HashSet<uint>();
            foreach (AudioSessionItem item in _clbTriggers.CheckedItems) triggerPids.Add(item.ProcessId);

            var targetPids = new HashSet<uint>();
            foreach (AudioSessionItem item in _clbTargets.CheckedItems) targetPids.Add(item.ProcessId);

            _defaultDevice.AudioSessionManager.RefreshSessions();
            var sessions = _defaultDevice.AudioSessionManager.Sessions;

            lock (_audioLock)
            {
                for (int i = 0; i < sessions.Count; i++)
                {
                    var session = sessions[i];
                    uint pid = session.GetProcessID;

                    if (session.State == AudioSessionState.AudioSessionStateExpired) continue;

                    if (triggerPids.Contains(pid)) _activeTriggers.Add(session);
                    if (targetPids.Contains(pid)) _activeTargets.Add(session);
                }
            }
        }

        private void ToggleDucking()
        {
            // Check if timer is currently running by inspecting state or bool flag
            if (_isCurrentlyDucking || _duckingTimer != null && IsTimerRunning())
            {
                StopDucking();
            }
            else
            {
                if (_clbTriggers.CheckedItems.Count == 0 || _clbTargets.CheckedItems.Count == 0)
                {
                    MessageBox.Show("Please select at least 1 Trigger and 1 Target app.", "Setup Required", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    return;
                }

                PrepareDuckingSessions();

                // Start background thread timer (due time: 0ms, period: 50ms)
                _duckingTimer.Change(0, 50);

                _btnToggle.Text = "S&top Ducking";
                _btnToggle.BackColor = Color.FromArgb(196, 43, 28);
                UpdateStatusUI("Status: Listening (Idle)", _colorTextSecondary);
                _notifyIcon.Text = "WASAPI Audio Ducker - Listening";
            }
        }

        private bool IsTimerRunning()
        {
            // If active triggers/targets contain items or monitoring is enabled, we can verify via a simple flag or checking timer change.
            // Using _duckingTimer.Change(DueTime, Period) with a flag is safest. Here we track via _isCurrentlyDucking or a separate active flag.
            return _duckingTimerActive;
        }

        private volatile bool _duckingTimerActive = false;

        private void StopDucking()
        {
            _duckingTimer.Change(Timeout.Infinite, Timeout.Infinite);
            _duckingTimerActive = false;
            _holdStopwatch.Stop();

            RestoreVolumes();

            _isCurrentlyDucking = false;
            _btnToggle.Text = "&Start Ducking";
            _btnToggle.BackColor = _colorAccent;
            UpdateStatusUI("Status: Stopped", _colorTextSecondary);

            UpdatePeakMeterUI(0);
            _notifyIcon.Text = "WASAPI Audio Ducker - Stopped";
        }

        /// <summary>
        /// Background thread callback executed every 50ms by System.Threading.Timer
        /// </summary>
        private void DuckingTimer_Callback(object? state)
        {
            _duckingTimerActive = true;
            float maxTriggerPeak = 0.0f;

            lock (_audioLock)
            {
                for (int i = _activeTriggers.Count - 1; i >= 0; i--)
                {
                    var trigger = _activeTriggers[i];
                    try
                    {
                        if (trigger.State == AudioSessionState.AudioSessionStateExpired)
                        {
                            _activeTriggers.RemoveAt(i);
                            continue;
                        }

                        float peak = trigger.AudioMeterInformation.MasterPeakValue;
                        if (peak > maxTriggerPeak) maxTriggerPeak = peak;
                    }
                    catch (Exception)
                    {
                        _activeTriggers.RemoveAt(i);
                    }
                }
            }

            int peakPercentage = (int)Math.Min(100, Math.Max(0, maxTriggerPeak * 100));
            UpdatePeakMeterUI(peakPercentage);

            // Access trackbar values safely on UI thread or store values locally. TrackBar values can be read safely via Invoke if needed, or cached. 
            // Reading TrackBar value cross-thread in Windows Forms can throw exception, so we use Invoke to fetch configuration values safely.
            float threshold = 0.05f;
            float duckMultiplier = 0.20f;
            int holdTimeMs = 1500;

            if (this.IsHandleCreated && !this.IsDisposed)
            {
                this.Invoke(new Action(() =>
                {
                    threshold = _tbSensitivity.Value / 100.0f;
                    duckMultiplier = _tbDuckedVolume.Value / 100.0f;
                    holdTimeMs = _tbHoldDelay.Value;
                }));
            }

            if (maxTriggerPeak > threshold)
            {
                _holdStopwatch.Restart();
                if (!_isCurrentlyDucking)
                {
                    _isCurrentlyDucking = true;
                    ApplyDucking(duckMultiplier);
                    UpdateStatusUI("Status: Ducking Active! (Audio Detected)", _colorActiveGreen);
                }
            }
            else if (_isCurrentlyDucking && _holdStopwatch.ElapsedMilliseconds > holdTimeMs)
            {
                _isCurrentlyDucking = false;
                _holdStopwatch.Stop();
                RestoreVolumes();
                UpdateStatusUI("Status: Listening (Idle)", _colorTextSecondary);
            }
        }

        private void UpdatePeakMeterUI(int peakPercentage)
        {
            if (this.IsDisposed || !this.IsHandleCreated) return;

            if (this.InvokeRequired)
            {
                this.BeginInvoke(new Action(() => UpdatePeakMeterUI(peakPercentage)));
                return;
            }

            if (_lastPeakPercentage != peakPercentage)
            {
                _pbPeakMeter.Value = peakPercentage;
                _lblMeterVal.Text = $"{peakPercentage}%";
                _lastPeakPercentage = peakPercentage;
            }
        }

        private void UpdateStatusUI(string text, Color color)
        {
            if (this.IsDisposed || !this.IsHandleCreated) return;

            if (this.InvokeRequired)
            {
                this.BeginInvoke(new Action(() => UpdateStatusUI(text, color)));
                return;
            }

            _lblStatus.Text = text;
            _lblStatus.ForeColor = color;
        }

        private void ApplyDucking(float multiplier)
        {
            lock (_audioLock)
            {
                for (int i = _activeTargets.Count - 1; i >= 0; i--)
                {
                    var target = _activeTargets[i];
                    try
                    {
                        if (target.State == AudioSessionState.AudioSessionStateExpired)
                        {
                            _activeTargets.RemoveAt(i);
                            continue;
                        }

                        uint pid = target.GetProcessID;
                        float currentVol = target.SimpleAudioVolume.Volume;

                        _originalVolumes.TryAdd(pid, currentVol);
                        target.SimpleAudioVolume.Volume = currentVol * multiplier;
                    }
                    catch (Exception)
                    {
                        _activeTargets.RemoveAt(i);
                    }
                }
            }
        }

        private void UpdateDuckingVolumeLive()
        {
            if (!_isCurrentlyDucking) return;

            float multiplier = _tbDuckedVolume.Value / 100.0f;
            lock (_audioLock)
            {
                for (int i = _activeTargets.Count - 1; i >= 0; i--)
                {
                    var target = _activeTargets[i];
                    try
                    {
                        if (target.State == AudioSessionState.AudioSessionStateExpired) continue;

                        uint pid = target.GetProcessID;
                        if (_originalVolumes.TryGetValue(pid, out float origVol))
                        {
                            target.SimpleAudioVolume.Volume = origVol * multiplier;
                        }
                    }
                    catch (Exception) { }
                }
            }
        }

        private void RestoreVolumes()
        {
            lock (_audioLock)
            {
                for (int i = _activeTargets.Count - 1; i >= 0; i--)
                {
                    var target = _activeTargets[i];
                    try
                    {
                        if (target.State == AudioSessionState.AudioSessionStateExpired)
                        {
                            _activeTargets.RemoveAt(i);
                            continue;
                        }

                        uint pid = target.GetProcessID;
                        if (_originalVolumes.TryGetValue(pid, out float origVol))
                        {
                            target.SimpleAudioVolume.Volume = origVol;
                        }
                    }
                    catch (Exception)
                    {
                        _activeTargets.RemoveAt(i);
                    }
                }
                _originalVolumes.Clear();
            }
        }

        private void SetupUILayout()
        {
            this.Text = "WASAPI Audio Ducker";
            this.Size = new Size(600, 560);
            this.BackColor = _colorBg;
            this.ForeColor = _colorTextPrimary;
            this.Font = new Font("Segoe UI", 9.5f, FontStyle.Regular);
            this.FormBorderStyle = FormBorderStyle.FixedSingle;
            this.MaximizeBox = false;

            _uiToolTips = new ToolTip();
            _uiToolTips.AutoPopDelay = 5000;
            _uiToolTips.InitialDelay = 500;
            _uiToolTips.ReshowDelay = 100;
            _uiToolTips.ShowAlways = true;

            Label lblTrig = new Label { Text = "Trigger Apps:", Location = new Point(20, 15), AutoSize = true, ForeColor = _colorTextSecondary };
            _clbTriggers = new CheckedListBox
            {
                Location = new Point(20, 38),
                Width = 260,
                Height = 120,
                CheckOnClick = true,
                BackColor = _colorCardBg,
                ForeColor = _colorTextPrimary,
                BorderStyle = BorderStyle.None,
                HorizontalScrollbar = true,
                AccessibleName = "Trigger Applications",
                TabIndex = 0
            };
            _clbTriggers.ItemCheck += ClbTriggers_ItemCheck;

            Label lblTarg = new Label { Text = "Target Apps:", Location = new Point(300, 15), AutoSize = true, ForeColor = _colorTextSecondary };
            _clbTargets = new CheckedListBox
            {
                Location = new Point(300, 38),
                Width = 260,
                Height = 120,
                CheckOnClick = true,
                BackColor = _colorCardBg,
                ForeColor = _colorTextPrimary,
                BorderStyle = BorderStyle.None,
                HorizontalScrollbar = true,
                AccessibleName = "Target Applications",
                TabIndex = 1
            };
            _clbTargets.ItemCheck += ClbTargets_ItemCheck;

            Label lblMeter = new Label { Text = "Live Trigger Audio Peak:", Location = new Point(20, 172), AutoSize = true, ForeColor = _colorTextSecondary };
            _pbPeakMeter = new ProgressBar { Location = new Point(20, 192), Width = 470, Height = 18, Minimum = 0, Maximum = 100, Value = 0, AccessibleName = "Audio Peak Meter" };
            _lblMeterVal = new Label { Text = "0%", Location = new Point(500, 192), AutoSize = true, ForeColor = _colorTextSecondary };

            Label lblSens = new Label { Text = "Trigger Sensitivity (Threshold):", Location = new Point(20, 225), AutoSize = true };
            _tbSensitivity = new TrackBar { Location = new Point(20, 245), Width = 470, Minimum = 1, Maximum = 50, Value = 5, BackColor = _colorBg, TickStyle = TickStyle.None, AccessibleName = "Sensitivity Threshold Slider", TabIndex = 2 };
            _uiToolTips.SetToolTip(_tbSensitivity, "How loud the trigger app needs to be before it lowers target volumes.");

            _lblSensitivityVal = new Label { Text = "5%", Location = new Point(500, 248), AutoSize = true, ForeColor = _colorTextSecondary };
            _tbSensitivity.ValueChanged += (s, e) => _lblSensitivityVal.Text = $"{_tbSensitivity.Value}%";

            Label lblVol = new Label { Text = "Ducked Volume (Multiplier):", Location = new Point(20, 295), AutoSize = true };
            _tbDuckedVolume = new TrackBar { Location = new Point(20, 315), Width = 470, Minimum = 0, Maximum = 100, Value = 20, BackColor = _colorBg, TickStyle = TickStyle.None, AccessibleName = "Ducked Volume Slider", TabIndex = 3 };
            _uiToolTips.SetToolTip(_tbDuckedVolume, "What percentage to reduce the target app's volume to (e.g., 20% of its original volume).");

            _lblDuckedVolumeVal = new Label { Text = "20%", Location = new Point(500, 318), AutoSize = true, ForeColor = _colorTextSecondary };
            _tbDuckedVolume.ValueChanged += (s, e) =>
            {
                _lblDuckedVolumeVal.Text = $"{_tbDuckedVolume.Value}%";
                UpdateDuckingVolumeLive();
            };

            Label lblHold = new Label { Text = "Release Hold Delay:", Location = new Point(20, 365), AutoSize = true };
            _tbHoldDelay = new TrackBar { Location = new Point(20, 385), Width = 470, Minimum = 500, Maximum = 3000, SmallChange = 100, LargeChange = 250, Value = 1500, BackColor = _colorBg, TickStyle = TickStyle.None, AccessibleName = "Release Hold Delay Slider", TabIndex = 4 };
            _uiToolTips.SetToolTip(_tbHoldDelay, "How long to wait after the trigger app goes quiet before restoring the target volume.");

            _lblHoldDelayVal = new Label { Text = "1500 ms", Location = new Point(500, 388), AutoSize = true, ForeColor = _colorTextSecondary };
            _tbHoldDelay.ValueChanged += (s, e) => _lblHoldDelayVal.Text = $"{_tbHoldDelay.Value} ms";

            _btnRefresh = new Button
            {
                Text = "&Refresh Apps",
                Location = new Point(20, 445),
                Width = 130,
                Height = 35,
                FlatStyle = FlatStyle.Flat,
                BackColor = _colorCardBg,
                ForeColor = _colorTextPrimary,
                Cursor = Cursors.Hand,
                AccessibleName = "Refresh Applications Button",
                TabIndex = 5
            };
            _btnRefresh.FlatAppearance.BorderSize = 0;
            _btnRefresh.Click += (s, e) => RefreshAudioSessions();

            _btnToggle = new Button
            {
                Text = "&Start Ducking",
                Location = new Point(160, 445),
                Width = 400,
                Height = 35,
                FlatStyle = FlatStyle.Flat,
                BackColor = _colorAccent,
                ForeColor = Color.White,
                Font = new Font("Segoe UI", 10f, FontStyle.Bold),
                Cursor = Cursors.Hand,
                AccessibleName = "Toggle Ducking Button",
                TabIndex = 6
            };
            _btnToggle.FlatAppearance.BorderSize = 0;
            _btnToggle.Click += (s, e) => ToggleDucking();

            _lblStatus = new Label
            {
                Text = "Status: Stopped",
                Location = new Point(20, 490),
                AutoSize = true,
                Font = new Font("Segoe UI", 9.5f, FontStyle.Bold),
                ForeColor = _colorTextSecondary,
                AccessibleName = "Application Status"
            };

            this.Controls.Add(lblTrig); this.Controls.Add(_clbTriggers);
            this.Controls.Add(lblTarg); this.Controls.Add(_clbTargets);
            this.Controls.Add(lblMeter); this.Controls.Add(_pbPeakMeter); this.Controls.Add(_lblMeterVal);
            this.Controls.Add(lblSens); this.Controls.Add(_tbSensitivity); this.Controls.Add(_lblSensitivityVal);
            this.Controls.Add(lblVol); this.Controls.Add(_tbDuckedVolume); this.Controls.Add(_lblDuckedVolumeVal);
            this.Controls.Add(lblHold); this.Controls.Add(_tbHoldDelay); this.Controls.Add(_lblHoldDelayVal);
            this.Controls.Add(_btnRefresh); this.Controls.Add(_btnToggle); this.Controls.Add(_lblStatus);
        }

        private class AudioSessionItem
        {
            public string DisplayName { get; set; } = string.Empty;
            public string ProcessName { get; set; } = string.Empty;
            public uint ProcessId { get; set; }
            public override string ToString() => DisplayName;
        }
    }
}