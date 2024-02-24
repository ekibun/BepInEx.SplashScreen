#if !GUI
using System;
using System.Collections;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using BepInEx.Configuration;
using BepInEx.Logging;
using HarmonyLib;

namespace BepInEx.SplashScreen
{
    public static class SplashScreenController
    {
        internal static readonly ManualLogSource Logger = Logging.Logger.CreateLogSource("Splash");
        private static readonly Queue _StatusQueue = Queue.Synchronized(new Queue(10, 2));
        private static LoadingLogListener _logListener;
        private static Process _guiProcess;

        public static void SpawnSplash()
        {
            try
            {
                var config = (ConfigFile)AccessTools.Property(typeof(ConfigFile), "CoreConfig").GetValue(null, null);

                var isEnabled = config.Bind("SplashScreen", "Enabled", true, "Display a splash screen with information about game load progress on game start-up.").Value;
#if DEBUG
                const bool onlyNoConsoleDefault = false;
#else
                const bool onlyNoConsoleDefault = true;
#endif
                var consoleNotAllowed = config.Bind("SplashScreen", "OnlyNoConsole", onlyNoConsoleDefault, "Only display the splash screen if the logging console is turned off.").Value;

                if (!isEnabled)
                {
                    Logger.LogDebug("Not showing splash because the Enabled setting is off");
                    return;
                }

                if (consoleNotAllowed)
                {
                    if (config.TryGetEntry("Logging.Console", "Enabled", out ConfigEntry<bool> entry) && entry.Value)
                    {
                        Logger.LogDebug("Not showing splash because the console is enabled");
                        return;
                    }
                }

                NativeMethods.SetWinEventHook(NativeMethods.EVENT_OBJECT_CREATE, NativeMethods.EVENT_OBJECT_CREATE, IntPtr.Zero,
                        hookCallback, 0, 0, 0);

                _logListener = LoadingLogListener.StartListening();
            }
            catch (Exception e)
            {
                Logger.LogError("Failed to start GUI: " + e);
                KillSplash();
            }
        }

        static IntPtr _gameWindow = IntPtr.Zero;
        static IntPtr _splashWindow = IntPtr.Zero;

        static NativeMethods.WinEventDelegate hookCallback = (IntPtr hWinEventHook, uint eventType,
                    IntPtr hWnd, int idObject, int idChild, uint dwEventThread, uint dwmsEventTime) =>
        {
            NativeMethods.GetWindowThreadProcessId(hWnd, out var windowProcessId);

            if (windowProcessId == Process.GetCurrentProcess().Id)
            {
                StringBuilder className = new StringBuilder(256);
                NativeMethods.GetClassName(hWnd, className, className.Capacity);

                if (className.ToString() == "UnityWndClass")
                {
                    Logger.LogDebug("UnityWndClass Window created");
                    NativeMethods.SetWindowPos(hWnd, IntPtr.Zero, 0, 0, 1280, 720, NativeMethods.SWP_NOMOVE);
                    NativeMethods.ShowWindow(hWnd, NativeMethods.SW_RESTORE);

                    _gameWindow = hWnd;

                    var guiExecutablePath = Path.Combine(Path.GetDirectoryName(typeof(SplashScreenController).Assembly.Location) ?? Paths.PatcherPluginPath, "BepInEx.SplashScreen.GUI.exe");

                    if (!File.Exists(guiExecutablePath))
                        throw new FileNotFoundException("Executable not found or inaccessible at " + guiExecutablePath);

                    Logger.Log(LogLevel.Debug, "Starting GUI process: " + guiExecutablePath);

                    var psi = new ProcessStartInfo(guiExecutablePath, hWnd.ToString())
                    {
                        UseShellExecute = false,
                        RedirectStandardInput = true,
                        RedirectStandardOutput = true,
                        RedirectStandardError = true,
                        StandardOutputEncoding = Encoding.UTF8,
                        StandardErrorEncoding = Encoding.UTF8,
                    };
                    _guiProcess = Process.Start(psi);

                    new Thread(CommunicationThread) { IsBackground = true }.Start(_guiProcess);

                    NativeMethods.UnhookWinEvent(hWinEventHook);
                }
            }
        };

