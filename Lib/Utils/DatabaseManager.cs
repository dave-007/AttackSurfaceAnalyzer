﻿// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License.
using AttackSurfaceAnalyzer.Objects;
using AttackSurfaceAnalyzer.Types;
using LiteDB;
using Serilog;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace AttackSurfaceAnalyzer.Utils
{
    public static class DatabaseManager
    {
        private const string SCHEMA_VERSION = "5";
        private static bool WriterStarted = false;

        public static ConcurrentQueue<WriteObject> WriteQueue { get; private set; } = new ConcurrentQueue<WriteObject>();

        public static bool FirstRun { get; private set; } = true;

        public static LiteDatabase db;

        public static string Filename { get; private set; } = "asa.litedb";

        public static bool Setup(string filename = null)
        {
            if (filename != null)
            {
                if (Filename != filename)
                {

                    if (db != null)
                    {
                        CloseDatabase();
                    }

                    Filename = filename;
                }
            }

            var StopWatch = System.Diagnostics.Stopwatch.StartNew();

            if (System.IO.File.Exists(Filename))
            {
                Log.Debug($"Loading Database {Filename} of size {new FileInfo(Filename).Length}");
            }
            else
            {
                Log.Debug($"Initializing database at {Filename}");
            }
            db = new LiteDatabase($"Filename={Filename};Journal=false;Mode=Exclusive");

            StopWatch.Stop();
            var t = TimeSpan.FromMilliseconds(StopWatch.ElapsedMilliseconds);
            var answer = string.Format(CultureInfo.InvariantCulture, "{0:D2}h:{1:D2}m:{2:D2}s:{3:D3}ms",
                                    t.Hours,
                                    t.Minutes,
                                    t.Seconds,
                                    t.Milliseconds);
            Log.Debug("Completed opening database in {0}", answer);

            var col = db.GetCollection<WriteObject>("WriteObjects");

            col.EnsureIndex(x => x.IdentityHash);
            col.EnsureIndex(x => x.InstanceHash);
            col.EnsureIndex(x => x.ColObj.ResultType);
            col.EnsureIndex(x => x.RunId);

            var cr = db.GetCollection<CompareResult>("CompareResults");

            cr.EnsureIndex(x => x.BaseRunId);
            cr.EnsureIndex(x => x.CompareRunId);
            cr.EnsureIndex(x => x.ResultType);

            var settings = db.GetCollection<Setting>("Settings");

            SetOptOut(false);

            var res = settings.Count(x => x.Name.Equals("SchemaVersion"));

            if (res == 0)
            {
                settings.Insert(new Setting() { Name = "SchemaVersion", Value = SCHEMA_VERSION });
            }

            if (!WriterStarted)
            {
                ((Action)(async () =>
                {
                    await Task.Run(() => KeepSleepAndFlushQueue()).ConfigureAwait(false);
                }))();
                WriterStarted = true;
            }
                
            return true;
        }

        public static List<DataRunModel> GetResultModels(RUN_STATUS status)
        {
            var output = new List<DataRunModel>();
            var comparisons = db.GetCollection<Comparison>("Comparisons");

            var results = comparisons.Find(x => x.Status.Equals(status));

            foreach(var result in results)
            {
                output.Add(new DataRunModel { Key = result.FirstRunId + " vs. " + result.SecondRunId, Text = result.FirstRunId + " vs. " + result.SecondRunId });
            }

            return output;
        }

        public static void TrimToLatest()
        {
            List<string> Runs = new List<string>();

            var runs = db.GetCollection<Run>("Runs");

            var all = runs.FindAll();

            var allButLatest = all.Except(new List<Run>() { all.Last() });

            foreach(var run in allButLatest)
            {
                DeleteRun(run.RunId);
            }
        }

        public static bool HasElements()
        {
            return !WriteQueue.IsEmpty;
        }

        public static void KeepSleepAndFlushQueue()
        {
            while (true)
            {
                SleepAndFlushQueue();
            }
        }
        public static void SleepAndFlushQueue()
        {
            while (!WriteQueue.IsEmpty)
            {
                WriteNext();
            }
            Thread.Sleep(100);
        }

        public static PLATFORM RunIdToPlatform(string runid)
        {
            var col = db.GetCollection<Run>("Runs");

            var results = col.Find(x => x.RunId.Equals(runid));
            if (results.Any())
            {
                return (PLATFORM)Enum.Parse(typeof(PLATFORM), results.First().Platform);
            }
            else
            {
                return PLATFORM.UNKNOWN;
            }
        }

        public static List<WriteObject> GetResultsByRunid(string runid)
        {
            var output = new List<WriteObject>();

            var wo = db.GetCollection<WriteObject>("WriteObjects");

            return wo.Find(x => x.RunId.Equals(runid)).ToList();
        }

        public static void InsertAnalyzed(CompareResult objIn)
        {
            if (objIn != null)
            {
                var cr = db.GetCollection<CompareResult>("CompareResults");

                cr.Insert(objIn);
            }
        }

        public static void VerifySchemaVersion()
        {
            var settings = db.GetCollection<Setting>("Settings");

            if (!(settings.Find(x => x.Name.Equals("SchemaVersion") && x.Value.Equals(SCHEMA_VERSION)).Any()))
            {
                Log.Fatal("Schema version of database is {0} but {1} is required. Use config --reset-database to delete the incompatible database.", settings.FindOne(x => x.Name.Equals("SchemaVersion")).Value, SCHEMA_VERSION);
                Environment.Exit(-1);
            }
        }

        public static List<string> GetLatestRunIds(int numberOfIds, string type)
        {
            var runs = db.GetCollection<Run>("Runs");

            var latest = runs.FindOne(Query.All(Query.Descending));

            return runs.Find(x => x.Id > latest.Id - numberOfIds).Select(x => x.RunId).ToList();
        }

        public static Dictionary<RESULT_TYPE, int> GetResultTypesAndCounts(string runId)
        {
            var outDict = new Dictionary<RESULT_TYPE, int>() { };

            var wo = db.GetCollection<WriteObject>("WriteObjects");

            foreach(RESULT_TYPE resultType in Enum.GetValues(typeof(RESULT_TYPE)))
            {
                var count = wo.Count(x => x.ColObj.ResultType.Equals(resultType));

                if (count > 0)
                {
                    outDict.Add(resultType, count);
                }
            }

            return outDict;
        }

        public static int GetNumResults(RESULT_TYPE ResultType, string runId)
        {
            var wo = db.GetCollection<WriteObject>("WriteObjects");

            return wo.Count(Query.And(Query.EQ("RunId", runId), Query.EQ("ColObj.ResultType", (int)ResultType)));
        }

        public static IEnumerable<FileMonitorEvent> GetSerializedMonitorResults(string runId)
        {
            List<FileMonitorEvent> records = new List<FileMonitorEvent>();

            var fme = db.GetCollection<FileMonitorEvent>("FileMonitorEvents");

            return fme.Find(x => x.RunId.Equals(runId));
        }

        public static void InsertRun(string runId, Dictionary<RESULT_TYPE, bool> dictionary)
        {
            var runs = db.GetCollection<Run>("Runs");

            runs.Insert(new Run() {
                RunId = runId,
                ResultTypes = dictionary,
                Platform = AsaHelpers.GetPlatformString(),
                Timestamp = DateTime.Now.ToString("o", CultureInfo.InvariantCulture),
                Type = (dictionary.ContainsKey(RESULT_TYPE.FILEMONITOR) && dictionary[RESULT_TYPE.FILEMONITOR]) ? RUN_TYPE.MONITOR : RUN_TYPE.COLLECT,
                Version = AsaHelpers.GetVersionString()
            });
        }

        public static Dictionary<RESULT_TYPE, bool> GetResultTypes(string runId)
        {
            var runs = db.GetCollection<Run>("Runs");

            var run = runs.FindOne(x => x.RunId.Equals(runId));

            return run.ResultTypes;
        }

        public static void CloseDatabase()
        {
            db.Dispose();
            db = null;
        }

        public static void Write(CollectObject objIn, string runId)
        {
            if (objIn != null && runId != null)
            {
                WriteQueue.Enqueue(new WriteObject() { ColObj = objIn, RunId = runId });
            }
        }

        public static void InsertCompareRun(string firstRunId, string secondRunId, RUN_STATUS runStatus)
        {
            var crs = db.GetCollection<CompareRun>("CompareRun");

            var cr = new CompareRun() { FirstRunId = firstRunId, SecondRunId = secondRunId, Status = runStatus };

            crs.Insert(cr);
        }

        public static void WriteNext()
        {
            var list = new List<WriteObject>();
            for (int i = 0; i < Math.Min(1000,WriteQueue.Count); i++)
            {
                WriteObject ColObj;
                WriteQueue.TryDequeue(out ColObj);
                list.Add(ColObj);
            }

            var col = db.GetCollection<WriteObject>("WriteObjects");
            col.InsertBulk(list);
        }

        public static IEnumerable<WriteObject> GetMissingFromFirst(string firstRunId, string secondRunId)
        {
            var col = db.GetCollection<WriteObject>("WriteObjects");

            var firstRun = col.Find(x => x.RunId.Equals(firstRunId));
            var firstRunIdentities = firstRun.Select(x => x.IdentityHash).ToHashSet();
            var res = col.Find(x => x.RunId.Equals(secondRunId) && !firstRunIdentities.Contains(x.IdentityHash));
            if (res == null)
            {
                return new List<WriteObject>();
            }
            else
            {
                return res;
            }
        }

        public static IEnumerable<Tuple<WriteObject,WriteObject>> GetModified(string firstRunId, string secondRunId)
        {
            var col = db.GetCollection<WriteObject>("WriteObjects");

            var firstRun = col.Find(x => x.RunId.Equals(firstRunId));
            var firstRunIdentities = firstRun.Select(x => x.IdentityHash).ToHashSet();
            var firstRunHashes = firstRun.Select(x => x.InstanceHash).ToHashSet();
            var secondRun = col.Find(x => x.RunId.Equals(secondRunId) && firstRunIdentities.Contains(x.IdentityHash) && !firstRunHashes.Contains(x.InstanceHash));
            return secondRun.Select(x => new Tuple<WriteObject,WriteObject>(x, firstRun.First(x => x.IdentityHash.Equals(x.IdentityHash))));
        }

        public static void UpdateCompareRun(string firstRunId, string secondRunId, RUN_STATUS runStatus)
        {
            var crs = db.GetCollection<CompareRun>("CompareRun");

            var cr = crs.FindOne(x => x.FirstRunId.Equals(firstRunId) && x.SecondRunId.Equals(secondRunId));
            cr.Status = runStatus;
            crs.Update(cr);
        }

        public static void DeleteRun(string runId)
        {
            var Runs = db.GetCollection<Run>("Runs");

            Runs.Delete(x => x.RunId.Equals(runId));

            var Results = db.GetCollection<WriteObject>("WriteObjects");

            Results.Delete(x => x.RunId.Equals(runId));
        }

        public static bool GetOptOut()
        {
            var settings = db.GetCollection<Setting>("Settings");
            var optout = settings.FindOne(x => x.Name == "TelemetryOptOut");
            return bool.Parse(optout.Value);
        }

        public static void SetOptOut(bool OptOut)
        {
            var settings = db.GetCollection<Setting>("Settings");

            settings.Upsert(new Setting() { Name = "TelemetryOptOut", Value = OptOut.ToString() });
        }

        public static void WriteFileMonitor(FileMonitorObject obj, string runId)
        {
            var fme = db.GetCollection<FileMonitorEvent>();

            fme.Insert(new FileMonitorEvent()
            {
                RunId = runId,
                FMO = obj
            });
        }

        public static Run GetRun(string RunId)
        {
            var runs = db.GetCollection<Run>("Runs");

            return runs.FindOne(x => x.RunId.Equals(RunId));
        }

        public static List<string> GetMonitorRuns()
        {
            return GetRuns("monitor");
        }

        public static List<string> GetRuns(string type)
        {
            var runs = db.GetCollection<Run>("Runs");

            return runs.Find(x => x.Type.Equals(type)).Select(x => x.RunId).ToList();
        }

        public static List<string> GetRuns()
        {
            return GetRuns("collect");
        }

        public static List<FileMonitorEvent> GetMonitorResults(string runId, int offset, int numResults)
        {
            var fme = db.GetCollection<FileMonitorEvent>("FileMonitorEvents");
            return fme.Find(x => x.RunId.Equals(runId), skip: offset, limit: numResults).ToList();
        }

        public static int GetNumMonitorResults(string runId)
        {
            var fme = db.GetCollection<FileMonitorEvent>("FileMonitorEvent");
            return fme.Count(x => x.RunId.Equals(runId));
        }

        public static IEnumerable<CompareResult> GetComparisonResults(string firstRunId, string secondRunId, RESULT_TYPE resultType, int offset = 0, int numResults = 2147483647)
        {
            var crs = db.GetCollection<CompareResult>("CompareResult");

            return crs.Find(x => x.BaseRunId.Equals(firstRunId) && x.CompareRunId.Equals(secondRunId) && x.ResultType.Equals(resultType),offset,numResults);
        }

        public static int GetComparisonResultsCount(string firstRunId, string secondRunId, int resultType)
        {
            var crs = db.GetCollection<CompareResult>("CompareResult");

            return crs.Count(x => x.BaseRunId.Equals(firstRunId) && x.CompareRunId.Equals(secondRunId) && x.ResultType.Equals(resultType));
        }

        public static object GetCommonResultTypes(string baseId, string compareId)
        {
            var json_out = new Dictionary<string, bool>(){
                { "File", false },
                { "Certificate", false },
                { "Registry", false },
                { "Port", false },
                { "Service", false },
                { "User", false },
                { "Firewall", false },
                { "Com", false },
                { "Log", false }
            };

            var runs = db.GetCollection<Run>("Runs");

            var firstRun = runs.FindOne(x => x.RunId.Equals(baseId));
            var secondRun = runs.FindOne(x => x.RunId.Equals(compareId));

            foreach (var collectType in firstRun.ResultTypes)
            {
                if (collectType.Value.Equals(true) && secondRun.ResultTypes[collectType.Key].Equals(true))
                {
                    switch (collectType.Key)
                    {
                        case RESULT_TYPE.FILE:
                            json_out["File"] = true;
                            break;
                        case RESULT_TYPE.CERTIFICATE:
                            json_out["Certificate"] = true;
                            break;
                        case RESULT_TYPE.REGISTRY:
                            json_out["Registry"] = true;
                            break;
                        case RESULT_TYPE.PORT:
                            json_out["Port"] = true;
                            break;
                        case RESULT_TYPE.SERVICE:
                            json_out["Service"] = true;
                            break;
                        case RESULT_TYPE.USER:
                            json_out["User"] = true;
                            break;
                        case RESULT_TYPE.FIREWALL:
                            json_out["Firewall"] = true;
                            break;
                        case RESULT_TYPE.COM:
                            json_out["Com"] = true;
                            break;
                        case RESULT_TYPE.LOG:
                            json_out["Log"] = true;
                            break;
                    }
                }
            }

            return json_out;
        }

        public static bool GetComparisonCompleted(string firstRunId, string secondRunId)
        {
            var cr = db.GetCollection<CompareRun>("CompareRuns");

            return cr.Exists(x => x.FirstRunId.Equals(firstRunId) && x.SecondRunId.Equals(secondRunId));
        }
    }
}
