﻿using System;
using System.IO;
using System.ServiceProcess;
using System.Diagnostics;
using System.Collections.Generic;
using System.Web.Script.Serialization;
using System.Collections;
using System.Threading;
using Toolkit;
using System.Linq;

namespace CustomWatchdog
{
    public partial class CustomBatchWatchdog : ServiceBase
    {
        private readonly ManualResetEventSlim m_stopEvt = new ManualResetEventSlim(false);
        // defaults (can be overriden from a config file)
        private int healthCheckInterval = 10000;
        private uint recoveryExecutionTimeout = 60000 * 5;
        private int criticalCounts = 10;
        private bool noConsoleForRecoveryScript = false;
        private List<RecoveryItem> recoveryItems = new List<RecoveryItem>();


        private string configFileName = "cbwatchdog.json";
        private string eventLogSource = "Custom Batch Watchdog";

        // Windows event log handling
        private void InitEventLog()
        {
            if (!EventLog.SourceExists(eventLogSource))
                EventLog.CreateEventSource(eventLogSource, "Application");
        }
        private void PrintWarning(string evt)
        { EventLog.WriteEntry(eventLogSource, evt, EventLogEntryType.Warning, 0x01); }
        private void PrintError(string evt)
        { EventLog.WriteEntry(eventLogSource, evt, EventLogEntryType.Error, 0x02); }
        private void PrintInfo(string evt)
        { EventLog.WriteEntry(eventLogSource, evt, EventLogEntryType.Information, 0x03); }

        private void LoadConfigFromFile()
        {
            string cfgPath = null;
            try
            {
                JavaScriptSerializer ser = new JavaScriptSerializer();
                cfgPath = GetConfigFile();
                PrintInfo("Reading Configuration File :" + cfgPath);

                var dict = ser.Deserialize<Dictionary<string, object>>(File.ReadAllText(cfgPath));

                if (dict.ContainsKey("healthCheckInterval"))
                {
                    healthCheckInterval = int.Parse((string)dict["healthCheckInterval"]);
                }
                if (dict.ContainsKey("recoveryExecutionTimeout"))
                {
                    recoveryExecutionTimeout = uint.Parse((string)dict["recoveryExecutionTimeout"]);
                }
                if (dict.ContainsKey("criticalCounts"))
                {
                    criticalCounts = int.Parse((string)dict["criticalCounts"]);
                }
                if (dict.ContainsKey("noConsoleForRecoveryScript"))
                {
                    noConsoleForRecoveryScript = bool.Parse((string)dict["noConsoleForRecoveryScript"]);
                }

                if (dict.ContainsKey("recoveryItems"))
                {
                    ArrayList recoveryItemDictList = (ArrayList)dict["recoveryItems"];
                    foreach (Dictionary<string, object> recoveryItemDict in recoveryItemDictList)
                    {
                        RecoveryItem recoveryItem = new RecoveryItem();

                        if (recoveryItemDict.ContainsKey("recoveryBatch"))
                        {
                            recoveryItem.RecoveryBatch = (string)recoveryItemDict["recoveryBatch"];
                        }

                        if (recoveryItemDict.ContainsKey("overrideRecoveryExecutionTimeout"))
                        {
                            recoveryItem.OverrideRecoveryExecutionTimeout = uint.Parse((string)recoveryItemDict["overrideRecoveryExecutionTimeout"]);
                        }

                        if (recoveryItemDict.ContainsKey("starcounterBinDirectory"))
                        {
                            recoveryItem.StarcounterBinDirectory = (string)recoveryItemDict["starcounterBinDirectory"];
                        }

                        if (recoveryItemDict.ContainsKey("scDatabase"))
                        {
                            recoveryItem.ScDatabase = (string)recoveryItemDict["scDatabase"];
                        }

                        if (recoveryItemDict.ContainsKey("processes"))
                        {
                            ArrayList procsList = (ArrayList)recoveryItemDict["processes"];
                            foreach (var proc in procsList)
                            {
                                recoveryItem.Processes.Add((string)proc);
                            }
                        }
                        if (recoveryItemDict.ContainsKey("scAppNames"))
                        {
                            ArrayList appNameList = (ArrayList)recoveryItemDict["scAppNames"];
                            foreach (var appName in appNameList)
                            {
                                recoveryItem.ScAppNames.Add((string)appName);
                            }
                        }

                        this.recoveryItems.Add(recoveryItem);
                    }
                }

                string recoveryItemsInfo = string.Join("", recoveryItems);

                PrintInfo("Watchdog will be started with:\n" +
                   "    healthCheckInterval : " + healthCheckInterval.ToString() + "\n" +
                   "    recoveryExecutionTimeout : " + recoveryExecutionTimeout.ToString() + "\n" +
                   "    noConsoleForRecoveryScript : " + noConsoleForRecoveryScript.ToString() + "\n" +
                   "    criticalCounts : " + criticalCounts.ToString() + "\n" +
                   recoveryItemsInfo
                   );
            }
            catch (IOException e)
            {
                var name = cfgPath ?? configFileName;
                throw new Exception("Invalid format on: " + name, e);
            }
        }