        private static class NativeMethods
        {
            [DllImport("user32.dll", SetLastError = true)]
            [return: MarshalAs(UnmanagedType.Bool)]
            public static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int X, int Y, int cx, int cy, uint uFlags);

            public const uint SWP_NOSIZE = 0x0001;
            public const uint SWP_NOMOVE = 0x0002;

            [DllImport("user32.dll")]
            public static extern IntPtr SetParent(IntPtr hWndChild, IntPtr hWndNewParent);

            public const uint EVENT_OBJECT_CREATE = 0x8000;

            public delegate void WinEventDelegate(IntPtr hWinEventHook, uint eventType,
                IntPtr hwnd, int idObject, int idChild, uint dwEventThread, uint dwmsEventTime);

            [DllImport("user32.dll")]
            public static extern IntPtr SetWinEventHook(uint eventMin, uint eventMax, IntPtr hmodWinEventProc,
                WinEventDelegate lpfnWinEventProc, uint idProcess, uint idThread, uint dwFlags);

            [DllImport("user32.dll")]
            [return: MarshalAs(UnmanagedType.Bool)]
            public static extern bool UnhookWinEvent(IntPtr hWinEventHook);

            [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
            public static extern int GetClassName(IntPtr hWnd, StringBuilder lpClassName, int nMaxCount);

            [DllImport("user32.dll")]
            public static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

            [DllImport("user32.dll")]
            public static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

            public const int SW_RESTORE = 9;
        }

        internal static void SendMessage(string message)
        {
            _StatusQueue.Enqueue(message);
        }

        private static void CommunicationThread(object processArg)
        {
            try
            {
                var guiProcess = (Process)processArg;

                guiProcess.Exited += (sender, args) => KillSplash();

                guiProcess.OutputDataReceived += (sender, args) =>
                {
                    if (args.Data != null) Logger.Log(LogLevel.Debug, "[GUI] " + args.Data.Replace('\t', '\n').TrimEnd('\n'));
                };
                guiProcess.BeginOutputReadLine();

                guiProcess.ErrorDataReceived += (sender, args) =>
                {
                    if (args.Data != null) Logger.Log(LogLevel.Error, "[GUI] " + args.Data.Replace('\t', '\n').TrimEnd('\n'));
                };
                guiProcess.BeginErrorReadLine();

                guiProcess.StandardInput.AutoFlush = false;

                Logger.LogDebug("Connected to the GUI process");

                var any = false;
                while (!guiProcess.HasExited)
                {
                    while (_StatusQueue.Count > 0 && guiProcess.StandardInput.BaseStream.CanWrite)
                    {
                        guiProcess.StandardInput.WriteLine(_StatusQueue.Dequeue());
                        any = true;
                    }

                    if (any)
                    {
                        any = false;
                        guiProcess.StandardInput.Flush();
                    }

                    Thread.Sleep(150);
                }
            }
            catch (ThreadAbortException)
            {
                // I am die, thank you forever
            }
            catch (Exception e)
            {
                Logger.LogError((object)$"Crash in {nameof(CommunicationThread)}, aborting. Exception: {e}");
            }
            finally
            {
                KillSplash();
            }
        }

        internal static void KillSplash()
        {
            try
            {
                _logListener?.Dispose();

                _StatusQueue.Clear();
                _StatusQueue.TrimToSize();

                try
                {
                    if (_guiProcess != null && !_guiProcess.HasExited)
                    {
                        Logger.LogDebug("Closing GUI process");
                        _guiProcess.Kill();
                    }
                }
                catch (Exception)
                {
                    // _guiProcess already quit so Kill threw
                }

                Logger.Dispose();
                // todo not thread safe
                // Logging.Logger.Sources.Remove(Logger);
            }
            catch (Exception e)
            {
                // Welp, no Logger left to use. This shouldn't ever happen annyways.
                Console.WriteLine(e);
            }
        }
    }
}
#endif