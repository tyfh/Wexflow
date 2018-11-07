﻿using Quartz;
using Quartz.Impl;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Xml.Linq;
using System.Xml.XPath;
using Wexflow.Core.Db;

namespace Wexflow.Core
{
    /// <summary>
    /// Wexflow engine.
    /// </summary>
    public class WexflowEngine
    {
        /// <summary>
        /// Settings file path.
        /// </summary>
        public string SettingsFile { get; private set; }
        /// <summary>
        /// Workflows folder path.
        /// </summary>
        public string WorkflowsFolder { get; private set; }
        /// <summary>
        /// Trash folder path.
        /// </summary>
        public string TrashFolder { get; private set; }
        /// <summary>
        /// Temp folder path.
        /// </summary>
        public string TempFolder { get; private set; }
        /// <summary>
        /// XSD path.
        /// </summary>
        public string XsdPath { get; private set; }
        /// <summary>
        /// Tasks names file path.
        /// </summary>
        public string TasksNamesFile { get; private set; }
        /// <summary>
        /// Tasks settings file path.
        /// </summary>
        public string TasksSettingsFile { get; private set; }
        /// <summary>
        /// List of the Workflows loaded by Wexflow engine.
        /// </summary>
        public IList<Workflow> Workflows { get; private set; }
        /// <summary>
        /// Database connection string.
        /// </summary>
        public string ConnectionString { get; private set; }
        /// <summary>
        /// Database
        /// </summary>
        public Db.Db Database { get; private set; }

        //
        // Quartz scheduler
        //
        private static readonly ISchedulerFactory SchedulerFactory = new StdSchedulerFactory();
        private static readonly IScheduler Quartzcheduler = SchedulerFactory.GetScheduler();

        /// <summary>
        /// Creates a new instance of Wexflow engine.
        /// </summary>
        /// <param name="settingsFile">Settings file path.</param>
        public WexflowEngine(string settingsFile)
        {
            SettingsFile = settingsFile;
            Workflows = new List<Workflow>();

            Logger.Info("");
            Logger.Info("Starting Wexflow Engine");

            LoadSettings();

            Database = new Db.Db(ConnectionString);
            Database.Init();

            LoadWorkflows(); 
        }

        /// <summary>
        /// Checks whether a cron expression is valid or not.
        /// </summary>
        /// <param name="expression">Cron expression</param>
        /// <returns></returns>
        public static bool IsCronExpressionValid(string expression)
        {
            bool res = CronExpression.IsValidExpression(expression);
            return res;
        }

        void LoadSettings()
        {
            var xdoc = XDocument.Load(SettingsFile);
            WorkflowsFolder = GetWexflowSetting(xdoc, "workflowsFolder");
            TrashFolder = GetWexflowSetting(xdoc, "trashFolder");
            TempFolder = GetWexflowSetting(xdoc, "tempFolder");
            if (!Directory.Exists(TempFolder)) Directory.CreateDirectory(TempFolder);
            XsdPath = GetWexflowSetting(xdoc, "xsd");
            TasksNamesFile = GetWexflowSetting(xdoc, "tasksNamesFile");
            TasksSettingsFile = GetWexflowSetting(xdoc, "tasksSettingsFile");
            ConnectionString = GetWexflowSetting(xdoc, "connectionString");
        }

        string GetWexflowSetting(XDocument xdoc, string name)
        {
            try
            {
                var xValue = xdoc.XPathSelectElement(string.Format("/Wexflow/Setting[@name='{0}']", name)).Attribute("value");
                if (xValue == null) throw new Exception("Wexflow Setting Value attribute not found.");
                return xValue.Value;
            }
            catch (Exception e)
            {
                Logger.ErrorFormat("An error occured when reading Wexflow settings: Setting[@name='{0}']", e, name);
                return string.Empty;
            }
        }

