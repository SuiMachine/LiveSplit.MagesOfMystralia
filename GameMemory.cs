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
        public event EventHandler OnLevelChanged;
        public event EventHandler OnFirstLevelLoad;

        private Task _thread;
        private CancellationTokenSource _cancelSource;
        private SynchronizationContext _uiThread;
        private List<int> _ignorePIDs;
        private MagesOfMystraliaSettings _settings;

        IntPtr UILoadingScreenPointer = IntPtr.Zero;
        IntPtr originalShowLoadFunctionAddress = IntPtr.Zero;
        IntPtr codeDetour = IntPtr.Zero;

        private byte[] OriginalInstructionBytesShowLoad = new byte[] {
            0x55,
            0x48, 0x8B, 0xEC,
            0x56,
            0x41, 0x57,
            0x48, 0x83, 0xEC, 0x10,
            0x48, 0x8B, 0xF1,
            0x48, 0x8B, 0x46, 0x38,
            0x48, 0x8B, 0xC8,
            0x33, 0xD2,
            0x48, 0x83, 0xEC, 0x20
        };

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

            if(originalShowLoadFunctionAddress != IntPtr.Zero && game != null && !game.HasExited && isLevelSystemHooked)
            {
                Debug.WriteLine("[NOLOADS] Restoring original function.");
                game.Suspend();
                game.WriteBytes(originalShowLoadFunctionAddress, OriginalInstructionBytesShowLoad);
                game.FreeMemory(codeDetour);
                game.FreeMemory(UILoadingScreenPointer);
                game.Resume();

            }

            _cancelSource.Cancel();
            _thread.Wait();


        }

        int failedScansCount = 0;
        bool isLevelSystemHooked = false;
        bool isLoading = false;
        bool prevIsLoading = false;
        int currentLevelID = 0;
        int prevLevelId = 0;
        bool loadingStarted = false;

        //Used in displaying status in Settings
        enum InjectionStatus
        {
            NoProcess,
            FoundProcessWaiting,
            Scanning,
            FailedScanning,
            FailedToInject,
            Injected
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
                        if (!isLevelSystemHooked)
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
                                var contentOfAShowLoadHook = new List<byte>();
                                var contentOfAHideLoadHook = new List<byte>();
                                UILoadingScreenPointer = game.AllocateMemory(IntPtr.Size);

                                var sigScanTarget = new SigScanTarget(OriginalInstructionBytesShowLoad);

                                Debug.WriteLine("[NOLOADS] injectedPtrForLevelSystemPtr allocated at: " + UILoadingScreenPointer.ToString("X8"));
                                var injectedPtrForLevelSystemBytes = BitConverter.GetBytes(UILoadingScreenPointer.ToInt64());

                                originalShowLoadFunctionAddress = IntPtr.Zero;
                                contentOfAShowLoadHook.AddRange(new byte[] { 0x48, 0xB8 });         //mov rax,....
                                contentOfAShowLoadHook.AddRange(injectedPtrForLevelSystemBytes);    //address for rax^^
                                contentOfAShowLoadHook.AddRange(new byte[] { 0x48, 0x89, 0x08 });  //mov [rax], rcx
                                contentOfAShowLoadHook.AddRange(OriginalInstructionBytesShowLoad);
                                contentOfAShowLoadHook.AddRange(new byte[] { 0x90, 0x90, 0x90, 0x90, 0x90, 0x90, 0x90, 0x90, 0x90, 0x90, 0x90, 0x90, 0x90, 0x90, 0x90 }); //14 nops for jmp back (actually needs I think 2 less)

                                Debug.WriteLine("[NOLOADS] Scanning for signature (UILoadingScreen)");
                                foreach (var page in game.MemoryPages())
                                {
                                    var scanner = new SignatureScanner(game, page.BaseAddress, (int)page.RegionSize);
                                    if ((originalShowLoadFunctionAddress = scanner.Scan(sigScanTarget)) != IntPtr.Zero)
                                    {
                                        break;
                                    }
                                }


                                if (originalShowLoadFunctionAddress == IntPtr.Zero)
                                {
                                    failedScansCount++;
                                    Debug.WriteLine("[NOLOADS] Failed scans: " + failedScansCount);
                                    game.FreeMemory(UILoadingScreenPointer);
                                }
                                else
                                {
                                    Debug.WriteLine("[NOLOADS] FOUND SIGNATURE FOR LOADSHOW AT: 0x" + originalShowLoadFunctionAddress.ToString("X8"));

                                    codeDetour = game.AllocateMemory(contentOfAShowLoadHook.Count);
                                    game.Suspend();

                                    try
                                    {
                                        var oInitPtr = game.WriteBytes(codeDetour, contentOfAShowLoadHook.ToArray());
                                        var detourInstalled = game.WriteDetour(originalShowLoadFunctionAddress, OriginalInstructionBytesShowLoad.Length, codeDetour);
                                        var returnInstalled = game.WriteJumpInstruction(codeDetour + contentOfAShowLoadHook.Count - 15, originalShowLoadFunctionAddress + 14);
                                        isLevelSystemHooked = true;
                                        SetInjectionLabelInSettings(InjectionStatus.Injected, UILoadingScreenPointer);
                                    }
                                    catch
                                    {
                                        SetInjectionLabelInSettings(InjectionStatus.FailedToInject, IntPtr.Zero);
                                        throw;
                                    }
                                    finally
                                    {
                                        game.Resume();
                                    }
                                }
                            }
                            else
                                SetInjectionLabelInSettings(InjectionStatus.FoundProcessWaiting, IntPtr.Zero);
                            #endregion
                        }
                        else
                        {
                            var currentPointerDepth0 = game.ReadPointer(UILoadingScreenPointer);
                            var currentPointerDepth1 = game.ReadPointer(game.ReadPointer(UILoadingScreenPointer) + 0x28);

                            currentLevelID = game.ReadValue<int>(game.ReadPointer(game.ReadPointer(UILoadingScreenPointer) + 0x28) +0x48);
                            byte wtf = game.ReadValue<byte>(game.ReadPointer(game.ReadPointer(game.ReadPointer(game.ReadPointer(UILoadingScreenPointer) + 0x28) +0x20) +0x98) +0x14);
                            isLoading = wtf != 0;

                            if (isLoading != prevIsLoading || currentLevelID != prevLevelId)
                            {
#if DEBUG
                                if (currentLevelID != prevLevelId)
                                    Debug.WriteLine("Level changed from " + prevLevelId + " -> " + currentLevelID);
#endif

                                if (isLoading || (currentLevelID == 90))
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

                                if (currentLevelID != prevLevelId && currentLevelID != 90 && prevLevelId != 90 )
                                {
                                    _uiThread.Post(d =>
                                    {
                                        if (OnLevelChanged != null)
                                        {
                                            OnLevelChanged(this, EventArgs.Empty);
                                        }
                                    }, null);
                                }
                            }

                            prevIsLoading = isLoading;
                            prevLevelId = currentLevelID;
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
                UILoadingScreenPointer = IntPtr.Zero;
                originalShowLoadFunctionAddress = IntPtr.Zero;

                failedScansCount = 0;
                isLevelSystemHooked = false;
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
                        case (InjectionStatus.FailedToInject):
                            _settings.L_InjectionStatus.ForeColor = System.Drawing.Color.Red;
                            _settings.L_InjectionStatus.Text = "Failed to inject code!";
                            break;
                        case (InjectionStatus.Injected):
                            _settings.L_InjectionStatus.ForeColor = System.Drawing.Color.Green;
                            _settings.L_InjectionStatus.Text = "Successfully injected code. Ptr copy at: 0x" + injectedPointer.ToString("X8");
                            break;
                    }

                    lastInjectionStatus = currentInjectionStatus;
                }
            }
        }
    }
}
