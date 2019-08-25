using LiveSplit.ComponentUtil;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace LiveSplit.MagesOfMystralia
{
    class GameMemory
    {

        public event EventHandler OnLoadStarted;
        public event EventHandler OnLoadFinished;
        //public event EventHandler OnLevelChanged;
        public event EventHandler OnFirstLevelLoad;

        private Task _thread;
        private CancellationTokenSource _cancelSource;
        private SynchronizationContext _uiThread;
        private List<int> _ignorePIDs;
        private MagesOfMystraliaSettings _settings;

        IntPtr currentIsPausedAddress = IntPtr.Zero;

        string SigScanPattern_InventoryStatusUpdateCurrentTimePlayed =
            "55 " +
            "48 8B EC " +
            "48 83 EC 10 " +
            "B8 ?? ?? ?? ?? " +
            "48 0F B6 00 " +
            "85 C0 " +
            "0F 84 ?? ?? ?? ?? " +
            "B8 ?? ?? ?? ?? " +
            "F3 0F 10 00 " +
            "F3 0F 5A C0 " +
            "F2 0F 5A E8 " +
            "F3 0F 11 6D FC";

        public GameMemory(MagesOfMystraliaSettings componentSettings)
        {
            _settings = componentSettings;
            _ignorePIDs = new List<int>();
        }

        public void StartMonitoring()
        {
            if (_thread != null && _thread.Status == TaskStatus.Running)
            {
                throw new InvalidOperationException();
            }
            if (!(SynchronizationContext.Current is WindowsFormsSynchronizationContext))
            {
                throw new InvalidOperationException("SynchronizationContext.Current is not a UI thread.");
            }

            _uiThread = SynchronizationContext.Current;
            _cancelSource = new CancellationTokenSource();
            _thread = Task.Factory.StartNew(MemoryReadThread);
        }

        public void Stop()
        {
            if (_cancelSource == null || _thread == null || _thread.Status != TaskStatus.Running)
            {
                return;
            }

            _cancelSource.Cancel();
            _thread.Wait();


        }

        int failedScansCount = 0;
        bool isLoading = false;
        bool prevIsLoading = false;
        bool loadingStarted = false;

        //Used in displaying status in Settings
        enum InjectionStatus
        {
            NoProcess,
            FoundProcessWaiting,
            Scanning,
            FailedScanning,
            Found
        }

        InjectionStatus lastInjectionStatus = InjectionStatus.NoProcess;

        Process game;

        void MemoryReadThread()
        {
            Debug.WriteLine("[NoLoads] MemoryReadThread");

            while (!_cancelSource.IsCancellationRequested)
            {
                try
                {
                    Debug.WriteLine("[NoLoads] Waiting for Build.exe...");

                    while ((game = GetGameProcess()) == null)
                    {
                        Thread.Sleep(250);
                        if (_cancelSource.IsCancellationRequested)
                        {
                            return;
                        }

                        isLoading = true;

                        if (isLoading != prevIsLoading)
                        {
                            loadingStarted = true;

                            // pause game timer
                            _uiThread.Post(d =>
                            {
                                if (OnLoadStarted != null)
                                {
                                    OnLoadStarted(this, EventArgs.Empty);
                                }
                            }, null);
                        }

                        prevIsLoading = true;

                        SetInjectionLabelInSettings(InjectionStatus.NoProcess, IntPtr.Zero);
                    }

                    Debug.WriteLine("[NoLoads] Got games process!");

                    uint frameCounter = 0;

                    while (!game.HasExited)
                    {
                        if (currentIsPausedAddress == IntPtr.Zero)
                        {
                            #region Hooking
                            if (_settings.RescansLimit != 0 && failedScansCount >= _settings.RescansLimit)
                            {
                                var result = MessageBox.Show("Failed to find the pattern during the 3 scan loops. Want to retry scans?", "Error", MessageBoxButtons.RetryCancel, MessageBoxIcon.Exclamation);
                                if (result == DialogResult.Cancel)
                                {
                                    _ignorePIDs.Add(game.Id);
                                }
                                else
                                    failedScansCount = 0;
                                //Should refresh game pages... hopefully. The memory pages extansion is really poop.
                                game = null;

                                SetInjectionLabelInSettings(InjectionStatus.FailedScanning, IntPtr.Zero);
                            }
                            //Hook only if the process is at least 15s old (since it takes forever with allocating stuff)
                            else if (game.UserProcessorTime >= TimeSpan.FromSeconds(15))
                            {
                                SetInjectionLabelInSettings(InjectionStatus.Scanning, IntPtr.Zero);

                                var sigScanTarget = new SigScanTarget(SigScanPattern_InventoryStatusUpdateCurrentTimePlayed);
                                IntPtr StartOfUpdateCurrentTime = IntPtr.Zero;

                                Debug.WriteLine("[NOLOADS] Scanning for signature (InventoryStats:UpdateCurrentTimePlayed)");
                                foreach (var page in game.MemoryPages())
                                {
                                    var scanner = new SignatureScanner(game, page.BaseAddress, (int)page.RegionSize);
                                    if ((StartOfUpdateCurrentTime = scanner.Scan(sigScanTarget)) != IntPtr.Zero)
                                    {
                                        break;
                                    }
                                }


                                if (StartOfUpdateCurrentTime == IntPtr.Zero)
                                {
                                    failedScansCount++;
                                    Debug.WriteLine("[NOLOADS] Failed scans: " + failedScansCount);
                                }
                                else
                                {
                                    currentIsPausedAddress = StartOfUpdateCurrentTime + 0x9;
                                    Debug.WriteLine("[NOLOADS] FOUND SIGNATURE FOR _isPaused AT: 0x" + currentIsPausedAddress.ToString("X8"));

                                    SetInjectionLabelInSettings(InjectionStatus.Found, currentIsPausedAddress);
                                }
                            }
                            else
                                SetInjectionLabelInSettings(InjectionStatus.FoundProcessWaiting, IntPtr.Zero);
                            #endregion
                        }
                        else
                        {
                            var addy = new IntPtr(game.ReadValue<int>(currentIsPausedAddress));
                            isLoading = game.ReadValue<byte>(addy) == 0;

                            if (isLoading != prevIsLoading)
                            {
                                if (isLoading)
                                {
                                    Debug.WriteLine(String.Format("[NoLoads] Load Start - {0}", frameCounter));

                                    loadingStarted = true;

                                    // pause game timer
                                    _uiThread.Post(d =>
                                    {
                                        if (OnLoadStarted != null)
                                        {
                                            OnLoadStarted(this, EventArgs.Empty);
                                        }
                                    }, null);
                                }
                                else
                                {
                                    Debug.WriteLine(String.Format("[NoLoads] Load End - {0}", frameCounter));

                                    if (loadingStarted)
                                    {
                                        loadingStarted = false;

                                        // unpause game timer
                                        _uiThread.Post(d =>
                                        {
                                            if (OnLoadFinished != null)
                                            {
                                                OnLoadFinished(this, EventArgs.Empty);
                                            }
                                        }, null);

                                        _uiThread.Post(d =>
                                        {
                                            if(OnFirstLevelLoad != null)
                                            {
                                                OnFirstLevelLoad(this, EventArgs.Empty);
                                            }
                                        }, null);
                                    }
                                }
                            }

                            prevIsLoading = isLoading;
                            frameCounter++;

                            Thread.Sleep(15);

                            if (_cancelSource.IsCancellationRequested)
                            {
                                return;
                            }
                        }

                    }

                    // pause game timer on exit or crash
                    _uiThread.Post(d =>
                    {
                        if (OnLoadStarted != null)
                        {
                            OnLoadStarted(this, EventArgs.Empty);
                        }
                    }, null);
                    isLoading = true;
                }
                catch (Exception ex)
                {
                    Debug.WriteLine(ex.ToString());
                    Thread.Sleep(1000);
                }
            }
        }


        Process GetGameProcess()
        {
            Process game = Process.GetProcesses().FirstOrDefault(p => p.ProcessName.ToLower() == "build" && !p.HasExited && !_ignorePIDs.Contains(p.Id));

            if (game == null)
            {
                currentIsPausedAddress = IntPtr.Zero;

                failedScansCount = 0;
                return null;
            }

            if (game.MainWindowTitle == null || game.MainWindowTitle == "")
            {
                return null;
            }

            if (game.MainWindowTitle != "Mages of Mystralia")
            {
                _ignorePIDs.Add(game.Id);
                return null;
            }

            return game;
        }

        private void SetInjectionLabelInSettings(InjectionStatus currentInjectionStatus, IntPtr injectedPointer)
        {
            if(lastInjectionStatus != currentInjectionStatus || currentInjectionStatus == InjectionStatus.Scanning)
            {
                //UI is on different thread, invoke is required
                if (_settings.L_InjectionStatus.InvokeRequired)
                    _settings.L_InjectionStatus.Invoke(new Action(() => SetInjectionLabelInSettings(currentInjectionStatus, injectedPointer)));
                else
                {
                    switch (currentInjectionStatus)
                    {
                        case (InjectionStatus.NoProcess):
                            _settings.L_InjectionStatus.ForeColor = System.Drawing.Color.Black;
                            _settings.L_InjectionStatus.Text = "No process found";
                            break;
                        case (InjectionStatus.FoundProcessWaiting):
                            _settings.L_InjectionStatus.ForeColor = System.Drawing.Color.DarkBlue;
                            _settings.L_InjectionStatus.Text = "Found process! Waiting for it to mature (15 seconds).";
                            break;
                        case (InjectionStatus.Scanning):
                            _settings.L_InjectionStatus.ForeColor = System.Drawing.Color.Blue;
                            _settings.L_InjectionStatus.Text = string.Format("Scanning... ({0} failed scans).", failedScansCount);
                            break;
                        case (InjectionStatus.FailedScanning):
                            _settings.L_InjectionStatus.ForeColor = System.Drawing.Color.Red;
                            _settings.L_InjectionStatus.Text = "Scan failed!";
                            break;
                        case (InjectionStatus.Found):
                            _settings.L_InjectionStatus.ForeColor = System.Drawing.Color.Green;
                            _settings.L_InjectionStatus.Text = "Successfully found adress at: 0x" + injectedPointer.ToString("X8");
                            break;
                    }

                    lastInjectionStatus = currentInjectionStatus;
                }
            }
        }
    }
}