        void LoadWorkflows()
        {
            foreach (string file in Directory.GetFiles(WorkflowsFolder))
            {
                var workflow = LoadWorkflowFromFile(file);
                if (workflow != null)
                {
                    Workflows.Add(workflow);
                }
            }

            var watcher = new FileSystemWatcher(WorkflowsFolder, "*.xml")
            {
                EnableRaisingEvents = true,
                IncludeSubdirectories = false
            };

            watcher.Created += (_, args) =>
            {
                var workflow = LoadWorkflowFromFile(args.FullPath);
                if (workflow != null)
                {
                    Workflows.Add(workflow);
                    ScheduleWorkflow(workflow);
                }
            };

            watcher.Deleted += (_, args) =>
            {
                var removedWorkflow = Workflows.SingleOrDefault(wf => wf.WorkflowFilePath == args.FullPath);
                if (removedWorkflow != null)
                {
                    Logger.InfoFormat("Workflow {0} is stopped and removed because its definition file {1} was deleted.",
                        removedWorkflow.Name, removedWorkflow.WorkflowFilePath);
                    removedWorkflow.Stop();
                    
                    StopCronJobs(removedWorkflow.Id);
                    Workflows.Remove(removedWorkflow);
                }
            };

            watcher.Changed += (_, args) =>
            {
                try
                {
                    if (Workflows != null)
                    {
                        var changedWorkflow = Workflows.SingleOrDefault(wf => wf.WorkflowFilePath == args.FullPath);

                        if (changedWorkflow != null)
                        {
                            // the existing file might have caused an error during loading, so there may be no corresponding
                            // workflow to the changed file
                            changedWorkflow.Stop();
                            
                            StopCronJobs(changedWorkflow.Id);
                            Workflows.Remove(changedWorkflow);
                            Logger.InfoFormat("A change in the definition file {0} of workflow {1} has been detected. The workflow will be reloaded.", changedWorkflow.WorkflowFilePath, changedWorkflow.Name);
                        }
                    }
                }
                catch (Exception e)
                {
                    Logger.Error("Error during workflow reload", e);
                }

                var reloaded = LoadWorkflowFromFile(args.FullPath);
                if (reloaded != null)
                {
                    var duplicateId = Workflows.SingleOrDefault(wf => wf.Id == reloaded.Id);
                    if (duplicateId != null)
                    {
                        Logger.ErrorFormat(
                            "An error occured while loading the workflow : {0}. The workflow Id {1} is already assgined in {2}",
                            args.FullPath, reloaded.Id, duplicateId.WorkflowFilePath);
                    }
                    else
                    {
                        Workflows.Add(reloaded);
                        ScheduleWorkflow(reloaded);
                    }
                }
            };
        }

        private void StopCronJobs(int workflowId)
        {
            string jobIdentity = "Workflow Job " + workflowId;
            var jobKey = new JobKey(jobIdentity);
            if (Quartzcheduler.CheckExists(jobKey))
            {
                Quartzcheduler.DeleteJob(jobKey);
            }
        }

        Workflow LoadWorkflowFromFile(string file)
        {
            try
            {
                var wf = new Workflow(file, TempFolder, XsdPath, Database);
                Logger.InfoFormat("Workflow loaded: {0} ({1})", wf, file);
                return wf;
            }
            catch (Exception e)
            {
                Logger.ErrorFormat("An error occured while loading the workflow : {0} Please check the workflow configuration. Error: {1}", file, e.Message);
                return null;
            }
        }

        /// <summary>
        /// Starts Wexflow engine.
        /// </summary>
        public void Run()
        {
            foreach (Workflow workflow in Workflows)
            {
                ScheduleWorkflow(workflow);
            }

            if (!Quartzcheduler.IsStarted)
            {
                Quartzcheduler.Start();
            }
        }

        private void ScheduleWorkflow(Workflow wf)
        {
            if (wf.IsEnabled)
            {
                if (wf.LaunchType == LaunchType.Startup)
                {
                    wf.Start();
                }
                else if (wf.LaunchType == LaunchType.Periodic)
                {
                    IDictionary<string, object> map = new Dictionary<string, object>();
                    map.Add("workflow", wf);

                    string jobIdentity = "Workflow Job " + wf.Id;
                    IJobDetail jobDetail = JobBuilder.Create<WorkflowJob>()
                        .WithIdentity(jobIdentity)
                        .SetJobData(new JobDataMap(map))
                        .Build();

                    ITrigger trigger = TriggerBuilder.Create()
                        .ForJob(jobDetail)
                        .WithSimpleSchedule( x => x.WithInterval(wf.Period).RepeatForever())
                        .WithIdentity("Workflow Trigger " + wf.Id)
                        .StartNow()
                        .Build();

                    var jobKey = new JobKey(jobIdentity);
                    if (Quartzcheduler.CheckExists(jobKey))
                    {
                        Quartzcheduler.DeleteJob(jobKey);
                    }

                    Quartzcheduler.ScheduleJob(jobDetail, trigger);

                }
                else if (wf.LaunchType == LaunchType.Cron)
                {
                    IDictionary<string, object> map = new Dictionary<string, object>();
                    map.Add("workflow", wf);

                    string jobIdentity = "Workflow Job " + wf.Id;
                    IJobDetail jobDetail = JobBuilder.Create<WorkflowJob>()
                        .WithIdentity(jobIdentity)
                        .SetJobData(new JobDataMap(map))
                        .Build();

                    ITrigger trigger = TriggerBuilder.Create()
                        .ForJob(jobDetail)
                        .WithCronSchedule(wf.CronExpression)
                        .WithIdentity("Workflow Trigger " + wf.Id)
                        .StartNow()
                        .Build();

                    var jobKey = new JobKey(jobIdentity);
                    if (Quartzcheduler.CheckExists(jobKey))
                    {
                        Quartzcheduler.DeleteJob(jobKey);
                    }

                    Quartzcheduler.ScheduleJob(jobDetail, trigger);
                }
            }
        }