        /// <summary>
        /// Looks for a config file in the same directory as the assembly
        /// </summary>
        /// <returns></returns>
        private string GetConfigFile()
        {
            // Check if the path is rooted
            if (Path.IsPathRooted(configFileName))
            {
                // A full path has been provided in some way, probably command line args
                return configFileName;
            }
            else
            {
                var file = new Uri(GetType().Assembly.Location).LocalPath;
                var dir = Path.GetDirectoryName(file);
                return Path.Combine(dir, configFileName);
            }
            
        }

        private void Recover(RecoveryItem rc)
        {
            ApplicationLoader.PROCESS_INFORMATION procInfo;
            if (rc.OverrideRecoveryExecutionTimeout != 0)
            {
                recoveryExecutionTimeout = rc.OverrideRecoveryExecutionTimeout;
            }
            ApplicationLoader.StartProcessAndBypassUAC(rc.RecoveryBatch, noConsoleForRecoveryScript, recoveryExecutionTimeout, PrintInfo, out procInfo);
        }

        private bool Check(RecoveryItem rc)
        {
            Process[] processlist = Process.GetProcesses();
            foreach (string procName in rc.Processes)
            {
                bool found = false;
                foreach (Process theprocess in processlist)
                {
                    if (theprocess.ProcessName.Equals(procName))
                    {
                        found = true;
                        break;
                    }
                }
                if (!found)
                {
                    PrintWarning("Watchdog couldn't find the process " + procName + ".");
                    return false;
                }
            }

            return CheckStarcounterApps(rc);
        }

        private bool CheckStarcounterApps(RecoveryItem rc)
        {
            var scFileName = "staradmin.exe";
            System.Diagnostics.Process process = new System.Diagnostics.Process();
            System.Diagnostics.ProcessStartInfo startInfo = new System.Diagnostics.ProcessStartInfo();
            startInfo.FileName = string.IsNullOrEmpty(rc.StarcounterBinDirectory) ? scFileName : Path.Combine(rc.StarcounterBinDirectory, scFileName);
            startInfo.Arguments = $"--database={rc.ScDatabase} list app";
            startInfo.UseShellExecute = false;
            startInfo.RedirectStandardOutput = true;
            startInfo.CreateNoWindow = true;
            process.StartInfo = startInfo;
            process.Start();

            string stdOutput = process.StandardOutput.ReadToEnd();

            bool allAppsAreRunning = rc.ScAppNames.All(appName => stdOutput.Contains($"{appName} (in {rc.ScDatabase})"));

            return allAppsAreRunning;
        }

        private void RunForever()
        {
            while (!m_stopEvt.IsSet)
            {
                foreach (RecoveryItem rc in recoveryItems)
                {
                    bool check = Check(rc);
                    int cntr = 0;

                    if (check == false)
                    {
                        do
                        {
                            cntr++;

                            if (cntr == criticalCounts)
                            {
                                // maximum number of recovery attemps has been succeeded, abort
                                PrintInfo($"{(criticalCounts - 1).ToString()} recovery attemps for {rc.RecoveryBatch} file has been made, aborting further attemps and moving on with next revoceryItem");
                                break;
                            }
                            else
                            {
                                // execute recovery
                                PrintInfo("Watchdog's recovery attempt #" + (cntr).ToString() + " procedure started: " + rc.RecoveryBatch);
                                Recover(rc);
                            }

                            check = Check(rc);
                            if (check == true)
                            {
                                PrintInfo("Watchdog's recovery attempt #" + (cntr).ToString() + " SUCCESS: " + rc.RecoveryBatch);
                            }
                            else
                            {
                                PrintInfo("Watchdog's recovery attempt #" + (cntr).ToString() + " FAILED: " + rc.RecoveryBatch);
                            }
                        } while (check == false);
                    }
                }
                m_stopEvt.Wait(healthCheckInterval);
                //Thread.Sleep(healthCheckInterval);
            }
        }
        protected override void OnStart(string[] args)
        {
            //System.Diagnostics.Debugger.Launch();
            PrintInfo("Custom batch watchdog has been started.");

            if (args.Length > 0)
            {
                OverrideSettings(args);
            }

            LoadConfigFromFile();
            ThreadPool.QueueUserWorkItem(o => { RunForever(); });
        }

        private void OverrideSettings(string[] args)
        {
            if (args.Length > 0)
            {
                configFileName = args[0];
                if (configFileName.IndexOf('/') > -1)
                {
                    configFileName = configFileName.Replace("/", string.Empty);
                }

                PrintInfo("Config file updated to: " + configFileName);
            }
            if (args.Length > 1)
            {
                eventLogSource = args[1];
                if (eventLogSource.IndexOf('/') > -1)
                {
                    eventLogSource = eventLogSource.Replace("/", string.Empty);
                }
                PrintInfo("Event Source Name updated to: " + eventLogSource);
            }
        }

        public CustomBatchWatchdog() { InitializeComponent(); }

        protected override void OnStop()
        {
            PrintInfo("Custom batch watchdog has been signalled to stop.");
            m_stopEvt.Set();
        }
    }
}
