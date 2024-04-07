﻿/*
 * This file is part of SWBF2Admin (https://github.com/jweigelt/swbf2admin). 
 * Copyright(C) 2017, 2018  Jan Weigelt <jan@lekeks.de>
 *
 * SWBF2Admin is free software: you can redistribute it and/or modify
 * it under the terms of the GNU General Public License as published by
 * the Free Software Foundation, either version 3 of the License, or
 * (at your option) any later version.

 * SWBF2Admin is distributed in the hope that it will be useful,
 * but WITHOUT ANY WARRANTY; without even the implied warranty of
 * MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.See the
 * GNU General Public License for more details.

 * You should have received a copy of the GNU General Public License
 * along with SWBF2Admin. If not, see<http://www.gnu.org/licenses/>.
 */
using System;
using System.IO;
using System.Diagnostics;
using System.Text.RegularExpressions;

using SWBF2Admin.Utility;
using SWBF2Admin.Structures;
using SWBF2Admin.Config;

namespace SWBF2Admin.Gameserver
{
    public enum ServerStatus
    {
        Online = 0,
        Offline = 1,
        Starting = 2,
        Stopping = 3,
        SteamPending = 4,
        ProtonPending = 5,
    }

    public class ServerManager : ComponentBase
    {
        private const string DLLLOADER_FILENAME_32 = "dllloader_32.exe";
        private const string DLLLOADER_FILENAME_64 = "dllloader_64.exe";
        private const int STEAMMODE_PDECT_TIMEOUT = 1000;
        private const int STEAMMODE_MAX_RETRY = 30;

        public event EventHandler ServerCrashed;
        public event EventHandler ServerStarted;
        public event EventHandler ServerStopped;
        public event EventHandler SteamServerStarting;

        public string ServerExecutable { get; set; } = "BattlefrontII.exe";
        public string ServerProcessName { get; set; } = "BattlefrontII";
        public string ServerPath { get; set; } = "./server";
        public string ServerArgs { get; set; } = "/win /norender /nosound /nointro /autonet dedicated /resolution 640 480";

        private Process serverProcess = null;
        private ServerStatus status = ServerStatus.Offline;
        public ServerStatus Status { get { return status; } }
        private ServerStopReason stopReason = ServerStopReason.STOP_EXIT;
        public ServerSettings Settings { get; set; }
        public virtual Process ServerProcess { get { return serverProcess; } }
        private string ProcessArgs;

        private int steamLaunchRetryCount = 0;
        private GameserverType serverType;
        private HostingType hostingType;

        public ServerManager(AdminCore core) : base(core) { }

        public override void Configure(CoreConfiguration config)
        {
            ServerPath = Core.Files.ParseFileName(config.ServerPath);
            serverType = config.ServerType;
            hostingType = config.HostingType;
            
            if (serverType == GameserverType.Steam)
            {
                ServerExecutable = ServerPath + "/BattlefrontII.exe";
                ServerArgs = string.Empty;
            }
            else if (serverType == GameserverType.Aspyr)
            {
                ServerExecutable = config.ServerPath + "/Battlefront.exe";
                ServerArgs = config.ServerArgs;
                var appid_txt = Path.GetFullPath(ServerPath + "/steam_appid.txt");
                if (!File.Exists(appid_txt))
                {
                    Core.Files.WriteFileText(appid_txt, "2446550");
                }
                ServerProcessName = "Battlefront";
            }
            else
            {
                ServerExecutable = ServerPath + "/BattlefrontII.exe";
                ServerArgs = config.ServerArgs;
            }

            if (hostingType == HostingType.LinuxProton)
            {
                ServerProcessName += ".exe";
            }

            UpdateInterval = STEAMMODE_PDECT_TIMEOUT; //updates for detecting steam startup
        }

        public override void OnInit()
        {
            Attach(false);
            Settings = ServerSettings.FromSettingsFile(Core, ServerPath);
        }

        protected override void OnUpdate()
        {
            if (Status == ServerStatus.SteamPending || Status == ServerStatus.ProtonPending)
            {
                if (Attach(true))
                {
                    DisableUpdates();
                    steamLaunchRetryCount = 0;
                }
                else if (++steamLaunchRetryCount > STEAMMODE_MAX_RETRY)
                {
                    Logger.Log(LogLevel.Error, "Server didn't start after {0} retries. Assuming it has crashed.", steamLaunchRetryCount.ToString());
                    status = ServerStatus.Offline;
                    DisableUpdates();
                }
            }
        }