        /// <summary>
        /// Stops Wexflow engine.
        /// </summary>
        /// <param name="stopQuartzScheduler">Tells if Quartz scheduler should be stopped or not.</param>
        /// <param name="clearStatusCountAndEntries">Indicates whether to clear statusCount and entries.</param>
        public void Stop(bool stopQuartzScheduler, bool clearStatusCountAndEntries)
        {
            if (stopQuartzScheduler)
            {
                Quartzcheduler.Shutdown();
            }

            foreach (var wf in Workflows)
            {
                if (wf.IsRunning)
                {
                    wf.Stop();
                }
            }

            if (clearStatusCountAndEntries)
            {
                Database.ClearStatusCount();
                Database.ClearEntries();
            }
        }

        /// <summary>
        /// Gets a workflow.
        /// </summary>
        /// <param name="workflowId">Workflow Id.</param>
        /// <returns></returns>
        public Workflow GetWorkflow(int workflowId)
        {
            return Workflows.FirstOrDefault(wf => wf.Id == workflowId);
        }

        /// <summary>
        /// Starts a workflow.
        /// </summary>
        /// <param name="workflowId">Workflow Id.</param>
        public void StartWorkflow(int workflowId)
        {
            var wf = GetWorkflow(workflowId);

            if (wf == null)
            {
                Logger.ErrorFormat("Workflow {0} not found.", workflowId);
            }
            else
            {
                if (wf.IsEnabled) wf.Start();
            }
        }

        /// <summary>
        /// Stops a workflow.
        /// </summary>
        /// <param name="workflowId">Workflow Id.</param>
        public void StopWorkflow(int workflowId)
        {
            var wf = GetWorkflow(workflowId);

            if (wf == null)
            {
                Logger.ErrorFormat("Workflow {0} not found.", workflowId);
            }
            else
            {
                if (wf.IsEnabled) wf.Stop();
            }
        }

        /// <summary>
        /// Suspends a workflow.
        /// </summary>
        /// <param name="workflowId">Workflow Id.</param>
        public void SuspendWorkflow(int workflowId)
        {
            var wf = GetWorkflow(workflowId);

            if (wf == null)
            {
                Logger.ErrorFormat("Workflow {0} not found.", workflowId);
            }
            else
            {
                if (wf.IsEnabled) wf.Suspend();
            }
        }

        /// <summary>
        /// Resumes a workflow.
        /// </summary>
        /// <param name="workflowId">Workflow Id.</param>
        public void ResumeWorkflow(int workflowId)
        {
            var wf = GetWorkflow(workflowId);

            if (wf == null)
            {
                Logger.ErrorFormat("Workflow {0} not found.", workflowId);
            }
            else
            {
                if (wf.IsEnabled) wf.Resume();
            }
        }

        /// <summary>
        /// Returns status count
        /// </summary>
        /// <returns>Returns status count</returns>
        public StatusCount GetStatusCount()
        {
            return Database.GetStatusCount();
        }

        /// <summary>
        /// Returns all the entries
        /// </summary>
        /// <returns>Returns all the entries</returns>
        public Entry[] GetEntries()
        {
            return Database.GetEntries().ToArray();
        }

        /// <summary>
        /// Inserts a user.
        /// </summary>
        /// <param name="username">Username.</param>
        /// <param name="password">Password.</param>
        public void InsertUser(string username, string password)
        {
            Database.InsertUser(new User { Username = username, Password = password});
        }

        /// <summary>
        /// Gets a user.
        /// </summary>
        /// <param name="username">Username.</param>
        /// <returns></returns>
        public User GetUser(string username)
        {
            return Database.GetUser(username);
        }

        /// <summary>
        /// Gets a password.
        /// </summary>
        /// <param name="username">Username.</param>
        /// <returns></returns>
        public string GetPassword(string username)
        {
            return Database.GetPassword(username);
        }

        /// <summary>
        /// Returns all the entries.
        /// </summary>
        /// <returns>Returns all the entries</returns>
        public HistoryEntry[] GetHistoryEntries()
        {
            return Database.GetHistoryEntries().ToArray();
        }