        private Process FindProcess(string name)
        {
            if (hostingType == HostingType.LinuxProton)
            {
                ProcessStartInfo startInfo = new ProcessStartInfo("/bin/sh")
                {
                    ArgumentList = {
                        "-c",
                        string.Format("pgrep -f 'Z:{0}'", Path.GetFullPath(ServerExecutable).Replace("/", "\\\\"))
                    },
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                };

                Process process = Process.Start(startInfo);
                process.WaitForExit(1000);
                string output = process.StandardOutput.ReadToEnd();
                process.Close();

                if (output != "")
                {
                    try
                    {
                        int pid = Int32.Parse(output);
                        Process p = Process.GetProcessById(pid);
                        return p;
                    }
                    catch (FormatException)
                    {
                        Logger.Log(LogLevel.Error, "Unable to parse process id '{0}'", output);
                    }
                }
                return null;
            }
            foreach (Process p in Process.GetProcessesByName(name))
            {
                try
                {
                    //NOTE: as there's no easy way to detect steam startup, we assume we're already in running mode when re-attaching
                    if (Path.GetFullPath(p.MainModule.FileName).Equals(Path.GetFullPath(ServerPath + $"\\{name}.exe")))
                    {
                        Logger.Log(LogLevel.Info, "Found running server process '{0}' ({1}), re-attaching...", p.MainWindowTitle, p.Id.ToString());
                        return p;
                    }
                }
                catch (Exception e)
                {
                    Logger.Log(LogLevel.Warning, "Can't access BattlefrontII process #{0} ({1})", p.Id.ToString(), e.Message);
                }
            }
            return null;
        }

        private string FindProcessIdInsideWine()
        {
            ProcessStartInfo startInfo = new ProcessStartInfo("proton")
            {
                ArgumentList = {
                    "runinprefix",
                    "tasklist",
                    "/fi",
                    $"IMAGENAME eq {ServerProcessName}",
                    "/fo",
                    "list",
                },
                UseShellExecute = false,
                RedirectStandardOutput = true,
            };

            Process process = Process.Start(startInfo);
            process.WaitForExit(5000);
            string stdOut = process.StandardOutput.ReadToEnd();
            process.Close();

            Match match = Regex.Match(stdOut, @"PID:\s*([0-9]+)");
            if (match.Success)
            {
                return match.Groups[1].Value;
            }
            else
            {
                Logger.Log(LogLevel.Error, "Can't find process id inside wine: {0}", stdOut);
                return null;
            }
        }

        private bool Attach(bool starting)
        {
            var process = FindProcess(ServerProcessName);
            if (process != null)
            {
                if (hostingType == HostingType.LinuxProton && serverProcess != null && !serverProcess.HasExited)
                {
                    serverProcess.EnableRaisingEvents = false;
                }

                serverProcess = process;
                serverProcess.EnableRaisingEvents = true;
                serverProcess.Exited += new EventHandler(ServerProcess_Exited);
                status = ServerStatus.Online;

                InvokeEvent(ServerStarted, this, new StartEventArgs(!starting));
                if (starting) InjectRconDllIfRequired();
                if (Core.Config.EnableHighPriority)
                {
                    serverProcess.PriorityClass = ProcessPriorityClass.High;
                }
                return true;
            }
            return false;
        }

        public void Start()
        {
            if (serverProcess == null)
            {
                ProcessArgs = ServerArgs;
                if (serverType == GameserverType.Aspyr)
                {
                    ProcessArgs += " /bf2";
                    //ProcessArgs += " /netregion \"" + Core.Server.Settings.NetRegion + "\"";
                    if (!string.IsNullOrEmpty(Core.Server.Settings.Password))
                    {
                        ProcessArgs += " /password \"" + Core.Server.Settings.Password + "\"";
                    }
                }

                Logger.Log(LogLevel.Info, "Launching server with args '{0}'", ProcessArgs);
                status = ServerStatus.Starting;

                Environment.SetEnvironmentVariable("SPAWN_TIMER", Core.Server.Settings.AutoAnnouncePeriod.ToString());

                ProcessStartInfo startInfo = new ProcessStartInfo(Core.Files.ParseFileName(ServerExecutable), ProcessArgs)
                {
                    WorkingDirectory = Core.Files.ParseFileName(ServerPath)
                };

                if (hostingType == HostingType.LinuxProton)
                {
                    ProcessArgs = "run " + Core.Files.ParseFileName(ServerExecutable) + " " + ProcessArgs;
                    startInfo = new ProcessStartInfo(Core.Files.ParseFileName("proton"), ProcessArgs)
                    {
                        WorkingDirectory = Core.Files.ParseFileName(ServerPath)
                    };

                    steamLaunchRetryCount = 0;
                    serverProcess = Process.Start(startInfo);
                    serverProcess.EnableRaisingEvents = true;
                    serverProcess.Exited += new EventHandler(ServerProcess_Exited);
                    status = ServerStatus.ProtonPending;
                    EnableUpdates();
                }
                //if we're in steam mode, steam will start a launcher exe prior to the actual game
                else if (serverType == GameserverType.Steam)
                {
                    InvokeEvent(SteamServerStarting, this, new EventArgs());
                    steamLaunchRetryCount = 0;
                    Core.Scheduler.PushDelayedTask(() =>
                    {
                        serverProcess = Process.Start(startInfo);
                        serverProcess.EnableRaisingEvents = true;
                        serverProcess.Exited += new EventHandler(ServerProcess_Exited);
                        if (Core.Config.EnableHighPriority)
                        {
                            serverProcess.PriorityClass = ProcessPriorityClass.High;
                        }
                        //Start game minimized because of mouse locking on Aspyr version
                        serverProcess.StartInfo.WindowStyle = ProcessWindowStyle.Minimized;
                    }
                    , 5000);
                    status = ServerStatus.SteamPending;
                }
                else
                {
                    serverProcess = Process.Start(startInfo);
                    serverProcess.EnableRaisingEvents = true;
                    serverProcess.Exited += new EventHandler(ServerProcess_Exited);
                    if (Core.Config.EnableHighPriority)
                    {
                        serverProcess.PriorityClass = ProcessPriorityClass.High;
                    }

                    status = ServerStatus.Online;
                    InvokeEvent(ServerStarted, this, new StartEventArgs(false));
                    InjectRconDllIfRequired();
                }
            }
        }

        public void Stop(ServerStopReason reason = ServerStopReason.STOP_EXIT)
        {
            if (serverProcess != null)
            {
                Logger.Log(LogLevel.Info, "Stopping Server...");
                status = ServerStatus.Stopping;
                stopReason = reason;

                if (Core.Config.EnableRuntime)
                {
                    Logger.Log(LogLevel.Verbose, "Asking server to stop");
                    Core.Scheduler.PushTask(() => { Core.Rcon.SendCommand("shutdown"); });
                    Core.Scheduler.PushDelayedTask(() => KillServer(), 1000);
                }
                else KillServer();
            }
        }

        public void Restart()
        {
            Stop(ServerStopReason.STOP_RESTART);
        }

        private void KillServer()
        {
            if (!serverProcess.HasExited)
            {
                Logger.Log(LogLevel.Verbose, "Stopping process...");
                serverProcess.Kill();
                serverProcess = null;
            }
        }

        private void ServerProcess_Exited(object sender, EventArgs e)
        {
            Process p = serverProcess;
            serverProcess = null;

            if (status != ServerStatus.Stopping && status != ServerStatus.SteamPending)
            {
                Logger.Log(LogLevel.Warning, "Server has crashed.");
                status = ServerStatus.Offline;
                InvokeEvent(ServerCrashed, this, new EventArgs());
            }
            else if (status == ServerStatus.SteamPending)
            {
                Logger.Log(LogLevel.Info, "Steam Launcher closed. Trying to attach to the server process.");
                EnableUpdates();
            }
            else
            {
                Logger.Log(LogLevel.Info, "Server stopped.");
                status = ServerStatus.Offline;
                InvokeEvent(ServerStopped, this, new StopEventArgs(stopReason));
            }
        }

        private void InjectRconDllIfRequired()
        {
            if (serverType == GameserverType.GoG || serverType == GameserverType.Steam)
            {
                string loader;
                string dll;
                if (serverType == GameserverType.Aspyr)
                {
                    loader = $"{Core.Files.ParseFileName(Core.Config.ServerPath)}/{DLLLOADER_FILENAME_64}";
                    dll = "rconserver_64.dll";
                }
                else
                {
                    loader = $"{Core.Files.ParseFileName(Core.Config.ServerPath)}/{DLLLOADER_FILENAME_32}";
                    dll = "rconserver_32.dll";
                }

                if (File.Exists(loader))
                {
                    if (hostingType == HostingType.LinuxProton)
                    {
                        string winePid = FindProcessIdInsideWine();
                        if (winePid != null)
                        {
                            ProcessStartInfo startInfo = new ProcessStartInfo("proton")
                            {
                                ArgumentList = {
                                    "runinprefix",
                                    loader,
                                    "--pid",
                                    winePid,
                                    "--dll",
                                    string.Format("Z:{0}\\\\{1}", Path.GetFullPath(ServerPath).Replace("/", "\\\\"), dll),
                                },
                            };

                            Process process = Process.Start(startInfo);
                            process.WaitForExit(5000);
                        }
                    }
                    else
                    {
                        Process.Start(loader, string.Format("--pid {0} --dll {1}", serverProcess.Id, dll));
                    }
                }
                else
                {
                    Logger.Log(LogLevel.Error, "Can't find {0}", loader);
                }
            }
        }
    }
}