        /// <summary>
        /// Returns the entries by a keyword.
        /// </summary>
        /// <param name="keyword">Search keyword.</param>
        /// <returns>Returns all the entries</returns>
        public HistoryEntry[] GetHistoryEntries(string keyword)
        {
            return Database.GetHistoryEntries(keyword).ToArray();
        }

        /// <summary>
        /// Returns the entries by a keyword.
        /// </summary>
        /// <param name="keyword">Search keyword.</param>
        /// <param name="page">Page number.</param>
        /// <param name="entriesCount">Number of entries.</param>
        /// <returns>Returns all the entries</returns>
        public HistoryEntry[] GetHistoryEntries(string keyword, int page, int entriesCount)
        {
            return Database.GetHistoryEntries(keyword, page, entriesCount).ToArray();
        }

        /// <summary>
        /// Returns the entries by a keyword.
        /// </summary>
        /// <param name="keyword">Search keyword.</param>
        /// <param name="from">Date From.</param>
        /// <param name="to">Date To.</param>
        /// <param name="page">Page number.</param>
        /// <param name="entriesCount">Number of entries.</param>
        /// <param name="heo">HistoryEntryOrderBy</param>
        /// <returns>Returns all the entries</returns>
        public HistoryEntry[] GetHistoryEntries(string keyword, DateTime from, DateTime to, int page, int entriesCount, HistoryEntryOrderBy heo)
        {
            var col = Database.GetHistoryEntries(keyword, from, to, page, entriesCount, heo);

            if (!col.Any())
            {
                return new HistoryEntry[] { };
            }
            else
            {
                return col.ToArray();
            }
        }

        /// <summary>
        /// Returns the entries by a keyword.
        /// </summary>
        /// <param name="keyword">Search keyword.</param>
        /// <param name="from">Date From.</param>
        /// <param name="to">Date To.</param>
        /// <param name="page">Page number.</param>
        /// <param name="entriesCount">Number of entries.</param>
        /// <param name="heo">HistoryEntryOrderBy</param>
        /// <returns>Returns all the entries</returns>
        public Entry[] GetEntries(string keyword, DateTime from, DateTime to, int page, int entriesCount, HistoryEntryOrderBy heo)
        {
            var col = Database.GetEntries(keyword, from, to, page, entriesCount, heo);

            if (!col.Any())
            {
                return new Entry[] { };
            }
            else
            {
                return col.ToArray();
            }
        }

        /// <summary>
        /// Gets the number of history entries by search keyword.
        /// </summary>
        /// <param name="keyword">Search keyword.</param>
        /// <returns>The number of history entries by search keyword.</returns>
        public long GetHistoryEntriesCount(string keyword)
        {
            return Database.GetHistoryEntriesCount(keyword);
        }

        /// <summary>
        /// Gets the number of history entries by search keyword and date filter.
        /// </summary>
        /// <param name="keyword">Search keyword.</param>
        /// <param name="from">Date from.</param>
        /// <param name="to">Date to.</param>
        /// <returns></returns>
        public long GetHistoryEntriesCount(string keyword, DateTime from, DateTime to)
        {
            return Database.GetHistoryEntriesCount(keyword, from, to);
        }

        /// <summary>
        /// Gets the number of entries by search keyword and date filter.
        /// </summary>
        /// <param name="keyword">Search keyword.</param>
        /// <param name="from">Date from.</param>
        /// <param name="to">Date to.</param>
        /// <returns></returns>
        public long GetEntriesCount(string keyword, DateTime from, DateTime to)
        {
            return Database.GetEntriesCount(keyword, from, to);
        }

        /// <summary>
        /// Returns Status Date Min value.
        /// </summary>
        /// <returns>Status Date Min value.</returns>
        public DateTime GetHistoryEntryStatusDateMin()
        {
            return Database.GetHistoryEntryStatusDateMin();
        }

        /// <summary>
        /// Returns Status Date Max value.
        /// </summary>
        /// <returns>Status Date Max value.</returns>
        public DateTime GetHistoryEntryStatusDateMax()
        {
            return Database.GetHistoryEntryStatusDateMax();
        }

        /// <summary>
        /// Returns Status Date Min value.
        /// </summary>
        /// <returns>Status Date Min value.</returns>
        public DateTime GetEntryStatusDateMin()
        {
            return Database.GetEntryStatusDateMin();
        }

        /// <summary>
        /// Returns Status Date Max value.
        /// </summary>
        /// <returns>Status Date Max value.</returns>
        public DateTime GetEntryStatusDateMax()
        {
            return Database.GetEntryStatusDateMax();
        }
    }
}
